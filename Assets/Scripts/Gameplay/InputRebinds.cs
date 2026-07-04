using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using JumpNowBro.Util;

namespace JumpNowBro.Gameplay
{
    /// Control rebinding (#135) over the project-wide actions asset (`InputSystem.actions` resolves to the
    /// SAME imported instance the Player prefab's input sources serialize, so overrides applied here reach
    /// gameplay directly, solo and networked alike).
    ///
    /// Model (per Kerem's round-2 feedback): keyboard cells are a P1 | P2 grid editing the SOLO maps
    /// (Player1/Player2 — the two halves' identities), and every keyboard rebind MIRRORS to the NetPlayer
    /// twin binding (same action name + same original path; the NetPlayer keyboard set is exactly the union
    /// of the two solo halves since the layout unification), so one rebind works in solo AND LAN. Gamepad
    /// Jump/Dash cells live on NetPlayer and mirror to both solo maps. Move stick/dpad not rebindable.
    /// Conflicts are checked across BOTH solo halves with map-qualified names ("P2 Jump"), so the couch
    /// split can't silently share a key.
    ///
    /// Persistence: Input System override JSON in PlayerPrefs (keyed per binding GUID), restored before the
    /// first scene loads.
    public static class InputRebinds
    {
        const string NetMap = "NetPlayer";
        static readonly (string map, string col)[] SoloMaps = { ("Player1", "P1"), ("Player2", "P2") };

        /// True while an interactive rebind is listening. SettingsPanel suppresses its Esc/Start toggle on
        /// this AND through the end frame below (the Input System processes the cancel before Update runs).
        public static bool Listening { get; private set; }
        public static int LastEndFrame { get; private set; } = -1;
        public static bool SuppressUiToggle => Listening || Time.frameCount <= LastEndFrame;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Restore()
        {
            var actions = InputSystem.actions;
            if (actions == null) return;
            var json = GameSettings.InputOverrides;
            if (!string.IsNullOrEmpty(json))
            {
                try { actions.LoadBindingOverridesFromJson(json); }
                catch (Exception e) { Debug.LogWarning($"InputRebinds: saved overrides rejected, resetting ({e.Message})"); GameSettings.SetInputOverrides(""); }
            }
        }

        public static void Save()
        {
            var actions = InputSystem.actions;
            if (actions != null) GameSettings.SetInputOverrides(actions.SaveBindingOverridesAsJson());
        }

        public static void ResetAll()
        {
            var actions = InputSystem.actions;
            if (actions == null) return;
            actions.RemoveAllBindingOverrides();
            GameSettings.SetInputOverrides("");
        }

        // ---- cell discovery (the settings UI builds one rebind cell per entry) ----

        public struct Cell
        {
            public InputAction Action;
            public int BindingIndex;
            public bool Gamepad;
            public string Label;        // row label, e.g. "Move Left"
            public int Column;          // 0 = P1, 1 = P2, -1 = shared (gamepad)
            public string Qualified;    // conflict-scope name, e.g. "P1 Jump" (pad: the action name)
        }

        /// The rebindable cells: for each solo half, Move Left / Move Right / Jump / Dash (keyboard), then
        /// the shared gamepad Jump/Dash. Resolved fresh each call (indices are stable; cheap to re-derive).
        public static List<Cell> Cells()
        {
            var list = new List<Cell>();
            var actions = InputSystem.actions;
            if (actions == null) return list;

            foreach (var (mapName, col) in SoloMaps)
            {
                var map = actions.FindActionMap(mapName);
                if (map == null) continue;
                int column = col == "P1" ? 0 : 1;
                AddCompositePart(list, map.FindAction("Move"), "left", "Move Left", column, col);
                AddCompositePart(list, map.FindAction("Move"), "right", "Move Right", column, col);
                AddButtonCell(list, map.FindAction("Jump"), "<Keyboard>", false, "Jump", column, col);
                AddButtonCell(list, map.FindAction("Dash"), "<Keyboard>", false, "Dash", column, col);
            }

            var net = actions.FindActionMap(NetMap);
            if (net != null)
            {
                AddButtonCell(list, net.FindAction("Jump"), "<Gamepad>", true, "Jump (pad)", -1, null);
                AddButtonCell(list, net.FindAction("Dash"), "<Gamepad>", true, "Dash (pad)", -1, null);
            }
            return list;
        }

