using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using JumpNowBro.Gameplay;
using JumpNowBro.Util;

namespace JumpNowBro.Networking
{
    /// Pre-game lobby screen (#144, v2.3). Shown while NetworkManager.InLobby: host from hosting start
    /// (waiting card until a client latches), client once Established pre-game. Both player cards carry
    /// name + colour swatch + a TEXT status (never colour alone); the host picks the level and Starts once
    /// the client readies; the client toggles Ready; both can Leave.
    ///
    /// Code-built on its own self-spawned canvas (zero scene authoring), sortingOrder 110: above the main
    /// menu (100), below the settings panel (200) so Esc-settings opens on top. Built with the shared
    /// UiKit factories. It POLLS NetworkManager.InLobby every frame — visibility rules live in that one
    /// predicate so this screen, the menu, and the lost overlay cannot disagree.
    public sealed class LobbyUI : MonoBehaviour
    {
        public static LobbyUI Instance { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void AutoSpawn()
        {
            if (Instance == null)
                new GameObject(nameof(LobbyUI)).AddComponent<LobbyUI>();
        }

        GameObject root;
        Image hostSwatch, peerSwatch;
        TMP_Text hostNameLabel, peerNameLabel, peerStatusLabel;
        GameObject hostBlock, clientBlock, levelRow;
        Button startBtn, readyBtn, leaveBtn;
        TMP_Text startHint, readyLabel, clientHint, clientLevelLabel;
        Button[] levelButtons;
        bool visible;
        float nextRefresh;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 110;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();

            Build();
            root.SetActive(false);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            PlayerIdentity.OnChanged -= OnIdentityChanged;
        }

        void Update()
        {
            var net = NetworkManager.Instance;
            bool show = net != null && net.InLobby;
            if (show != visible)
            {
                visible = show;
                root.SetActive(show);
                if (show)
                {
                    PlayerIdentity.OnChanged += OnIdentityChanged;
                    EnsureLevelButtons();
                    Refresh(net);
                    SelectFirst(net);
                }
                else
                {
                    PlayerIdentity.OnChanged -= OnIdentityChanged;
                }
            }
            if (!show) return;

            if (Time.time >= nextRefresh)
            {
                nextRefresh = Time.time + 0.25f;
                Refresh(net);
            }
            // Don't steal selection while the settings panel (sortingOrder 200) is open on top of us.
            if (SettingsPanel.Instance == null || !SettingsPanel.Instance.IsOpen)
                UiKit.EnsureSelection(FirstSelectable(net));
        }

        void OnIdentityChanged() { if (visible && NetworkManager.Instance != null) Refresh(NetworkManager.Instance); }

        GameObject FirstSelectable(NetworkManager net) =>
            net.Role == GameRole.Hosting
                ? (levelButtons != null && levelButtons.Length > 0 ? levelButtons[Mathf.Clamp(net.LobbySelectedLevel, 0, levelButtons.Length - 1)].gameObject : leaveBtn.gameObject)
                : readyBtn.gameObject;

        void SelectFirst(NetworkManager net)
        {
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(FirstSelectable(net));
        }

        void Build()
        {
            root = UiKit.Panel(transform, new Color(0.08f, 0.10f, 0.16f, 0.97f));
            UiKit.Stretch(root);

            var col = UiKit.Column(root.transform, 14f);
            // Root column needs its own fitter (bare TMP labels have zero min size; without it the column
            // keeps its default 100px rect, starves the layout, and every hint label collapses invisible).
            // Nested columns must stay fitter-free, so this lives here, not in UiKit.Column.
            var fit = col.AddComponent<ContentSizeFitter>();
            fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var crt = col.GetComponent<RectTransform>();
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(0.5f, 0.5f);
            crt.anchoredPosition = Vector2.zero;

            UiKit.Label(col.transform, "Lobby", 44, FontStyles.Bold);

            // Player cards: host (P1) + partner (P2 or a waiting placeholder).
            var cards = UiKit.Row(col.transform, 16f);
            BuildCard(cards.transform, out hostSwatch, out hostNameLabel, out var hostStatus);
            hostStatus.text = "HOST";
            BuildCard(cards.transform, out peerSwatch, out peerNameLabel, out peerStatusLabel);

            // Host block: level select + Start.
            hostBlock = UiKit.Column(col.transform, 8f);
            levelRow = UiKit.Row(hostBlock.transform, 10f);
            startBtn = UiKit.MakeButton(hostBlock.transform, "Start", 330, 54, () => NetworkManager.Instance?.StartGameFromLobby());
            startHint = UiKit.Label(hostBlock.transform, "", 16, FontStyles.Italic);
            startHint.color = new Color(1f, 1f, 1f, 0.7f);

            // Client block: the host's pick + Ready toggle.
            clientBlock = UiKit.Column(col.transform, 8f);
            clientLevelLabel = UiKit.Label(clientBlock.transform, "", 20, FontStyles.Bold);
            readyBtn = UiKit.MakeButton(clientBlock.transform, "", 330, 54, () =>
            {
                var net = NetworkManager.Instance;
                if (net != null) { net.SetLobbyReady(!net.LocalReady); Refresh(net); }
            });
            readyLabel = readyBtn.GetComponentInChildren<TMP_Text>();
            clientHint = UiKit.Label(clientBlock.transform, "", 16, FontStyles.Italic);
            clientHint.color = new Color(1f, 1f, 1f, 0.7f);

            leaveBtn = UiKit.MakeButton(col.transform, "Leave", 200, 44, () => NetworkManager.Instance?.EndSessionFromUi());
        }

