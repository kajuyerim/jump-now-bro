namespace JumpNowBro.Util
{
    /// The recorded, single-source v1.5 client-prediction constants (#83). These were locked on a clean LAN
    /// (127.0.0.1 + ParrelSync); adverse-network re-verification/re-tuning is tracked separately for the
    /// hardening milestone (#108). VisualSmoothing and ClientPrediction reference these so the values live in
    /// exactly one place instead of as scattered magic numbers.
    ///
    /// Rationale for the locked values:
    /// - SmoothingDecayPerTick 0.30 → render offset is ~99% gone in ~13 ticks (~140 ms at 60 Hz): fast enough to
    ///   feel responsive, slow enough that a correction reads as a glide rather than a snap.
    /// - SnapThreshold 4.0 u (v1.7 #108; was 1.5 on clean LAN) → with host-owned motion now interpolated rather
    ///   than extrapolated (see ClientPrediction.DeadReckonHost), the per-STATE catch-up under lag is a forward
    ///   ease of up to ~3 u; the threshold sits above that so it glides instead of teleporting. Client-owned
    ///   corrections stay sub-u so they're unaffected. (Respawn/level teleports always cut regardless, via Clear().)
    /// - ReplayCap 64 ticks (~1.07 s) → half the ClientHistory ring, longer than any expected LAN RTT + brief
    ///   stall, but bounded so a post-stall catch-up storm hard-snaps instead of replaying hundreds of ticks
    ///   (and never reads past the ClientHistory ring).
    /// - MaxForwardPredictTicks 64 (#89) → under heavy loss/stall STATE stops arriving and the predictor would
    ///   otherwise free-run the body forward indefinitely; after this many ticks with no fresh snapshot it holds
    ///   the last pose instead. It currently matches ReplayCap so the hold engages where reconcile hard-snaps.
    ///   Only reachable under adverse networks (clean LAN delivers a STATE every ~2 ticks); #108 re-tunes.
    ///
    /// NOTE: same-tick prediction (host stays the only tick authority) means there is deliberately NO inputLead
    /// knob — the client predicts at its current tick and reconciles, rather than running ahead of the host.
    public static class PredictionTuning
    {
        public const float SmoothingDecayPerTick = 0.30f;   // ~140 ms blend at 60 Hz
        public const float SnapThreshold         = 4.0f;    // v1.7 #108: was 1.5 on clean LAN; raised so host-movement catch-up under lag eases instead of teleporting
        public const int   ReplayCap             = ClientHistory.Capacity / 2; // max ticks before hard-snap; half the history ring
        public const int   MaxForwardPredictTicks = 64;     // #89: ticks of free-running forward (no fresh STATE) before holding the pose
    }
}
