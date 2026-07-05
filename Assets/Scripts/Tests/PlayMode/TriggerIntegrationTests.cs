using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using JumpNowBro.Gameplay;
using JumpNowBro.Util;

namespace JumpNowBro.Tests.PlayMode
{
    /// Integration coverage (closes #113) + cosmetic-pass guard. Boots the real Bootstrap managers + a real level
    /// so PlayerSpawner spawns the actual Player.prefab, then drives that player into each trigger to prove the
    /// swap / checkpoint / hazard / goal effects still fire — and that the player's collider + ground anchor
    /// survive a slime swap. Authority.IsHost is true (NetworkManager registers host for the SinglePlayer
    /// Bootstrap), so the four host-gated trigger volumes run.
    ///
    /// PlayMode-only (real Physics2D + scenes) → out of the no-Unity CI globs. Bootstrap is loaded once for the
    /// fixture; each test additively loads a level, grabs the spawned player and makes it inert (Inject(null,null)
    /// → PlayerController.FixedUpdate early-returns), drives it, and unloads. Park-then-MovePosition is what fires
    /// OnTriggerEnter2D against the static trigger colliders for a kinematic body (UseFullKinematicContacts=1).
    public class TriggerIntegrationTests
    {
        static bool bootstrapLoaded;
        PlayerController player;
        GameObject playerGo;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (!bootstrapLoaded)
            {
                yield return SceneManager.LoadSceneAsync("Bootstrap", LoadSceneMode.Single);
                yield return null;                       // manager Awakes/Starts; NetworkManager registers Authority=host
                bootstrapLoaded = true;
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (sc.IsValid() && sc.name != null && sc.name.StartsWith("Level_"))
                    yield return SceneManager.UnloadSceneAsync(sc);
            }
            if (playerGo != null) Object.Destroy(playerGo);          // clear our inert player so it can't enter the next test's triggers
            player = null;
            playerGo = null;
            yield return null;
        }

        IEnumerator LoadLevelAndGrabPlayer(string level)
        {
            yield return SceneManager.LoadSceneAsync(level, LoadSceneMode.Additive);
            yield return null;                           // let PlayerSpawner.HandleSceneLoaded spawn the player
            player = PlayerSpawner.Instance != null ? PlayerSpawner.Instance.CurrentPlayer : null;
            Assert.IsNotNull(player, $"PlayerSpawner did not spawn a player in {level}.");
            playerGo = player.gameObject;
            player.Inject(null, null);                   // inert: null input sources → FixedUpdate early-returns → body holds position
            playerGo.GetComponent<Rigidbody2D>().useFullKinematicContacts = true;
        }

        IEnumerator DriveInto(Collider2D trigger)
        {
            var rb = playerGo.GetComponent<Rigidbody2D>();
            Vector2 center = trigger.bounds.center;
            // Clear of the trigger above it: trigger half-height + the player's own half-height (1) + margin,
            // so the 2-tall player body starts fully outside and the step-in is a real not→overlapping ENTER.
            float approach = trigger.bounds.extents.y + 1.5f;
            Vector2 from = center + new Vector2(0f, approach);

            // Clear any previous trigger (fires Exit), then teleport to just outside the target.
            rb.position = new Vector2(1000f, 1000f);
            Physics2D.SyncTransforms();
            yield return new WaitForFixedUpdate();
            rb.position = from;
            Physics2D.SyncTransforms();
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();

            // Step in with small MovePositions. A single large MovePosition gets distance-clamped per physics
            // step and never reaches the trigger, so sweep it in over short hops to fire a clean OnTriggerEnter2D.
            const int steps = 8;
            for (int i = 1; i <= steps; i++)
            {
                rb.MovePosition(Vector2.Lerp(from, center, i / (float)steps));
                yield return new WaitForFixedUpdate();
            }
            yield return new WaitForFixedUpdate();
        }

        [UnityTest]
        public IEnumerator PlayerPrefab_KeepsColliderAndGroundAnchor()
        {
            yield return LoadLevelAndGrabPlayer("Level_01");

            var box = playerGo.GetComponent<BoxCollider2D>();
            Assert.IsNotNull(box, "Player lost its BoxCollider2D.");
            Assert.AreEqual(new Vector2(1f, 1f), box.size, "Player body collider is 1x1 (square, matching the slime).");
            Assert.AreEqual(new Vector2(0f, 0f), box.offset, "Player collider is centered on the root (which sits at the slime's centre).");
            Assert.IsTrue(playerGo.CompareTag("Player"), "Player must keep the 'Player' tag (triggers detect it by tag).");

            var groundCheck = playerGo.transform.Find("GroundCheck");
            Assert.IsNotNull(groundCheck, "Player lost its GroundCheck child.");
            Assert.AreEqual(new Vector3(0f, -0.5f, 0f), groundCheck.localPosition, "GroundCheck sits at the feet (collider bottom, y=-0.5).");
        }

