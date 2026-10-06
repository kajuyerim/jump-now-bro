using NUnit.Framework;
using JumpNowBro.Util;

namespace JumpNowBro.Tests
{
    public class ControlMapTests
    {
        [Test]
        public void Default_HasAllActionsOnP1()
        {
            var map = ControlMap.Default;
            Assert.AreEqual(InputOwner.P1, map.moveOwner);
            Assert.AreEqual(InputOwner.P1, map.jumpOwner);
            Assert.AreEqual(InputOwner.P1, map.dashOwner);
        }

        [Test]
        public void WithSwap_DefaultMap_FlipsOnlyJumpOwner_WhenActionIsJump()
        {
            var result = ControlMap.WithSwap(ControlMap.Default, PlayerAction.Jump);
            Assert.AreEqual(InputOwner.P1, result.moveOwner);
            Assert.AreEqual(InputOwner.P2, result.jumpOwner);
            Assert.AreEqual(InputOwner.P1, result.dashOwner);
        }

        [Test]
        public void WithSwap_DoubleSwap_RestoresOriginal()
        {
            var original = ControlMap.Default;
            var swapped = ControlMap.WithSwap(original, PlayerAction.Jump);
            var back = ControlMap.WithSwap(swapped, PlayerAction.Jump);
            Assert.AreEqual(original.moveOwner, back.moveOwner);
            Assert.AreEqual(original.jumpOwner, back.jumpOwner);
            Assert.AreEqual(original.dashOwner, back.dashOwner);
        }

        [Test]
        public void WithSwap_DoesNotMutateInput()
        {
            var original = ControlMap.Default;
            _ = ControlMap.WithSwap(original, PlayerAction.Jump);
            Assert.AreEqual(InputOwner.P1, original.moveOwner);
            Assert.AreEqual(InputOwner.P1, original.jumpOwner);
            Assert.AreEqual(InputOwner.P1, original.dashOwner);
        }

        [Test]
        public void WithSwap_EachAction_FlipsCorrectOwner()
        {
            var moveSwap = ControlMap.WithSwap(ControlMap.Default, PlayerAction.MoveHorizontal);
            Assert.AreEqual(InputOwner.P2, moveSwap.moveOwner);
            Assert.AreEqual(InputOwner.P1, moveSwap.jumpOwner);
            Assert.AreEqual(InputOwner.P1, moveSwap.dashOwner);

            var dashSwap = ControlMap.WithSwap(ControlMap.Default, PlayerAction.Dash);
            Assert.AreEqual(InputOwner.P1, dashSwap.moveOwner);
            Assert.AreEqual(InputOwner.P1, dashSwap.jumpOwner);
            Assert.AreEqual(InputOwner.P2, dashSwap.dashOwner);
        }

        [Test]
        public void WithSwap_UnknownAction_Throws()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => ControlMap.WithSwap(ControlMap.Default, (PlayerAction)99));
        }

        // ---- Route ----

        static PlayerInputFrame Frame(bool left = false, bool right = false,
                                      bool jp = false, bool jh = false, bool dp = false) =>
            new PlayerInputFrame { moveLeft = left, moveRight = right,
                                   jumpPressed = jp, jumpHeld = jh, dashPressed = dp };

        [Test]
        public void Route_DefaultMap_TakesAllFromP1()
        {
            var p1 = Frame(right: true, jp: true, jh: true, dp: true);
            var p2 = Frame(left: true);                                  // p2 fully active but ignored
            var e = ControlMap.Route(ControlMap.Default, p1, p2);
            Assert.AreEqual(+1, e.moveDir);
            Assert.IsTrue(e.jumpPressed);
            Assert.IsTrue(e.jumpHeld);
            Assert.IsTrue(e.dashPressed);
        }

        [Test]
        public void Route_JumpSwapped_JumpFromP2_OthersFromP1()
        {
            var map = ControlMap.WithSwap(ControlMap.Default, PlayerAction.Jump);
            var p1 = Frame(right: true, dp: true);                       // owns move + dash
            var p2 = Frame(jp: true, jh: true);                          // owns jump
            var e = ControlMap.Route(map, p1, p2);
            Assert.AreEqual(+1, e.moveDir);
            Assert.IsTrue(e.jumpPressed);
            Assert.IsTrue(e.jumpHeld);
            Assert.IsTrue(e.dashPressed);
        }

        [Test]
        public void Route_MoveDir_LeftPress_NegativeOne() =>
            Assert.AreEqual(-1, ControlMap.Route(ControlMap.Default, Frame(left: true), default).moveDir);

        [Test]
        public void Route_MoveDir_BothPressed_Zero() =>
            Assert.AreEqual(0, ControlMap.Route(ControlMap.Default, Frame(left: true, right: true), default).moveDir);

        [Test]
        public void Route_MoveDir_NeitherPressed_Zero() =>
            Assert.AreEqual(0, ControlMap.Route(ControlMap.Default, default, default).moveDir);

        [Test]
        public void Route_AllActionsSwapped_AllFromP2()
        {
            var map = ControlMap.WithSwap(
                       ControlMap.WithSwap(
                        ControlMap.WithSwap(ControlMap.Default, PlayerAction.MoveHorizontal),
                                                                PlayerAction.Jump),
                                                                PlayerAction.Dash);
            var p1 = Frame(right: true, jp: true, dp: true);             // ignored entirely
            var p2 = Frame(left: true, jh: true);
            var e = ControlMap.Route(map, p1, p2);
            Assert.AreEqual(-1, e.moveDir);
            Assert.IsFalse(e.jumpPressed);
            Assert.IsTrue(e.jumpHeld);
            Assert.IsFalse(e.dashPressed);
        }
    }
}
