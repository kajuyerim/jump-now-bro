using System;
using NUnit.Framework;
using JumpNowBro.Networking;
using JumpNowBro.Util;

namespace JumpNowBro.Tests
{
    public class MessagesTests
    {
        // ---------- PlayerInputFrame ----------

        [Test]
        public void PlayerInputFrame_AllFlags_RoundTrip()
        {
            var f = new PlayerInputFrame
            {
                moveLeft    = true,
                moveRight   = true,
                jumpPressed = true,
                jumpHeld    = true,
                dashPressed = true,
            };
            var roundtrip = PlayerInputFrame.Unpack(PlayerInputFrame.Pack(f));
            Assert.AreEqual(f.moveLeft,    roundtrip.moveLeft);
            Assert.AreEqual(f.moveRight,   roundtrip.moveRight);
            Assert.AreEqual(f.jumpPressed, roundtrip.jumpPressed);
            Assert.AreEqual(f.jumpHeld,    roundtrip.jumpHeld);
            Assert.AreEqual(f.dashPressed, roundtrip.dashPressed);
        }

        [Test]
        public void PlayerInputFrame_EmptyFrame_PacksAsZero()
        {
            Assert.AreEqual(0, PlayerInputFrame.Pack(default));
        }

        [Test]
        public void PlayerInputFrame_ReservedBitsIgnored_OnUnpack()
        {
            // Bits 5–7 are reserved; setting them must NOT influence the decoded flags.
            byte packed = (byte)(0b11100000 | (1 << 2));   // jumpPressed + all reserved bits
            var f = PlayerInputFrame.Unpack(packed);
            Assert.IsFalse(f.moveLeft);
            Assert.IsTrue(f.jumpPressed);
        }

        [Test]
        public void PlayerInputFrame_EveryBytePattern_IsValid()
        {
            for (int i = 0; i < 256; i++)
            {
                var f = PlayerInputFrame.Unpack((byte)i);
                // Re-pack and assert only the low 5 bits survive (reserved drop to zero).
                Assert.AreEqual((byte)(i & 0x1F), PlayerInputFrame.Pack(f), $"byte {i:X2}");
            }
        }

        // ---------- ControlMap ----------

        [Test]
        public void ControlMap_Default_RoundTrips()
        {
            var buf = new byte[ControlMap.PackedSize];
            int n = ControlMap.Pack(ControlMap.Default, buf);
            Assert.AreEqual(ControlMap.PackedSize, n);
            Assert.IsTrue(ControlMap.TryUnpack(buf, out var map));
            Assert.AreEqual(InputOwner.P1, map.moveOwner);
            Assert.AreEqual(InputOwner.P1, map.jumpOwner);
            Assert.AreEqual(InputOwner.P1, map.dashOwner);
        }

        [Test]
        public void ControlMap_Swapped_RoundTrips()
        {
            var swapped = ControlMap.WithSwap(ControlMap.Default, PlayerAction.Jump);
            var buf = new byte[ControlMap.PackedSize];
            ControlMap.Pack(swapped, buf);
            Assert.IsTrue(ControlMap.TryUnpack(buf, out var map));
            Assert.AreEqual(InputOwner.P1, map.moveOwner);
            Assert.AreEqual(InputOwner.P2, map.jumpOwner);
            Assert.AreEqual(InputOwner.P1, map.dashOwner);
        }

        [Test]
        public void ControlMap_OutOfRangeOwnerByte_RejectedAsMalformed()
        {
            Assert.IsFalse(ControlMap.TryUnpack(new byte[] { 0, 0, 7 }, out _));   // 7 not in {P1=0, P2=1}
        }

        [Test]
        public void ControlMap_TruncatedBuffer_Rejected()
        {
            Assert.IsFalse(ControlMap.TryUnpack(new byte[] { 0, 1 }, out _));      // need 3, got 2
        }

        // ---------- MovementState ----------

        static MovementState SampleState() => new MovementState
        {
            posX = 12.5f, posY = -3.25f, velX = 9f, velY = -16f,
            state = MoveState.Dashing, facing = -1,
            coyoteTimer = 0.08f, jumpBufferTimer = 0.0f, dashTimer = 0.12f, invulnTimer = 0.05f,
            freezeTicksRemaining = 3,
            dashChargeAvailable = false, wasJumpHeld = true, isDead = false,
        };

