using System.Collections.Generic;
using UnityEngine;
using JumpNowBro.Util;

namespace JumpNowBro.Gameplay
{
    /// Physics2D-backed ICollisionWorld. `Rigidbody2D.Cast` casts the body's own collider at the query's
    /// position — self-hit-free (sidesteps queriesStartInColliders) and tracks any future collider
    /// resize automatically. ContactFilter2D{useTriggers=false} excludes Hazard/Checkpoint/Swap/Goal
    /// per-call, no global flag fight. Local kSkin absorbs Physics2D.defaultContactOffset (0.01 in
    /// this project) without mutating the global setting.
    ///
    /// Explicit origins let the X sweep use the resolved Y position and corner probes use their lift,
    /// without moving the live Rigidbody. Grounded overlaps a body-width box below the feet anchor.
    public sealed class UnityCollisionWorld : ICollisionWorld
    {
        readonly Rigidbody2D rb;
        readonly Transform groundCheckPoint;
        readonly LayerMask solidLayers;
        readonly float groundCheckRadius;   // legacy OverlapCircle radius; unused since the #101 feet box
        readonly List<RaycastHit2D> hitBuf = new List<RaycastHit2D>(4);
        readonly ContactFilter2D filter;
        const float kSkin = 0.01f;
        // Feet-box ground check (#101): wider than the old 0.15-radius circle so a body still grounds when
        // its center sits past a platform edge, but narrower than the 1-wide body so being flush against a
        // wall can't false-ground. Seated just below the feet, so it never overlaps the body's own collider.
        const float kGroundBoxWidth = 0.9f;
        const float kGroundBoxHeight = 0.1f;

        public UnityCollisionWorld(Rigidbody2D rb, LayerMask solidLayers,
                                   Transform groundCheckPoint, float groundCheckRadius)
        {
            this.rb = rb;
            this.solidLayers = solidLayers;
            this.groundCheckPoint = groundCheckPoint;
            this.groundCheckRadius = groundCheckRadius;
            filter = new ContactFilter2D
            {
                useTriggers = false,
                useLayerMask = true,
                layerMask = solidLayers,
            };
        }

        public bool Grounded(float x, float y)
        {
            var off = groundCheckPoint.localPosition;
            var center = new Vector2(x + off.x, y + off.y - kSkin - kGroundBoxHeight * 0.5f);
            return Physics2D.OverlapBox(center, new Vector2(kGroundBoxWidth, kGroundBoxHeight), 0f, solidLayers) != null;
        }

        public void SweepX(float x, float y, float dx, out float resolvedDx, out bool blocked)
        {
            if (dx == 0f) { resolvedDx = 0f; blocked = false; return; }
            var dir = dx > 0f ? Vector2.right : Vector2.left;
            int hits = rb.Cast(new Vector2(x, y), rb.rotation, dir, filter, hitBuf, Mathf.Abs(dx));
            if (hits > 0)
            {
                resolvedDx = dir.x * Mathf.Max(0f, NearestDistance(hits) - kSkin);
                blocked = true;
            }
            else { resolvedDx = dx; blocked = false; }
        }

        public void SweepY(float x, float y, float dy, out float resolvedDy, out bool blocked)
        {
            if (dy == 0f) { resolvedDy = 0f; blocked = false; return; }
            var dir = dy > 0f ? Vector2.up : Vector2.down;
            int hits = rb.Cast(new Vector2(x, y), rb.rotation, dir, filter, hitBuf, Mathf.Abs(dy));
            if (hits > 0)
            {
                resolvedDy = dir.y * Mathf.Max(0f, NearestDistance(hits) - kSkin);
                blocked = true;
            }
            else { resolvedDy = dy; blocked = false; }
        }

        float NearestDistance(int hits)
        {
            float distance = hitBuf[0].distance;
            for (int i = 1; i < hits; i++) distance = Mathf.Min(distance, hitBuf[i].distance);
            return distance;
        }
    }
}
