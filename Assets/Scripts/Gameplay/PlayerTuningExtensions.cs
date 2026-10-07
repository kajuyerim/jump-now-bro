using System;
using JumpNowBro.Util;

namespace JumpNowBro.Gameplay
{
    /// Project the PlayerTuning ScriptableObject (Gameplay) into the engine-free MovementTuning POD
    /// (Util) that Movement.Step actually consumes. Rebuilt every FixedUpdate so the Inspector
    /// live-tune workflow keeps working — copying once at Awake would freeze constants until restart.
    /// `dashFreezeTicks` uses Ceiling to match the v0.4-mvp behavior where `freezeTimer > 0` plus a
    /// `freezeTimer -= dt` per tick yields ceil(duration/dt) ticks of freeze (e.g. 0.05 / 0.02 → 3
    /// ticks, not 2 — Math.Round would have given 2 via banker's rounding).
    public static class PlayerTuningExtensions
    {
        public static MovementTuning AsMovementTuning(this PlayerTuning t, float dt, float fallLimitY)
        {
            return new MovementTuning
            {
                runSpeed                    = t.runSpeed,
                airControlMultiplier        = t.airControlMultiplier,
                jumpVelocity                = t.jumpVelocity,
                gravity                     = t.gravity,
                coyoteTime                  = t.coyoteTime,
                jumpBufferTime              = t.jumpBufferTime,
                variableJumpCutMultiplier   = t.variableJumpCutMultiplier,
                dashDistance                = t.dashDistance,
                dashDuration                = t.dashDuration,
                dashInvulnerabilityDuration = t.dashInvulnerabilityDuration,
                dashFreezeTicks             = (sbyte)Math.Clamp(Math.Ceiling(t.dashFreezeFrameDuration / dt), 0, sbyte.MaxValue),
                fallLimitY                  = fallLimitY,
            };
        }
    }
}
