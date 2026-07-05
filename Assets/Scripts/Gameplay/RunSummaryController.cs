using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;
using JumpNowBro.Util;

namespace JumpNowBro.Gameplay
{
    /// End-of-level co-op summary (#130) + record reporting (#129). Owns the LevelRunTracker (the stat
    /// source on host/solo), the hold state (LevelManager.SummaryHold: the sim freezes at goal touch and
    /// stays frozen until Continue), the run totals for the CompleteScreen, and the code-built card.
    ///
    /// The client never tracks: it renders the canonical host stats from the RunSummary EVENT
    /// (NetworkManager calls ShowRemote at receipt and HideAndRelease when the LevelLoad lands), and
    /// books them into its own local LAN records table.
    ///
    /// Canvas sorting 90: above the HUD overlay (50), deliberately BELOW the menu canvas (100) that hosts
    /// the connection-lost overlay and Leave bar — a mid-hold disconnect must cover and raycast-block
    /// this card, with the hold surviving underneath for a rejoin-resume.
    public sealed class RunSummaryController : MonoBehaviour
    {
        public static RunSummaryController Instance { get; private set; }

        /// Solo-vs-LAN seam for the records table (#129): Gameplay can't see NetworkManager.Role, and
        /// Authority.IsHost is true for BOTH solo and hosting, so it can't split the tables. NetworkManager
        /// registers "am I hosting a LAN session"; unregistered (pure solo boot) means Solo.
        public static Func<bool> LanSessionProvider;

        public struct RunTotals { public int levels, timeMs, deaths, swaps, bestStreakMs; }

        /// Host/solo goal accounting just completed — NetworkManager forwards it as the RunSummary EVENT.
        /// Raised at GOAL TOUCH (not at Continue's LoadNext) so the client's card shows through the hold.
        public event Action<int, LevelRunStats> OnRunCompleted;

        readonly LevelRunTracker tracker = new LevelRunTracker();
        RunTotals totals;
        // Totals/records idempotence: the host re-sends the latched RunSummary to a mid-hold rejoiner, so
        // the SAME level must never book twice. Level indices are monotonic within a session; every replay
        // path (EndSessionFromUi, ReturnHostToLobby) goes through ResetAll, which clears the latch.
        int lastAccountedLevel = -1;
        GameRecords.RunReport lastReport;
        (int level, LevelRunStats stats) currentHold;
        bool holdActive;

        // Card UI
        GameObject root;
        TMP_Text titleLabel, firstClearLabel, timeLabel, timeTagLabel;
        TMP_Text deathsValue, deathsTagLabel, swapsValue, streakValue, waitingLabel;
        Image p1Swatch, p2Swatch;
        TMP_Text p1Name, p2Name;
        Button continueBtn;

        static readonly Color Amber = new Color(1f, 0.82f, 0.35f, 1f);
        static readonly Color Dim = new Color(1f, 1f, 1f, 0.55f);

        public bool HoldActive => holdActive;
        public RunTotals Totals => totals;
        /// The latched (level, stats) while holding — the host's Established re-send reads this.
        public (int level, LevelRunStats stats) CurrentHold => currentHold;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void AutoSpawn()
        {
            if (Instance == null)
                new GameObject(nameof(RunSummaryController)).AddComponent<RunSummaryController>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 90;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();

            Build();
            root.SetActive(false);
        }

        void Start()
        {
            // Bootstrap singletons don't exist yet at the BeforeSceneLoad self-spawn — subscribe in Start
            // (SwapScheduleDriver precedent).
            if (LevelManager.Instance != null) LevelManager.Instance.OnLevelLoaded += HandleLevelLoaded;
            if (DeathNotifier.Instance != null) DeathNotifier.Instance.OnDeath += HandleDeath;
        }

        void OnDestroy()
        {
            if (LevelManager.Instance != null) LevelManager.Instance.OnLevelLoaded -= HandleLevelLoaded;
            if (DeathNotifier.Instance != null) DeathNotifier.Instance.OnDeath -= HandleDeath;
            if (Instance == this) Instance = null;
        }

        // ---- stat feeds (host/solo; the client's tracker Begins but is display-unused and never fed) ----

        void HandleLevelLoaded(int _) => tracker.Begin();

        void HandleDeath(int _)
        {
            if (!Authority.IsHost) return;                 // the client's STATE mirror raises this too
            tracker.NotifyDeath();
        }

        /// Called by SwapScheduleDriver at the shared due-apply moment — NOT ControlMapStore.OnChanged,
        /// which also fires on level-load resets and death checkpoint restores and would over-count.
        public void NotifySwapApplied()
        {
            if (!Authority.IsHost) return;
            tracker.NotifySwapApplied();
        }

        void FixedUpdate()
        {
            // Exactly the sim-gate predicate: the timer freezes during loads, the LEVEL_READY barrier,
            // the connection-loss pause, and the summary hold itself.
            var lm = LevelManager.Instance;
            if (Authority.IsHost && tracker.Running && lm != null && !lm.SimGated)
                tracker.Tick(Time.fixedDeltaTime);
        }

