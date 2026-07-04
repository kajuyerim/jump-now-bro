using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using JumpNowBro.Util;

namespace JumpNowBro.Gameplay
{
    /// Persistent screen-space overlay for the v2.1 swap-coordination cues: the swap announcement (#115),
    /// the screen-edge flash (#126), and the prep-telegraph label (#127). Self-spawns before the first scene
    /// (like SwapScheduleDriver / AudioManager) and builds its own ScreenSpaceOverlay canvas at sortingOrder 50
    /// (above the gameplay HUD at 0, below the main menu at 100), so it survives level loads with no scene wiring.
    public sealed class GameHudOverlay : MonoBehaviour
    {
        public static GameHudOverlay Instance { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void AutoSpawn()
        {
            if (Instance == null)
                new GameObject(nameof(GameHudOverlay)).AddComponent<GameHudOverlay>();
        }

        // #115 announcement
        TMP_Text announceLabel;
        CanvasGroup announceGroup;
        Coroutine announceRoutine;

        // #126 edge flash
        CanvasGroup flashGroup;
        Image flashLeftCore, flashRightCore;
        Image flashIconL, flashIconR;              // #136: per-action icon, the non-colour channel on the flash
        Coroutine flashRoutine;
        static Sprite edgeGradientL, edgeGradientR;

        // #127 proximity pulse (a gentle screen vignette in the upcoming action's colour when very near an armed trigger)
        CanvasGroup proximityGroup;
        Image proximityImage;
        Image proximityIcon;                       // #136: action icon, OWN alpha (the vignette group caps at 0.4, too faint for a glyph)
        static Sprite vignetteSprite;
        float proximityBestT;
        bool proximityReported;
        const float ProximityMaxAlpha = 0.4f;
        const float ProximityIconMaxAlpha = 0.8f;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            BuildCanvas();
            BuildAnnouncement();
            BuildEdgeFlash();
            BuildProximity();
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        // Drive the proximity vignette from the nearest armed trigger that reported this frame: brightness scales
        // with closeness (fades in as the character nears, out as it leaves), no pulsing. Zero when none report.
        void LateUpdate()
        {
            if (proximityGroup == null) return;
            proximityGroup.alpha = proximityReported ? proximityBestT * ProximityMaxAlpha : 0f;
            if (proximityIcon != null)
            {
                var c = proximityIcon.color;
                c.a = proximityReported ? proximityBestT * ProximityIconMaxAlpha : 0f;
                proximityIcon.color = c;
            }
            proximityReported = false;
            proximityBestT = 0f;
        }

        void BuildCanvas()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;
            // No GraphicRaycaster: the overlay is purely presentational and must never eat clicks.
        }

        void BuildAnnouncement()
        {
            var go = new GameObject("SwapAnnounce", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            announceLabel = go.AddComponent<TextMeshProUGUI>();
            announceLabel.alignment = TextAlignmentOptions.Center;
            announceLabel.enableAutoSizing = false;
            announceLabel.fontSize = 46;
            announceLabel.fontStyle = FontStyles.Bold;
            announceLabel.color = Color.white;
            announceLabel.raycastTarget = false;
            if (TMP_Settings.defaultFontAsset != null) announceLabel.font = TMP_Settings.defaultFontAsset;

            var rt = announceLabel.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0f, 110f);            // slightly above centre (#115)
            rt.sizeDelta = new Vector2(1000f, 90f);

            ApplySoftShadow(announceLabel);
            announceGroup = go.AddComponent<CanvasGroup>();
            announceGroup.alpha = 0f;
        }

        /// "JUMP -> Name": action word in its action colour, the name in that player's colour, arrow white.
        /// Identical text on both ends (the SwapScheduleDriver apply loop runs the same map on host + client
        /// at the shared tick).
        public void Announce(PlayerAction action, InputOwner newOwner)
        {
            if (announceLabel == null) return;
            string nameHex = ColorUtility.ToHtmlStringRGB(PlayerIdentity.ColorOf(newOwner));
            announceLabel.text = $"{ActionStyle.RichLabel(action)} → <color=#{nameHex}>{PlayerIdentity.NameOf(newOwner)}</color>";
            if (announceRoutine != null) StopCoroutine(announceRoutine);
            announceRoutine = StartCoroutine(AnnounceRoutine());     // newest-replaces
        }

