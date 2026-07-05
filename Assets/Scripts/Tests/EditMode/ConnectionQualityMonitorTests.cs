using NUnit.Framework;
using JumpNowBro.Networking;

namespace JumpNowBro.Tests
{
    /// #132 quality monitor: hysteresis + sustain behaviour against synthetic RTT/loss feeds.
    /// Fair-profile numbers (~0.17 s two-editor RTT, 5% loss, even OR Bernoulli) must never trip;
    /// Stress-grade sustained RTT (0.30 s) or gross loss (20%) must, after the sustain window.
    public class ConnectionQualityMonitorTests
    {
        const float Dt = 1f / 30f;   // typical feed cadence (a 30 Hz-ish Update)

        // Drive `seconds` of simulated traffic: `pktPerSec` inbound packets, every `missEvery`-th missing
        // (0 = lossless), at a constant raw RTT. Returns the monitor for further phases.
        static ConnectionQualityMonitor Run(ConnectionQualityMonitor m, float seconds, float rtt,
                                            int pktPerSec, int missEvery,
                                            ref int accepted, ref int missed, ref double pktAcc)
        {
            int steps = (int)(seconds / Dt);
            for (int i = 0; i < steps; i++)
            {
                pktAcc += pktPerSec * (double)Dt;
                while (pktAcc >= 1.0)
                {
                    pktAcc -= 1.0;
                    int n = accepted + missed + 1;                       // 1-based packet ordinal
                    if (missEvery > 0 && n % missEvery == 0) missed++;
                    else accepted++;
                }
                m.Tick(Dt, rtt, accepted, missed);
            }
            return m;
        }

        [Test]
        public void FairProfileNumbers_NeverTrip()
        {
            var m = new ConnectionQualityMonitor();
            int a = 0, x = 0; double p = 0;
            Run(m, 60f, rtt: 0.17f, pktPerSec: 30, missEvery: 20, ref a, ref x, ref p);   // 5% loss
            Assert.IsFalse(m.Unstable);
        }

        // Regression: REAL loss is Bernoulli, not evenly spaced — 5% wobbles well past 5% in any small
        // window. The original 8% enter threshold false-tripped on the Fair sim within minutes; the gross
        // 15% gate must hold quiet through minutes of seeded-random 5% loss.
        [Test]
        public void FairProfileBernoulliLoss_NeverTrips()
        {
            var m = new ConnectionQualityMonitor();
            var rng = new System.Random(4242);
            int accepted = 0, missed = 0;
            double pktAcc = 0;
            int steps = (int)(180f / Dt);                            // 3 minutes at 30 pkt/s
            for (int i = 0; i < steps; i++)
            {
                pktAcc += 30 * (double)Dt;
                while (pktAcc >= 1.0)
                {
                    pktAcc -= 1.0;
                    if (rng.NextDouble() < 0.05) missed++; else accepted++;
                }
                m.Tick(Dt, 0.17f, accepted, missed);
                Assert.IsFalse(m.Unstable, $"false trip at step {i} (t={i * Dt:F1}s)");
            }
        }

        [Test]
        public void SustainedHighRtt_Trips_AfterSustain()
        {
            var m = new ConnectionQualityMonitor();
            int a = 0, x = 0; double p = 0;
            Run(m, 1.9f, rtt: 0.30f, pktPerSec: 30, missEvery: 0, ref a, ref x, ref p);
            Assert.IsFalse(m.Unstable, "must not trip before the 2 s sustain");
            Run(m, 0.3f, rtt: 0.30f, pktPerSec: 30, missEvery: 0, ref a, ref x, ref p);
            Assert.IsTrue(m.Unstable, "must trip once sustained past 2 s");
        }

        [Test]
        public void SustainedGrossLoss_Trips_WithLowRtt()
        {
            var m = new ConnectionQualityMonitor();
            int a = 0, x = 0; double p = 0;
            Run(m, 15f, rtt: 0.05f, pktPerSec: 60, missEvery: 5, ref a, ref x, ref p);   // 20% loss: gross
            Assert.IsTrue(m.Unstable);
        }

