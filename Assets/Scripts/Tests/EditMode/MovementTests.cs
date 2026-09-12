using NUnit.Framework;
using JumpNowBro.Util;
using JumpNowBro.Tests.CollisionWorlds;

namespace JumpNowBro.Tests
{
    public class MovementTests
    {
        const float Dt = 1f / 60f;

        static MovementTuning DefaultTuning() => new MovementTuning
        {
            runSpeed                    = 9f,
            airControlMultiplier        = 0.85f,
            jumpVelocity                = 16f,
            gravity                     = 50f,
            coyoteTime                  = 0.1f,
            jumpBufferTime              = 0.1f,
            variableJumpCutMultiplier   = 0.5f,
            dashDistance                = 4f,
            dashDuration                = 0.15f,
            dashInvulnerabilityDuration = 0.15f,
            dashFreezeTicks             = 3,                                // Ceiling(0.05 / (1/60)) = 3
            fallLimitY                  = -20f,
        };

        static MovementState GroundedAtOrigin() => new MovementState
        {
            state               = MoveState.Grounded,
            facing              = 1,
            dashChargeAvailable = true,
            posY                = 1f,                                       // feet at y=0 in FakeFlatGroundWorld
        };

        // ---- Determinism ----

        [Test]
        public void Replay_SameInputs_BitEqual()
        {
            var t = DefaultTuning();
            var w = new FakeFlatGroundWorld();
            var s0 = GroundedAtOrigin();
            var seq = new EffectiveInput[120];
            for (int i = 0; i < 120; i++)
            {
                seq[i] = new EffectiveInput
                {
                    moveDir     = ((i / 30) % 2 == 0) ? 1 : -1,
                    jumpPressed = (i % 17 == 0),
                    jumpHeld    = (i % 17 < 5),
                    dashPressed = (i == 45 || i == 90),
                };
            }

            var s1 = s0; var s2 = s0;
            for (int i = 0; i < seq.Length; i++)
            {
                (s1, _) = Movement.Step(s1, seq[i], t, Dt, w);
                (s2, _) = Movement.Step(s2, seq[i], t, Dt, w);
            }
            AssertStateEqual(s1, s2);
        }

        // ---- Jump ----

        [Test]
        public void JumpFromGrounded_TransitionsToJumping_AndSetsVelY()
        {
            var t = DefaultTuning();
            var w = new FakeFlatGroundWorld();
            var s = GroundedAtOrigin();
            var input = new EffectiveInput { jumpPressed = true, jumpHeld = true };

            EdgeFlags edges; (s, edges) = Movement.Step(s, input, t, Dt, w);

            Assert.AreEqual(MoveState.Jumping, s.state);
            Assert.AreNotEqual(EdgeFlags.None, edges & EdgeFlags.JumpedThisTick);
            // FireJump sets velY = 16, then integration block subtracts gravity*dt.
            Assert.That(s.velY, Is.EqualTo(t.jumpVelocity - t.gravity * Dt).Within(0.01f));
        }

        [Test]
        public void JumpApex_FromStandstill_WithinAnalyticTolerance()
        {
            var t = DefaultTuning();
            var w = new FakeFlatGroundWorld();
            var s = GroundedAtOrigin();
            var inputJump = new EffectiveInput { jumpPressed = true, jumpHeld = true };
            var inputHold = new EffectiveInput { jumpHeld = true };

            float maxY = s.posY;
            (s, _) = Movement.Step(s, inputJump, t, Dt, w);
            if (s.posY > maxY) maxY = s.posY;
            for (int i = 0; i < 50 && s.state == MoveState.Jumping; i++)
            {
                (s, _) = Movement.Step(s, inputHold, t, Dt, w);
                if (s.posY > maxY) maxY = s.posY;
            }

            float apex = maxY - 1f;
            float analytic = (t.jumpVelocity * t.jumpVelocity) / (2f * t.gravity);
            Assert.That(apex, Is.EqualTo(analytic).Within(0.5f * Dt * t.jumpVelocity));
        }

        [Test]
        public void VariableJumpCut_FiresOnReleaseTick_AndHalvesVelY()
        {
            var t = DefaultTuning();
            var w = new FakeFlatGroundWorld();
            var s = new MovementState { state = MoveState.Jumping, facing = 1, velY = 10f, wasJumpHeld = true, posY = 5f };
            var input = new EffectiveInput { jumpHeld = false };

            EdgeFlags edges; (s, edges) = Movement.Step(s, input, t, Dt, w);

            Assert.AreNotEqual(EdgeFlags.None, edges & EdgeFlags.JumpCutThisTick);
            Assert.That(s.velY, Is.EqualTo(10f * 0.5f - t.gravity * Dt).Within(0.01f));
        }