        void Update()
        {
            if (!holdActive || root == null || !root.activeSelf) return;
            // Never fight the connection-lost overlay (SimPaused proxies it here) or the settings panel
            // for selection — Enter/submit on a still-selected Continue would advance behind them.
            bool overlayUp = LevelManager.Instance != null && LevelManager.Instance.SimPaused;
            bool settingsUp = SettingsPanel.Instance != null && SettingsPanel.Instance.IsOpen;
            if (!overlayUp && !settingsUp && continueBtn != null && continueBtn.gameObject.activeSelf)
                UiKit.EnsureSelection(continueBtn.gameObject);
        }

        // ---- hold lifecycle ----

        /// Host/solo, from LevelGoal at goal touch. Falls back to the old instant advance when no tracked
        /// run is live (scenes loaded outside LevelManager, e.g. stripped test fixtures): no card, no record.
        public void NotifyGoalReached()
        {
            var lm = LevelManager.Instance;
            if (lm == null) return;
            int level = lm.CurrentLevelIndex;              // still the completed level: LoadNext runs at Continue
            if (!tracker.Running || level < 0) { lm.LoadNext(); return; }

            var stats = tracker.Complete();
            var mode = LanSessionProvider != null && LanSessionProvider() ? GameRecords.Mode.Lan : GameRecords.Mode.Solo;
            var report = Account(level, stats, mode);
            currentHold = (level, stats);
            holdActive = true;
            lm.SummaryHold = true;
            ShowCard(level, stats, report, canContinue: true);
            OnRunCompleted?.Invoke(level, stats);
        }

        /// Client, from NetworkManager on RunSummary receipt: enter the hold with the host's canonical stats.
        public void ShowRemote(int level, LevelRunStats stats)
        {
            var lm = LevelManager.Instance;
            if (lm == null) return;
            var report = Account(level, stats, GameRecords.Mode.Lan);
            currentHold = (level, stats);
            holdActive = true;
            lm.SummaryHold = true;
            ShowCard(level, stats, report, canContinue: false);
        }

        /// Host/solo Continue. The clear + LoadNext run in ONE synchronous stack: OnBeforeLevelLoad's
        /// scheduler reset and IsLoading both land before any FixedUpdate can run with the gate open.
        public void ContinueFromCard()
        {
            if (!holdActive) return;
            if (continueBtn == null || !continueBtn.gameObject.activeSelf) return;   // client card is not continuable
            var lm = LevelManager.Instance;
            if (lm == null) return;
            if (lm.SimPaused) return;                      // the connection-lost overlay owns the screen
            if (SettingsPanel.Instance != null && SettingsPanel.Instance.IsOpen) return;
            holdActive = false;
            lm.SummaryHold = false;
            root.SetActive(false);
            lm.LoadNext();
        }

        /// Clear the hold + card but KEEP totals and the accounting latch. Client LevelLoad receipt (before
        /// LoadByIndex: the card must not cover the CompleteScreen on the 0xFE victory sentinel) and
        /// RejoinFromUi (a rejoiner resumes the same run; the host's re-sent summary re-shows the card
        /// without double-booking) both land here.
        public void HideAndRelease()
        {
            holdActive = false;
            currentHold = default;
            if (LevelManager.Instance != null) LevelManager.Instance.SummaryHold = false;
            if (root != null) root.SetActive(false);
        }

        /// Full teardown for session end (EndSessionFromUi, ReturnHostToLobby): also stops the tracker —
        /// post-teardown the sim gate is open and an un-Reset tracker would keep ticking menu time.
        public void ResetAll()
        {
            HideAndRelease();
            tracker.Reset();
            totals = default;
            lastAccountedLevel = -1;
            lastReport = default;
        }

        GameRecords.RunReport Account(int level, in LevelRunStats stats, GameRecords.Mode mode)
        {
            if (level == lastAccountedLevel) return lastReport;   // re-shown (rejoin re-send): card only
            lastAccountedLevel = level;
            totals.levels++;
            totals.timeMs += stats.timeMs;
            totals.deaths += stats.deaths;
            totals.swaps += stats.swaps;
            if (stats.streakMs > totals.bestStreakMs) totals.bestStreakMs = stats.streakMs;
            GameRecords.AddLifetime(stats);                       // #149: latch-guarded, so once per level
            lastReport = GameRecords.ReportRun(mode, level, stats);
            return lastReport;
        }

        // ---- card ----