        static void BuildCard(Transform parent, out Image swatch, out TMP_Text nameLabel, out TMP_Text statusLabel)
        {
            var card = new GameObject("PlayerCard", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(LayoutElement));
            card.transform.SetParent(parent, false);
            card.GetComponent<Image>().color = new Color(0.14f, 0.16f, 0.24f, 0.9f);
            var le = card.GetComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = 280f;
            le.preferredHeight = le.minHeight = 120f;
            var v = card.GetComponent<VerticalLayoutGroup>();
            v.childAlignment = TextAnchor.MiddleCenter;
            v.spacing = 6f;
            v.padding = new RectOffset(12, 12, 12, 12);
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = v.childForceExpandHeight = false;

            swatch = UiKit.Swatch(card.transform, 24f);
            nameLabel = UiKit.Label(card.transform, "", 22, FontStyles.Bold);
            statusLabel = UiKit.Label(card.transform, "", 16, FontStyles.Normal);
            statusLabel.color = new Color(1f, 1f, 1f, 0.8f);
        }

        // Level buttons need LevelManager (a Bootstrap scene object), which doesn't exist yet when this
        // self-spawns BeforeSceneLoad — build them on first show instead.
        void EnsureLevelButtons()
        {
            if (levelButtons != null) return;
            int count = LevelManager.Instance != null ? LevelManager.Instance.LevelCount : 0;
            if (count <= 0) { levelButtons = new Button[0]; return; }
            levelButtons = new Button[count];
            for (int i = 0; i < count; i++)
            {
                int idx = i;
                levelButtons[i] = UiKit.MakeButton(levelRow.transform, "Level " + (i + 1), 130, 48, () =>
                {
                    var net = NetworkManager.Instance;
                    if (net != null) { net.SetLobbyLevel(idx); Refresh(net); }
                });
            }
        }

        void Refresh(NetworkManager net)
        {
            bool hosting = net.Role == GameRole.Hosting;

            // Host card: identity is always known locally (menu entry / WELCOME).
            hostSwatch.color = PlayerIdentity.ColorOf(InputOwner.P1);
            hostNameLabel.text = PlayerIdentity.NameOf(InputOwner.P1);
            hostNameLabel.color = PlayerIdentity.ColorOf(InputOwner.P1);

            // Partner card. Guard on PeerConnected before touching PlayerIdentity: a departed client's
            // name lingers there until the next HELLO overwrites it (never show a stale name).
            if (net.PeerConnected)
            {
                var p2 = PlayerIdentity.ColorOf(InputOwner.P2);
                peerSwatch.color = p2;
                peerNameLabel.text = PlayerIdentity.NameOf(InputOwner.P2);
                peerNameLabel.color = p2;
                bool ready = hosting ? net.PeerReady : net.LocalReady;
                peerStatusLabel.text = ready ? "Ready" : "Not ready";
            }
            else
            {
                peerSwatch.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
                peerNameLabel.text = "...";
                peerNameLabel.color = new Color(1f, 1f, 1f, 0.5f);
                peerStatusLabel.text = "Waiting for a player to join...";
            }

            hostBlock.SetActive(hosting);
            clientBlock.SetActive(!hosting);
            if (hosting)
            {
                if (levelButtons != null)
                    for (int i = 0; i < levelButtons.Length; i++)
                        levelButtons[i].GetComponent<Image>().color =
                            i == net.LobbySelectedLevel ? new Color(0.30f, 0.55f, 0.95f, 1f) : new Color(1f, 1f, 1f, 0.15f);
                startBtn.interactable = net.PeerConnected && net.PeerReady;
                startHint.text = !net.PeerConnected ? ""
                               : !net.PeerReady ? $"Waiting for {PlayerIdentity.NameOf(InputOwner.P2)} to ready up..."
                               : "Ready to start!";
            }
            else
            {
                clientLevelLabel.text = "Level " + (net.LobbySelectedLevel + 1);
                readyLabel.text = net.LocalReady ? "Ready: On" : "Ready: Off";
                clientHint.text = net.LocalReady ? "Waiting for the host to start..." : "";
            }
        }
    }
}
