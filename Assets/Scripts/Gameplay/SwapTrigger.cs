using System;
using System.Collections.Generic;
using UnityEngine;
using JumpNowBro.Util;

namespace JumpNowBro.Gameplay
{
    /// What the host emits when the character crosses a swap volume — the action to flip and a stable
    /// per-level id so the SWAP EVENT can tell the client which banner to telegraph.
    public readonly struct SwapRequest
    {
        public readonly PlayerAction action;
        public readonly byte triggerId;
        public SwapRequest(PlayerAction action, byte triggerId) { this.action = action; this.triggerId = triggerId; }
    }

    [RequireComponent(typeof(Collider2D))]
    public class SwapTrigger : MonoBehaviour
    {
        static readonly List<SwapTrigger> active = new List<SwapTrigger>();
        static Sprite quadSprite;

        /// Raised on the host when the character enters the volume. The swap scheduler (Networking) composes
        /// the new map, picks an apply tick, and sends the reliable SWAP EVENT. Static so it crosses the
        /// asmdef boundary without Gameplay referencing Networking; the single subscriber unsubscribes on destroy.
        public static event Action<SwapRequest> OnSwapRequested;

        [SerializeField] PlayerAction actionToSwap;
        [SerializeField] byte triggerId;              // unique per level; carried in the SWAP EVENT for client banner targeting
        [SerializeField] bool showBanner = true;
        [SerializeField] float armedAlpha = 0.55f;
        [SerializeField] Color firedColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
        [SerializeField] int bannerSortingOrder = 1;

        // #127 proximity pulse: nudge a gentle screen pulse when the character is within a few body-lengths of an
        // ARMED trigger (no in-world text; the pulse + the at-swap announcement carry the message).
        const float ProximityFar = 4.5f;   // units (~a few body-lengths): pulse begins, faint
        const float ProximityNear = 1.0f;  // units: pulse at full (still gentle) strength

        bool fired;
        bool firedAtCheckpoint;
        bool bannerArmed;
        SpriteRenderer banner;
        Collider2D col;
        static Transform cachedPlayer;     // the one rendered character (host or client); re-resolved if destroyed

        void OnEnable() => active.Add(this);
        void OnDisable() => active.Remove(this);

        void Start()
        {
            col = GetComponent<Collider2D>();
            if (showBanner) AcquireBanner();
            SetBannerArmed(!fired);
        }

        /// Nudge a gentle screen pulse while ARMED and the character is within a few body-lengths. Gated on the
        /// banner's armed state (not `fired`): the host marks `fired` on entry, but the banner stays armed until
        /// GreyById at the apply tick, so host + client pulse alike through the lead window and stop together.
        /// The in-world banner keeps its static armed look; the only cue is the screen pulse (+ the at-swap
        /// announcement). No in-world text (#127, revised).
        void Update()
        {
            if (!bannerArmed || col == null) return;
            var player = LocalPlayer();
            if (player == null) return;
            float t = Mathf.InverseLerp(ProximityFar, ProximityNear, Vector2.Distance(player.position, (Vector2)col.bounds.center));
            if (t > 0.001f) GameHudOverlay.Instance?.ReportProximity(t, actionToSwap);
        }

        static Transform LocalPlayer()
        {
            if (cachedPlayer == null)
            {
                var go = GameObject.FindWithTag("Player");
                if (go != null) cachedPlayer = go.transform;
            }
            return cachedPlayer;
        }

        // Checkpoint locks in which triggers have fired up to this point.
        public static void SaveCheckpointStates()
        {
            foreach (var t in active) t.firedAtCheckpoint = t.fired;
        }

        // Respawn: triggers crossed since the checkpoint re-arm; ones crossed before it stay consumed.
        public static void RestoreCheckpointStates()
        {
            foreach (var t in active)
            {
                t.fired = t.firedAtCheckpoint;
                t.SetBannerArmed(!t.fired);
            }
        }

        // Grey the banner of the trigger(s) with this id, at the moment the scheduled swap applies. Drives the
        // visual on BOTH ends (host + client) off the scheduler so the banner greys on the exact apply tick,
        // not on physical entry — the v1.6 EVENT-driven replacement for the old "visual above the gate" hack.
        public static void GreyById(byte id)
        {
            foreach (var t in active)
                if (t.triggerId == id) { t.fired = true; t.SetBannerArmed(false); }
        }