        IEnumerator AnnounceRoutine()
        {
            const float inT = 0.12f, hold = 0.8f, outT = 0.35f;
            var rt = announceLabel.rectTransform;

            // Snappy in: alpha 0->1 (ease-out), scale 0.85->1.0 with a slight overshoot (ease-out-back).
            for (float t = 0f; t < inT; t += Time.unscaledDeltaTime)
            {
                float k = t / inT;
                announceGroup.alpha = 1f - (1f - k) * (1f - k);
                rt.localScale = Vector3.one * Mathf.LerpUnclamped(0.85f, 1f, EaseOutBack(k));
                yield return null;
            }
            announceGroup.alpha = 1f;
            rt.localScale = Vector3.one;

            for (float t = 0f; t < hold; t += Time.unscaledDeltaTime) yield return null;

            // Soft out: alpha 1->0.
            for (float t = 0f; t < outT; t += Time.unscaledDeltaTime)
            {
                float k = t / outT;
                announceGroup.alpha = 1f - k * k;       // ease-in fade
                yield return null;
            }
            announceGroup.alpha = 0f;
            announceRoutine = null;
        }

        static float EaseOutBack(float k)
        {
            const float c1 = 1.70158f, c3 = c1 + 1f;
            float p = k - 1f;
            return 1f + c3 * p * p * p + c1 * p * p;
        }

