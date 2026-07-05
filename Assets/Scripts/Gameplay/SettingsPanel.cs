using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using TMPro;

namespace JumpNowBro.Gameplay
{
    /// Code-built settings panel (#128): audio (master/music/SFX sliders + mute) and display (resolution,
    /// fullscreen, vsync, quality), all live-applied and persisted through GameSettings. Self-spawns its own
    /// ScreenSpaceOverlay canvas at sortingOrder 200 (above the main menu at 100) like GameHudOverlay, so it
    /// needs no scene authoring. Opened from the menu's Settings button or by Esc; it's a non-pausing overlay
    /// (a networked host can't be paused without desync), so the game keeps running behind it.
    ///
    /// Resolution + fullscreen are no-ops in the Editor and only take effect in a standalone build (a hint in
    /// the panel says so). Built with sliders + toggle-buttons + prev/next cyclers rather than TMP_Dropdown:
    /// the dropdown is ~150 lines of template scaffolding to build in code, and cyclers are simpler and just as
    /// keyboard-navigable (they're ordinary Selectables).
    public sealed class SettingsPanel : MonoBehaviour
    {
        public static SettingsPanel Instance { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void AutoSpawn()
        {
            if (Instance == null)
                new GameObject(nameof(SettingsPanel)).AddComponent<SettingsPanel>();
        }

        GameObject root;                                  // the dim + card; toggled on/off
        Slider masterSlider, musicSlider, sfxSlider;
        TMP_Text muteLabel, fullscreenLabel, vsyncLabel, resLabel, qualityLabel, paletteLabel;
        GameObject firstSelectable;

        // #135 tabs + rebind cells
        GameObject[] tabs;
        Image[] tabButtons;
        int activeTab;
        TMP_Text rebindStatus, p1Header, p2Header;
        readonly List<(InputRebinds.Cell cell, TMP_Text label)> rebindCells = new List<(InputRebinds.Cell, TMP_Text)>();

        // #129 Data tab: two-click-armed records reset
        TMP_Text recordsResetLabel, recordsStatus;
        float recordsResetArmedUntil;

        readonly List<Vector2Int> resOptions = new List<Vector2Int>();
        int resIndex;
        int qualityIndex;

        public bool IsOpen => root != null && root.activeSelf;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            BuildCanvas();
            BuildPanel();
            root.SetActive(false);
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        void Update()
        {
            // #135: while a rebind is listening (and through the frame its cancel lands on — the Input
            // System processes the cancel BEFORE MonoBehaviour.Update), Esc/Start belong to the rebind,
            // not the panel toggle.
            if (InputRebinds.SuppressUiToggle)
            {
                fieldFocusedLastFrame = TypingInInputField();
                return;
            }

            // Esc while typing in a TMP_InputField (menu name/lobby/IP) cancels the edit; it must not ALSO
            // open settings. The field releases focus on the SAME frame the cancel lands (before or after
            // this Update depending on execution order), so a same-frame isFocused check misses it: remember
            // last frame's focus too. The Esc after that one opens settings as normal.
            bool focusedNow = TypingInInputField();
            bool esc = Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
            // #134 gamepad: Start toggles; East closes (mirrors the UI Cancel convention).
            bool padStart = Gamepad.current != null && Gamepad.current.startButton.wasPressedThisFrame;
            bool padBack = IsOpen && Gamepad.current != null && Gamepad.current.buttonEast.wasPressedThisFrame;
            if ((esc && !focusedNow && !fieldFocusedLastFrame) || padStart) Toggle();
            else if (padBack) Close();
            fieldFocusedLastFrame = focusedNow;

            if (IsOpen) UiKit.EnsureSelection(firstSelectable);   // pad/keyboard nav always has a starting point
        }

        bool fieldFocusedLastFrame;

        static bool TypingInInputField()
        {
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (selected == null) return false;
            var field = selected.GetComponent<TMP_InputField>();
            return field != null && field.isFocused;
        }

        public void Toggle() { if (IsOpen) Close(); else Open(); }

        public void Open()
        {
            SyncFromState();
            root.SetActive(true);
            if (EventSystem.current != null && firstSelectable != null)
                EventSystem.current.SetSelectedGameObject(firstSelectable);
        }

        public void Close()
        {
            root.SetActive(false);
            GameSettings.Flush();                          // one disk write per editing session, not per slider tick
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        }

        // Pull live values into the controls each time the panel opens (without firing the change callbacks).
        void SyncFromState()
        {
            var am = AudioManager.Instance;
            masterSlider.SetValueWithoutNotify(am != null ? am.MasterVolume : GameSettings.MasterVolume);
            musicSlider.SetValueWithoutNotify(am != null ? am.MusicVolume : GameSettings.MusicVolume);
            sfxSlider.SetValueWithoutNotify(am != null ? am.SFXVolume : GameSettings.SFXVolume);
            muteLabel.text = "Mute: " + (GameSettings.Muted ? "On" : "Off");

            BuildResolutionOptions();
            fullscreenLabel.text = "Fullscreen: " + (GameSettings.Fullscreen ? "On" : "Off");
            vsyncLabel.text = "VSync: " + (GameSettings.VSync ? "On" : "Off");
            paletteLabel.text = "Colourblind palette: " + (GameSettings.PaletteMode != 0 ? "On" : "Off");
            RefreshRebindLabels();   // saved overrides may have restored after this panel was built
            // #129: disarm a half-armed records reset from a previous open.
            recordsResetArmedUntil = 0f;
            if (recordsResetLabel != null) recordsResetLabel.text = "Reset records";
            if (recordsStatus != null) recordsStatus.text = "";
            RefreshResLabel();
            qualityIndex = Mathf.Clamp(GameSettings.QualityLevel, 0, Mathf.Max(0, QualitySettings.names.Length - 1));
            RefreshQualityLabel();
        }

        // ---- build ----

        void BuildCanvas()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 200;                     // above the main menu (100) and HUD overlay (50)
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();   // interactive, unlike the presentational HUD overlay
        }

