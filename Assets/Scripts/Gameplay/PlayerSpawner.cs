using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using JumpNowBro.Util;

namespace JumpNowBro.Gameplay
{
    public class PlayerSpawner : MonoBehaviour
    {
        public static PlayerSpawner Instance { get; private set; }

        [SerializeField] GameObject playerPrefab;
        [SerializeField] CameraFollow cameraFollow;

        PlayerController currentPlayer;
        GameObject currentPlayerInstance;                                       // GameObject ref survives client's PlayerController destroy
        int accumulatedDeaths;

        public PlayerController CurrentPlayer => currentPlayer;
        /// Returns the spawned Player GameObject (or null if none spawned / destroyed). Use this for
        /// teardown on EndSessionFromUi — PlayerController may have been destroyed by the client wiring.
        public GameObject CurrentPlayerInstance => currentPlayerInstance;
        // Player is re-instantiated per level, so its DeathCount is per-level; this
        // folds finished levels into a running total for the completion summary.
        public int TotalDeaths => accumulatedDeaths + (currentPlayer != null ? currentPlayer.DeathCount : 0);

        /// Zero the folded total between sessions. Without this the accumulator leaked across Leave/re-Host:
        /// DeathNotifier.Reset blanked the HUD, but the FIRST death of the next session raised
        /// Raise(TotalDeaths) with the previous run's deaths still folded in, jumping the counter.
        public void ResetDeathAccumulation() => accumulatedDeaths = 0;
        /// Fires with the spawned Player GameObject — subscribers TryGetComponent for PlayerController
        /// when they need it. The v1.4 client destroys PlayerController, so the previous Action<PlayerController>
        /// signature would have fired with null and NRE'd downstream subscribers.
        public event Action<GameObject> OnPlayerSpawned;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        void OnEnable()
        {
            SceneManager.sceneLoaded += HandleSceneLoaded;
            if (DeathNotifier.Instance != null) DeathNotifier.Instance.OnDeath += HandleDeathFromNotifier;
        }

        void OnDisable()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            if (DeathNotifier.Instance != null) DeathNotifier.Instance.OnDeath -= HandleDeathFromNotifier;
        }

        // Camera shake on every death — host's PlayerController fires it via HandlePlayerDeath -> Raise;
        // client's ClientStateRenderer fires it on STATE.deathCount delta. Same path here either way.
        void HandleDeathFromNotifier(int deathCount)
        {
            if (cameraFollow != null) cameraFollow.Shake();
        }

        void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Only spawn for additive level-scene loads; Bootstrap is the single root scene.
            if (mode != LoadSceneMode.Additive) return;

            var spawnPoint = FindSpawnPointInScene(scene);
            if (spawnPoint == null)
            {
                Debug.LogWarning($"PlayerSpawner: no PlayerSpawnPoint found in scene '{scene.name}'.");
                return;
            }

            // Gate on the GameObject ref, not the PlayerController component — the client wiring destroys
            // the controller, so a `currentPlayer != null` check evaporates to fake-null and the previous
            // Player(Clone) stays alive across level loads (one extra clone per level — the bug seen at #78).
            if (currentPlayerInstance != null)
            {
                if (currentPlayer != null)
                {
                    accumulatedDeaths += currentPlayer.DeathCount;
                    currentPlayer.OnDeath -= HandlePlayerDeath;
                }
                Destroy(currentPlayerInstance);
            }

            var instance = Instantiate(playerPrefab, spawnPoint.transform.position, Quaternion.identity);
            currentPlayerInstance = instance;
            currentPlayer = instance.GetComponent<PlayerController>();
            if (currentPlayer != null)
            {
                currentPlayer.SetCheckpoint(spawnPoint.transform.position, ControlMap.Default);
                currentPlayer.OnDeath += HandlePlayerDeath;
            }

            if (cameraFollow != null)
                cameraFollow.SetTarget(instance.transform);                // works regardless of role: client destroys PlayerController, but the transform stays

            OnPlayerSpawned?.Invoke(instance);
        }

        // Bridge PlayerController.OnDeath -> DeathNotifier so all subscribers (HUD, camera) see deaths
        // through a single source — same path the client takes via STATE-delta in ClientStateRenderer.
        // Raise with cumulative TotalDeaths (not per-level) so HUD survives level transitions and the
        // client receives the same number via STATE.deathCount (which the host's broadcaster reads here).
        void HandlePlayerDeath(int deathCount) =>
            DeathNotifier.Instance?.Raise(TotalDeaths);

        PlayerSpawnPoint FindSpawnPointInScene(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                var sp = root.GetComponentInChildren<PlayerSpawnPoint>(includeInactive: true);
                if (sp != null) return sp;
            }
            return null;
        }
    }
}
