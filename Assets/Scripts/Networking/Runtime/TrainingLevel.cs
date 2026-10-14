using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using TMPro;
using JumpNowBro.Gameplay;
using JumpNowBro.Util;

namespace JumpNowBro.Networking
{
    /// Scene-local guidance reads the same character pose and ownership map on each peer.
    /// The course's obstacles require the actions; tutorial progress needs no separate network state.
    public sealed class TrainingLevel : MonoBehaviour
    {
        [SerializeField] SwapTrigger movePortal;
        [SerializeField] float[] soloNoteStarts = { 2, 12, 20, 33, 62, 93, 108 };
        [SerializeField] float[] multiplayerNoteStarts = { 2, 7, 12, 33, 62, 93, 108 };
        [SerializeField] Vector3 soloSwapPosition = new Vector3(24, 1.1f, 0);
        [SerializeField] Vector3 multiplayerSwapPosition = new Vector3(4, 1.1f, 0);

        [SerializeField] float minimumViewWidth = 28f;

        Camera trainingCamera;
        float previousCameraSize;
        const float MinimumNoteSeconds = 5f;
        bool multiplayer;
        int note;
        float furthestX = float.NegativeInfinity;
        float noteVisibleSeconds;
        RectTransform canvasRect, panelRect;
        GameObject panel;
        TMP_Text heading, instruction, controls;
        float nextRefresh, lastRefresh;

        public string Instructions => instruction != null ? instruction.text : "";

        void Awake()
        {
            multiplayer = NetworkManager.Instance != null && NetworkManager.Instance.Role != GameRole.SinglePlayer;
            movePortal.transform.position = multiplayer ? multiplayerSwapPosition : soloSwapPosition;
        }

        void Start()
        {
            trainingCamera = Camera.main;
            if (trainingCamera != null) previousCameraSize = trainingCamera.orthographicSize;
            var canvas = new GameObject("TrainingGuidance", typeof(Canvas), typeof(CanvasScaler));
            canvas.transform.SetParent(transform, false);
            canvasRect = canvas.GetComponent<RectTransform>();
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.GetComponent<Canvas>().sortingOrder = 40;
            var scaler = canvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;
            panel = UiKit.Panel(canvas.transform, new Color(0.06f, 0.09f, 0.15f, 0.94f));
            panel.GetComponent<Image>().raycastTarget = false;
            panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = panelRect.anchorMax = panelRect.pivot = new Vector2(0.5f, 0f);
            panelRect.anchoredPosition = new Vector2(0, 96);
            heading = TextAt(22, 12, 28, FontStyles.Bold);
            instruction = TextAt(20, 44, 52, FontStyles.Normal);
            controls = TextAt(16, 100, 36, FontStyles.Normal);
            controls.color = new Color(0.78f, 0.87f, 1f);
            lastRefresh = Time.unscaledTime;
        }

        void LateUpdate()
        {
            if (trainingCamera != null)
                trainingCamera.orthographicSize = Mathf.Max(previousCameraSize,
                    minimumViewWidth / (2f * Mathf.Max(0.1f, trainingCamera.aspect)));
        }

        void OnDestroy()
        {
            if (trainingCamera != null) trainingCamera.orthographicSize = previousCameraSize;
        }

        TMP_Text TextAt(int size, float top, float height, FontStyles style)
        {
            var text = UiKit.Label(panel.transform, "", size, style);
            text.alignment = TextAlignmentOptions.Left;
            text.raycastTarget = false;
            var rect = text.rectTransform;
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(1, 1);
            rect.pivot = new Vector2(0.5f, 1);
            rect.offsetMin = new Vector2(20, -top - height);
            rect.offsetMax = new Vector2(-20, -top);
            return text;
        }

        void Update()
        {
            if (panel == null || Time.unscaledTime < nextRefresh) return;
            float elapsed = Time.unscaledTime - lastRefresh;
            lastRefresh = Time.unscaledTime;
            nextRefresh = Time.unscaledTime + 0.1f;
            var lm = LevelManager.Instance;
            var net = NetworkManager.Instance;
            var player = PlayerSpawner.Instance != null ? PlayerSpawner.Instance.CurrentPlayerInstance : null;
            bool visible = player != null && lm != null && lm.IsTraining(lm.CurrentLevelIndex)
                           && !lm.IsLoading && (net == null || (!net.InLobby && !net.ConnectionLost));
            panel.SetActive(visible);
            if (!visible) return;

            var map = ControlMapStore.Instance != null ? ControlMapStore.Instance.Current : ControlMap.Default;
            furthestX = Mathf.Max(furthestX, player.transform.position.x);
            noteVisibleSeconds += elapsed;
            int reached = ReachedNote();
            if (reached > note && noteVisibleSeconds >= MinimumNoteSeconds)
            {
                note++;
                noteVisibleSeconds = 0f;
            }

            bool intro = note == 0;
            heading.gameObject.SetActive(intro);
            if (intro) heading.text = multiplayer ? "Co-op training" : "Solo training";
            panelRect.sizeDelta = new Vector2(Mathf.Min(920, canvasRect.rect.width - 32), intro ? 144 : 112);
            PlaceText(instruction, intro ? 44 : 12, 52);
            PlaceText(controls, intro ? 100 : 68, 36);
            instruction.text = NoteText();
            controls.text = Hint("Move", map.moveOwner) + "     |     " + Hint("Jump", map.jumpOwner)
                            + "     |     " + Hint("Dash", map.dashOwner);
        }

