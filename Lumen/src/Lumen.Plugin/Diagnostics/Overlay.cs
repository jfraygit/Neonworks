using System;
using UnityEngine;

namespace Lumen.Diagnostics
{
    /// <summary>
    /// The developer harness: probes, A/B readings and the scene report.
    /// <para>
    /// Everything is drawn from flat tinted rectangles rather than the IMGUI skin. Nine-slice
    /// skin boxes smear badly on short rects and this panel is made almost entirely of short
    /// rects; crisp edges also suit a neon-city game better than rounded chrome would.
    /// </para>
    /// <para>
    /// Repaint events only. Nothing here reads input - keys are polled in Update from Win32 -
    /// which sidesteps the IMGUI event-order traps entirely and, more importantly, means the
    /// panel never shows or unlocks the mouse cursor. A mod that grabs the cursor mid-game is
    /// how soft-locks start.
    /// </para>
    /// </summary>
    internal static class Overlay
    {
        private const float Margin = 32f;
        private const float Width = 620f;
        private const float PadX = 24f;
        private const float HeaderHeight = 68f;
        private const float RowHeight = 34f;
        private const float SectionHeight = 26f;
        private const float FooterHeight = 40f;
        private const float Line = 17f;

        private const float AccentWidth = 3f;
        private const float ShadowSize = 10f;

        private static readonly Color Cyan = new Color(0.36f, 0.82f, 1.00f);
        private static readonly Color Amber = new Color(1.00f, 0.74f, 0.36f);
        private static readonly Color Green = new Color(0.52f, 0.90f, 0.62f);

        private static GUIStyle _wordmark, _kicker, _section;
        private static GUIStyle _label, _labelDim, _note, _footer, _pillOn;

        private static Texture2D _white;
        private static bool _ready;

        internal static void Draw(Harness harness)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (!harness.Visible) return;

            EnsureStyles();
            GUI.depth = -1000;

            // Settings live on the Canvas panel; IMGUI draws only the developer harness.
            if (harness.DeveloperMode) DrawDeveloper(harness);
        }

        // -----------------------------------------------------------------------------
        // Developer harness. Denser on purpose: this one is read, not browsed.
        // -----------------------------------------------------------------------------

        private static void DrawDeveloper(Harness harness)
        {
            int probes = harness.Probes.Count;
            int logRows = Math.Max(1, harness.Measure.Log.Count);

            float height = 96f + Line * (probes + logRows + 4f) + FooterHeight;
            var panel = new Rect(Margin, Margin, 680f, height);

            DrawShadow(panel);
            Box(panel, new Color(0.043f, 0.055f, 0.075f, 0.96f));
            Box(new Rect(panel.x, panel.y, AccentWidth, panel.height), Amber);

            float x = panel.x + PadX;
            float w = panel.width - PadX * 2f;
            float y = panel.y + 16f;

            string busy = harness.Measure.BusyText;
            GUI.Label(new Rect(x, y, w, 22f), "LUMEN", _wordmark);
            GUI.Label(new Rect(x + 78f, y + 4f, w, Line),
                busy == null ? "developer" : $"developer   [{busy}]", _kicker);
            y += 28f;

            float ms = harness.Stats.AverageMs(2f);
            GUI.Label(new Rect(x, y, w, Line),
                $"{ms:0.0} ms   {FrameStats.ToFps(ms):0.0} fps   " +
                $"1% low {harness.Stats.OnePercentLowFps(10f):0.0}   gen0/s {harness.Stats.Gen0PerSecond}",
                _label);
            y += Line;

            GUI.Label(new Rect(x, y, w, Line),
                harness.CountsStale
                    ? "scene counts: F9 (sweeps the scene, never mid-reading)"
                    : $"lights {harness.LightCount} ({harness.ShadowCastingLightCount} casting)   " +
                      $"NPCs {harness.CharacterCount}   traffic {harness.TrafficCount}",
                _note);
            y += Line;

            GUI.Label(new Rect(x, y, w, Line),
                $"{SystemInfo.graphicsDeviceType}   {DisplayMode.Describe()}", _note);
            y += Line + 10f;

            for (int i = 0; i < probes; i++)
            {
                var probe = harness.Probes[i];
                bool selected = i == harness.Selected;

                if (selected)
                {
                    Box(new Rect(panel.x + AccentWidth, y - 2f, panel.width - AccentWidth, Line + 2f),
                        new Color(1f, 1f, 1f, 0.06f));
                    Box(new Rect(panel.x, y - 2f, AccentWidth, Line + 2f), Amber);
                }

                string status = string.IsNullOrEmpty(probe.Status) ? "" : "   " + probe.Status;
                GUI.Label(new Rect(x, y, w, Line),
                    $"{(probe.Active ? "[*]" : "[ ]")} {probe.Name}{status}",
                    probe.Active ? _pillOn : (selected ? _label : _labelDim));
                y += Line;
            }

            y += 8f;
            GUI.Label(new Rect(x, y, w, Line), "A/B readings (also in LogOutput.log)", _section);
            y += Line;

            var log = harness.Measure.Log;
            if (log.Count == 0)
            {
                GUI.Label(new Rect(x, y, w, Line), "none yet - select a probe and press Enter", _note);
                y += Line;
            }
            else
            {
                for (int i = 0; i < log.Count; i++)
                {
                    GUI.Label(new Rect(x, y, w, Line), log[i], _labelDim);
                    y += Line;
                }
            }

            y += 6f;
            var current = harness.Probes[harness.Selected];
            if (!string.IsNullOrEmpty(current.Hint))
                GUI.Label(new Rect(x, y, w, Line), current.Hint, _note);

            DrawFooter(panel,
                "Enter measure      LEFT/RIGHT adjust      Backspace restore      " +
                "F9 counts      F11 report      F12 settings");
        }