        static void ApplySoftShadow(TMP_Text t)
        {
            // Per-label material instance (fontMaterial) with a soft underlay = drop shadow, no backplate.
            var mat = t.fontMaterial;
            mat.EnableKeyword("UNDERLAY_ON");
            mat.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.75f));
            mat.SetFloat("_UnderlayOffsetX", 0.5f);
            mat.SetFloat("_UnderlayOffsetY", -0.5f);
            mat.SetFloat("_UnderlayDilate", 0.1f);
            mat.SetFloat("_UnderlaySoftness", 0.25f);
        }

        // ---- #126 edge flash --------------------------------------------------------------------------

        void BuildEdgeFlash()
        {
            var root = new GameObject("EdgeFlash", typeof(RectTransform));
            root.transform.SetParent(transform, false);
            var rrt = root.GetComponent<RectTransform>();
            rrt.anchorMin = Vector2.zero;
            rrt.anchorMax = Vector2.one;
            rrt.offsetMin = rrt.offsetMax = Vector2.zero;
            flashGroup = root.AddComponent<CanvasGroup>();
            flashGroup.alpha = 0f;
            flashGroup.blocksRaycasts = false;
            flashGroup.interactable = false;

            // Left+right only (#126), in the action's colour. The flash only ever shows on the screen of the
            // player the change affects, so the action colour alone identifies it; no player-colour rim needed.
            flashLeftCore  = MakeEdgeStrip(root.transform, leftSide: true,  width: 200f);
            flashRightCore = MakeEdgeStrip(root.transform, leftSide: false, width: 200f);

            // #136: the flash was colour-only — add the action's icon at each edge's vertical centre. Children
            // of the flash root, so the existing CanvasGroup animates them with the strips for free.
            flashIconL = MakeEdgeIcon(root.transform, leftSide: true);
            flashIconR = MakeEdgeIcon(root.transform, leftSide: false);
        }

        static Image MakeEdgeIcon(Transform parent, bool leftSide)
        {
            var go = new GameObject(leftSide ? "EdgeIconL" : "EdgeIconR", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = Color.white;
            img.raycastTarget = false;
            img.preserveAspect = true;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(leftSide ? 0f : 1f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(leftSide ? 48f : -48f, 0f);
            rt.sizeDelta = new Vector2(64f, 64f);
            return img;
        }

        // A side-anchored full-height strip using the edge-gradient sprite (opaque at the screen edge, fading inward).
        static Image MakeEdgeStrip(Transform parent, bool leftSide, float width)
        {
            var go = new GameObject(leftSide ? "EdgeStripL" : "EdgeStripR", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.sprite = EdgeGradientSprite(opaqueAtLeft: leftSide);   // opaque side hugs this screen edge
            img.raycastTarget = false;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(leftSide ? 0f : 1f, 0f);
            rt.anchorMax = new Vector2(leftSide ? 0f : 1f, 1f);
            rt.pivot = new Vector2(leftSide ? 0f : 1f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(width, 0f);                 // full height (top/bottom anchored), fixed width
            return img;
        }

        // White horizontal alpha ramp: opaque at one edge, transparent inward, eased for a soft glow. Cached per side
        // (the right side needs a genuinely mirrored sprite, not a transform flip, or its rect lands off-screen).
        static Sprite EdgeGradientSprite(bool opaqueAtLeft)
        {
            if (opaqueAtLeft && edgeGradientL != null) return edgeGradientL;
            if (!opaqueAtLeft && edgeGradientR != null) return edgeGradientR;

            const int w = 64, h = 4;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[w * h];
            for (int x = 0; x < w; x++)
            {
                float t = (float)x / (w - 1);
                float a = opaqueAtLeft ? 1f - t : t;
                a *= a;                                              // ease the falloff
                for (int y = 0; y < h; y++) px[y * w + x] = new Color(1f, 1f, 1f, a);
            }
            tex.SetPixels(px);
            tex.Apply();
            var sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
            if (opaqueAtLeft) edgeGradientL = sprite; else edgeGradientR = sprite;
            return sprite;
        }

        /// Single bright pulse on the side edges in the action's colour. Only fired when THIS screen's player
        /// gains the action (#126); losing an action shows nothing.
        public void Flash(PlayerAction action)
        {
            if (flashGroup == null) return;
            flashLeftCore.color = flashRightCore.color = ActionStyle.ColorOf(action);
            var icon = ActionStyle.IconOf(action);                     // #136: icon = non-colour identity
            flashIconL.sprite = flashIconR.sprite = icon;
            flashIconL.enabled = flashIconR.enabled = icon != null;
            if (flashRoutine != null) StopCoroutine(flashRoutine);
            flashRoutine = StartCoroutine(FlashRoutine());
        }

        IEnumerator FlashRoutine()
        {
            const float peak = 0.85f, inT = 0.12f, outT = 0.30f;
            for (float t = 0f; t < inT; t += Time.unscaledDeltaTime)
            {
                float k = t / inT;
                flashGroup.alpha = Mathf.Lerp(0f, peak, 1f - (1f - k) * (1f - k));   // ease-out in
                yield return null;
            }
            flashGroup.alpha = peak;
            for (float t = 0f; t < outT; t += Time.unscaledDeltaTime)
            {
                float k = t / outT;
                flashGroup.alpha = Mathf.Lerp(peak, 0f, k);
                yield return null;
            }
            flashGroup.alpha = 0f;
            flashRoutine = null;
        }

        // ---- #127 proximity pulse ---------------------------------------------------------------------

        void BuildProximity()
        {
            var go = new GameObject("SwapProximity", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(transform, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;        // full screen
            proximityImage = go.GetComponent<Image>();
            proximityImage.sprite = VignetteSprite();
            proximityImage.raycastTarget = false;
            proximityGroup = go.AddComponent<CanvasGroup>();
            proximityGroup.alpha = 0f;
            proximityGroup.blocksRaycasts = false;
            proximityGroup.interactable = false;

            // #136: the upcoming action's icon, bottom-centre. Parented to the overlay root (NOT the vignette
            // group: its alpha caps at 0.4, too faint for a readable glyph) with its own alpha in LateUpdate.
            var icon = new GameObject("SwapProximityIcon", typeof(RectTransform), typeof(Image));
            icon.transform.SetParent(transform, false);
            proximityIcon = icon.GetComponent<Image>();
            proximityIcon.color = new Color(1f, 1f, 1f, 0f);
            proximityIcon.raycastTarget = false;
            proximityIcon.preserveAspect = true;
            proximityIcon.enabled = false;
            var irt = icon.GetComponent<RectTransform>();
            irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 0f);
            irt.pivot = new Vector2(0.5f, 0f);
            irt.anchoredPosition = new Vector2(0f, 36f);
            irt.sizeDelta = new Vector2(48f, 48f);
        }

        /// An armed SwapTrigger within a few body-lengths reports itself each frame: how close (t: 0 at the outer
        /// edge -> 1 very near) and which action is about to swap. The nearest reporter wins; LateUpdate drives a
        /// gentle breathing vignette in that action's colour, fading out when none report. No text (#127, revised).
        public void ReportProximity(float t, PlayerAction action)
        {
            if (proximityImage == null) return;
            if (proximityReported && t <= proximityBestT) return;
            proximityReported = true;
            proximityBestT = Mathf.Clamp01(t);
            proximityImage.color = ActionStyle.ColorOf(action);
            var icon = ActionStyle.IconOf(action);                     // #136: which action is coming, without colour
            proximityIcon.sprite = icon;
            proximityIcon.enabled = icon != null;
        }

        // Soft radial vignette: transparent centre, colour ramping in toward the screen edges/corners.
        static Sprite VignetteSprite()
        {
            if (vignetteSprite == null)
            {
                const int n = 128;
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                var px = new Color[n * n];
                Vector2 c = new Vector2((n - 1) * 0.5f, (n - 1) * 0.5f);
                float maxD = c.magnitude;
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x, y), c) / maxD;     // 0 centre -> 1 corner
                        float a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.5f, 1f, d));
                        px[y * n + x] = new Color(1f, 1f, 1f, a);
                    }
                tex.SetPixels(px);
                tex.Apply();
                vignetteSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
            }
            return vignetteSprite;
        }
    }
}
