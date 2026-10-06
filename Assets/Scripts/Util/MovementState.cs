using System;

namespace JumpNowBro.Util
{
    /// Replayable per-tick snapshot of the shared character. Separate floats (no Vector2) keep Util
    /// engine-free, give us a stable wire-friendly layout for v1.4 STATE serialization, and let
    /// Movement.Step run identically on host and the v1.5 client predictor (same compiled symbol).
    /// Mutable struct — `in MovementState` on Step's parameter blocks accidental caller mutation;
    /// Step builds a fresh value internally and returns it.
    public struct MovementState
    {
        public float posX, posY, velX, velY;
        public MoveState state;
        public sbyte facing;                      // +1 / -1
        public float coyoteTimer, jumpBufferTimer, dashTimer, invulnTimer;
        public sbyte freezeTicksRemaining;        // tick counter (not seconds): survives v1.5 variable-dt rollback
        public bool dashChargeAvailable, wasJumpHeld, isDead;
        public bool dashTouchedGround;           // distinguishes a ledge dash from an entirely airborne dash

        // Wire layout — 46 bytes. 32 bytes of floats + 4 small fields + 10 reserved.
        //   0  posX                f32 BE
        //   4  posY                f32 BE
        //   8  velX                f32 BE
        //  12  velY                f32 BE
        //  16  coyoteTimer         f32 BE
        //  20  jumpBufferTimer     f32 BE
        //  24  dashTimer           f32 BE
        //  28  invulnTimer         f32 BE
        //  32  state               u8
        //  33  facing              i8
        //  34  freezeTicksRemaining i8
        //  35  flags               u8   bit 0=dashChargeAvailable, 1=wasJumpHeld, 2=isDead, 3=dashTouchedGround
        //  36  _padding            10 zero bytes — v1.6/v1.7 append-only reserve
        public const int PackedSize = 46;
        public const int MinPackedSize = 36;      // everything through flags; padding can be truncated

        public static int Pack(in MovementState s, Span<byte> dst)
        {
            if (dst.Length < PackedSize) throw new ArgumentException("MovementState.Pack: dst too small");
            WriteFloatBE(dst, 0, s.posX);
            WriteFloatBE(dst, 4, s.posY);
            WriteFloatBE(dst, 8, s.velX);
            WriteFloatBE(dst, 12, s.velY);
            WriteFloatBE(dst, 16, s.coyoteTimer);
            WriteFloatBE(dst, 20, s.jumpBufferTimer);
            WriteFloatBE(dst, 24, s.dashTimer);
            WriteFloatBE(dst, 28, s.invulnTimer);
            dst[32] = (byte)s.state;
            dst[33] = (byte)s.facing;
            dst[34] = (byte)s.freezeTicksRemaining;
            byte flags = 0;
            if (s.dashChargeAvailable) flags |= 1 << 0;
            if (s.wasJumpHeld)         flags |= 1 << 1;
            if (s.isDead)              flags |= 1 << 2;
            if (s.dashTouchedGround)   flags |= 1 << 3;
            dst[35] = flags;
            dst.Slice(36, 10).Clear();
            return PackedSize;
        }

        /// Tolerates fewer trailing bytes — readers accept >= MinPackedSize (36) and default missing padding to zero.
        /// Forward-compat: a v1.5 sender that puts data in the padding region is silently ignored by a v1.4 reader.
        public static bool TryUnpack(ReadOnlySpan<byte> src, out MovementState s)
        {
            s = default;
            if (src.Length < MinPackedSize) return false;
            if (!TryReadFloatBE(src, 0,  out s.posX))            return false;
            if (!TryReadFloatBE(src, 4,  out s.posY))            return false;
            if (!TryReadFloatBE(src, 8,  out s.velX))            return false;
            if (!TryReadFloatBE(src, 12, out s.velY))            return false;
            if (!TryReadFloatBE(src, 16, out s.coyoteTimer))     return false;
            if (!TryReadFloatBE(src, 20, out s.jumpBufferTimer)) return false;
            if (!TryReadFloatBE(src, 24, out s.dashTimer))       return false;
            if (!TryReadFloatBE(src, 28, out s.invulnTimer))     return false;
            if (src[32] > (byte)MoveState.Dashing) return false;       // out-of-range MoveState byte → malformed
            sbyte facing               = (sbyte)src[33];
            sbyte freezeTicksRemaining = (sbyte)src[34];
            if (facing != -1 && facing != 1) return false;             // documented facing is ±1
            if (freezeTicksRemaining < 0) return false;               // countdown cannot be negative
            s.state                = (MoveState)src[32];
            s.facing               = facing;
            s.freezeTicksRemaining = freezeTicksRemaining;
            byte flags             = src[35];
            s.dashChargeAvailable  = (flags & (1 << 0)) != 0;
            s.wasJumpHeld          = (flags & (1 << 1)) != 0;
            s.isDead               = (flags & (1 << 2)) != 0;
            s.dashTouchedGround    = (flags & (1 << 3)) != 0;
            return true;
        }

        static void WriteFloatBE(Span<byte> dst, int offset, float v)
        {
            uint bits = (uint)BitConverter.SingleToInt32Bits(v);
            dst[offset]     = (byte)(bits >> 24);
            dst[offset + 1] = (byte)(bits >> 16);
            dst[offset + 2] = (byte)(bits >> 8);
            dst[offset + 3] = (byte)bits;
        }

        static bool TryReadFloatBE(ReadOnlySpan<byte> src, int offset, out float v)
        {
            if (offset + 4 > src.Length) { v = 0; return false; }
            uint bits = ((uint)src[offset] << 24) | ((uint)src[offset + 1] << 16) | ((uint)src[offset + 2] << 8) | src[offset + 3];
            v = BitConverter.Int32BitsToSingle((int)bits);
            return true;
        }
    }
}
