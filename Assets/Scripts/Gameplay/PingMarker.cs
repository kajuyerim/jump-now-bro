using UnityEngine;

namespace JumpNowBro.Gameplay
{
    /// v2.5 location ping (#153): a brief sender-coloured ring at a world spot, shown on both screens.
    /// Pop-in, gentle pulse, fade, self-destroy (GhostFade's self-contained pattern). Spawned scene-less
    /// (Bootstrap is the active scene and never unloads), so the spawner must destroy live markers on
    /// level load and session teardown — CommsController holds the refs and does.
    public sealed class PingMarker : MonoBehaviour
    {
        static Sprite ringSprite;

        const float Lifetime = 2.5f;
        const float PopTime = 0.15f;

        SpriteRenderer ring;
        float age;

        public static PingMarker Spawn(Vector2 pos, Color tint)
        {
            var go = new GameObject(nameof(PingMarker));
            go.transform.position = new Vector3(pos.x, pos.y, 0f);   // z 0 always — never the camera plane
            var m = go.AddComponent<PingMarker>();
            m.ring = go.AddComponent<SpriteRenderer>();
            m.ring.sprite = RingSprite();
            m.ring.color = tint;
            m.ring.sortingOrder = 150;                               // above level art, below the callout bubble (200)
            return m;
        }

        void Update()
        {
            age += Time.deltaTime;
            float k = age / Lifetime;
            if (k >= 1f) { Destroy(gameObject); return; }

            float scale;
            if (age < PopTime)
            {
                float t = age / PopTime;
                scale = Mathf.Lerp(0.4f, 1f, t) * (1f + 0.3f * (1f - t) * Mathf.Sin(t * Mathf.PI));
            }
            else
            {
                scale = 1f + 0.05f * Mathf.Sin((age - PopTime) * 6f);   // gentle pulse while it lives
            }
            transform.localScale = Vector3.one * scale;

            float a = k < 0.6f ? 1f : 1f - (k - 0.6f) / 0.4f;           // hold, then fade the last 40%
            var col = ring.color; col.a = a; ring.color = col;
        }

        // Runtime-generated white ring + centre dot, tinted per spawn (no art asset needed).
        static Sprite RingSprite()
        {
            if (ringSprite != null) return ringSprite;
            const int s = 64;
            const float c = (s - 1) * 0.5f;
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float band = Mathf.Clamp01(2.5f - Mathf.Abs(d - 26f));   // ~5px ring band, soft edges
                    float dotA = Mathf.Clamp01(7f - d);                      // filled centre dot
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Max(band, dotA * 0.9f)));
                }
            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;
            ringSprite = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 48f);
            return ringSprite;
        }
    }
}