        [UnityTest]
        public IEnumerator Swap_FiresExpectedActionAndId()
        {
            yield return LoadLevelAndGrabPlayer("Level_02");         // Move + Jump + Dash swaps

            // Collect every request: each trigger fires at most once (the `fired` guard), so order and any
            // incidental sweep-through of a neighbour while approaching another don't matter — the set of
            // requests must match the set of triggers exactly.
            var fired = new List<SwapRequest>();
            void Handler(SwapRequest r) => fired.Add(r);
            SwapTrigger.OnSwapRequested += Handler;
            try
            {
                var swaps = Object.FindObjectsByType<SwapTrigger>();
                foreach (var trigger in swaps)
                    yield return DriveInto(trigger.GetComponent<Collider2D>());

                Assert.AreEqual(swaps.Length, fired.Count, "each swap trigger should fire exactly once");
                foreach (var trigger in swaps)
                {
                    var action = (PlayerAction)GetField(trigger, "actionToSwap");
                    byte id = (byte)GetField(trigger, "triggerId");
                    Assert.IsTrue(fired.Any(r => r.action == action && r.triggerId == id),
                        $"no swap request matched '{trigger.name}' ({action}, id {id}).");
                }
            }
            finally { SwapTrigger.OnSwapRequested -= Handler; }
        }

        [UnityTest]
        public IEnumerator Checkpoint_SetsRespawnToItsPosition()
        {
            yield return LoadLevelAndGrabPlayer("Level_01");
            var checkpoint = Object.FindAnyObjectByType<Checkpoint>();
            Assert.IsNotNull(checkpoint, "Level_01 has no Checkpoint.");

            yield return DriveInto(checkpoint.GetComponent<Collider2D>());

            Assert.AreEqual((Vector2)checkpoint.transform.position, player.CheckpointPosition,
                "Reaching the checkpoint must set the player's respawn to it.");
        }

        [UnityTest]
        public IEnumerator Hazard_KillsPlayer()
        {
            yield return LoadLevelAndGrabPlayer("Level_02");         // dash-over-spikes
            var hazard = Object.FindAnyObjectByType<Hazard>();
            Assert.IsNotNull(hazard, "Level_02 has no Hazard/Spike.");

            int deaths = 0;
            void OnDeath(int n) => deaths = n;
            player.OnDeath += OnDeath;
            try
            {
                yield return DriveInto(hazard.GetComponent<Collider2D>());
                Assert.GreaterOrEqual(deaths, 1, "Touching a spike must kill the player (OnDeath should fire).");
            }
            finally { player.OnDeath -= OnDeath; }
        }

        [UnityTest]
        public IEnumerator Goal_HoldsAtSummary_ThenContinueAdvances()
        {
            yield return LoadLevelAndGrabPlayer("Level_01");
            var goal = Object.FindAnyObjectByType<LevelGoal>();
            Assert.IsNotNull(goal, "Level_01 has no LevelGoal.");

            // Swap the live level list for an empty one so Continue's LoadNext takes the completion path (fires
            // OnBeforeLevelLoad + OnAllLevelsComplete) instead of actually loading the next scene. Restored after.
            var lm = LevelManager.Instance;
            var savedLevels = GetField(lm, "levelSceneNames");
            SetField(lm, "levelSceneNames", new string[0]);

            // #130: the raw SceneManager loads in this fixture never drive LevelManager, so seed what a real
            // load provides — a live tracker run and a current level index — or the goal takes the no-run
            // fallback (instant advance) and the hold path goes untested.
            var rsc = RunSummaryController.Instance;
            Assert.IsNotNull(rsc, "RunSummaryController did not self-spawn.");
            ((LevelRunTracker)GetField(rsc, "tracker")).Begin();
            SetField(lm, "currentLevelIndex", 0);
            // The goal accounting writes a REAL solo record for level 0 — capture the developer's, restore after.
            int prevBestT = PlayerPrefs.GetInt("records.solo.level0.bestTimeMs", -1);
            int prevBestD = PlayerPrefs.GetInt("records.solo.level0.fewestDeaths", -1);

            bool advanced = false;
            void OnBefore(int _) => advanced = true;
            lm.OnBeforeLevelLoad += OnBefore;
            try
            {
                yield return DriveInto(goal.GetComponent<Collider2D>());
                Assert.IsFalse(advanced, "The goal must HOLD at the summary card, not advance immediately (#130).");
                Assert.IsTrue(rsc.HoldActive, "Reaching the goal must enter the summary hold.");
                Assert.IsTrue(lm.SummaryHold, "The hold must gate the sim (LevelManager.SummaryHold).");

                rsc.ContinueFromCard();
                Assert.IsTrue(advanced, "Continue must advance the level (LevelManager.LoadNext).");
                Assert.IsFalse(lm.SummaryHold, "Continue must release the hold.");
            }
            finally
            {
                lm.OnBeforeLevelLoad -= OnBefore;
                SetField(lm, "levelSceneNames", savedLevels);
                SetField(lm, "currentLevelIndex", -1);
                rsc.ResetAll();          // hold/totals latch on the DontDestroyOnLoad controller across tests otherwise
                if (prevBestT >= 0) PlayerPrefs.SetInt("records.solo.level0.bestTimeMs", prevBestT);
                else PlayerPrefs.DeleteKey("records.solo.level0.bestTimeMs");
                if (prevBestD >= 0) PlayerPrefs.SetInt("records.solo.level0.fewestDeaths", prevBestD);
                else PlayerPrefs.DeleteKey("records.solo.level0.fewestDeaths");
                PlayerPrefs.Save();
            }
        }

        static object GetField(object t, string f) =>
            t.GetType().GetField(f, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(t);
        static void SetField(object t, string f, object v) =>
            t.GetType().GetField(f, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(t, v);
    }
}