        [Test]
        public void MovementState_RoundTrip()
        {
            var s = SampleState();
            var buf = new byte[MovementState.PackedSize];
            int n = MovementState.Pack(s, buf);
            Assert.AreEqual(MovementState.PackedSize, n);
            Assert.IsTrue(MovementState.TryUnpack(buf, out var rt));
            Assert.AreEqual(s.posX, rt.posX);
            Assert.AreEqual(s.posY, rt.posY);
            Assert.AreEqual(s.velX, rt.velX);
            Assert.AreEqual(s.velY, rt.velY);
            Assert.AreEqual(s.state, rt.state);
            Assert.AreEqual(s.facing, rt.facing);
            Assert.AreEqual(s.coyoteTimer, rt.coyoteTimer);
            Assert.AreEqual(s.jumpBufferTimer, rt.jumpBufferTimer);
            Assert.AreEqual(s.dashTimer, rt.dashTimer);
            Assert.AreEqual(s.invulnTimer, rt.invulnTimer);
            Assert.AreEqual(s.freezeTicksRemaining, rt.freezeTicksRemaining);
            Assert.AreEqual(s.dashChargeAvailable, rt.dashChargeAvailable);
            Assert.AreEqual(s.wasJumpHeld, rt.wasJumpHeld);
            Assert.AreEqual(s.isDead, rt.isDead);
        }

        [Test]
        public void MovementState_AllFlags_RoundTrip()
        {
            var s = new MovementState
            {
                dashChargeAvailable = true, wasJumpHeld = true, isDead = true,
                facing = 1, state = MoveState.Grounded,
            };
            var buf = new byte[MovementState.PackedSize];
            MovementState.Pack(s, buf);
            Assert.IsTrue(MovementState.TryUnpack(buf, out var rt));
            Assert.IsTrue(rt.dashChargeAvailable);
            Assert.IsTrue(rt.wasJumpHeld);
            Assert.IsTrue(rt.isDead);
        }

        [Test]
        public void MovementState_TruncatedBelowMin_Rejected()
        {
            Assert.IsFalse(MovementState.TryUnpack(new byte[MovementState.MinPackedSize - 1], out _));
        }

        [Test]
        public void MovementState_AcceptsExactMinSize_PaddingMissing()
        {
            // Padding-tolerant reader: 36 bytes is the lower bound; trailing 10 zeros are optional.
            var s = SampleState();
            var full = new byte[MovementState.PackedSize];
            MovementState.Pack(s, full);
            var trimmed = full.AsSpan(0, MovementState.MinPackedSize);
            Assert.IsTrue(MovementState.TryUnpack(trimmed, out var rt));
            Assert.AreEqual(s.posX, rt.posX);
            Assert.AreEqual(s.isDead, rt.isDead);
        }

        [Test]
        public void MovementState_OutOfRangeMoveState_Rejected()
        {
            var buf = new byte[MovementState.PackedSize];
            MovementState.Pack(SampleState(), buf);
            buf[32] = 99;                                       // corrupt the MoveState byte (valid range 0..3)
            Assert.IsFalse(MovementState.TryUnpack(buf, out _));
        }

        // ---------- InputBody ----------

        [Test]
        public void InputBody_RoundTrip()
        {
            var frames = new byte[] {
                PlayerInputFrame.Pack(new PlayerInputFrame { jumpPressed = true }),
                PlayerInputFrame.Pack(new PlayerInputFrame { jumpHeld    = true }),
                PlayerInputFrame.Pack(new PlayerInputFrame { dashPressed = true }),
                PlayerInputFrame.Pack(new PlayerInputFrame { moveRight   = true }),
                PlayerInputFrame.Pack(new PlayerInputFrame { moveLeft    = true }),
                PlayerInputFrame.Pack(new PlayerInputFrame()),                       // all-zero
            };
            var buf = new byte[InputBody.HeaderSize + frames.Length];
            int n = InputBody.Write(buf, baseTick: 42u, frames);
            Assert.AreEqual(buf.Length, n);

            Assert.IsTrue(InputBody.TryRead(buf, out var baseTick, out var count, out var read));
            Assert.AreEqual(42u, baseTick);
            Assert.AreEqual(6, count);
            Assert.AreEqual(frames, read.ToArray());
        }

