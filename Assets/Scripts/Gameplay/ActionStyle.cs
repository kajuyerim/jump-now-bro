using UnityEngine;
using JumpNowBro.Util;

namespace JumpNowBro.Gameplay
{
    /// Canonical per-action look: colour + label + icon. The single source for "what an action looks like" across
    /// the control HUD, the swap-trigger banners, the swap announcement (#115), the prep telegraph (#127) and the
    /// edge flash (#126). Replaces the two divergent action-colour tables that predated v2.1 (the HUD's hex set
    /// and SwapTrigger.ColorFor's RGB set). The HUD's green/orange/cyan is the canonical palette (most prominent
    /// surface); kept distinct from PlayerIdentity's per-player colours so "what" and "who" never read as one.
    ///
    /// #136: a colourblind palette (Okabe-Ito, hue-family-preserving so learned associations survive) switches
    /// through SetPalette; persistent surfaces refresh via PlayerIdentity.OnChanged + SwapTrigger.RetintAll,
    /// transient cues sample at fire time. Icons give the colour-only cues a non-colour channel.
    public static class ActionStyle
    {
        public enum PaletteKind : byte { Default = 0, Colourblind = 1 }

        public static PaletteKind Palette { get; private set; } = PaletteKind.Default;

        /// GameSettings.SetPaletteMode drives this (persisted); callers there also trigger the redraws.
        public static void SetPalette(PaletteKind kind) => Palette = kind;

        public static Color ColorOf(PlayerAction action) => Palette == PaletteKind.Colourblind
            ? action switch
            {
                PlayerAction.MoveHorizontal => new Color(0.000f, 0.620f, 0.451f),   // #009E73 Okabe-Ito bluish green
                PlayerAction.Jump           => new Color(0.902f, 0.624f, 0.000f),   // #E69F00 Okabe-Ito orange
                PlayerAction.Dash           => new Color(0.337f, 0.706f, 0.914f),   // #56B4E9 Okabe-Ito sky blue
                _                           => Color.white,
            }
            : action switch
            {
                PlayerAction.MoveHorizontal => new Color(0.451f, 0.902f, 0.549f),   // #73E68C
                PlayerAction.Jump           => new Color(1.000f, 0.722f, 0.251f),   // #FFB840
                PlayerAction.Dash           => new Color(0.451f, 0.800f, 1.000f),   // #73CCFF
                _                           => Color.white,
            };

        public static string LabelOf(PlayerAction action) => action switch
        {
            PlayerAction.MoveHorizontal => "MOVE",
            PlayerAction.Jump           => "JUMP",
            PlayerAction.Dash           => "DASH",
            _                           => "",
        };

        /// Hex (no leading #) for inline TMP rich-text colour tags.
        public static string HexOf(PlayerAction action) => ColorUtility.ToHtmlStringRGB(ColorOf(action));

        /// The action label wrapped in its colour, e.g. "<color=#FFB840>JUMP</color>".
        public static string RichLabel(PlayerAction action) => $"<color=#{HexOf(action)}>{LabelOf(action)}</color>";

        // #136: per-action icon (Kenney sprites under Resources/ActionIcons) — the non-colour channel for
        // cues that would otherwise communicate by hue alone (edge flash, proximity vignette). Cached;
        // null-safe when the sprites are absent (headless/tests).
        static Sprite iconMove, iconJump, iconDash;
        static bool iconsLoaded;

        public static Sprite IconOf(PlayerAction action)
        {
            if (!iconsLoaded)
            {
                iconsLoaded = true;
                // icon_move is a two-glyph sheet (spriteMode Multiple): take the first sub-sprite by name.
                var moveSubs = Resources.LoadAll<Sprite>("ActionIcons/icon_move");
                iconMove = moveSubs != null && moveSubs.Length > 0 ? moveSubs[0] : null;
                iconJump = Resources.Load<Sprite>("ActionIcons/icon_jump");
                iconDash = Resources.Load<Sprite>("ActionIcons/icon_dash");
            }
            return action switch
            {
                PlayerAction.MoveHorizontal => iconMove,
                PlayerAction.Jump           => iconJump,
                PlayerAction.Dash           => iconDash,
                _                           => null,
            };
        }
    }
}
