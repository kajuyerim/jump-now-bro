using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using TMPro;
using JumpNowBro.Gameplay;

namespace JumpNowBro.Networking
{
    /// Code-built UGUI main menu (the #99 UI). Shows on game-open: level select (1/2/3) + Solo / Host / Join+IP /
    /// Quit, plus a small in-session Leave bar and the connection-lost overlay (which replaces the old ConnectionUI
    /// IMGUI box). Level select sets LevelManager.PendingStartIndex, consumed by NetworkManager's Solo/Host start;
    /// the client follows the host via WELCOME, so there's no protocol change.
    ///
    /// Built in code (TMP for crisp text at any scale + layout groups) so it needs no scene authoring: it attaches
    /// to the Manager and raises its own Canvas. Shown while idle, hidden in-session, reshown after Leave/win.
    [RequireComponent(typeof(NetworkManager))]
    public sealed class MainMenuUI : MonoBehaviour
    {
        NetworkManager net;
        GameObject menu, leaveBar, lostOverlay;
        TMP_InputField ipField, lobbyField, nameField;
        TMP_Text lostTitle, lostMsg, lostWaitLabel, pingLabel, statusBanner;
        Button lostRejoinBtn;
        float nextPingRefresh;
        readonly Button[] levelButtons = new Button[3];
        DiscoveryService browse;                                           // passive LAN listener while the menu is up
        GameObject hostList;
        float nextHostRefresh;
        readonly HashSet<string> shownHosts = new HashSet<string>();

        void Awake()
        {
            net = GetComponent<NetworkManager>();
            FindAnyObjectByType<LevelManager>()?.SuppressAutoStart();           // UI is the entry point
            Build();
        }

        void Update()
        {
            var s = net.CurrentSessionState;
            bool idle = !net.SoloActive && (s == null || s == Session.SessionState.Disconnected) && net.Role == GameRole.SinglePlayer;
            if (menu.activeSelf != idle) menu.SetActive(idle);
            bool inGame = !idle && !net.ConnectionLost;                          // a loss is owned by the connection-lost overlay below
            if (leaveBar.activeSelf != inGame) leaveBar.SetActive(inGame);

            bool showPing = inGame && net.Role != GameRole.SinglePlayer;         // RTT only exists with a peer, not in Solo
            if (pingLabel.gameObject.activeSelf != showPing) pingLabel.gameObject.SetActive(showPing);
            if (showPing && Time.time >= nextPingRefresh)
            {
                nextPingRefresh = Time.time + 0.25f;
                int ms = Mathf.RoundToInt(net.CurrentRtt * 1000f);
                pingLabel.text = $"Ping {ms} ms";
                pingLabel.color = ms < 60 ? new Color(0.45f, 0.90f, 0.45f)
                                : ms < 120 ? new Color(1f, 0.82f, 0.35f)
                                :            new Color(1f, 0.5f, 0.4f);
            }

            // Pre-game status while no level is up: the client is dialing the host (it re-probes for ~15 s, #120),
            // or the host is listening for a peer. Keeps the blank pre-session screen from reading as a hang.
            bool connecting = !net.ConnectionLost && net.Role == GameRole.Client && s == Session.SessionState.Connecting;
            bool waiting    = !net.ConnectionLost && net.Role == GameRole.Hosting && s != Session.SessionState.Established;
            bool showStatus = connecting || waiting;
            if (statusBanner.gameObject.activeSelf != showStatus) statusBanner.gameObject.SetActive(showStatus);
            if (showStatus) statusBanner.text = connecting ? "Connecting to host..." : "Waiting for a player to join...";

            bool lost = net.ConnectionLost;
            if (lostOverlay.activeSelf != lost)
            {
                if (lost) RefreshLostOverlay();                                  // role + reason vary per drop
                lostOverlay.SetActive(lost);
            }

            if (idle)                                                            // browse the LAN for hosts while the menu is shown
            {
                if (browse == null)
                    try { browse = DiscoveryService.StartClient(net.DiscoveryPort); } catch { browse = null; }
                browse?.Tick(Time.timeAsDouble);
                if (Time.time >= nextHostRefresh) { nextHostRefresh = Time.time + 0.5f; RefreshHosts(); }
            }
            else DisposeBrowse();                                                // free the discovery port before a session binds it
        }

