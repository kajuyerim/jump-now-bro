using System;

namespace JumpNowBro.Networking
{
    /// Tuning for ConnectionQualityMonitor. Sustained SMOOTHED RTT is the PRIMARY signal: feed the EMA,
    /// never raw 1 Hz PONG samples (a raw sample holds between arrivals, so a seconds-scale sustain window
    /// degenerates to "two unlucky samples" and jitter/frame-quantization false-trips the Fair sim). With
    /// the EMA: two-sided Fair settles ~150-175 ms (below enter), two-sided Stress ~250-285 ms (trips in
    /// ~10 s). The loss gate is a GROSS-loss detector only: real Bernoulli loss wobbles hard in a small
    /// window (5% over ~180 packets swings past 10% routinely), so a threshold close to Fair's 5% would
    /// false-trip within minutes; 5% vs 10% cannot be told apart reliably at this sample size.
    public struct ConnectionQualityTuning
    {
        public float RttEnterSeconds;     // sustained raw RTT at/above this is "bad"
        public float RttExitSeconds;      // recovery requires RTT at/below this (hysteresis band between)
        public float LossEnterRatio;      // windowed inbound loss at/above this is "bad" (gross loss only)
        public float LossExitRatio;
        public double EnterSustainSeconds;   // bad must hold continuously this long to trip
        public double ExitSustainSeconds;    // good must hold continuously this long to clear
        public double BucketSeconds;         // loss window = BucketCount rolling buckets of this length
        public int BucketCount;
        public int MinWindowPackets;         // below this many packets in the window the loss gate is OFF
                                             // (a 1 Hz PING-only lobby must not read as 25% loss; the
                                             // indicator is RTT-only on sparse traffic BY DESIGN)

        // EXIT thresholds must sit ABOVE the steady state of a link that's considered "fine": two-sided
        // Fair idles at ~146-156 ms EMA with 5-8% window loss, and the original exits (0.150 / 0.06) sat
        // BELOW that floor — one transient spike then latched Unstable forever (observed live: a client
        // stuck Unstable while reading rtt 156 / loss 5%, well under the ENTER thresholds).
        public static ConnectionQualityTuning Default => new ConnectionQualityTuning
        {
            RttEnterSeconds = 0.220f,     // clears two-sided Fair's ~150-175 ms EMA even on slow editors (frame quantization)
            RttExitSeconds = 0.180f,      // above Fair's ~156 ms floor so recovery is actually reachable
            LossEnterRatio = 0.15f,       // gross loss only; Fair's 5% cannot reach it at a full window
            LossExitRatio = 0.10f,        // above Fair's 5-8% window wobble so it can't hold the latch
            EnterSustainSeconds = 2.0,
            ExitSustainSeconds = 3.0,
            BucketSeconds = 1.0,
            BucketCount = 6,              // 6 s window: enough samples that the 15% gate is statistically stable
            MinWindowPackets = 60,        // a small just-armed window (game start) can read 5% loss as 20%+:
                                          // observed 23% on ~40 packets when true loss was 5%. 60+ samples
                                          // keeps the gate honest; in-game traffic reaches it in ~1-2 s.
        };
    }

    /// Engine-free "is the connection degraded?" decider (#132), CI-tested. Fed each frame with the raw
    /// last RTT sample and the transport's cumulative inbound accepted/missed packet counters (deltas are
    /// taken internally; the first Tick is baseline-only). Hysteresis + sustain windows keep it from
    /// nagging: enter on (rtt OR loss) bad held EnterSustain, exit on BOTH good held ExitSustain.
    /// Lifetime: one monitor per transport instance (counters are per-transport; a rejoin's fresh
    /// transport must get a fresh monitor or deltas go negative).
    public sealed class ConnectionQualityMonitor
    {
        readonly ConnectionQualityTuning tuning;
        readonly int[] bucketAccepted;
        readonly int[] bucketMissed;
        int bucketIndex;
        double bucketElapsed;

        bool baselined;
        int lastAcceptedTotal;
        int lastMissedTotal;

        double badFor;
        double goodFor;

        public bool Unstable { get; private set; }
        /// Diagnostics: loss ratio over the rolling window (0 while the gate is off).
        public float WindowLossRatio { get; private set; }
        /// Diagnostics: the RTT value fed on the last Tick (what the RTT gate actually sees).
        public float LastRttFed { get; private set; }

        public ConnectionQualityMonitor() : this(ConnectionQualityTuning.Default) { }

        public ConnectionQualityMonitor(ConnectionQualityTuning tuning)
        {
            this.tuning = tuning;
            bucketAccepted = new int[Math.Max(1, tuning.BucketCount)];
            bucketMissed = new int[Math.Max(1, tuning.BucketCount)];
        }

        public void Tick(double dt, float rttSeconds, int packetsAcceptedTotal, int packetsMissedTotal)
        {
            LastRttFed = rttSeconds;
            if (!baselined || packetsAcceptedTotal < lastAcceptedTotal || packetsMissedTotal < lastMissedTotal)
            {
                // First feed, or counters went backwards (defensive: a swapped transport): baseline only.
                baselined = true;
                lastAcceptedTotal = packetsAcceptedTotal;
                lastMissedTotal = packetsMissedTotal;
                return;
            }

            bucketAccepted[bucketIndex] += packetsAcceptedTotal - lastAcceptedTotal;
            bucketMissed[bucketIndex] += packetsMissedTotal - lastMissedTotal;
            lastAcceptedTotal = packetsAcceptedTotal;
            lastMissedTotal = packetsMissedTotal;

            bucketElapsed += dt;
            while (bucketElapsed >= tuning.BucketSeconds)
            {
                bucketElapsed -= tuning.BucketSeconds;
                bucketIndex = (bucketIndex + 1) % bucketAccepted.Length;
                bucketAccepted[bucketIndex] = 0;
                bucketMissed[bucketIndex] = 0;
            }

            int accepted = 0, missed = 0;
            for (int i = 0; i < bucketAccepted.Length; i++) { accepted += bucketAccepted[i]; missed += bucketMissed[i]; }
            int windowPackets = accepted + missed;
            WindowLossRatio = windowPackets >= tuning.MinWindowPackets ? missed / (float)windowPackets : 0f;

            bool bad = rttSeconds >= tuning.RttEnterSeconds || WindowLossRatio >= tuning.LossEnterRatio;
            bool good = rttSeconds <= tuning.RttExitSeconds && WindowLossRatio <= tuning.LossExitRatio;

            if (!Unstable)
            {
                badFor = bad ? badFor + dt : 0.0;
                if (badFor >= tuning.EnterSustainSeconds) { Unstable = true; goodFor = 0.0; }
            }
            else
            {
                goodFor = good ? goodFor + dt : 0.0;
                if (goodFor >= tuning.ExitSustainSeconds) { Unstable = false; badFor = 0.0; }
            }
        }

        public void Reset()
        {
            Unstable = false;
            WindowLossRatio = 0f;
            baselined = false;
            badFor = goodFor = 0.0;
            bucketIndex = 0;
            bucketElapsed = 0.0;
            Array.Clear(bucketAccepted, 0, bucketAccepted.Length);
            Array.Clear(bucketMissed, 0, bucketMissed.Length);
        }
    }
}