        void Build()
        {
            root = UiKit.Panel(transform, new Color(0f, 0f, 0f, 0.75f));
            UiKit.Stretch(root);

            var card = new GameObject("Card", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            card.transform.SetParent(root.transform, false);
            card.GetComponent<Image>().color = new Color(0.10f, 0.12f, 0.18f, 0.98f);
            var vl = card.GetComponent<VerticalLayoutGroup>();
            vl.childAlignment = TextAnchor.UpperCenter;
            vl.spacing = 8;
            vl.padding = new RectOffset(36, 36, 24, 24);
            vl.childControlWidth = vl.childControlHeight = true;
            vl.childForceExpandWidth = vl.childForceExpandHeight = false;
            var fit = card.GetComponent<ContentSizeFitter>();
            fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var rt = card.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;

            titleLabel = UiKit.Label(card.transform, "", 34, FontStyles.Bold);
            firstClearLabel = UiKit.Label(card.transform, "First clear!", 18, FontStyles.Bold);
            firstClearLabel.color = Amber;

            var names = UiKit.Row(card.transform, 10f);
            p1Swatch = UiKit.Swatch(names.transform, 18f);
            p1Name = UiKit.Label(names.transform, "", 20, FontStyles.Bold);
            var plus = UiKit.Label(names.transform, "+", 20, FontStyles.Normal);
            plus.color = Dim;
            p2Swatch = UiKit.Swatch(names.transform, 18f);
            p2Name = UiKit.Label(names.transform, "", 20, FontStyles.Bold);

            timeLabel = UiKit.Label(card.transform, "", 44, FontStyles.Bold);
            timeTagLabel = UiKit.Label(card.transform, "", 16, FontStyles.Bold);

            deathsValue = StatRow(card.transform, "Deaths", out deathsTagLabel);
            swapsValue = StatRow(card.transform, "Swaps survived", out _);
            streakValue = StatRow(card.transform, "Longest deathless streak", out _);

            continueBtn = UiKit.MakeButton(card.transform, "Continue", 320, 50, ContinueFromCard);
            waitingLabel = UiKit.Label(card.transform, "Waiting for host...", 17, FontStyles.Italic);
            waitingLabel.color = new Color(1f, 1f, 1f, 0.7f);
        }

        static TMP_Text StatRow(Transform parent, string caption, out TMP_Text tag)
        {
            var row = UiKit.Row(parent, 18f);
            var cap = UiKit.Label(row.transform, caption, 18, FontStyles.Normal);
            cap.color = new Color(1f, 1f, 1f, 0.75f);
            var value = UiKit.Label(row.transform, "", 18, FontStyles.Bold);
            tag = UiKit.Label(row.transform, "", 15, FontStyles.Bold);
            return value;
        }

        void ShowCard(int level, in LevelRunStats stats, in GameRecords.RunReport report, bool canContinue)
        {
            titleLabel.text = $"LEVEL {level + 1} COMPLETE";
            firstClearLabel.gameObject.SetActive(report.firstCompletion);

            p1Swatch.color = PlayerIdentity.ColorOf(InputOwner.P1);
            p1Name.text = PlayerIdentity.NameOf(InputOwner.P1);
            p1Name.color = PlayerIdentity.ColorOf(InputOwner.P1);
            p2Swatch.color = PlayerIdentity.ColorOf(InputOwner.P2);
            p2Name.text = PlayerIdentity.NameOf(InputOwner.P2);
            p2Name.color = PlayerIdentity.ColorOf(InputOwner.P2);

            timeLabel.text = TimeFormat.MinutesSecondsCentis(stats.timeMs);
            // Time tag. First clear: the amber banner already says it — a NEW RECORD tag (and a delta with
            // no baseline) would be noise. The PB delta is measured against the best BEFORE this run.
            if (report.firstCompletion) timeTagLabel.text = "";
            else if (report.newBestTime)
            {
                timeTagLabel.text = report.prevBestTimeMs >= 0
                    ? $"NEW RECORD!  {TimeFormat.SignedDeltaCentis(stats.timeMs - report.prevBestTimeMs)}"
                    : "NEW RECORD!";
                timeTagLabel.color = Amber;
            }
            else
            {
                // Not first implies the key exists, so prevBestTimeMs >= 0 here.
                timeTagLabel.text = $"Best: {TimeFormat.MinutesSecondsCentis(report.bestTimeMs)}"
                                  + $"  ({TimeFormat.SignedDeltaCentis(stats.timeMs - report.prevBestTimeMs)})";
                timeTagLabel.color = Dim;
            }

            // Deaths tag. FLAWLESS! is about THIS run, so it outranks the firstCompletion blank and
            // replaces a deaths NEW RECORD tag (a zero-death run IS the record; one tag reads better).
            if (stats.deaths == 0) { deathsTagLabel.text = "FLAWLESS!"; deathsTagLabel.color = Amber; }
            else if (report.firstCompletion) deathsTagLabel.text = "";
            else
            {
                deathsTagLabel.text = report.newFewestDeaths ? "NEW RECORD!" : $"Fewest: {report.fewestDeaths}";
                deathsTagLabel.color = report.newFewestDeaths ? Amber : Dim;
            }

            deathsValue.text = stats.deaths.ToString();
            swapsValue.text = stats.swaps.ToString();
            streakValue.text = TimeFormat.MinutesSecondsCentis(stats.streakMs);

            continueBtn.gameObject.SetActive(canContinue);
            waitingLabel.gameObject.SetActive(!canContinue);
            root.SetActive(true);
            if (canContinue && EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(continueBtn.gameObject);
        }
    }
}