        void SelectLevel(int i)
        {
            if (LevelManager.Instance != null) LevelManager.Instance.PendingStartIndex = i;
            for (int k = 0; k < levelButtons.Length; k++)
                levelButtons[k].GetComponent<Image>().color =
                    k == i ? new Color(0.30f, 0.55f, 0.95f, 1f) : new Color(1f, 1f, 1f, 0.15f);
        }

        void Build()
        {
            var canvasGo = new GameObject("MainMenuCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;                                          // above the level/HUD canvases
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);

            menu = Panel(canvasGo.transform, new Color(0.08f, 0.10f, 0.16f, 0.97f));
            Stretch(menu);

            var col = new GameObject("Column", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            col.transform.SetParent(menu.transform, false);
            var vlg = col.GetComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.MiddleCenter;
            vlg.spacing = 12;
            vlg.childControlWidth = vlg.childControlHeight = true;
            vlg.childForceExpandWidth = vlg.childForceExpandHeight = false;
            var fit = col.GetComponent<ContentSizeFitter>();
            fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var crt = col.GetComponent<RectTransform>();
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(0.5f, 0.5f);
            crt.anchoredPosition = Vector2.zero;

            Label(col.transform, "Jump Now Bro!", 52, FontStyles.Bold);
            Label(col.transform, "Pick a level, then choose how to play", 20, FontStyles.Normal);

            var lvlRow = Row(col.transform, 12);
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                levelButtons[i] = MakeButton(lvlRow.transform, "Level " + (i + 1), 150, 54, () => SelectLevel(idx));
            }

            MakeButton(col.transform, "Solo (single-player)", 330, 54, () => { DisposeBrowse(); net.BeginSoloFromUi(); });

            Label(col.transform, "Your name (shown to your partner):", 16, FontStyles.Italic);
            nameField = MakeInput(col.transform, "NameField", DefaultPlayerName(), 330, 50);
            nameField.characterLimit = 16;

            var hostRow = Row(col.transform, 8);
            lobbyField = MakeInput(hostRow.transform, "LobbyField", DefaultLobbyName(), 210, 54);
            lobbyField.characterLimit = 24;
            MakeButton(hostRow.transform, "Host", 112, 54, () => { DisposeBrowse(); net.BeginHostingFromUi(lobbyField.text, nameField.text); });

            var joinRow = Row(col.transform, 8);
            ipField = MakeInput(joinRow.transform, "IPField", "127.0.0.1", 210, 54);
            MakeButton(joinRow.transform, "Join", 112, 54, () => { DisposeBrowse(); net.BeginClientFromUi(ipField.text, nameField.text); });

            Label(col.transform, "LAN games (auto-discovered):", 16, FontStyles.Italic);
            hostList = new GameObject("HostList", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            hostList.transform.SetParent(col.transform, false);
            var hlv = hostList.GetComponent<VerticalLayoutGroup>();
            hlv.childAlignment = TextAnchor.UpperCenter;
            hlv.spacing = 4;
            hlv.childControlWidth = hlv.childControlHeight = true;
            hlv.childForceExpandWidth = hlv.childForceExpandHeight = false;
            var hlf = hostList.GetComponent<ContentSizeFitter>();
            hlf.horizontalFit = hlf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            MakeButton(col.transform, "Settings", 330, 46, () => SettingsPanel.Instance?.Open());
            MakeButton(col.transform, "Quit", 330, 46, () => Application.Quit());

#if UNITY_EDITOR
            var simBtn = MakeButton(col.transform, "Sim: " + NetworkManager.EditorSimProfile, 330, 38, null);
            var simTxt = simBtn.GetComponentInChildren<TMP_Text>();
            simBtn.onClick.AddListener(() =>
            {
                NetworkManager.EditorSimProfile = (NetworkManager.LagProfile)(((int)NetworkManager.EditorSimProfile + 1) % 3);
                simTxt.text = "Sim: " + NetworkManager.EditorSimProfile;
            });
#endif
            SelectLevel(0);

            // In-session Leave bar (top-left), shown only while a session/solo is active.
            leaveBar = new GameObject("LeaveBar", typeof(RectTransform));
            leaveBar.transform.SetParent(canvasGo.transform, false);
            var lrt = leaveBar.GetComponent<RectTransform>();
            lrt.anchorMin = lrt.anchorMax = new Vector2(0f, 1f);
            lrt.pivot = new Vector2(0f, 1f);
            lrt.anchoredPosition = new Vector2(12f, -12f);
            lrt.sizeDelta = new Vector2(120f, 44f);
            var leaveBtn = MakeButton(leaveBar.transform, "Leave", 120, 44, () => net.EndSessionFromUi());
            Stretch(leaveBtn.gameObject);
            leaveBar.SetActive(false);

            // In-session RTT readout, top-left (networked sessions only; Solo has no peer). Colour-coded by latency.
            pingLabel = Label(canvasGo.transform, "", 18, FontStyles.Bold);
            var prt = pingLabel.rectTransform;
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0f, 1f);
            prt.anchoredPosition = new Vector2(18f, -98f);
            prt.sizeDelta = new Vector2(170f, 26f);
            pingLabel.alignment = TextAlignmentOptions.Left;
            pingLabel.gameObject.SetActive(false);

            // Pre-game status banner, centered, over the empty pre-session screen (host listening / client dialing).
            statusBanner = Label(canvasGo.transform, "", 30, FontStyles.Bold);
            var srt = statusBanner.rectTransform;
            srt.anchorMin = srt.anchorMax = srt.pivot = new Vector2(0.5f, 0.5f);
            srt.anchoredPosition = new Vector2(0f, -40f);
            srt.sizeDelta = new Vector2(640f, 44f);
            statusBanner.color = new Color(1f, 1f, 1f, 0.85f);
            statusBanner.gameObject.SetActive(false);

            BuildLostOverlay(canvasGo.transform);
        }

