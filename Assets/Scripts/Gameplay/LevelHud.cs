using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;

namespace JumpNowBro.Gameplay
{
    public class LevelHud : MonoBehaviour
    {
        [SerializeField] TMP_Text levelLabel;
        [SerializeField] TMP_Text deathLabel;
        [SerializeField] PlayerSpawner playerSpawner;

        void Start()
        {
            // Style + position in code so it reads as a clean HUD: deaths top-right, level top-left under the menu bar.
            if (deathLabel != null)
            {
                var rt = deathLabel.rectTransform;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
                rt.anchoredPosition = new Vector2(-18f, -14f);
                rt.sizeDelta = new Vector2(220f, 36f);
                deathLabel.enableAutoSizing = false;
                deathLabel.fontSize = 26;
                deathLabel.fontStyle = FontStyles.Bold;
                deathLabel.color = new Color(1f, 0.82f, 0.82f);
                deathLabel.alignment = TextAlignmentOptions.Right;
                FollowDefaultFont(deathLabel);
            }
            if (levelLabel != null)
            {
                var rt = levelLabel.rectTransform;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
                rt.anchoredPosition = new Vector2(18f, -64f);
                rt.sizeDelta = new Vector2(240f, 30f);
                levelLabel.enableAutoSizing = false;
                levelLabel.fontSize = 22;
                levelLabel.fontStyle = FontStyles.Bold;
                levelLabel.color = new Color(0.78f, 0.85f, 1f);
                levelLabel.alignment = TextAlignmentOptions.Left;
                FollowDefaultFont(levelLabel);
            }
        }

        // The serialized labels carry an explicit font; repoint them at the TMP default (Inter) so the whole UI is one font.
        static void FollowDefaultFont(TMP_Text t)
        {
            if (t != null && TMP_Settings.defaultFontAsset != null) t.font = TMP_Settings.defaultFontAsset;
        }

        void OnEnable()
        {
            if (playerSpawner != null) playerSpawner.OnPlayerSpawned += HandlePlayerSpawned;
            // Single death source on every role — Host/SinglePlayer feed it via PlayerController.OnDeath;
            // Client feeds it via STATE.deathCount delta. HUD doesn't need to know which.
            if (DeathNotifier.Instance != null) DeathNotifier.Instance.OnDeath += HandleDeath;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        void OnDisable()
        {
            if (playerSpawner != null) playerSpawner.OnPlayerSpawned -= HandlePlayerSpawned;
            if (DeathNotifier.Instance != null) DeathNotifier.Instance.OnDeath -= HandleDeath;
            SceneManager.sceneLoaded -= HandleSceneLoaded;
        }

        void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Additive) return;
            var levelManager = LevelManager.Instance;
            bool training = levelManager != null && levelManager.IsTraining(levelManager.CurrentLevelIndex);
            if (levelLabel != null)
                levelLabel.text = training ? levelManager.GetLevelDisplayName(levelManager.CurrentLevelIndex) : Pretty(scene.name);
            if (deathLabel != null) deathLabel.gameObject.SetActive(!training);
        }

        // "Level_01" -> "Level 1" (drop the underscore + leading zero).
        static string Pretty(string sceneName)
        {
            var s = sceneName.Replace("Level_0", "Level ").Replace("Level_", "Level ");
            return s == sceneName ? sceneName : s;
        }

        // DeathNotifier.Current is the single source of truth — on Host it's set by PlayerSpawner.Raise(TotalDeaths),
        // on Client it's set by ClientStateRenderer.Raise(STATE.deathCount). Both carry cumulative count.
        // Spawn-time read picks up whatever's already been pushed (e.g., client mid-game join with prior deaths).
        void HandlePlayerSpawned(GameObject player) =>
            UpdateDeathLabel(DeathNotifier.Instance != null ? DeathNotifier.Instance.Current : 0);

        void HandleDeath(int newCount) => UpdateDeathLabel(newCount);

        void UpdateDeathLabel(int count)
        {
            if (deathLabel != null) deathLabel.text = $"Deaths: {count}";
        }

        /// Blank the labels on session teardown (Leave). The level scene stays loaded until the next session's
        /// load (unloading here raced the next load — see LevelManager.ResetIndex), so clear the text in place.
        public void Clear()
        {
            if (levelLabel != null) levelLabel.text = "";
            if (deathLabel != null) deathLabel.text = "";
        }
    }
}
