using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GemmaStage.Core
{
    /// <summary>
    /// Central design tokens for GemmaStage — colors, typography, layout, and small
    /// runtime helpers. Reference these from any UI script instead of hardcoding values.
    ///
    /// Typography font asset is injected at runtime by <see cref="StylesBootstrap"/>
    /// (a static class can't hold an Inspector reference); helpers degrade gracefully
    /// if the asset hasn't been wired yet.
    /// </summary>
    public static class Styles
    {
        // --- Palette (design sheet, top-level for ease of access) ---
        public static readonly Color32 Background    = new(0x1E, 0x1E, 0x22, 0xFF);
        public static readonly Color32 Panel         = new(0x27, 0x27, 0x2C, 0xFF);
        public static readonly Color32 PanelMuted    = new(0x27, 0x27, 0x2C, 0xD9); // ~85% alpha for stacked overlays
        public static readonly Color32 Divider       = new(0x3A, 0x3A, 0x41, 0xFF);
        public static readonly Color32 Overlay       = new(0x00, 0x00, 0x00, 0x8C); // ~55% black for modal scrims
        public static readonly Color32 Primary       = new(0x7B, 0x61, 0xFF, 0xFF);
        public static readonly Color32 PrimaryHover  = new(0x9D, 0x87, 0xFF, 0xFF);
        public static readonly Color32 PrimaryText   = new(0xFF, 0xFF, 0xFF, 0xFF);
        public static readonly Color32 SecondaryText = new(0xB0, 0xB0, 0xB5, 0xFF);
        public static readonly Color32 Warning       = new(0xF5, 0xA6, 0x23, 0xFF);
        public static readonly Color32 Error         = new(0xE1, 0x5B, 0x64, 0xFF);
        public static readonly Color32 IconSuccess   = new(0x3F, 0xB8, 0x7B, 0xFF);

        // --- Domain tokens (semantic aliases) ---
        // Countdown / lobby wall clock digits on the wrist watch.
        public static readonly Color32 TimerCountdown = Primary;
        // Penalty count-up digits after the countdown reaches zero (PHASE_5_TASKS §5.6.3).
        public static readonly Color32 TimerPenalty   = new(0x8B, 0x00, 0x00, 0xFF);

        /// <summary>
        /// Typography tokens. VR-anchored point sizes calibrated against the existing
        /// 28 pt SettingRow label baseline (canvas scale 0.0015, 600 unit width).
        /// </summary>
        public static class Type
        {
            public const float HeadingPt = 40f; // SemiBold — panel headers
            public const float BodyPt    = 28f; // Regular — primary body, setting labels
            public const float ButtonPt  = 32f; // Medium — button text
            public const float CaptionPt = 22f; // Regular — small helper text

            public const FontStyles HeadingStyle = FontStyles.Bold;   // Inter-Regular SDF has no SemiBold variant; Bold approximates
            public const FontStyles BodyStyle    = FontStyles.Normal;
            public const FontStyles ButtonStyle  = FontStyles.Bold;
            public const FontStyles CaptionStyle = FontStyles.Normal;

            /// <summary>
            /// Inter-Regular SDF, injected by <see cref="StylesBootstrap"/> on Awake.
            /// May be null in early-frame Awake order; consumers should fall back to
            /// the existing TMP_Text.font when null.
            /// </summary>
            public static TMP_FontAsset Inter;
        }

        /// <summary>
        /// Layout tokens — spacing, padding, and corner-radius units (in canvas pixels,
        /// pre-scale). The world-space VR canvases use 0.0015 scale so a 24-unit radius
        /// renders at ~3.6 mm in headset.
        /// </summary>
        public static class Layout
        {
            // Corner radii — surfaces use the matching 9-sliced sprite under Core/UI/Sprites
            // (RoundedRect24 / RoundedRect12). Borders on those sprites match the radius
            // exactly, so any Image tinted with these tokens stays visually consistent.
            public const float PanelCornerRadius  = 24f; // Session-setup / Game-settings / Live Q&A / popup surfaces
            public const float PopupCornerRadius  = 24f; // Floating popups (PresentationPicker, dialogs) — alias of PanelCornerRadius
            public const float ButtonCornerRadius = 12f;

            public const float PanelPadding       = 32f;
            public const float RowSpacing         = 16f;
            public const float ControlGap         = 12f;
            public const float ContentGap         = 24f;
        }

        /// <summary>
        /// Small runtime helpers — used when a controller swaps a button between
        /// primary/secondary states or applies a text role. Authoring-time prefabs
        /// don't need these; they exist for state-driven controllers.
        /// </summary>
        public static class Apply
        {
            public enum TextRole { Heading, Body, Button, Caption }

            public static void Text(TMP_Text label, TextRole role, Color32? colorOverride = null)
            {
                if (label == null) return;
                switch (role)
                {
                    case TextRole.Heading:
                        label.fontSize  = Type.HeadingPt;
                        label.fontStyle = Type.HeadingStyle;
                        label.color     = colorOverride ?? PrimaryText;
                        break;
                    case TextRole.Body:
                        label.fontSize  = Type.BodyPt;
                        label.fontStyle = Type.BodyStyle;
                        label.color     = colorOverride ?? PrimaryText;
                        break;
                    case TextRole.Button:
                        label.fontSize  = Type.ButtonPt;
                        label.fontStyle = Type.ButtonStyle;
                        label.color     = colorOverride ?? PrimaryText;
                        break;
                    case TextRole.Caption:
                        label.fontSize  = Type.CaptionPt;
                        label.fontStyle = Type.CaptionStyle;
                        label.color     = colorOverride ?? SecondaryText;
                        break;
                }
                if (Type.Inter != null) label.font = Type.Inter;
            }

            public static void Image(Image image, Color32 color)
            {
                if (image == null) return;
                image.color = color;
            }
        }
    }
}