        // Connection-lost overlay (replaces ConnectionUI's IMGUI box): a full-screen dim behind a centered
        // card, shown while net.ConnectionLost. The client gets Rejoin + Return to menu; the host gets a
        // "waiting for rejoin" line + Return to menu. RefreshLostOverlay sets role/reason text each time.
        void BuildLostOverlay(Transform canvas)
        {
            lostOverlay = Panel(canvas, new Color(0f, 0f, 0f, 0.6f));
            Stretch(lostOverlay);

            var card = new GameObject("Card", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            card.transform.SetParent(lostOverlay.transform, false);
            card.GetComponent<Image>().color = new Color(0.10f, 0.12f, 0.18f, 0.98f);
            var vl = card.GetComponent<VerticalLayoutGroup>();
            vl.childAlignment = TextAnchor.MiddleCenter;
            vl.spacing = 10;
            vl.padding = new RectOffset(30, 30, 24, 24);
            vl.childControlWidth = vl.childControlHeight = true;
            vl.childForceExpandWidth = vl.childForceExpandHeight = false;
            var fit = card.GetComponent<ContentSizeFitter>();
            fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var rt = card.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;

            lostTitle     = Label(card.transform, "Connection lost", 30, FontStyles.Bold);
            lostMsg       = Label(card.transform, "", 19, FontStyles.Normal);
            lostWaitLabel = Label(card.transform, "Waiting for the other player to rejoin...", 17, FontStyles.Italic);
            lostRejoinBtn = MakeButton(card.transform, "Rejoin", 320, 50, () => net.RejoinFromUi());
            MakeButton(card.transform, "Return to menu", 320, 50, () => net.EndSessionFromUi());
            lostOverlay.SetActive(false);
        }

        void RefreshLostOverlay()
        {
            bool host = net.Role == GameRole.Hosting;
            bool neverConnected = net.LostReason == Session.DisconnectReason.HandshakeFailed;   // client gave up dialing (#120)
            lostTitle.text = host ? "Partner disconnected"
                                  : (neverConnected ? "Couldn't reach host" : "Connection lost");
            lostMsg.text   = ReasonText(net.LostReason);
            lostWaitLabel.gameObject.SetActive(host);                            // host waits for the client to rejoin
            lostRejoinBtn.gameObject.SetActive(!host);                           // only the client initiates a rejoin
            if (!host) lostRejoinBtn.GetComponentInChildren<TMP_Text>().text = neverConnected ? "Retry" : "Rejoin";
        }

        static string ReasonText(Session.DisconnectReason r)
        {
            switch (r)
            {
                case Session.DisconnectReason.PeerLeft:        return "The other player left.";
                case Session.DisconnectReason.ConnectionLost:  return "The connection timed out.";
                case Session.DisconnectReason.HandshakeFailed: return "Could not reach the host.";
                default:                                       return "";
            }
        }

        // Rebuild the host buttons only when the discovered set changes (avoids per-frame UI churn).
        void RefreshHosts()
        {
            if (hostList == null) return;
            var hosts = browse?.Hosts;
            var current = new HashSet<string>();
            if (hosts != null) foreach (var h in hosts.Hosts) current.Add(h.Endpoint.Address + "|" + h.Name);
            if (current.SetEquals(shownHosts)) return;
            shownHosts.Clear();
            foreach (var c in current) shownHosts.Add(c);
            foreach (Transform child in hostList.transform) Destroy(child.gameObject);
            if (hosts == null) return;
            foreach (var h in hosts.Hosts)
            {
                string ip = h.Endpoint.Address.ToString();
                string label = string.IsNullOrEmpty(h.Name) ? ip : $"{h.Name}  {ip}";
                var btn = MakeButton(hostList.transform, label, 330, 38, () => { DisposeBrowse(); net.BeginClientFromUi(ip, nameField.text); });
                var lt = btn.GetComponentInChildren<TMP_Text>();   // a long lobby name shrinks instead of overflowing the button
                lt.enableAutoSizing = true; lt.fontSizeMin = 12; lt.fontSizeMax = 20;
            }
        }

        void DisposeBrowse()
        {
            browse?.Dispose();
            browse = null;
            shownHosts.Clear();
            if (hostList != null) foreach (Transform child in hostList.transform) Destroy(child.gameObject);
        }

        void OnDisable() => DisposeBrowse();

        // ---- tiny UGUI builders ----

        GameObject Panel(Transform parent, Color color)
        {
            var go = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            return go;
        }

        static void Stretch(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        GameObject Row(Transform parent, float spacing)
        {
            var go = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            go.transform.SetParent(parent, false);
            var h = go.GetComponent<HorizontalLayoutGroup>();
            h.childAlignment = TextAnchor.MiddleCenter;
            h.spacing = spacing;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = h.childForceExpandHeight = false;
            return go;
        }

        TMP_Text Label(Transform parent, string text, float size, FontStyles style)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = Color.white;
            t.alignment = TextAlignmentOptions.Center;
            t.raycastTarget = false;                                            // clicks fall through to the button/input graphic
            return t;
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
            var txt = Label(go.transform, label, 22, FontStyles.Normal);
            Stretch(txt.gameObject);
            if (onClick != null) btn.onClick.AddListener(onClick);
            return btn;
        }

        // The host's discovery-beacon display name defaults to this machine's name, so two LAN hosts don't both
        // show the generic title; the user can overwrite it before hosting. Truncated to the input's char cap.
        static string DefaultLobbyName()
        {
            var n = SystemInfo.deviceName;
            if (string.IsNullOrWhiteSpace(n) || n == SystemInfo.unsupportedIdentifier) return "Jump Now Bro!";
            return n.Length > 24 ? n.Substring(0, 24) : n;
        }

        // Pre-fill the player-name field with the machine name (capped to the field's 16-char limit), so a
        // session has a sensible display name even if the player never edits it. Falls back to "Player".
        static string DefaultPlayerName()
        {
            var n = SystemInfo.deviceName;
            if (string.IsNullOrWhiteSpace(n) || n == SystemInfo.unsupportedIdentifier) return "Player";
            return n.Length > 16 ? n.Substring(0, 16) : n;
        }

        TMP_InputField MakeInput(Transform parent, string name, string value, float w, float h)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.SetActive(false);                                               // configure before TMP_InputField wakes
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.18f);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = w;
            le.preferredHeight = le.minHeight = h;

            var viewport = new GameObject("TextArea", typeof(RectTransform), typeof(RectMask2D));
            viewport.transform.SetParent(go.transform, false);
            var vrt = viewport.GetComponent<RectTransform>();
            vrt.anchorMin = Vector2.zero;
            vrt.anchorMax = Vector2.one;
            vrt.offsetMin = new Vector2(10f, 4f);
            vrt.offsetMax = new Vector2(-10f, -4f);

            var tgo = new GameObject("Text", typeof(RectTransform));
            tgo.transform.SetParent(viewport.transform, false);
            var t = tgo.AddComponent<TextMeshProUGUI>();
            Stretch(tgo);
            t.fontSize = 20;
            t.color = Color.white;
            t.alignment = TextAlignmentOptions.Left;
            t.raycastTarget = false;

            var input = go.AddComponent<TMP_InputField>();
            input.textViewport = vrt;
            input.textComponent = t;
            input.targetGraphic = img;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.text = value;
            go.SetActive(true);
            return input;
        }
    }
}
