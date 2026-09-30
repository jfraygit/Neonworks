using System;
using TMPro;
using UnityEngine;

namespace Lumen.Diagnostics
{
    /// <summary>
    /// Fonts and generated sprites, found once and shared by both panel renderers.
    /// </summary>
    internal static class UiAssets
    {
        private static bool _searched;
        private static Font _font;
        private static TMP_FontAsset _tmpFont;

        /// <summary>
        /// A typeface for IMGUI, borrowed from the game, in place of the built-in Arial.
        /// <para>
        /// May legitimately be null: a TMP font asset ships its atlas, and the source font
        /// file is often stripped from a build. Callers keep the built-in font in that case.
        /// </para>
        /// </summary>
        internal static Font ImguiFont
        {
            get { Search(); return _font; }
        }

        /// <summary>The game's default TMP font asset, for the Canvas panel.</summary>
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
                    // No default configured; take any loaded asset.
                    var assets = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                    if (assets != null && assets.Length > 0) _tmpFont = assets[0];
                }

                if (_tmpFont != null) _font = _tmpFont.sourceFontFile;

                if (_font == null)
                {
                    // Fall back to any plain Font the game has loaded.
                    var fonts = Resources.FindObjectsOfTypeAll<Font>();
                    if (fonts != null)
                    {
                        for (int i = 0; i < fonts.Length; i++)
                        {
                            if (fonts[i] == null) continue;
                            _font = fonts[i];
                            break;
                        }
                    }
                }

                LumenPlugin.Log.LogInfo(
                    $"UI fonts: TMP='{(_tmpFont != null ? _tmpFont.name : "none")}'  " +
                    $"IMGUI='{(_font != null ? _font.name : "built-in Arial")}'");
            }
            catch (Exception ex)
            {
                LumenPlugin.Log.LogWarning($"Font search failed, using built-in: {ex.Message}");
            }
        }

        /// <summary>
        /// A filled rounded rectangle, generated rather than shipped.
        /// <para>
        /// Set up for nine-slice so one sprite serves every panel size without the corner
        /// radius stretching.
        /// </para>
        /// </summary>
        internal static Sprite RoundedRect(int radius)
        {
            return Build(radius, pixelDistance => Mathf.Clamp01(0.5f - pixelDistance));
        }

        /// <summary>
        /// A rounded rectangle outline - one continuous border, the whole way round.
        /// </summary>
        internal static Sprite RoundedOutline(int radius, float thickness)
        {
            return Build(radius, pixelDistance =>
            {
                float outer = Mathf.Clamp01(0.5f - pixelDistance);
                float inner = Mathf.Clamp01(pixelDistance + thickness + 0.5f);
                return Mathf.Min(outer, inner);
            });
        }

        /// <summary>
        /// Rasterises a rounded rect from its signed distance field: negative inside,
        /// positive outside, zero exactly on the edge. Corners come out as true arcs and
        /// the one-pixel ramp either side keeps them from looking stepped.
        /// </summary>
        private static Sprite Build(int radius, Func<float, float> alphaFromDistance)
        {
            int size = radius * 2 + 4;
            float half = size * 0.5f;
            float straight = half - radius;   // half-extent of the square part

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = Mathf.Abs(x + 0.5f - half) - straight;
                    float py = Mathf.Abs(y + 0.5f - half) - straight;

                    float qx = Mathf.Max(px, 0f);
                    float qy = Mathf.Max(py, 0f);

                    float distance = Mathf.Sqrt(qx * qx + qy * qy) - radius;

                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alphaFromDistance(distance)));
                }
            }

            texture.Apply();

            int border = radius + 1;
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
                100f, 0, SpriteMeshType.FullRect,
                new Vector4(border, border, border, border));
        }

        internal static Sprite Solid()
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.SetPixel(0, 0, Color.white);
            texture.Apply();

            return Sprite.Create(texture, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
        }
    }
}