        void BuildPanel()
        {
            root = new GameObject("Dim", typeof(RectTransform), typeof(Image));
            root.transform.SetParent(transform, false);
            root.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);   // raycastTarget on: eats clicks behind it
            Stretch(root);

            var card = new GameObject("Card", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            card.transform.SetParent(root.transform, false);
            card.GetComponent<Image>().color = new Color(0.10f, 0.12f, 0.18f, 0.98f);
            var vl = card.GetComponent<VerticalLayoutGroup>();
            vl.childAlignment = TextAnchor.UpperCenter;
            vl.spacing = 8;
            vl.padding = new RectOffset(28, 28, 22, 22);
            vl.childControlWidth = vl.childControlHeight = true;
            vl.childForceExpandWidth = vl.childForceExpandHeight = false;
            var fit = card.GetComponent<ContentSizeFitter>();
            fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var rt = card.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;

            Label(card.transform, "Settings", 34, FontStyles.Bold, 0.95f);

            // #135: tabs (the controls grid would overflow a single column). Content lives in per-tab
            // columns under the same card; SelectTab toggles visibility and the fitter re-sizes the card.
            var tabBar = Row(card.transform);
            string[] tabNames = { "Audio", "Display", "Controls", "Access", "Data" };
            tabs = new GameObject[tabNames.Length];
            tabButtons = new Image[tabNames.Length];
            for (int i = 0; i < tabNames.Length; i++)
            {
                int idx = i;
                var b = MakeButton(tabBar.transform, tabNames[i], 86, 34, () => SelectTab(idx));
                tabButtons[i] = b.GetComponent<Image>();
                tabs[i] = Column(card.transform);
            }

            // -- Audio --
            var audio = tabs[0].transform;
            masterSlider = SliderRow(audio, "Master", GameSettings.SetMasterVolume);
            musicSlider  = SliderRow(audio, "Music",  GameSettings.SetMusicVolume);
            sfxSlider    = SliderRow(audio, "SFX",    GameSettings.SetSFXVolume);
            muteLabel = ToggleButton(audio, () =>
            {
                bool m = !GameSettings.Muted; GameSettings.SetMuted(m);
                muteLabel.text = "Mute: " + (m ? "On" : "Off");
            });

            // -- Display --
            var display = tabs[1].transform;
            resLabel = CyclerRow(display, "Resolution", () => StepResolution(-1), () => StepResolution(1));
            fullscreenLabel = ToggleButton(display, () =>
            {
                bool on = !GameSettings.Fullscreen; GameSettings.SetFullscreen(on);
                fullscreenLabel.text = "Fullscreen: " + (on ? "On" : "Off");
            });
            vsyncLabel = ToggleButton(display, () =>
            {
                bool on = !GameSettings.VSync; GameSettings.SetVSync(on);
                vsyncLabel.text = "VSync: " + (on ? "On" : "Off");
            });
            if (QualitySettings.names.Length > 1)
                qualityLabel = CyclerRow(display, "Quality", () => StepQuality(-1), () => StepQuality(1));
            Label(display, "Resolution / fullscreen apply in a standalone build, not the Editor.", 13, FontStyles.Italic, 0.5f);

            // -- Controls (#135) --
            BuildControlsTab(tabs[2].transform);

            // -- Access (#136: colourblind palette through ActionStyle + PlayerIdentity) --
            paletteLabel = ToggleButton(tabs[3].transform, () =>
            {
                int mode = GameSettings.PaletteMode != 0 ? 0 : 1;
                GameSettings.SetPaletteMode(mode);
                paletteLabel.text = "Colourblind palette: " + (mode != 0 ? "On" : "Off");
            });

            // -- Data (#129: per-level records) --
            var data = tabs[4].transform;
            Label(data, "Records are stored separately for solo and LAN.", 13, FontStyles.Italic, 0.5f);
            var resetBtn = MakeButton(data, "Reset records", 360, 40, OnResetRecordsClicked);
            recordsResetLabel = resetBtn.GetComponentInChildren<TMP_Text>();
            recordsStatus = Label(data, "", 14, FontStyles.Italic, 0.85f);

            MakeButton(card.transform, "Back", 360, 48, Close);
            SelectTab(0);
        }

