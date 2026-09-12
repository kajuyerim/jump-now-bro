using System;

namespace JumpNowBro.Util
{
    /// The pure, replayable movement step. Host (Gameplay) and v1.5 client predictor (Networking)
    /// both call this same compiled symbol — the only Util/CI-testable boundary that v1.5 prediction
    /// can stand on. State machine + jump/cut/coyote/buffer + dash/freeze + gravity integration are
    /// all here; axis-separated Y-then-X sweep is delegated to ICollisionWorld.
    ///
    /// `sweep = false` skips the SweepX/Y calls and leaves velocity un-zeroed on block. Used at #69
    /// where the Dynamic body's Box2D solver still handles collision and we'd otherwise diverge from
    /// v0.4-mvp on first-contact ticks (vel zeroed → Box2D moves nothing → player ends tick up to
    /// runSpeed·dt short of the wall, busts the #72 A/B tolerance). At #70 onwards, sweep = true.
    public static class Movement
    {
        public static (MovementState, EdgeFlags) Step(
            in MovementState s_in, in EffectiveInput input, in MovementTuning t,
            float dt, ICollisionWorld world, bool sweep = true)
        {
            var s = s_in;
            EdgeFlags edges = EdgeFlags.None;

            if (s.posY < t.fallLimitY)
            {
                edges |= EdgeFlags.DiedThisTick;
                s.isDead = true;
                return (s, edges);
            }

            s.coyoteTimer     = MathF.Max(0f, s.coyoteTimer     - dt);
            s.jumpBufferTimer = MathF.Max(0f, s.jumpBufferTimer - dt);
            s.invulnTimer     = MathF.Max(0f, s.invulnTimer     - dt);

            bool grounded = world.Grounded(s.posX, s.posY);                              // pre-move grounding (matches v0.4-mvp line 103)

            if (input.moveDir != 0) s.facing = (sbyte)input.moveDir;
            if (input.jumpPressed) s.jumpBufferTimer = t.jumpBufferTime;
            bool jumpAllowed = InputForgiveness.CanJump(s.coyoteTimer, s.jumpBufferTimer, grounded, input.jumpPressed);

            switch (s.state)
            {
                case MoveState.Grounded:
                    if (input.dashPressed && s.dashChargeAvailable) FireDash(ref s, t, ref edges);
                    else if (jumpAllowed) FireJump(ref s, t, ref edges);
                    else if (!grounded)
                    {
                        s.state = MoveState.Falling;
                        s.coyoteTimer = t.coyoteTime;                                    // arm coyote on leaving ground (line 130)
                    }
                    break;

                case MoveState.Jumping:
                    if (input.dashPressed && s.dashChargeAvailable) FireDash(ref s, t, ref edges);
                    else
                    {
                        if (s.velY > 0f && !input.jumpHeld && s.wasJumpHeld)
                        {
                            s.velY *= t.variableJumpCutMultiplier;
                            edges |= EdgeFlags.JumpCutThisTick;
                        }
                        if (s.velY <= 0f) s.state = MoveState.Falling;
                    }
                    break;

                case MoveState.Falling:
                    if (grounded)
                    {
                        s.state = MoveState.Grounded;
                        s.dashChargeAvailable = true;                                    // refund dash charge on land (line 147)
                        edges |= EdgeFlags.LandedThisTick;
                    }
                    if (input.dashPressed && s.dashChargeAvailable) FireDash(ref s, t, ref edges);
                    else if (jumpAllowed) FireJump(ref s, t, ref edges);
                    break;

                case MoveState.Dashing:
                    if (s.freezeTicksRemaining > 0)
                    {
                        s.velX = 0f; s.velY = 0f;
                        s.freezeTicksRemaining--;
                        if (s.freezeTicksRemaining == 0)
                        {
                            float dashSpeed = t.dashDistance / t.dashDuration;
                            s.velX = s.facing * dashSpeed;
                        }
                    }
                    else
                    {
                        s.dashTimer = MathF.Max(0f, s.dashTimer - dt);
                        if (s.dashTimer <= 0f) s.state = MoveState.Falling;
                    }
                    break;
            }

            if (s.state != MoveState.Dashing)
            {
                float speedMul = (s.state == MoveState.Grounded) ? 1f : t.airControlMultiplier;
                s.velX = input.moveDir * t.runSpeed * speedMul;
                s.velY -= t.gravity * dt;
            }

            if (sweep)
            {
                world.SweepY(s.posX, s.posY, s.velY * dt, out float resolvedDy, out bool blockedY);
                if (blockedY) s.velY = 0f;
                s.posY += resolvedDy;

                float wantDx = s.velX * dt;
                world.SweepX(s.posX, s.posY, wantDx, out float resolvedDx, out bool blockedX);
                if (blockedX) s.velX = 0f;
                s.posX += resolvedDx;

                // #102 corner correction. Airborne, pushing horizontally, and both axes blocked: that's
                // either a corner-on-corner wedge or flush against a full wall. A small upward lift that
                // frees the horizontal move distinguishes them — only a corner clears, so we lift over it
                // instead of sticking. A full-height wall never frees (no-op), and flat ground never blocks
                // X, so WallStop_RunningRight and the v1.3 golden master are unchanged.
                if (blockedX && blockedY && !grounded && input.moveDir != 0)
                    CornerCorrect(ref s, wantDx, world);
            }

            s.wasJumpHeld = input.jumpHeld;
            return (s, edges);
        }

        static void FireJump(ref MovementState s, in MovementTuning t, ref EdgeFlags edges)
        {
            s.velY = t.jumpVelocity;
            s.state = MoveState.Jumping;
            s.jumpBufferTimer = 0f;
            s.coyoteTimer = 0f;
            edges |= EdgeFlags.JumpedThisTick;
        }

        static void FireDash(ref MovementState s, in MovementTuning t, ref EdgeFlags edges)
        {
            s.state = MoveState.Dashing;
            s.freezeTicksRemaining = t.dashFreezeTicks;
            s.dashTimer = t.dashDuration;
            s.invulnTimer = t.dashInvulnerabilityDuration;
            s.dashChargeAvailable = false;
            s.velX = 0f; s.velY = 0f;
            edges |= EdgeFlags.DashedThisTick;
        }

        // #102: probe small upward lifts (up to ~1/4 of the 1-unit body) for one that frees the blocked
        // horizontal move; apply the first that works so a corner wedge lifts over instead of sticking.
        const float CornerCorrectMax  = 0.25f;
        const float CornerCorrectStep = 0.05f;

        static void CornerCorrect(ref MovementState s, float wantDx, ICollisionWorld world)
        {
            for (float lift = CornerCorrectStep; lift <= CornerCorrectMax + 1e-4f; lift += CornerCorrectStep)
            {
                world.SweepY(s.posX, s.posY, lift, out float upDy, out bool upBlocked);
                if (upBlocked || upDy < lift - 1e-4f) return;        // something above blocks the lift: give up
                world.SweepX(s.posX, s.posY + lift, wantDx, out float clearDx, out bool stillBlocked);
                if (!stillBlocked) { s.posY += lift; s.posX += clearDx; return; }
            }
        }
    }
}