        [Test]
        public void WasJumpHeld_PreservedAcrossDashFreeze()
        {
            var t = DefaultTuning();
            var w = new FakeFlatGroundWorld();
            var s = GroundedAtOrigin();

            // Jump → Dash mid-air with jumpHeld still true throughout
            (s, _) = Movement.Step(s, new EffectiveInput { jumpPressed = true, jumpHeld = true }, t, Dt, w);
            Assert.AreEqual(MoveState.Jumping, s.state);
            Assert.IsTrue(s.wasJumpHeld);

            (s, _) = Movement.Step(s, new EffectiveInput { jumpHeld = true, dashPressed = true }, t, Dt, w);
            Assert.AreEqual(MoveState.Dashing, s.state);

            for (int i = 0; i < t.dashFreezeTicks; i++)
            {
                (s, _) = Movement.Step(s, new EffectiveInput { jumpHeld = true }, t, Dt, w);
                Assert.IsTrue(s.wasJumpHeld, $"wasJumpHeld lost at freeze tick {i}");
            }
        }

        // ---- Coyote time ----

        [Test]
        public void Coyote_WithinWindow_JumpFires()
        {
            var t = DefaultTuning();
            var w = new ConfigurableGroundWorld { isGrounded = true };
            var s = new MovementState { state = MoveState.Grounded, facing = 1, dashChargeAvailable = true, posY = 1f };

            w.isGrounded = false;
            (s, _) = Movement.Step(s, new EffectiveInput(), t, Dt, w);
            Assert.AreEqual(MoveState.Falling, s.state);
            Assert.That(s.coyoteTimer, Is.GreaterThan(0f));

            for (int i = 0; i < 3; i++)
                (s, _) = Movement.Step(s, new EffectiveInput(), t, Dt, w);

            (s, _) = Movement.Step(s, new EffectiveInput { jumpPressed = true }, t, Dt, w);
            Assert.AreEqual(MoveState.Jumping, s.state);
        }

        [Test]
        public void Coyote_WithSpentDash_JumpDoesNotRefundOrLand()
        {
            var t = DefaultTuning();
            var w = new ConfigurableGroundWorld { isGrounded = false };
            var s = GroundedAtOrigin();
            s.dashChargeAvailable = false;

            (s, _) = Movement.Step(s, new EffectiveInput(), t, Dt, w);
            Assert.AreEqual(MoveState.Falling, s.state);
            Assert.That(s.coyoteTimer, Is.GreaterThan(0f));

            EdgeFlags edges; (s, edges) = Movement.Step(s, new EffectiveInput { jumpPressed = true }, t, Dt, w);

            Assert.AreEqual(MoveState.Jumping, s.state);
            Assert.AreEqual(EdgeFlags.JumpedThisTick, edges);
            Assert.IsFalse(s.dashChargeAvailable);
        }

        [Test]
        public void Coyote_BeyondWindow_JumpDoesNotFire()
        {
            var t = DefaultTuning();
            var w = new ConfigurableGroundWorld { isGrounded = true };
            var s = new MovementState { state = MoveState.Grounded, facing = 1, dashChargeAvailable = true, posY = 1f };

            w.isGrounded = false;
            (s, _) = Movement.Step(s, new EffectiveInput(), t, Dt, w);

            int waitTicks = (int)System.Math.Ceiling(t.coyoteTime / Dt) + 2;
            for (int i = 0; i < waitTicks; i++)
                (s, _) = Movement.Step(s, new EffectiveInput(), t, Dt, w);
            Assert.AreEqual(0f, s.coyoteTimer);

            (s, _) = Movement.Step(s, new EffectiveInput { jumpPressed = true }, t, Dt, w);
            Assert.AreEqual(MoveState.Falling, s.state);
        }

        // ---- Jump buffer ----