        static void PlaceText(TMP_Text text, float top, float height)
        {
            text.rectTransform.offsetMin = new Vector2(18, -top - height);
            text.rectTransform.offsetMax = new Vector2(-18, -top);
        }

        int ReachedNote()
        {
            var starts = multiplayer ? multiplayerNoteStarts : soloNoteStarts;
            for (int i = starts.Length - 1; i >= 0; i--)
                if (furthestX >= starts[i]) return i + 1;
            return 0;
        }

        string NoteText()
        {
            if (note == 0)
                return multiplayer
                    ? "You share one character. Each action belongs to one of you.\nThe HUD and bindings show your current part."
                    : "One character, two control sets. You use both P1 and P2.\nThe HUD and bindings show which set owns each action.";
            if (note == 7)
                return "All three actions have changed hands. Combine jumps and dashes here.\nThe exit leads to Level 1 whenever you're ready.";
            if (note == 6)
                return multiplayer
                    ? LocalOwner == InputOwner.P1
                        ? "JUMP passes to your partner at this portal, joining MOVE and DASH.\nThe last steps give them room to practice the full set."
                        : "JUMP passes to you at this portal, joining MOVE and DASH.\nThe last steps give you room to practice the full set."
                    : "The JUMP portal passes jumping to P2, joining MOVE and DASH.\nThe last steps give you room to practice with that control set.";
            if (note == 5)
                return multiplayer
                    ? LocalOwner == InputOwner.P1
                        ? "The airborne DASH portal passes dash to your partner; you keep JUMP.\nJump first, then watch the HUD handoff. Swaps stay applied after a miss."
                        : "The airborne DASH portal gives you dash; your partner keeps JUMP.\nSave dash for the HUD handoff. Swaps stay applied after a miss."
                    : "The airborne DASH portal passes dash to P2; P1 keeps JUMP.\nJump first, then use P2's dash after the HUD handoff. Misses are safe.";
            if (note == 4)
                return "An air dash stops your rise, so gaining jump height first helps cross gaps.\nThe lower floor and left steps offer a safe retry.";
            if (note == (multiplayer ? 2 : 1))
                return "Holding jump gives more height; a quick tap makes a shorter hop.\nThe low blocks give you room to try both.";
            if (note == (multiplayer ? 3 : 2))
                return "Dash gives a short horizontal burst, on the ground or in the air.\nDash recharges on landing. This runway is a safe place to try it.";
            if (!multiplayer)
                return "MOVE portals pass movement to the other control set.\nJUMP and DASH keep their owners; the bindings below follow the handoff.";
            return LocalOwner == InputOwner.P1
                ? "The MOVE portal hands movement to your partner.\nYou keep jumping and dashing; the HUD tracks the handoff."
                : "The MOVE portal hands movement to you.\nYour partner keeps jumping and dashing; the HUD tracks the handoff.";
        }

        InputOwner LocalOwner => NetworkManager.Instance.Role == GameRole.Client ? InputOwner.P2 : InputOwner.P1;

        string Owner(InputOwner owner)
        {
            if (!multiplayer) return owner == InputOwner.P1 ? "P1" : "P2";
            return owner == LocalOwner ? "You" : PlayerIdentity.NameOf(owner);
        }

        string Hint(string actionName, InputOwner owner)
        {
            string label = Owner(owner) + ": " + actionName;
            if (multiplayer)
            {
                if (owner != LocalOwner) return label;
            }
            var map = InputSystem.actions?.FindActionMap(multiplayer ? "NetPlayer" : owner == InputOwner.P1 ? "Player1" : "Player2");
            var action = map?.FindAction(actionName);
            if (action == null) return label;
            bool usingGamepad = Gamepad.current != null && (Keyboard.current == null
                || Gamepad.current.lastUpdateTime > Keyboard.current.lastUpdateTime);
            var names = new List<string>();
            foreach (var binding in action.bindings)
            {
                if (binding.isComposite || string.IsNullOrEmpty(binding.effectivePath)) continue;
                if (binding.isPartOfComposite && binding.name != "left" && binding.name != "right") continue;
                bool pad = binding.effectivePath.StartsWith("<Gamepad>");
                if (pad != usingGamepad) continue;
                string name = InputControlPath.ToHumanReadableString(binding.effectivePath,
                    InputControlPath.HumanReadableStringOptions.OmitDevice);
                if (!names.Contains(name)) names.Add(name);
            }
            return label + " [" + string.Join(" / ", names) + "]";
        }
    }
}
