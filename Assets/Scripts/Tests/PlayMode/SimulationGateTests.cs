using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using JumpNowBro.Gameplay;
using JumpNowBro.Networking;

namespace JumpNowBro.Tests.PlayMode
{
    public class SimulationGateTests
    {
        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (LevelManager.Instance == null)
                yield return SceneManager.LoadSceneAsync("Bootstrap", LoadSceneMode.Single);
        }

        [TestCase(false, false, false, false, false, false)]
        [TestCase(false, false, true, true, true, false)]
        [TestCase(false, true, false, true, false, false)]
        [TestCase(false, true, true, true, true, false)]
        [TestCase(true, false, false, true, true, true)]
        [TestCase(true, false, true, true, true, true)]
        [TestCase(true, true, false, true, true, true)]
        [TestCase(true, true, true, true, true, true)]
        public void GateVariants_PreserveTheirDistinctHolds(bool loading, bool paused, bool summary,
            bool gameplayBlocked, bool timelineBlocked, bool sceneBlocked)
        {
            var level = LevelManager.Instance;
            var previous = (level.IsLoading, level.SimPaused, level.SummaryHold);
            var go = new GameObject("GateProbe");
            var probe = go.AddComponent<GateProbe>();
            try
            {
                SetHolds(level, loading, paused, summary);
                Assert.AreEqual(gameplayBlocked, level.SimGated);
                Assert.AreEqual(timelineBlocked, level.TimelineGated);
                Assert.AreEqual(sceneBlocked, level.SceneEventsGated);
                foreach (var (gate, blocked) in new[] {
                    (SimulationGate.Gameplay, gameplayBlocked),
                    (SimulationGate.Timeline, timelineBlocked),
                    (SimulationGate.SceneLoading, sceneBlocked) })
                {
                    probe.Policy = gate;
                    probe.Ticks = 0;
                    probe.SendMessage("FixedUpdate");
                    Assert.AreEqual(blocked ? 0 : 1, probe.Ticks, gate.ToString());
                }
            }
            finally
            {
                SetHolds(level, previous.IsLoading, previous.SimPaused, previous.SummaryHold);
                Object.DestroyImmediate(go);
            }
        }

        [UnityTest]
        public IEnumerator UnityDispatchesInheritedTick_AfterHoldClears()
        {
            var level = LevelManager.Instance;
            var previous = (level.IsLoading, level.SimPaused, level.SummaryHold);
            var go = new GameObject("AutomaticGateProbe");
            var probe = go.AddComponent<GateProbe>();
            try
            {
                SetHolds(level, false, false, true);
                yield return new WaitForFixedUpdate();
                Assert.AreEqual(0, probe.Ticks);
                level.SummaryHold = false;
                yield return new WaitForFixedUpdate();
                Assert.Greater(probe.Ticks, 0);
            }
            finally
            {
                SetHolds(level, previous.IsLoading, previous.SimPaused, previous.SummaryHold);
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void FixedStepConsumers_CannotBypassTheSharedGate()
        {
            foreach (var assembly in new[] { typeof(PlayerController).Assembly, typeof(NetworkManager).Assembly })
            foreach (var type in assembly.GetTypes())
            {
                if (!typeof(MonoBehaviour).IsAssignableFrom(type)) continue;
                // The shared clock must advance through holds; it does not perform simulation work.
                if (type == typeof(TickClock) || type == typeof(GatedSimulationBehaviour)) continue;
                var tick = type.GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (tick == null) continue;
                Assert.IsTrue(typeof(GatedSimulationBehaviour).IsAssignableFrom(type),
                    $"{type.Name} must choose a SimulationGate and implement SimulationTick.");
                Assert.AreEqual(typeof(GatedSimulationBehaviour), tick.DeclaringType,
                    $"{type.Name} must not hide the gated FixedUpdate entry point.");
            }
        }

        static void SetHolds(LevelManager level, bool loading, bool paused, bool summary)
        {
            typeof(LevelManager).GetProperty(nameof(LevelManager.IsLoading)).SetValue(level, loading);
            level.SimPaused = paused;
            level.SummaryHold = summary;
        }

        public sealed class GateProbe : GatedSimulationBehaviour
        {
            public SimulationGate Policy;
            public int Ticks;
            protected override SimulationGate Gate => Policy;
            protected override void SimulationTick() => Ticks++;
        }
    }
}