        [Test]
        public void JumpBuffer_PressedMidFall_FiresOnLandingTick()
        {
            var t = DefaultTuning();
            var w = new ConfigurableGroundWorld { isGrounded = false };
            var s = new MovementState { state = MoveState.Falling, facing = 1, dashChargeAvailable = true, posY = 5f };

            (s, _) = Movement.Step(s, new EffectiveInput { jumpPressed = true }, t, Dt, w);
            Assert.That(s.jumpBufferTimer, Is.EqualTo(t.jumpBufferTime).Within(0.001f));

            for (int i = 0; i < 2; i++)
                (s, _) = Movement.Step(s, new EffectiveInput(), t, Dt, w);
            Assert.That(s.jumpBufferTimer, Is.GreaterThan(0f));

            w.isGrounded = true;
            (s, _) = Movement.Step(s, new EffectiveInput(), t, Dt, w);
            Assert.AreEqual(MoveState.Jumping, s.state);
        }

        [Test]
        public void JumpBuffer_AfterDash_LandingRefundsAndAllowsNextAirDash()
        {
            var t = DefaultTuning();
            var w = new ConfigurableGroundWorld { isGrounded = false };
            var s = new MovementState { state = MoveState.Falling, facing = 1, dashChargeAvailable = true, posY = 5f };

            EdgeFlags edges; (s, edges) = Movement.Step(s, new EffectiveInput { dashPressed = true }, t, Dt, w);
            Assert.AreEqual(MoveState.Dashing, s.state);
            Assert.AreEqual(EdgeFlags.DashedThisTick, edges);
            Assert.IsFalse(s.dashChargeAvailable);

            for (int i = 0; i < 30 && s.state == MoveState.Dashing; i++)
                (s, _) = Movement.Step(s, new EffectiveInput(), t, Dt, w);
            Assert.AreEqual(MoveState.Falling, s.state);
            Assert.IsFalse(s.dashChargeAvailable);

            (s, edges) = Movement.Step(s, new EffectiveInput { jumpPressed = true, jumpHeld = true }, t, Dt, w);
            Assert.AreEqual(MoveState.Falling, s.state);
            Assert.AreEqual(EdgeFlags.None, edges);
            Assert.That(s.jumpBufferTimer, Is.GreaterThan(Dt));

            float landingY = s.posY;
            w.isGrounded = true;
            (s, edges) = Movement.Step(s, new EffectiveInput { jumpHeld = true }, t, Dt, w);

            Assert.AreEqual(MoveState.Jumping, s.state);
            Assert.IsTrue(s.dashChargeAvailable);
            Assert.AreEqual(EdgeFlags.LandedThisTick | EdgeFlags.JumpedThisTick, edges);
            Assert.AreEqual(0f, s.jumpBufferTimer);
            Assert.That(s.velY, Is.EqualTo(t.jumpVelocity - t.gravity * Dt).Within(0.01f));
            Assert.That(s.posY, Is.GreaterThan(landingY));

            w.isGrounded = false;
            (s, edges) = Movement.Step(s, new EffectiveInput { dashPressed = true, jumpHeld = true }, t, Dt, w);
            Assert.AreEqual(MoveState.Dashing, s.state);
            Assert.AreEqual(EdgeFlags.DashedThisTick, edges);
            Assert.IsFalse(s.dashChargeAvailable);
        }

        // ---- Dash ----

        [Test]
        public void Dash_FreezeThenLaunch_TransitionsToFalling_AndMovesPlayer()
        {
            var t = DefaultTuning();
            var w = new FakeFlatGroundWorld();
            var s = GroundedAtOrigin();

            EdgeFlags edges; (s, edges) = Movement.Step(s, new EffectiveInput { dashPressed = true }, t, Dt, w);
            Assert.AreEqual(MoveState.Dashing, s.state);
            Assert.AreNotEqual(EdgeFlags.None, edges & EdgeFlags.DashedThisTick);
            Assert.AreEqual(t.dashFreezeTicks, s.freezeTicksRemaining);

            int ticks = 0;
            while (s.state == MoveState.Dashing && ticks < 30)
            {
                (s, _) = Movement.Step(s, new EffectiveInput(), t, Dt, w);
                ticks++;
            }
            Assert.AreEqual(MoveState.Falling, s.state);
            Assert.That(s.posX, Is.GreaterThan(2f), "player should have moved right via dash");
            Assert.IsFalse(s.dashChargeAvailable);
        }