        [Test]
        public void RttSpike_OneSecond_DoesNotTrip()
        {
            var m = new ConnectionQualityMonitor();
            int a = 0, x = 0; double p = 0;
            Run(m, 5f, rtt: 0.05f, pktPerSec: 30, missEvery: 0, ref a, ref x, ref p);
            Run(m, 1f, rtt: 0.50f, pktPerSec: 30, missEvery: 0, ref a, ref x, ref p);     // 1 s spike < 2 s sustain
            Run(m, 5f, rtt: 0.05f, pktPerSec: 30, missEvery: 0, ref a, ref x, ref p);
            Assert.IsFalse(m.Unstable);
        }

        [Test]
        public void Hysteresis_HoldsInsideBand_ThenClears()
        {
            var m = new ConnectionQualityMonitor();
            int a = 0, x = 0; double p = 0;
            Run(m, 3f, rtt: 0.30f, pktPerSec: 30, missEvery: 0, ref a, ref x, ref p);
            Assert.IsTrue(m.Unstable);
            Run(m, 5f, rtt: 0.19f, pktPerSec: 30, missEvery: 0, ref a, ref x, ref p);     // inside the 0.18-0.22 band
            Assert.IsTrue(m.Unstable, "inside the hysteresis band must not clear");
            Run(m, 2.9f, rtt: 0.10f, pktPerSec: 30, missEvery: 0, ref a, ref x, ref p);
            Assert.IsTrue(m.Unstable, "recovery must sustain 3 s before clearing");
            Run(m, 0.3f, rtt: 0.10f, pktPerSec: 30, missEvery: 0, ref a, ref x, ref p);
            Assert.IsFalse(m.Unstable);
        }

        // Regression (observed live): two-sided Fair idles at ~150-156 ms EMA with ~5% loss. The original
        // exit thresholds (rtt 0.150 / loss 0.06) sat BELOW that steady state, so one transient spike
        // latched Unstable forever. Fair's steady state must be able to CLEAR a prior trip.
        [Test]
        public void FairSteadyState_AfterSpike_Clears()
        {
            var m = new ConnectionQualityMonitor();
            int a = 0, x = 0; double p = 0;
            Run(m, 5f, rtt: 0.155f, pktPerSec: 30, missEvery: 20, ref a, ref x, ref p);   // settle at Fair
            Run(m, 4f, rtt: 0.155f, pktPerSec: 60, missEvery: 3, ref a, ref x, ref p);    // gross 33% loss spike
            Assert.IsTrue(m.Unstable, "a gross loss spike must trip");
            Run(m, 12f, rtt: 0.155f, pktPerSec: 30, missEvery: 20, ref a, ref x, ref p);  // back to Fair steady state
            Assert.IsFalse(m.Unstable, "Fair steady state (rtt ~155 ms, 5% loss) must clear the latch");
        }

        [Test]
        public void SparseTraffic_LossGateDisabled()
        {
            var m = new ConnectionQualityMonitor();
            int a = 0, x = 0; double p = 0;
            // 1 pkt/s with every 2nd missing = 50% raw loss, but the window never reaches minWindowPackets.
            Run(m, 30f, rtt: 0.05f, pktPerSec: 1, missEvery: 2, ref a, ref x, ref p);
            Assert.IsFalse(m.Unstable);
            Assert.AreEqual(0f, m.WindowLossRatio, "loss gate must read 0 under the sample floor");
        }

        [Test]
        public void Reset_ClearsStateAndCounters()
        {
            var m = new ConnectionQualityMonitor();
            int a = 0, x = 0; double p = 0;
            Run(m, 3f, rtt: 0.30f, pktPerSec: 30, missEvery: 0, ref a, ref x, ref p);
            Assert.IsTrue(m.Unstable);

            m.Reset();
            Assert.IsFalse(m.Unstable);

            // Post-reset the first feed re-baselines: totals far ahead of the old baseline must not
            // register as a burst of loss (negative/garbage deltas).
            m.Tick(Dt, 0.05f, a + 5000, x);
            m.Tick(Dt, 0.05f, a + 5001, x);
            Assert.IsFalse(m.Unstable);
            Assert.AreEqual(0f, m.WindowLossRatio, 0.001f);
        }
    }
}
