namespace JumpNowBro.Networking
{
    /// Exponential moving average of round-trip samples (from PING/PONG). Drives the retransmit
    /// timer in the reliable send queue. Unit-agnostic floats — we feed and read seconds.
    public sealed class RttEstimator
    {
        readonly float smoothing;
        bool hasSample;

        public float RttSeconds { get; private set; }

        /// Most recent raw sample, unsmoothed (0 until the first sample). Diagnostics only: the #132
        /// quality monitor deliberately feeds on the EMA instead, because raw samples land at 1 Hz and
        /// hold between PONGs, which collapses a seconds-scale sustain window to "two unlucky samples".
        public float LastSampleSeconds { get; private set; }

        // smoothing 0.125 = 1/8, the TCP SRTT convention.
        public RttEstimator(float initialRttSeconds = 0.1f, float smoothing = 0.125f)
        {
            RttSeconds = initialRttSeconds;
            this.smoothing = smoothing;
        }

        public void AddSample(float rttSeconds)
        {
            if (rttSeconds < 0f) return;                 // impossible sample (clock skew) — ignore
            LastSampleSeconds = rttSeconds;
            if (!hasSample) { RttSeconds = rttSeconds; hasSample = true; }  // seed directly, no warmup bias
            else RttSeconds += smoothing * (rttSeconds - RttSeconds);       // EMA so one spike can't whipsaw it
        }
    }
}