        [Test]
        public void DashCharge_RefundedOnLanding()
        {
            var t = DefaultTuning();
            var w = new ConfigurableGroundWorld { isGrounded = false };
            var s = new MovementState { state = MoveState.Falling, facing = 1, dashChargeAvailable = false, posY = 5f };

            (s, _) = Movement.Step(s, new EffectiveInput(), t, Dt, w);
            Assert.IsFalse(s.dashChargeAvailable);

            w.isGrounded = true;
            EdgeFlags edges; (s, edges) = Movement.Step(s, new EffectiveInput(), t, Dt, w);
            Assert.AreEqual(MoveState.Grounded, s.state);
            Assert.IsTrue(s.dashChargeAvailable);
            Assert.AreNotEqual(EdgeFlags.None, edges & EdgeFlags.LandedThisTick);

            (s, edges) = Movement.Step(s, new EffectiveInput(), t, Dt, w);
            Assert.AreEqual(MoveState.Grounded, s.state);
            Assert.IsTrue(s.dashChargeAvailable);
            Assert.AreEqual(EdgeFlags.None, edges);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Dash_OnLanding_UsesRefundBeforeBufferedJump(bool bufferJump)
        {
            var t = DefaultTuning();
            var w = new ConfigurableGroundWorld { isGrounded = false };
            var s = new MovementState { state = MoveState.Falling, facing = 1, dashChargeAvailable = false, posY = 5f };

            (s, _) = Movement.Step(s, new EffectiveInput { jumpPressed = bufferJump, jumpHeld = bufferJump }, t, Dt, w);
            Assert.AreEqual(MoveState.Falling, s.state);
            Assert.IsFalse(s.dashChargeAvailable);
            if (bufferJump) Assert.That(s.jumpBufferTimer, Is.GreaterThan(Dt));

            w.isGrounded = true;
            EdgeFlags edges; (s, edges) = Movement.Step(s, new EffectiveInput { dashPressed = true, jumpHeld = bufferJump }, t, Dt, w);

            Assert.AreEqual(MoveState.Dashing, s.state);
            Assert.AreEqual(EdgeFlags.LandedThisTick | EdgeFlags.DashedThisTick, edges);
            Assert.IsFalse(s.dashChargeAvailable);
        }

        // ---- Fall limit ----

        [Test]
        public void FallLimit_EmitsDiedEdge_AndMarksDead()
        {
            var t = DefaultTuning();
            var w = new FakeFlatGroundWorld();
            var s = new MovementState { state = MoveState.Falling, facing = 1, posY = -25f };

            EdgeFlags edges; (s, edges) = Movement.Step(s, new EffectiveInput(), t, Dt, w);

            Assert.AreNotEqual(EdgeFlags.None, edges & EdgeFlags.DiedThisTick);
            Assert.IsTrue(s.isDead);
        }

        // ---- Resume from arbitrary state ----

        [Test]
        public void ResumeMidJump_CutFiresOnRelease()
        {
            var t = DefaultTuning();
            var w = new FakeFlatGroundWorld();
            var s = new MovementState { state = MoveState.Jumping, facing = 1, velY = 8f, wasJumpHeld = true, posY = 6f };

            EdgeFlags edges; (s, edges) = Movement.Step(s, new EffectiveInput { jumpHeld = false }, t, Dt, w);

            Assert.AreNotEqual(EdgeFlags.None, edges & EdgeFlags.JumpCutThisTick);
        }

        [Test]
        public void ResumeMidDashFreeze_LaunchesOnFreezeEndTick()
        {
            var t = DefaultTuning();
            var w = new FakeFlatGroundWorld();
            var s = new MovementState
            {
                state                = MoveState.Dashing,
                facing               = 1,
                freezeTicksRemaining = 1,
                dashTimer            = t.dashDuration,
                posY                 = 5f,
            };

            (s, _) = Movement.Step(s, new EffectiveInput(), t, Dt, w);

            Assert.AreEqual(0, s.freezeTicksRemaining);
            Assert.That(s.velX, Is.EqualTo(s.facing * t.dashDistance / t.dashDuration).Within(0.01f));
        }

        // ---- Collision world cases ----

        [Test]
        public void WallStop_RunningRight_ClampsAtWall_AndZeroesVelX()
        {
            var t = DefaultTuning();
            var w = new FakeWallOnRight();
            var s = new MovementState { state = MoveState.Grounded, facing = 1, dashChargeAvailable = true, posY = 1f, posX = 4.4f };
            var input = new EffectiveInput { moveDir = 1 };

            for (int i = 0; i < 10; i++)
                (s, _) = Movement.Step(s, input, t, Dt, w);

            Assert.That(s.posX, Is.LessThan(4.51f), "should clamp at wall - halfWidth - kSkin");
            Assert.That(s.velX, Is.EqualTo(0f).Within(0.01f));
        }

        [Test]
        public void CeilingBonk_JumpingUp_ClampsHeadAtCeiling()
        {
            var t = DefaultTuning();
            var w = new FakeCeilingAbove();
            var s = new MovementState { state = MoveState.Jumping, facing = 1, velY = 20f, wasJumpHeld = true, posY = 2f };

            for (int i = 0; i < 5; i++)
                (s, _) = Movement.Step(s, new EffectiveInput { jumpHeld = true }, t, Dt, w);

            Assert.That(s.posY, Is.LessThanOrEqualTo(3.01f), "head should clamp at ceilingY=4");
        }

        [Test]
        public void CornerStick_AirborneIntoCorner_LiftsOverInsteadOfSticking()
        {
            var t = DefaultTuning();
            var w = new FakeCornerStick();
            // Wedged on the platform's top-left corner: bottom edge at the top (y=2), right edge at the face (x=5).
            var s = new MovementState { state = MoveState.Falling, facing = 1, posX = 4.5f, posY = 2.5f, velY = -5f };
            var input = new EffectiveInput { moveDir = 1 };

            (s, _) = Movement.Step(s, input, t, Dt, w);

            Assert.That(s.posX, Is.GreaterThan(4.51f), "corner correction should advance X, not stick at 4.5");
            Assert.That(s.posY, Is.GreaterThan(2.5f),  "corner correction should lift the body over the corner");
        }

        [Test]
        public void CornerCorrect_DoesNotLiftWhenSlidingDownAFullWall()
        {
            // Airborne against a full-height wall with the floor far below: SweepX blocks but SweepY does
            // not (free fall), so the both-axes gate never trips and the body slides down, no phantom lift.
            var t = DefaultTuning();
            var w = new FakeWallOnRight();
            var s = new MovementState { state = MoveState.Falling, facing = 1, posX = 4.5f, posY = 6f, velY = -5f };
            var input = new EffectiveInput { moveDir = 1 };
            float startX = s.posX;

            (s, _) = Movement.Step(s, input, t, Dt, w);

            Assert.That(s.posY, Is.LessThan(6f), "should keep falling along the wall");
            Assert.That(s.posX, Is.LessThanOrEqualTo(startX + 0.001f), "should not be lifted/advanced past the wall");
        }

        [Test]
        public void OneTileGap_PlayerCenterInGap_FallsThrough()
        {
            var t = DefaultTuning();
            var w = new FakeOneTileGap();
            var s = new MovementState { state = MoveState.Falling, facing = 1, posY = 3f, posX = 3.5f };

            (s, _) = Movement.Step(s, new EffectiveInput(), t, Dt, w);

            Assert.AreEqual(MoveState.Falling, s.state);
            Assert.That(s.posY, Is.LessThan(3f), "should keep falling — gap has no floor");
        }

        // ---- Helpers ----

        static void AssertStateEqual(MovementState a, MovementState b)
        {
            Assert.AreEqual(a.posX,                 b.posX,                 "posX");
            Assert.AreEqual(a.posY,                 b.posY,                 "posY");
            Assert.AreEqual(a.velX,                 b.velX,                 "velX");
            Assert.AreEqual(a.velY,                 b.velY,                 "velY");
            Assert.AreEqual(a.state,                b.state,                "state");
            Assert.AreEqual(a.facing,               b.facing,               "facing");
            Assert.AreEqual(a.coyoteTimer,          b.coyoteTimer,          "coyoteTimer");
            Assert.AreEqual(a.jumpBufferTimer,      b.jumpBufferTimer,      "jumpBufferTimer");
            Assert.AreEqual(a.dashTimer,            b.dashTimer,            "dashTimer");
            Assert.AreEqual(a.invulnTimer,          b.invulnTimer,          "invulnTimer");
            Assert.AreEqual(a.freezeTicksRemaining, b.freezeTicksRemaining, "freezeTicksRemaining");
            Assert.AreEqual(a.dashChargeAvailable,  b.dashChargeAvailable,  "dashChargeAvailable");
            Assert.AreEqual(a.wasJumpHeld,          b.wasJumpHeld,          "wasJumpHeld");
            Assert.AreEqual(a.isDead,               b.isDead,               "isDead");
        }
    }
}
