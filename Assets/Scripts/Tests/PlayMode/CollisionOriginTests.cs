using JumpNowBro.Gameplay;
using JumpNowBro.Util;
using NUnit.Framework;
using UnityEngine;

namespace JumpNowBro.Tests.PlayMode
{
    public class CollisionOriginTests
    {
        GameObject body, platform;
        Rigidbody2D rb;
        UnityCollisionWorld world;

        [SetUp]
        public void SetUp()
        {
            platform = new GameObject("CollisionOriginPlatform") { layer = 8 };
            platform.transform.position = new Vector3(1002f, 999.5f, 0f);
            platform.AddComponent<BoxCollider2D>().size = new Vector2(2f, 1f);
            body = new GameObject("CollisionOriginBody");
            rb = body.AddComponent<Rigidbody2D>();
            rb.bodyType = RigidbodyType2D.Kinematic;
            body.AddComponent<BoxCollider2D>().size = Vector2.one;
            var feet = new GameObject("Feet").transform;
            feet.SetParent(body.transform, false);
            feet.localPosition = new Vector3(0f, -0.5f, 0f);
            world = new UnityCollisionWorld(rb, 1 << 8, feet, 0.15f);
            rb.position = new Vector2(1000.495f, 1000.505f);
            Physics2D.SyncTransforms();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(body);
            Object.DestroyImmediate(platform);
        }

        [Test]
        public void Sweeps_UseQueryPosition_WithoutMovingTheBody()
        {
            var original = rb.position;
            world.SweepX(original.x, original.y, 0.15f, out _, out bool atCorner);
            Assert.IsTrue(atCorner, "The body starts wedged against the platform corner.");
            world.SweepX(original.x, original.y + 0.05f, 0.15f, out float dx, out bool aboveCorner);
            Assert.IsFalse(aboveCorner, "The X sweep must use the already-resolved Y position.");
            Assert.AreEqual(0.15f, dx);
            world.SweepY(1002f, 1001f, -1f, out float dy, out bool abovePlatform);
            Assert.IsTrue(abovePlatform);
            Assert.That(dy, Is.EqualTo(-0.49f).Within(0.02f));
            Assert.AreEqual(original, rb.position, "Queries must not move the live or predicted body.");
        }

        [Test]
        public void CornerCorrection_LiftsPastARealPhysicsCorner()
        {
            var original = rb.position;
            Assert.IsFalse(world.Grounded(original.x, original.y));
            var state = new MovementState {
                posX = original.x, posY = original.y, velY = -5f,
                state = MoveState.Falling, facing = 1
            };
            var tuning = new MovementTuning {
                runSpeed = 9f, airControlMultiplier = 1f, gravity = 50f, fallLimitY = -20f
            };
            var (next, _) = Movement.Step(state, new EffectiveInput { moveDir = 1 },
                tuning, 1f / 60f, world);
            Assert.That(next.posY, Is.GreaterThan(original.y), "The corner must lift, not stick.");
            Assert.That(next.posX, Is.GreaterThan(original.x + 0.1f));
            Assert.AreEqual(original, rb.position);
        }
    }
}
