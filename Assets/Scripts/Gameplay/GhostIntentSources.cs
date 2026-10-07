using System;
using JumpNowBro.Util;

namespace JumpNowBro.Gameplay
{
    /// The intent a player is expressing on THIS machine right now: held move direction + held jump. Only the bits
    /// the ghost cue (#124) needs; rising edges ("taps") are detected by the cue itself.
    public readonly struct IntentSample
    {
        public readonly bool left, right, jumpHeld;
        public IntentSample(bool left, bool right, bool jumpHeld)
        {
            this.left = left; this.right = right; this.jumpHeld = jumpHeld;
        }
    }

    /// Inversion seam (like Authority): the solo / networking wiring registers how to read each player's intent on
    /// this machine, so the Gameplay-side GhostIntentCue never references Networking. Per role, P1/P2 resolve to a
    /// local keyboard or a remote frame. Registered in PlayerBootstrap (solo) and WireHosting/WireClient (networked,
    /// which overwrite the solo registration the same frame before any ghost reads it); cleared on teardown.
    public static class GhostIntentSources
    {
        static Func<IntentSample> p1Source, p2Source;

        public static void Register(Func<IntentSample> p1, Func<IntentSample> p2)
        {
            p1Source = p1;
            p2Source = p2;
        }

        public static void Reset() { p1Source = null; p2Source = null; }

        public static bool TryGet(InputOwner owner, out IntentSample sample)
        {
            var src = owner == InputOwner.P1 ? p1Source : p2Source;
            if (src == null) { sample = default; return false; }
            sample = src();
            return true;
        }

        public static IntentSample From(IInputSource s) =>
            s == null || (s is UnityEngine.Object obj && obj == null)
                ? default : new IntentSample(s.MoveLeft, s.MoveRight, s.JumpHeld);

        public static IntentSample From(in PlayerInputFrame f) =>
            new IntentSample(f.moveLeft, f.moveRight, f.jumpHeld);
    }
}
