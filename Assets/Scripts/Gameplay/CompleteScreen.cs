using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace JumpNowBro.Gameplay
{
    public class CompleteScreen : MonoBehaviour
    {
        [SerializeField] GameObject panel;
        [SerializeField] TMP_Text summaryLabel;
        [SerializeField] PlayerSpawner playerSpawner;
        [SerializeField] Color overlayColor = new Color(0f, 0f, 0f, 0.9f);

        public static CompleteScreen Instance { get; private set; }

        void Start()
        {
            Instance = this;
            ConfigurePanel();
            if (summaryLabel != null)
            {
                summaryLabel.textWrappingMode = TextWrappingModes.NoWrap;
                summaryLabel.alignment = TextAlignmentOptions.Center;
                if (TMP_Settings.defaultFontAsset != null) summaryLabel.font = TMP_Settings.defaultFontAsset;   // follow TMP default (Inter)
            }
            // Subscribe in Start so LevelManager.Awake has already run; the completion
            // event is many levels away, so there's no race with early loads.
            if (LevelManager.Instance != null)
                LevelManager.Instance.OnAllLevelsComplete += HandleComplete;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (LevelManager.Instance != null)
                LevelManager.Instance.OnAllLevelsComplete -= HandleComplete;
        }

        // Force a full-screen, near-opaque overlay regardless of how the panel was
        // authored, then start hidden.
        void ConfigurePanel()
        {
            if (panel == null) return;
            if (panel.transform is RectTransform rt)
            {
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
            }
            if (panel.TryGetComponent<Image>(out var img)) img.color = overlayColor;
            panel.SetActive(false);
        }

        void HandleComplete()
        {
            // DeathNotifier.Current is the single source of truth — host writes cumulative TotalDeaths,
            // client mirrors via STATE.deathCount. PlayerSpawner.TotalDeaths reads 0 on the client (where
            // PlayerController is destroyed by the role-aware spawner), so we'd have shown the wrong total.
            int deaths = DeathNotifier.Instance != null ? DeathNotifier.Instance.Current
                       : (playerSpawner != null ? playerSpawner.TotalDeaths : 0);
            string text = $"Complete!\nDeaths: {deaths}";
            // #130 run totals. Skipped when nothing was accounted: a client that joined post-victory has no
            // per-level history (same accepted partial-data precedent as its Deaths reading).
            var rsc = RunSummaryController.Instance;
            if (rsc != null && rsc.Totals.levels > 0)
            {
                var t = rsc.Totals;
                text += $"\nTime: {JumpNowBro.Util.TimeFormat.MinutesSecondsCentis(t.timeMs)}"
                      + $"   Swaps survived: {t.swaps}"
                      + $"   Best streak: {JumpNowBro.Util.TimeFormat.MinutesSecondsCentis(t.bestStreakMs)}";
            }
            if (summaryLabel != null)
                summaryLabel.text = text;
            if (panel != null) panel.SetActive(true);
        }

        /// Hide the overlay on session teardown (Leave). Shown by HandleComplete on all-levels-complete and
        /// never otherwise dismissed, so leaving from the completed state would strand it on screen (#112).
        public void HidePanel()
        {
            if (panel != null) panel.SetActive(false);
        }
    }
}