        void SelectTab(int index)
        {
            activeTab = index;
            for (int i = 0; i < tabs.Length; i++)
            {
                tabs[i].SetActive(i == index);
                tabButtons[i].color = i == index ? new Color(0.30f, 0.55f, 0.95f, 1f) : new Color(1f, 1f, 1f, 0.15f);
            }
            firstSelectable = tabButtons[index].gameObject;
            if (IsOpen && EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(firstSelectable);
        }

        // A P1 | P2 grid over the SOLO maps (the two halves' identities), one row per action; keyboard
        // rebinds mirror into the shared LAN layout inside InputRebinds. Shared gamepad cells sit below.
        void BuildControlsTab(Transform parent)
        {
            Label(parent, "Click a binding, then press the new key/button. Esc cancels.", 13, FontStyles.Italic, 0.5f);

            var header = Row(parent);
            RowLabel(header.transform, "");
            p1Header = HeaderCell(header.transform, "P1");
            p2Header = HeaderCell(header.transform, "P2");

            rebindCells.Clear();
            var rows = new Dictionary<string, GameObject>();
            bool padSection = false;
            foreach (var cell in InputRebinds.Cells())
            {
                if (cell.Column < 0 && !padSection)
                {
                    padSection = true;
                    Label(parent, "Gamepad (both players)", 14, FontStyles.Italic, 0.6f);
                }
                if (!rows.TryGetValue(cell.Label, out var row))
                {
                    row = Row(parent);
                    RowLabel(row.transform, cell.Label);
                    rows[cell.Label] = row;   // P1 cells arrive before P2 cells, so columns land in order
                }
                var btn = MakeButton(row.transform, InputRebinds.DisplayName(cell), 130, 34, null);
                var label = btn.GetComponentInChildren<TMP_Text>();
                var captured = cell;
                btn.onClick.AddListener(() => BeginRebind(captured, label));
                rebindCells.Add((captured, label));
            }
            rebindStatus = Label(parent, "", 14, FontStyles.Italic, 0.85f);
            MakeButton(parent, "Reset to defaults", 360, 40, () =>
            {
                InputRebinds.ResetAll();
                RefreshRebindLabels();
                rebindStatus.text = "Controls reset to defaults";
            });
        }

        // Records are precious and this wipes both tables: arm on the first click, execute only on a
        // second click inside 3 s (mirrors the destructive-action caution of the Controls reset, but that
        // one is recoverable by rebinding — records are not).
        void OnResetRecordsClicked()
        {
            if (Time.unscaledTime > recordsResetArmedUntil)
            {
                recordsResetArmedUntil = Time.unscaledTime + 3f;
                recordsResetLabel.text = "Really reset? Click again";
                recordsStatus.text = "";
                return;
            }
            recordsResetArmedUntil = 0f;
            recordsResetLabel.text = "Reset records";
            GameRecords.ResetAll(LevelManager.Instance != null ? LevelManager.Instance.LevelCount : 8);
            recordsStatus.text = "Records cleared";
        }

        TMP_Text HeaderCell(Transform parent, string text)
        {
            var go = new GameObject("Head", typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = 17;
            t.fontStyle = FontStyles.Bold;
            t.alignment = TextAlignmentOptions.Center;
            t.raycastTarget = false;
            var le = go.GetComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = 130f;
            le.preferredHeight = 24f;
            return t;
        }

        void BeginRebind(InputRebinds.Cell cell, TMP_Text label)
        {
            if (InputRebinds.Listening) return;
            label.text = "Press...";
            rebindStatus.text = "";
            InputRebinds.StartRebind(cell, error =>
            {
                RefreshRebindLabels();
                rebindStatus.text = error ?? "";
            });
        }

        void RefreshRebindLabels()
        {
            foreach (var (cell, label) in rebindCells) label.text = InputRebinds.DisplayName(cell);
            // Header colours follow the active palette (the colourblind toggle lives in this same panel).
            if (p1Header != null) p1Header.color = PlayerIdentity.ColorOf(JumpNowBro.Util.InputOwner.P1);
            if (p2Header != null) p2Header.color = PlayerIdentity.ColorOf(JumpNowBro.Util.InputOwner.P2);
        }

        // ---- display steppers ----

        void BuildResolutionOptions()
        {
            resOptions.Clear();
            var seen = new HashSet<long>();
            foreach (var r in Screen.resolutions)
            {
                long key = ((long)r.width << 20) | (uint)r.height;
                if (seen.Add(key)) resOptions.Add(new Vector2Int(r.width, r.height));
            }
            if (resOptions.Count == 0) resOptions.Add(new Vector2Int(Screen.width, Screen.height));

            int w = GameSettings.ResWidth, h = GameSettings.ResHeight;
            resIndex = resOptions.FindIndex(o => o.x == w && o.y == h);
            if (resIndex < 0)
            {
                // No exact match (common windowed/retina case): pick the nearest by area so the first
                // step doesn't jump the window to the largest mode.
                long target = (long)w * h;
                long best = long.MaxValue;
                for (int i = 0; i < resOptions.Count; i++)
                {
                    long diff = System.Math.Abs((long)resOptions[i].x * resOptions[i].y - target);
                    if (diff < best) { best = diff; resIndex = i; }
                }
            }
        }

        void StepResolution(int dir)
        {
            if (resOptions.Count == 0) return;
            resIndex = Mathf.Clamp(resIndex + dir, 0, resOptions.Count - 1);
            var o = resOptions[resIndex];
            GameSettings.SetResolution(o.x, o.y);
            RefreshResLabel();
        }

        void RefreshResLabel()
        {
            if (resOptions.Count == 0) { resLabel.text = "-"; return; }
            var o = resOptions[Mathf.Clamp(resIndex, 0, resOptions.Count - 1)];
            resLabel.text = $"{o.x} x {o.y}";
        }

        void StepQuality(int dir)
        {
            int n = QualitySettings.names.Length;
            qualityIndex = Mathf.Clamp(qualityIndex + dir, 0, n - 1);
            GameSettings.SetQuality(qualityIndex);
            RefreshQualityLabel();
        }

        void RefreshQualityLabel()
        {
            if (qualityLabel == null) return;
            qualityLabel.text = QualitySettings.names[Mathf.Clamp(qualityIndex, 0, QualitySettings.names.Length - 1)];
        }

        // ---- tiny UGUI builders ----

        static void Stretch(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        TMP_Text Label(Transform parent, string text, float size, FontStyles style, float alpha)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text; t.fontSize = size; t.fontStyle = style;
            t.color = new Color(1f, 1f, 1f, alpha);
            t.alignment = TextAlignmentOptions.Center;
            t.raycastTarget = false;
            go.GetComponent<LayoutElement>().preferredHeight = size + 8f;
            return t;
        }

        // A tab's content column: nested vertical group under the card (no fitter — the card's layout
        // group reads the group's preferred height directly; a nested ContentSizeFitter would fight it).
        GameObject Column(Transform parent)
        {
            var go = new GameObject("Tab", typeof(RectTransform), typeof(VerticalLayoutGroup));
            go.transform.SetParent(parent, false);
            var v = go.GetComponent<VerticalLayoutGroup>();
            v.childAlignment = TextAnchor.UpperCenter;
            v.spacing = 8;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = v.childForceExpandHeight = false;
            return go;
        }

        GameObject Row(Transform parent)
        {
            var go = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var h = go.GetComponent<HorizontalLayoutGroup>();
            h.childAlignment = TextAnchor.MiddleLeft;
            h.spacing = 10;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = h.childForceExpandHeight = false;
            go.GetComponent<LayoutElement>().preferredHeight = 40f;
            return go;
        }

        void RowLabel(Transform parent, string text)
        {
            var go = new GameObject("RowLabel", typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text; t.fontSize = 18; t.color = Color.white;
            t.alignment = TextAlignmentOptions.Left; t.raycastTarget = false;
            var le = go.GetComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = 130f;
            le.preferredHeight = 30f;
        }

        Slider SliderRow(Transform parent, string label, UnityAction<float> onChanged)
        {
            var row = Row(parent);
            RowLabel(row.transform, label);

            var go = new GameObject("Slider", typeof(RectTransform), typeof(Slider), typeof(LayoutElement));
            go.transform.SetParent(row.transform, false);
            var le = go.GetComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = 230f;
            le.preferredHeight = le.minHeight = 24f;

            var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(go.transform, false);
            var bgrt = bg.GetComponent<RectTransform>();
            bgrt.anchorMin = new Vector2(0f, 0.3f); bgrt.anchorMax = new Vector2(1f, 0.7f);
            bgrt.offsetMin = bgrt.offsetMax = Vector2.zero;
            bg.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.18f);

            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(go.transform, false);
            var fart = fillArea.GetComponent<RectTransform>();
            fart.anchorMin = new Vector2(0f, 0.3f); fart.anchorMax = new Vector2(1f, 0.7f);
            fart.offsetMin = new Vector2(0f, 0f); fart.offsetMax = new Vector2(-16f, 0f);
            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(fillArea.transform, false);
            fill.GetComponent<RectTransform>().sizeDelta = new Vector2(16f, 0f);
            fill.GetComponent<Image>().color = new Color(0.30f, 0.55f, 0.95f, 1f);

            var hsa = new GameObject("Handle Slide Area", typeof(RectTransform));
            hsa.transform.SetParent(go.transform, false);
            var hsart = hsa.GetComponent<RectTransform>();
            hsart.anchorMin = Vector2.zero; hsart.anchorMax = Vector2.one;
            hsart.offsetMin = new Vector2(8f, 0f); hsart.offsetMax = new Vector2(-8f, 0f);
            var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handle.transform.SetParent(hsa.transform, false);
            handle.GetComponent<RectTransform>().sizeDelta = new Vector2(16f, 0f);
            handle.GetComponent<Image>().color = Color.white;

            var s = go.GetComponent<Slider>();
            s.fillRect = fill.GetComponent<RectTransform>();
            s.handleRect = handle.GetComponent<RectTransform>();
            s.targetGraphic = handle.GetComponent<Image>();
            s.direction = Slider.Direction.LeftToRight;
            s.minValue = 0f; s.maxValue = 1f;
            s.onValueChanged.AddListener(onChanged);
            return s;
        }

        TMP_Text CyclerRow(Transform parent, string label, UnityAction prev, UnityAction next)
        {
            var row = Row(parent);
            RowLabel(row.transform, label);
            MakeButton(row.transform, "<", 40, 34, prev);
            var value = new GameObject("Value", typeof(RectTransform), typeof(LayoutElement));
            value.transform.SetParent(row.transform, false);
            var t = value.AddComponent<TextMeshProUGUI>();
            t.text = "-"; t.fontSize = 17; t.color = Color.white;
            t.alignment = TextAlignmentOptions.Center; t.raycastTarget = false;
            var le = value.GetComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = 150f; le.preferredHeight = 30f;
            MakeButton(row.transform, ">", 40, 34, next);
            return t;
        }

        // A button whose label flips On/Off; returns the label so the caller updates the text on click.
        TMP_Text ToggleButton(Transform parent, UnityAction onClick)
        {
            var btn = MakeButton(parent, "", 360, 40, onClick);
            return btn.GetComponentInChildren<TMP_Text>();
        }

        Button MakeButton(Transform parent, string label, float w, float h, UnityAction onClick)
        {
            var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.15f);
            var le = go.GetComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = w;
            le.preferredHeight = le.minHeight = h;
            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            var t = new GameObject("Text", typeof(RectTransform));
            t.transform.SetParent(go.transform, false);
            var txt = t.AddComponent<TextMeshProUGUI>();
            txt.text = label; txt.fontSize = 18; txt.color = Color.white;
            txt.alignment = TextAlignmentOptions.Center; txt.raycastTarget = false;
            Stretch(t);
            if (onClick != null) btn.onClick.AddListener(onClick);
            return btn;
        }
    }
}