        [Test]
        public void InputBody_CountOutOfRange_OnRead_Rejected()
        {
            // Construct a packet that says count = 17 (> MaxFrameCount = 16).
            var bad = new byte[] { 0, 0, 0, 0, 17, /* … */ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
            Assert.IsFalse(InputBody.TryRead(bad, out _, out _, out _));
        }

        [Test]
        public void InputBody_CountZero_OnWrite_Throws()
        {
            Assert.Throws<ArgumentException>(() => InputBody.Write(new byte[5], 0u, ReadOnlySpan<byte>.Empty));
        }

        [Test]
        public void InputBody_TruncatedFrames_Rejected()
        {
            // header says count=4, but only 2 frame bytes follow
            var bad = new byte[] { 0, 0, 0, 5, 4, 0xAA, 0xBB };
            Assert.IsFalse(InputBody.TryRead(bad, out _, out _, out _));
        }

        // ---------- StateBody ----------

        [Test]
        public void StateBody_RoundTrip()
        {
            var body = new StateBody
            {
                snapshotTick           = 1_000_000u,
                lastConsumedClientTick = 999_980u,
                deathCount             = 7,
                sceneIndex             = 2,
                controlMap             = ControlMap.WithSwap(ControlMap.Default, PlayerAction.Dash),
                remoteInputFrame       = new PlayerInputFrame { jumpHeld = true, moveRight = true },
                movementState          = SampleState(),
            };
            var buf = new byte[StateBody.Size];
            int n = body.Write(buf);
            Assert.AreEqual(StateBody.Size, n);

            Assert.IsTrue(StateBody.TryRead(buf, out var rt));
            Assert.AreEqual(body.snapshotTick, rt.snapshotTick);
            Assert.AreEqual(body.lastConsumedClientTick, rt.lastConsumedClientTick);
            Assert.AreEqual(body.deathCount, rt.deathCount);
            Assert.AreEqual(body.sceneIndex, rt.sceneIndex);
            Assert.AreEqual(InputOwner.P1, rt.controlMap.moveOwner);
            Assert.AreEqual(InputOwner.P1, rt.controlMap.jumpOwner);
            Assert.AreEqual(InputOwner.P2, rt.controlMap.dashOwner);
            Assert.IsTrue(rt.remoteInputFrame.jumpHeld);
            Assert.IsTrue(rt.remoteInputFrame.moveRight);
            Assert.AreEqual(body.movementState.posX, rt.movementState.posX);
            Assert.AreEqual(body.movementState.facing, rt.movementState.facing);
        }

        [Test]
        public void StateBody_TruncatedBuffer_Rejected()
        {
            // Anything below MinPackedSize MovementState (36) plus the 15-byte prefix is malformed.
            Assert.IsFalse(StateBody.TryRead(new byte[15 + MovementState.MinPackedSize - 1], out _));
        }

        [Test]
        public void StateBody_MalformedControlMap_Rejected()
        {
            // sceneIndex byte (offset 10) is fine, controlMap byte at offset 11 is 9 (out of range).
            var buf = new byte[StateBody.Size];
            buf[11] = 9;                                     // moveOwner = 9 -> reject
            Assert.IsFalse(StateBody.TryRead(buf, out _));
        }

        // ---------- EventBody ----------

        [Test]
        public void EventBody_LevelLoad_RoundTrip()
        {
            var buf = new byte[EventBody.MaxSize];
            int n = EventBody.LevelLoad(2).Write(buf);
            Assert.AreEqual(2, n);                               // kind + sceneIndex
            Assert.IsTrue(EventBody.TryRead(buf.AsSpan(0, n), out var rt));
            Assert.AreEqual(EventKind.LevelLoad, rt.kind);
            Assert.AreEqual(2, rt.sceneIndex);
        }

        [Test]
        public void EventBody_LevelReady_RoundTrip()
        {
            var buf = new byte[EventBody.MaxSize];
            int n = EventBody.LevelReady(1).Write(buf);
            Assert.AreEqual(2, n);
            Assert.IsTrue(EventBody.TryRead(buf.AsSpan(0, n), out var rt));
            Assert.AreEqual(EventKind.LevelReady, rt.kind);
            Assert.AreEqual(1, rt.sceneIndex);
        }

        [Test]
        public void EventBody_Swap_RoundTrip()
        {
            var map = ControlMap.WithSwap(ControlMap.Default, PlayerAction.Dash);
            var buf = new byte[EventBody.MaxSize];
            int n = EventBody.Swap(applyTick: 1_234_567u, map, triggerId: 5).Write(buf);
            Assert.AreEqual(1 + 4 + ControlMap.PackedSize + 1, n);   // kind + applyTick + map + triggerId
            Assert.IsTrue(EventBody.TryRead(buf.AsSpan(0, n), out var rt));
            Assert.AreEqual(EventKind.Swap, rt.kind);
            Assert.AreEqual(1_234_567u, rt.tick);
            Assert.AreEqual(5, rt.triggerId);
            Assert.AreEqual(InputOwner.P1, rt.map.moveOwner);
            Assert.AreEqual(InputOwner.P1, rt.map.jumpOwner);
            Assert.AreEqual(InputOwner.P2, rt.map.dashOwner);
        }

        [Test]
        public void EventBody_Death_RoundTrip()
        {
            var checkpointMap = ControlMap.WithSwap(ControlMap.Default, PlayerAction.Jump);
            var buf = new byte[EventBody.MaxSize];
            int n = EventBody.Death(deathTick: 9_000u, checkpointMap).Write(buf);
            Assert.AreEqual(1 + 4 + ControlMap.PackedSize, n);  // kind + deathTick + map
            Assert.IsTrue(EventBody.TryRead(buf.AsSpan(0, n), out var rt));
            Assert.AreEqual(EventKind.Death, rt.kind);
            Assert.AreEqual(9_000u, rt.tick);
            Assert.AreEqual(InputOwner.P2, rt.map.jumpOwner);
        }

        [Test]
        public void EventBody_LobbyReady_RoundTrip()
        {
            var buf = new byte[EventBody.MaxSize];
            foreach (bool isReady in new[] { true, false })
            {
                int n = EventBody.LobbyReady(isReady).Write(buf);
                Assert.AreEqual(2, n);                           // kind + ready flag
                Assert.IsTrue(EventBody.TryRead(buf.AsSpan(0, n), out var rt));
                Assert.AreEqual(EventKind.LobbyReady, rt.kind);
                Assert.AreEqual(isReady ? 1 : 0, rt.ready);
            }
        }

        [Test]
        public void EventBody_LobbyReady_FlagOutOfRange_Rejected()
        {
            // A flag byte is strictly 0|1, matching the ControlMap owner-byte strictness.
            Assert.IsFalse(EventBody.TryRead(new byte[] { (byte)EventKind.LobbyReady, 2 }, out _));
        }

        [Test]
        public void EventBody_LobbyReadyTruncated_Rejected()
        {
            Assert.IsFalse(EventBody.TryRead(new byte[1] { (byte)EventKind.LobbyReady }, out _));
        }

        [Test]
        public void EventBody_LobbyState_RoundTrip()
        {
            var buf = new byte[EventBody.MaxSize];
            int n = EventBody.LobbyState(2).Write(buf);
            Assert.AreEqual(2, n);                               // kind + selected level
            Assert.IsTrue(EventBody.TryRead(buf.AsSpan(0, n), out var rt));
            Assert.AreEqual(EventKind.LobbyState, rt.kind);
            Assert.AreEqual(2, rt.sceneIndex);
        }

        [Test]
        public void EventBody_LobbyStateTruncated_Rejected()
        {
            Assert.IsFalse(EventBody.TryRead(new byte[1] { (byte)EventKind.LobbyState }, out _));
        }

        [Test]
        public void EventBody_MaxSize_StillBoundsAllVariants()
        {
            // RunSummary is the largest variant; every other kind must fit the shared send scratch.
            var buf = new byte[EventBody.MaxSize];
            Assert.AreEqual(EventBody.MaxSize, EventBody.RunSummary(0, default).Write(buf));
            Assert.LessOrEqual(EventBody.Swap(1u, ControlMap.Default, 1).Write(buf), EventBody.MaxSize);
            Assert.LessOrEqual(EventBody.Death(1u, ControlMap.Default).Write(buf), EventBody.MaxSize);
            Assert.LessOrEqual(EventBody.LobbyReady(true).Write(buf), EventBody.MaxSize);
            Assert.LessOrEqual(EventBody.LobbyState(0).Write(buf), EventBody.MaxSize);
        }

        [Test]
        public void EventBody_UnknownKind_Rejected()
        {
            // Kind = 99: outside the defined enum range.
            Assert.IsFalse(EventBody.TryRead(new byte[] { 99, 0 }, out _));
            // And the exact boundary: one past the last defined kind (RunSummary = 6).
            Assert.IsFalse(EventBody.TryRead(new byte[] { 7, 0 }, out _));
        }

        [Test]
        public void EventBody_RunSummary_RoundTrip()
        {
            var stats = new LevelRunStats { timeMs = 83_450, deaths = 3, swaps = 7, streakMs = 41_200 };
            var buf = new byte[EventBody.MaxSize];
            int n = EventBody.RunSummary(2, stats).Write(buf);
            Assert.AreEqual(EventBody.MaxSize, n);               // RunSummary is the largest variant
            Assert.IsTrue(EventBody.TryRead(buf.AsSpan(0, n), out var rt));
            Assert.AreEqual(EventKind.RunSummary, rt.kind);
            Assert.AreEqual(2, rt.sceneIndex);
            Assert.AreEqual(83_450u, rt.timeMs);
            Assert.AreEqual(3, rt.deaths);
            Assert.AreEqual(7, rt.swaps);
            Assert.AreEqual(41_200u, rt.streakMs);
        }

        [Test]
        public void EventBody_RunSummaryTruncated_Rejected()
        {
            var buf = new byte[EventBody.MaxSize];
            int n = EventBody.RunSummary(1, new LevelRunStats { timeMs = 1000 }).Write(buf);
            Assert.IsFalse(EventBody.TryRead(buf.AsSpan(0, n - 1), out _));   // 13 of 14 bytes
        }

        [Test]
        public void EventBody_RunSummary_ClampsAtWireBoundary()
        {
            // Tracker stats are ints; the body is u16/u32. Negatives floor at 0, counts cap at u16 max.
            var stats = new LevelRunStats { timeMs = -5, deaths = 70_000, swaps = -1, streakMs = -5 };
            var buf = new byte[EventBody.MaxSize];
            EventBody.RunSummary(0, stats).Write(buf);
            Assert.IsTrue(EventBody.TryRead(buf, out var rt));
            Assert.AreEqual(0u, rt.timeMs);
            Assert.AreEqual(ushort.MaxValue, rt.deaths);
            Assert.AreEqual(0, rt.swaps);
            Assert.AreEqual(0u, rt.streakMs);
        }

        [Test]
        public void EventBody_LevelLoadTruncated_Rejected()
        {
            Assert.IsFalse(EventBody.TryRead(new byte[1] { (byte)EventKind.LevelLoad }, out _));
        }

        [Test]
        public void EventBody_SwapTruncatedMap_Rejected()
        {
            // kind + full applyTick, but only 2 of the 3 map bytes follow.
            Assert.IsFalse(EventBody.TryRead(new byte[] { (byte)EventKind.Swap, 0, 0, 0, 1, 0, 0 }, out _));
        }

        [Test]
        public void EventBody_SwapMalformedMap_Rejected()
        {
            // map dashOwner byte = 7 (not in {P1=0, P2=1}) -> ControlMap.TryUnpack rejects.
            Assert.IsFalse(EventBody.TryRead(new byte[] { (byte)EventKind.Swap, 0, 0, 0, 1, 0, 0, 7, 5 }, out _));
        }

        // ---------- Fuzz: no wire reader throws on garbage ----------

        [Test]
        public void Fuzz_NoDeserializerThrows_OnRandomBytes()
        {
            var rng = new System.Random(12345);                // fixed seed for a reproducible count; the byte values are irrelevant (we assert only no-throw)
            Assert.DoesNotThrow(() =>
            {
                for (int iter = 0; iter < 5000; iter++)
                {
                    var buf = new byte[rng.Next(0, 80)];
                    rng.NextBytes(buf);
                    PacketHeader.TryRead(buf, out _);
                    InputBody.TryRead(buf, out _, out _, out _);
                    StateBody.TryRead(buf, out _);
                    EventBody.TryRead(buf, out _);
                    MovementState.TryUnpack(buf, out _);
                    ControlMap.TryUnpack(buf, out _);
                    Hello.TryRead(buf, out _);
                    Welcome.TryRead(buf, out _);
                    Goodbye.TryRead(buf, out _);
                    PlayerInputFrame.Unpack(buf.Length > 0 ? buf[0] : (byte)0);
                }
            });
        }
    }
}