        static void AddCompositePart(List<Cell> list, InputAction action, string partName, string label, int column, string col)
        {
            if (action == null) return;
            for (int i = 0; i < action.bindings.Count; i++)
            {
                var b = action.bindings[i];
                if (b.isPartOfComposite && string.Equals(b.name, partName, StringComparison.OrdinalIgnoreCase)
                    && b.path.StartsWith("<Keyboard>", StringComparison.OrdinalIgnoreCase))
                    list.Add(new Cell { Action = action, BindingIndex = i, Gamepad = false, Label = label,
                                        Column = column, Qualified = $"{col} {action.name}" });
            }
        }

        static void AddButtonCell(List<Cell> list, InputAction action, string devicePrefix, bool gamepad, string label, int column, string col)
        {
            if (action == null) return;
            for (int i = 0; i < action.bindings.Count; i++)
            {
                var b = action.bindings[i];
                if (b.isComposite || b.isPartOfComposite) continue;
                if (!b.path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (gamepad && (b.path.Contains("leftStick") || b.path.Contains("dpad"))) continue;   // Move stays stick/dpad
                list.Add(new Cell { Action = action, BindingIndex = i, Gamepad = gamepad, Label = label,
                                    Column = column, Qualified = col != null ? $"{col} {action.name}" : action.name });
            }
        }

        public static string DisplayName(Cell c)
        {
            var path = c.Action.bindings[c.BindingIndex].effectivePath;
            return string.IsNullOrEmpty(path)
                ? "-"
                : InputControlPath.ToHumanReadableString(path, InputControlPath.HumanReadableStringOptions.OmitDevice);
        }

        // ---- the interactive rebind ----

        /// Listen for a new control for this cell. onDone(error) runs on completion/cancel: error is null on
        /// success or cancel, else the conflict message. The action is disabled around the operation
        /// (PerformInteractiveRebinding throws on enabled actions, and in-session the NetPlayer map is live
        /// under the non-pausing settings overlay).
        public static void StartRebind(Cell cell, Action<string> onDone)
        {
            if (Listening) return;
            var action = cell.Action;
            string previousOverride = action.bindings[cell.BindingIndex].overridePath;   // for conflict revert
            bool wasEnabled = action.enabled;
            if (wasEnabled) action.Disable();
            Listening = true;

            action.PerformInteractiveRebinding(cell.BindingIndex)
                .WithControlsExcluding("<Mouse>")
                .WithControlsExcluding("<Gamepad>/start")     // Start toggles the settings panel globally; binding
                .WithControlsExcluding("<Gamepad>/select")    // jump to it would open settings on every jump
                .WithControlsHavingToMatchPath(cell.Gamepad ? "<Gamepad>" : "<Keyboard>")
                .WithCancelingThrough("<Keyboard>/escape")
                .OnMatchWaitForAnother(0.05f)
                .OnComplete(o =>
                {
                    string error = null;
                    string newPath = action.bindings[cell.BindingIndex].effectivePath;
                    var conflict = BindingConflicts.FindConflict(FlattenForCell(cell), cell.Qualified, newPath);
                    if (conflict != null)
                    {
                        // Revert to what the cell had before listening.
                        if (string.IsNullOrEmpty(previousOverride)) action.RemoveBindingOverride(cell.BindingIndex);
                        else action.ApplyBindingOverride(cell.BindingIndex, previousOverride);
                        error = $"'{Pretty(newPath)}' is already bound to {conflict}";
                    }
                    else
                    {
                        if (cell.Gamepad) MirrorToSoloMaps(action.name, cell.BindingIndex, newPath);
                        else MirrorToNetPlayer(action.name, action.bindings[cell.BindingIndex].path, newPath);
                        Save();
                    }
                    Finish(o, action, wasEnabled);
                    onDone?.Invoke(error);
                })
                .OnCancel(o =>
                {
                    Finish(o, action, wasEnabled);
                    onDone?.Invoke(null);
                })
                .Start();
        }

        static void Finish(InputActionRebindingExtensions.RebindingOperation o, InputAction action, bool reEnable)
        {
            o.Dispose();
            Listening = false;
            LastEndFrame = Time.frameCount;   // the toggle key that ended this must not also toggle the panel
            if (reEnable) action.Enable();
        }

        // The conflict scope for a cell, as (qualifiedAction, effectivePath) pairs, excluding the cell itself.
        // Keyboard: BOTH solo maps with map-qualified names ("P1 Move"), INCLUDING composite parts without UI
        // cells (W/S, up/down) so a rebind onto them is caught, and cross-half duplicates ("P1 Jump" onto P2's
        // key) are conflicts, keeping the couch split from silently sharing a key. Gamepad: the NetPlayer pad set.
        static List<(string action, string path)> FlattenForCell(Cell cell)
        {
            var result = new List<(string, string)>();
            var actions = InputSystem.actions;
            if (actions == null) return result;

            if (cell.Gamepad)
            {
                var map = actions.FindActionMap(NetMap);
                if (map == null) return result;
                foreach (var action in map.actions)
                    for (int i = 0; i < action.bindings.Count; i++)
                    {
                        if (action == cell.Action && i == cell.BindingIndex) continue;
                        var b = action.bindings[i];
                        if (b.isComposite) continue;
                        var path = b.effectivePath;
                        if (string.IsNullOrEmpty(path) || !path.StartsWith("<Gamepad>", StringComparison.OrdinalIgnoreCase)) continue;
                        result.Add((action.name, path));
                    }
                return result;
            }

            foreach (var (mapName, col) in SoloMaps)
            {
                var map = actions.FindActionMap(mapName);
                if (map == null) continue;
                foreach (var action in map.actions)
                    for (int i = 0; i < action.bindings.Count; i++)
                    {
                        if (action == cell.Action && i == cell.BindingIndex) continue;
                        var b = action.bindings[i];
                        if (b.isComposite) continue;                    // headers have no control path
                        var path = b.effectivePath;
                        if (string.IsNullOrEmpty(path) || !path.StartsWith("<Keyboard>", StringComparison.OrdinalIgnoreCase)) continue;
                        result.Add(($"{col} {action.name}", path));
                    }
            }
            return result;
        }

        // A keyboard rebind on a solo half mirrors to its NetPlayer twin (same action name + same ORIGINAL
        // path: the NetPlayer keyboard set is exactly the union of the two solo halves), so the change
        // applies in LAN play too.
        static void MirrorToNetPlayer(string actionName, string originalPath, string overridePath)
        {
            var actions = InputSystem.actions;
            var map = actions != null ? actions.FindActionMap(NetMap) : null;
            var action = map != null ? map.FindAction(actionName) : null;
            if (action == null) return;
            for (int i = 0; i < action.bindings.Count; i++)
                if (!action.bindings[i].isComposite && action.bindings[i].path == originalPath)
                    action.ApplyBindingOverride(i, overridePath);
        }

        // #134 authored identical gamepad binding paths across NetPlayer/Player1/Player2, so a pad rebind in
        // the settings mirrors to the solo maps by matching the ORIGINAL path of the same action's binding.
        static void MirrorToSoloMaps(string actionName, int netBindingIndex, string overridePath)
        {
            var actions = InputSystem.actions;
            if (actions == null) return;
            var netMap = actions.FindActionMap(NetMap);
            if (netMap == null) return;
            string originalPath = netMap.FindAction(actionName).bindings[netBindingIndex].path;

            foreach (var mapName in new[] { "Player1", "Player2" })
            {
                var map = actions.FindActionMap(mapName);
                var action = map != null ? map.FindAction(actionName) : null;
                if (action == null) continue;
                for (int i = 0; i < action.bindings.Count; i++)
                    if (action.bindings[i].path == originalPath)
                        action.ApplyBindingOverride(i, overridePath);
            }
        }

        static string Pretty(string path) =>
            InputControlPath.ToHumanReadableString(path, InputControlPath.HumanReadableStringOptions.OmitDevice);
    }
}
