using UnityEngine;
using UnityEngine.EventSystems;

namespace JumpNowBro.Gameplay
{
    /// Shared UI helpers for code-built screens (#134+). New screens use this; the existing MainMenuUI /
    /// SettingsPanel builders stay private to their classes (verified screens are not restructured).
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
    }
}
