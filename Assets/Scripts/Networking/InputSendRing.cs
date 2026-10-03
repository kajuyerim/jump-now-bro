using System;
using JumpNowBro.Util;

namespace JumpNowBro.Networking
{
    /// Client's K=6 sliding window of recent input frames. On each sample, stores the frame at slot
    /// `tick % K` and emits an INPUT body covering the most-recent `min(K, tick+1)` ticks. The window
    /// is what makes one INPUT packet carry redundancy without involving the reliable channel — an
    /// edge press at tick T is re-sent in packets T..T+K-1, so up to K-1 consecutive drops can occur
    /// before the host hears nothing about it.
    ///
    /// Engine-free so the redundancy/window logic is CI-testable end-to-end.
    public sealed class InputSendRing
    {
        public const int K = 6;

        readonly PlayerInputFrame[] frames = new PlayerInputFrame[K];
        readonly byte[] packed = new byte[K];

        /// Append `f` for `tick`, then write the INPUT body for the current redundancy window into `dst`.
        /// `dst` must hold at least `InputBody.HeaderSize + K` bytes. Returns bytes written.
        public int Sample(uint tick, in PlayerInputFrame f, Span<byte> dst)
        {
            frames[tick % K] = f;
            uint baseTick = tick >= K - 1 ? tick - (K - 1) : 0u;
            // Avoid wrapping tick+1 at uint.MaxValue: the final pre-wrap window is still full-sized.
            int count = tick >= K - 1 ? K : (int)(tick + 1u);
            for (int i = 0; i < count; i++)
                packed[i] = PlayerInputFrame.Pack(frames[(baseTick + (uint)i) % K]);
            return InputBody.Write(dst, baseTick, packed.AsSpan(0, count));
        }
    }
}