        /// Reconcile every banner to an authoritative ControlMap — grey iff that action no longer belongs to its
        /// default owner (P1), armed otherwise. The client drives its banners off this (against the seeding STATE
        /// map and the DEATH-EVENT checkpoint map) instead of per-instance `fired`, which a scene reload re-arms
        /// wrongly (#105) and a respawn leaves stuck grey (#111). Correct while each action swaps at most once per
        /// level (the triggerId convention); a future double-swap level would need the scheduler's applied-list.
        public static void ReconcileBannersTo(ControlMap map)
        {
            foreach (var t in active)
            {
                bool swapped = OwnerFor(map, t.actionToSwap) != InputOwner.P1;
                t.fired = swapped;
                t.SetBannerArmed(!swapped);
            }
        }

        static InputOwner OwnerFor(ControlMap map, PlayerAction action) => action switch
        {
            PlayerAction.MoveHorizontal => map.moveOwner,
            PlayerAction.Jump           => map.jumpOwner,
            PlayerAction.Dash           => map.dashOwner,
            _                           => InputOwner.P1,
        };

        void OnTriggerEnter2D(Collider2D other)
        {
            if (fired) return;                      // already scheduled/consumed — a re-cross must not double-schedule
            // Detect Player via the "Player" tag, not TryGetComponent<PlayerController> — the client's role-aware
            // spawner destroys PlayerController, so the component check would always fail there.
            if (!other.CompareTag("Player")) return;

            // Host originates swaps; the client learns of them via the SWAP EVENT and greys its banner through
            // GreyById at the apply tick. Gate at the top now that the banner is EVENT-driven, not entry-driven.
            if (!Authority.IsHost) return;

            // Mark scheduled immediately so a re-cross during the lead window can't enqueue a second swap. The
            // banner stays armed (the telegraph) until GreyById greys it at the apply tick.
            fired = true;
            OnSwapRequested?.Invoke(new SwapRequest(actionToSwap, triggerId));
        }

        void AcquireBanner()
        {
            // Prefer a sprite the level author already placed on the trigger so we
            // recolor that instead of stacking a second quad on top of it.
            banner = GetComponentInChildren<SpriteRenderer>();
            if (banner == null) BuildBanner();
        }

        void BuildBanner()
        {
            var col = GetComponent<Collider2D>();
            var go = new GameObject("SwapBanner");
            banner = go.AddComponent<SpriteRenderer>();
            banner.sprite = QuadSprite();
            banner.sortingOrder = bannerSortingOrder;

            if (col is BoxCollider2D box)
            {
                go.transform.SetParent(transform, worldPositionStays: false);
                go.transform.localPosition = box.offset;
                go.transform.localScale = new Vector3(box.size.x, box.size.y, 1f);
            }
            else
            {
                // Non-box colliders: size from world bounds, parented in world space.
                var b = col.bounds;
                go.transform.SetParent(transform, worldPositionStays: true);
                go.transform.position = new Vector3(b.center.x, b.center.y, transform.position.z);
                go.transform.localScale = new Vector3(b.size.x, b.size.y, 1f);
            }
        }

        /// #136: re-tint every banner from the (possibly switched) ActionStyle palette. Banner colour is
        /// applied only in SetBannerArmed, so re-calling it with the current armed state is a full refresh.
        public static void RetintAll()
        {
            foreach (var t in active) t.SetBannerArmed(t.bannerArmed);
        }

        void SetBannerArmed(bool armed)
        {
            bannerArmed = armed;                 // gates the #127 telegraph (proximity vignette)
            if (banner == null) return;
            banner.color = armed ? ColorFor(actionToSwap, armedAlpha) : firedColor;
        }

        static Color ColorFor(PlayerAction action, float alpha)
        {
            Color c = ActionStyle.ColorOf(action);   // single source of action colour (shared with HUD + announcement)
            c.a = alpha;
            return c;
        }

        static Sprite QuadSprite()
        {
            if (quadSprite == null)
            {
                var tex = Texture2D.whiteTexture;
                quadSprite = Sprite.Create(
                    tex, new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), tex.width);
            }
            return quadSprite;
        }
    }
}
