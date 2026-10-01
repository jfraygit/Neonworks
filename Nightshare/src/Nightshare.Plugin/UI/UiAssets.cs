using System;
using TMPro;
using UnityEngine;

namespace Nightshare.UI
{
    /// <summary>
    /// Fonts and generated sprites for Nightshare's own UI.
    /// <para>
    /// The approach is borrowed from Lumen, where it produced a panel indistinguishable
    /// from the game's. The two things that matter are using the game's own typeface and
    /// generating rounded art rather than drawing rectangles: those are most of why an
    /// IMGUI panel reads as a debug window no matter how carefully it is composed.
    /// </para>
    /// </summary>
    internal static class UiAssets
    {
        private static bool _searched;
        private static TMP_FontAsset _tmpFont;

        /// <summary>
        /// The game's default TMP font asset.
        /// <para>
        /// May legitimately be null, in which case TextMeshPro falls back to its own
        /// default. The panel still works; it just looks less at home.
        /// </para>
        /// </summary>
        internal static TMP_FontAsset TmpFont
        {
            get { Search(); return _tmpFont; }
        }

        private static void Search()
        {
            if (_searched) return;
            _searched = true;

            try
            {
                var settings = TMP_Settings.instance;
                if (settings != null) _tmpFont = TMP_Settings.defaultFontAsset;

                if (_tmpFont == null)
                {
                    // No default configured; take any loaded asset. They are all the game's
                    // own typefaces, so any of them beats the TMP fallback.
                    var assets = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                    if (assets != null && assets.Length > 0) _tmpFont = assets[0];
                }

                NightsharePlugin.Logger?.LogInfo(
                    $"UI font: {(_tmpFont != null ? _tmpFont.name : "none found, using TMP default")}");
            }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogWarning($"Font search failed: {ex.Message}");
            }
        }

        /// <summary>A filled rounded rectangle, nine-sliced so one sprite serves any size.</summary>
        internal static Sprite RoundedRect(int radius) =>
            Build(radius, distance => Mathf.Clamp01(0.5f - distance));

        /// <summary>A rounded rectangle outline: one ring, the whole way round.</summary>
        internal static Sprite RoundedOutline(int radius, float thickness) =>
            Build(radius, distance =>
            {
                var outer = Mathf.Clamp01(0.5f - distance);
                var inner = Mathf.Clamp01(distance + thickness + 0.5f);
                return Mathf.Min(outer, inner);
            });

        /// <summary>A plain white pixel, for bars and rules.</summary>
        internal static Sprite Solid()
        {
            var texture = new Texture2D(4, 4, TextureFormat.RGBA32, mipChain: false)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };

            for (var y = 0; y < 4; y++)
                for (var x = 0; x < 4; x++)
                    texture.SetPixel(x, y, Color.white);

            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>
        /// Rasterise a rounded rect from its signed distance field: negative inside,
        /// positive outside, zero on the edge. Corners come out as true arcs and the
        /// one-pixel ramp either side stops them looking stepped.
        /// </summary>
        private static Sprite Build(int radius, Func<float, float> alphaFromDistance)
        {
            var size = radius * 2 + 4;
            var half = size * 0.5f;
            var straight = half - radius;

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var px = Mathf.Abs(x + 0.5f - half) - straight;
                    var py = Mathf.Abs(y + 0.5f - half) - straight;

                    var qx = Mathf.Max(px, 0f);
                    var qy = Mathf.Max(py, 0f);

                    var distance = Mathf.Sqrt(qx * qx + qy * qy) - radius;
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alphaFromDistance(distance)));
                }
            }

            texture.Apply();

            var border = radius + 1;
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
                                 100f, 0, SpriteMeshType.FullRect,
                                 new Vector4(border, border, border, border));
        }
    }

    /// <summary>
    /// Nivalis Nights' own palette, sampled from its quest panel and top bar.
    /// <para>
    /// <b>These are the game's colours, not another mod's.</b> Anything Nightshare draws
    /// should be indistinguishable from the game's own UI, so the values come from
    /// screenshots of the quest panel rather than from any existing theme.
    /// </para>
    /// </summary>
    internal static class Palette
    {
        /// <summary>
        /// Headings, markers and the left accent bar. The amber of DAY 3, WEDNESDAY,
        /// LOWTOWN and every quest title.
        /// </summary>
        public static readonly Color Gold = new Color(0.910f, 0.706f, 0.310f, 1f);

        /// <summary>
        /// Objective text. Warm light grey, noticeably not white: the game's objectives sit
        /// well below its headings in brightness.
        /// </summary>
        public static readonly Color Text = new Color(0.788f, 0.776f, 0.741f, 1f);

        /// <summary>A finished objective, as the game greys "Talk to Banor".</summary>
        public static readonly Color TextDone = new Color(0.455f, 0.451f, 0.435f, 1f);

        /// <summary>
        /// Panel fill. A cool dark slate rather than neutral black, which is what lets the
        /// game's panels sit over a neon street without looking like a cut-out.
        /// </summary>
        public static readonly Color Panel = new Color(0.086f, 0.106f, 0.129f, 0.900f);

        /// <summary>A hairline of muted gold around the panel.</summary>
        public static readonly Color Border = new Color(0.478f, 0.388f, 0.212f, 0.70f);
    }
}
