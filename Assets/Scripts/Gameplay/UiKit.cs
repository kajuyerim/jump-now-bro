using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

namespace JumpNowBro.Gameplay
{
    /// Shared UI helpers for code-built screens (#134+, first real consumer: the v2.3 LobbyUI). New screens
    /// use this; the existing MainMenuUI / SettingsPanel builders stay private to their classes (verified
    /// screens are not restructured). Shapes mirror the MainMenuUI conventions.
    public static class UiKit
    {
        /// Keep gamepad/keyboard navigation alive: UGUI needs a selected element for Navigate to work,
        /// and a mouse click on empty space (or a destroyed object) can strand pad users with nothing
        /// selected. Call each frame while a screen is up; re-selects the fallback when selection is
        /// null or inactive.
        public static void EnsureSelection(GameObject fallback)
        {
            var es = EventSystem.current;
            if (es == null || fallback == null || !fallback.activeInHierarchy) return;
            var cur = es.currentSelectedGameObject;
            if (cur == null || !cur.activeInHierarchy) es.SetSelectedGameObject(fallback);
        }

        public static GameObject Panel(Transform parent, Color color)
        {
            var go = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            return go;
        }

        public static void Stretch(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        public static GameObject Row(Transform parent, float spacing = 10f)
        {
            var go = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            go.transform.SetParent(parent, false);
            var h = go.GetComponent<HorizontalLayoutGroup>();
            h.childAlignment = TextAnchor.MiddleCenter;
            h.spacing = spacing;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = h.childForceExpandHeight = false;
            return go;
        }

        public static GameObject Column(Transform parent, float spacing = 8f)
        {
            var go = new GameObject("Column", typeof(RectTransform), typeof(VerticalLayoutGroup));
            go.transform.SetParent(parent, false);
            var v = go.GetComponent<VerticalLayoutGroup>();
            v.childAlignment = TextAnchor.UpperCenter;
            v.spacing = spacing;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = v.childForceExpandHeight = false;
            return go;
        }

        public static TMP_Text Label(Transform parent, string text, float size, FontStyles style)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = Color.white;
            t.alignment = TextAlignmentOptions.Center;
            t.raycastTarget = false;
            return t;
        }

        public static Button MakeButton(Transform parent, string label, float w, float h, UnityAction onClick)
        {
            var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.15f);
            var le = go.GetComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = w;
            le.preferredHeight = le.minHeight = h;
            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            var txt = Label(go.transform, label, 22, FontStyles.Normal);
            Stretch(txt.gameObject);
            if (onClick != null) btn.onClick.AddListener(onClick);
            return btn;
        }

        /// A fixed-size colour square (player swatch etc.) that plays nice inside layout groups.
        public static Image Swatch(Transform parent, float size)
        {
            var go = new GameObject("Swatch", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var le = go.GetComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = size;
            le.preferredHeight = le.minHeight = size;
            var img = go.GetComponent<Image>();
            img.raycastTarget = false;
            return img;
        }
    }
}