        // -----------------------------------------------------------------------------

        private static void DrawFooter(Rect panel, string text)
        {
            float y = panel.yMax - FooterHeight;

            Box(new Rect(panel.x + AccentWidth, y, panel.width - AccentWidth, 1f),
                new Color(1f, 1f, 1f, 0.08f));

            GUI.Label(new Rect(panel.x + PadX, y + 13f, panel.width - PadX * 2f, Line), text, _footer);
        }

        private static void Box(Rect rect, Color color)
        {
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, _white);
            GUI.color = previous;
        }

        private static void Frame(Rect rect, Color color)
        {
            Box(new Rect(rect.x, rect.y, rect.width, 1f), color);
            Box(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), color);
            Box(new Rect(rect.x, rect.y, 1f, rect.height), color);
            Box(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), color);
        }

        /// <summary>Stacked translucent rects: cheaper than a blurred sprite and reads the same.</summary>
        private static void DrawShadow(Rect panel)
        {
            for (int i = 1; i <= 4; i++)
            {
                float spread = ShadowSize * i / 4f;
                Box(new Rect(panel.x - spread, panel.y - spread,
                        panel.width + spread * 2f, panel.height + spread * 2f),
                    new Color(0f, 0f, 0f, 0.055f));
            }
        }

        private static void EnsureStyles()
        {
            if (_ready) return;

            _white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();
            _white.hideFlags = HideFlags.HideAndDontSave;

            _wordmark = Text(19, FontStyle.Bold, Color.white);
            _kicker = Text(11, FontStyle.Normal, new Color(0.45f, 0.62f, 0.72f));


            _section = Text(10, FontStyle.Bold, new Color(0.42f, 0.58f, 0.68f));
            _label = Text(14, FontStyle.Normal, new Color(0.97f, 0.98f, 1f));
            _labelDim = Text(14, FontStyle.Normal, new Color(0.70f, 0.74f, 0.80f));
            _note = Text(11, FontStyle.Normal, new Color(0.50f, 0.54f, 0.60f));
            _footer = Text(11, FontStyle.Normal, new Color(0.42f, 0.46f, 0.52f));

            _pillOn = Text(12, FontStyle.Bold, Cyan, TextAnchor.MiddleCenter);

            _ready = true;
        }

        private static GUIStyle Text(int size, FontStyle style, Color color,
            TextAnchor anchor = TextAnchor.MiddleLeft)
        {
            var result = new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                fontStyle = style,
                richText = false,
                wordWrap = false,
                clipping = TextClipping.Overflow,
                alignment = anchor,
                padding = new RectOffset(0, 0, 0, 0)
            };
            var font = UiAssets.ImguiFont;
            if (font != null) result.font = font;

            result.normal.textColor = color;
            return result;
        }
    }
}
