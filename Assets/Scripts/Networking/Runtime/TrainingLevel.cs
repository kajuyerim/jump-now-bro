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
        [SerializeField] Transform jumpObstacle;
        [SerializeField] Transform dashLanding;
        [SerializeField] Vector3 soloSwapPosition = new Vector3(28, 2, 0);
        [SerializeField] Vector3 multiplayerSwapPosition = new Vector3(4, 2, 0);

        bool multiplayer;
        GameObject panel;
        TMP_Text heading, instruction, controls;
        float nextRefresh;

        public string Instructions => instruction != null ? instruction.text : "";

        void Awake()
        {
            multiplayer = NetworkManager.Instance != null && NetworkManager.Instance.Role != GameRole.SinglePlayer;
            movePortal.transform.position = multiplayer ? multiplayerSwapPosition : soloSwapPosition;
        }

        void Start()
        {
            var canvas = new GameObject("TrainingGuidance", typeof(Canvas), typeof(CanvasScaler));
            canvas.transform.SetParent(transform, false);
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.GetComponent<Canvas>().sortingOrder = 40;
            var scaler = canvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;
            panel = UiKit.Panel(canvas.transform, new Color(0.06f, 0.09f, 0.15f, 0.94f));
            panel.GetComponent<Image>().raycastTarget = false;
            var rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0, 18);
            rect.sizeDelta = new Vector2(1000, 186);
            heading = TextAt(27, 16, 36, FontStyles.Bold);
            instruction = TextAt(21, 55, 65, FontStyles.Normal);
            controls = TextAt(17, 124, 52, FontStyles.Normal);
            controls.color = new Color(0.78f, 0.87f, 1f);
        }

        TMP_Text TextAt(int size, float top, float height, FontStyles style)
        {
            var text = UiKit.Label(panel.transform, "", size, style);
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
            nextRefresh = Time.unscaledTime + 0.1f;
            var lm = LevelManager.Instance;
            var net = NetworkManager.Instance;
            var player = PlayerSpawner.Instance != null ? PlayerSpawner.Instance.CurrentPlayerInstance : null;
            bool visible = player != null && lm != null && lm.IsTraining(lm.CurrentLevelIndex)
                           && !lm.IsLoading && (net == null || (!net.InLobby && !net.ConnectionLost));
            panel.SetActive(visible);
            if (!visible) return;

            var map = ControlMapStore.Instance != null ? ControlMapStore.Instance.Current : ControlMap.Default;
            var pos = player.transform.position;
            string lesson, message;
            if (pos.y < -0.5f)
            {
                lesson = "TRY AGAIN";
                message = "Safe landing! Take the steps on the left back up.\nThen jump from the edge and dash in the air.";
            }
            else if (pos.x < 2f && map.moveOwner == InputOwner.P1)
            {
                lesson = "MOVE";
                message = multiplayer
                    ? (LocalOwner == InputOwner.P1
                        ? "One character, two players. You start with MOVE, JUMP and DASH.\nHead right; the MOVE portal will give your partner MOVE."
                        : "One character, two players. Your partner starts with MOVE, JUMP and DASH.\nAt the MOVE portal, you will take over movement. Watch the HUD.")
                    : "You control both P1 and P2 on one character. Start by moving right.\nThe HUD shows which control set owns each action.";
            }
            else if (multiplayer && pos.x < jumpObstacle.position.x - 2f)
            {
                lesson = "SWAP";
                message = map.moveOwner == InputOwner.P1
                    ? (LocalOwner == InputOwner.P1
                        ? "Cross the MOVE portal to pass MOVE to your partner.\nYou will keep JUMP and DASH."
                        : "Your partner is approaching the MOVE portal. Get ready to move!\nThey will keep JUMP and DASH after the handoff.")
                    : "MOVE has changed hands. Move: " + Owner(map.moveOwner) + ". Jump: " + Owner(map.jumpOwner) + ".\nWork together to reach the step.";
            }
            else if (pos.x < jumpObstacle.position.x + 2f)
            {
                lesson = "JUMP";
                message = "Jump over the step ahead. Hold jump for more height; release for a shorter hop.\n"
                    + (multiplayer ? "Move: " + Owner(map.moveOwner) + ". Jump: " + Owner(map.jumpOwner) + "." : "Follow the active JUMP binding below.");
            }
            else if (pos.x < dashLanding.position.x + 1f)
            {
                lesson = "DASH";
                message = multiplayer && LocalOwner == map.moveOwner
                    ? "Keep moving right while your partner jumps from the edge and dashes.\nThe floor below is safe if you miss. Take your time together."
                    : "Jump near the edge, then dash right to cross the gap.\nDash recharges when you land. The floor below is safe if you miss.";
            }
            else if (!multiplayer && pos.x < soloSwapPosition.x + 3f)
            {
                lesson = "SWAP";
                message = map.moveOwner == InputOwner.P1
                    ? "The MOVE portal switches MOVE from P1 to P2.\nWatch the HUD and follow the MOVE binding after crossing."
                    : "MOVE now uses P2 controls. JUMP and DASH still use P1 controls.\nUse both control sets together to continue.";
            }
            else
            {
                lesson = "PUT IT TOGETHER";
                message = multiplayer
                    ? "Move: " + Owner(map.moveOwner) + ". Jump: " + Owner(map.jumpOwner) + ". Clear the final step.\nReach the exit portal together to begin Level 1."
                    : "Use P2 to move and P1 to jump over the final step.\nReach the exit portal to begin Level 1.";
            }
            heading.text = (multiplayer ? "CO-OP TRAINING / " : "SOLO TRAINING / ") + lesson;
            instruction.text = message;
            controls.text = Hint("Move", map.moveOwner) + "     |     " + Hint("Jump", map.jumpOwner)
                            + "     |     " + Hint("Dash", map.dashOwner);
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
