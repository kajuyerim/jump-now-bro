using System.Collections;
using TMPro;
using UnityEngine;

namespace JumpNowBro.Gameplay
{
    /// v2.5 comms speech bubble (#151): one shared bubble above the character (both players ARE the one
    /// character, so callouts read as its two brains talking). Plate + tail tint in the SENDER's player
    /// colour; the text stays near-black for contrast on every palette. Newest-replaces.
    ///
    /// Follows the player by re-resolving PlayerSpawner each LateUpdate instead of parenting: the client
    /// destroys PlayerController but keeps the GameObject, respawns teleport, and a parented bubble would
    /// inherit the squash-and-stretch scale. Holds its last anchor if the player vanishes mid-fade.
    /// World-space 3D TMP (no world canvas exists in the project, and none is needed for one label).
    public sealed class CalloutBubble : MonoBehaviour
    {
        static CalloutBubble instance;
        static Sprite plateSprite, tailSprite;

        const float OffsetY = 1.35f;                 // clears the 0.5-half-height slime plus stretch headroom
        const float PlateAlpha = 0.92f;

        SpriteRenderer plate, tail;
        TextMeshPro label;
        Coroutine routine;
        Vector3 anchor;
        float popScale = 1f;

        /// hold = seconds at full alpha before the fade; popScale > 1 gives the GO beat its bigger pop.
        public static void Show(string text, Color tint, float hold, float popScale = 1f)
        {
            if (instance == null) instance = Build();
            instance.Present(text, tint, hold, popScale);
        }

        /// Never lazily creates the bubble — ResetAll calls this on every level load and session teardown.
        public static void HideImmediate()
        {
            if (instance == null) return;
            if (instance.routine != null) { instance.StopCoroutine(instance.routine); instance.routine = null; }
            instance.gameObject.SetActive(false);
        }

        static CalloutBubble Build()
        {
            var go = new GameObject(nameof(CalloutBubble));
            DontDestroyOnLoad(go);
            var b = go.AddComponent<CalloutBubble>();

            var plateGo = new GameObject("Plate");
            plateGo.transform.SetParent(go.transform, false);
            plateGo.transform.localScale = new Vector3(1.1f, 0.9f, 1f);   // 2.2 x 0.9 world units
            b.plate = plateGo.AddComponent<SpriteRenderer>();
            b.plate.sprite = PlateSprite();
            b.plate.sortingOrder = 200;              // above every world sprite (level art tops out ~2)

            var tailGo = new GameObject("Tail");
            tailGo.transform.SetParent(go.transform, false);
            tailGo.transform.localPosition = new Vector3(0f, -0.5f, 0f);
            b.tail = tailGo.AddComponent<SpriteRenderer>();
            b.tail.sprite = TailSprite();
            b.tail.sortingOrder = 199;               // behind the plate so the overlap seam hides

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            b.label = textGo.AddComponent<TextMeshPro>();
            if (TMP_Settings.defaultFontAsset != null) b.label.font = TMP_Settings.defaultFontAsset;
            b.label.fontSize = 5f;
            b.label.fontStyle = FontStyles.Bold;
            b.label.alignment = TextAlignmentOptions.Center;
            b.label.rectTransform.sizeDelta = new Vector2(2.2f, 0.9f);
            var mr = textGo.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = 201;   // 3D TMP sorts against SpriteRenderers via its MeshRenderer

            go.SetActive(false);
            return b;
        }

        void Present(string text, Color tint, float hold, float pop)
        {
            anchor = ResolveAnchor(anchor);
            transform.position = anchor;             // land on the player BEFORE the first render
            gameObject.SetActive(true);
            label.text = text;
            label.color = new Color(0.09f, 0.09f, 0.12f);   // near-black: readable on every player colour
            plate.color = tint;
            tail.color = tint;
            popScale = pop;
            if (routine != null) StopCoroutine(routine);
            routine = StartCoroutine(PresentRoutine(hold));
        }

        IEnumerator PresentRoutine(float hold)
        {
            const float inTime = 0.1f, outTime = 0.3f;
            for (float t = 0f; t < inTime; t += Time.unscaledDeltaTime)
            {
                float k = t / inTime;
                float back = 1f + 0.35f * (1f - k) * Mathf.Sin(k * Mathf.PI);   // ease-out-back overshoot
                transform.localScale = Vector3.one * (popScale * Mathf.Lerp(0.6f, 1f, k) * back);
                SetAlpha(k);
                yield return null;
            }
            transform.localScale = Vector3.one * popScale;
            SetAlpha(1f);
            for (float t = 0f; t < hold; t += Time.unscaledDeltaTime) yield return null;
            for (float t = 0f; t < outTime; t += Time.unscaledDeltaTime)
            {
                SetAlpha(1f - t / outTime);
                yield return null;
            }
            routine = null;
            gameObject.SetActive(false);
        }

        void SetAlpha(float a)
        {
            var pc = plate.color; pc.a = a * PlateAlpha; plate.color = pc;
            var tc = tail.color;  tc.a = a * PlateAlpha; tail.color = tc;
            var lc = label.color; lc.a = a;              label.color = lc;
        }

        void LateUpdate()
        {
            anchor = ResolveAnchor(anchor);
            transform.position = anchor;
        }

        static Vector3 ResolveAnchor(Vector3 fallback)
        {
            var spawner = PlayerSpawner.Instance;
            var player = spawner != null ? spawner.CurrentPlayerInstance : null;
            return player != null ? player.transform.position + new Vector3(0f, OffsetY, 0f) : fallback;
        }

        // Runtime-generated white sprites, tinted per show (GameHudOverlay's vignette/gradient pattern).

        static Sprite PlateSprite()
        {
            if (plateSprite != null) return plateSprite;
            const int w = 64, h = 32, r = 10;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float dx = Mathf.Max(Mathf.Abs(x - (w - 1) * 0.5f) - ((w - 1) * 0.5f - r), 0f);
                    float dy = Mathf.Max(Mathf.Abs(y - (h - 1) * 0.5f) - ((h - 1) * 0.5f - r), 0f);
                    float a = Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy));   // rounded rect, 1px soft edge
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;
            plateSprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 32f);
            return plateSprite;
        }

        static Sprite TailSprite()
        {
            if (tailSprite != null) return tailSprite;
            const int w = 16, h = 12;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float half = (w * 0.5f) * (y / (float)(h - 1));   // apex at the bottom row
                    float a = Mathf.Abs(x - (w - 1) * 0.5f) <= half ? 1f : 0f;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;
            tailSprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 32f);
            return tailSprite;
        }
    }
}
