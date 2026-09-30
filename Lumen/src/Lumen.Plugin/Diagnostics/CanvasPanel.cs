using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Lumen.Diagnostics
{
    /// <summary>
    /// The settings panel, built as real Unity UI: a Canvas, generated sprites and
    /// TextMeshPro.
    /// <para>
    /// Deliberately has <b>no GraphicRaycaster and no EventSystem</b>. Navigation stays on
    /// the keyboard so the mouse cursor is never shown or unlocked, which matters for a
    /// panel opened during play.
    /// </para>
    /// <para>
    /// The hierarchy is built once and then only has its text and colours updated, so there
    /// is no per-frame allocation and no layout rebuild while it sits open. Theme changes
    /// recolour the existing objects rather than rebuilding them.
    /// </para>
    /// </summary>
    internal sealed class CanvasPanel
    {
        private const float Width = 600f;
        private const float PadX = 22f;
        private const float HeaderHeight = 64f;
        private const float RowHeight = 32f;
        private const float SectionHeight = 24f;
        private const float FooterHeight = 32f;

        private const int CornerRadius = 8;

        private GameObject _root;
        private RectTransform _panel;

        private Image _background, _border, _headerBand, _headerRule, _footerRule;
        private TextMeshProUGUI _wordmark, _version, _fps, _fpsUnit, _stats, _footer;

        private readonly List<TextMeshProUGUI> _sectionLabels = new List<TextMeshProUGUI>();
        private readonly List<Image> _sectionRules = new List<Image>();
        private readonly List<RowWidgets> _rows = new List<RowWidgets>();

        private Sprite _rounded, _outline, _pill, _solid;
        private bool _built;
        private string _appliedTheme;
        private string _lastFps;

        private sealed class RowWidgets
        {
            internal Image Highlight;
            internal TextMeshProUGUI Label;
            internal TextMeshProUGUI Note;

            internal Image PillBackground;
            internal TextMeshProUGUI PillText;

            internal Image TrackBackground;
            internal Image TrackFill;
            internal Image Knob;
            internal TextMeshProUGUI TrackValue;

            // Last values written, so nothing is assigned that has not changed.
            internal string LastNote, LastValue;
            internal int LastSelected = -1;
            internal float LastFraction = -2f;
        }

        internal bool Available => _built;

        internal void SetVisible(bool visible)
        {
            if (_root != null) _root.SetActive(visible);
        }

        internal void Build(Harness harness)
        {
            if (_built) return;

            try
            {
                _rounded = UiAssets.RoundedRect(CornerRadius);
                _outline = UiAssets.RoundedOutline(CornerRadius, 1.5f);
                _pill = UiAssets.RoundedRect(5);
                _solid = UiAssets.Solid();

                _root = new GameObject("LumenCanvas");
                _root.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(_root);

                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;

                // Above the game's own HUD. No raycaster: this canvas must never intercept
                // a click, because that would mean needing a cursor.
                canvas.sortingOrder = 30000;

                // Constant pixel size, with a floor of 1.0 so the panel stays readable on
                // a high-resolution display without being oversized on a normal one.
                var scaler = _root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.scaleFactor = Mathf.Max(1f, Screen.height / 1440f);

                BuildPanel(harness);
                ApplyTheme(harness);

                _built = true;
                _root.SetActive(false);

                LumenPlugin.Log.LogInfo("Canvas panel built.");
            }
            catch (Exception ex)
            {
                LumenPlugin.Log.LogError($"Canvas panel failed to build: {ex}");
                _built = false;

                if (_root != null) UnityEngine.Object.Destroy(_root);
                _root = null;
            }
        }

        private void BuildPanel(Harness harness)
        {
            var rows = harness.Settings;

            float body = 0f;
            string section = null;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Section != null && rows[i].Section != section)
                {
                    section = rows[i].Section;
                    body += SectionHeight;
                }
                body += RowHeight;
            }

            float height = HeaderHeight + body + FooterHeight + 10f;

            _panel = Rect(_root.transform, "Panel", new Vector2(40f, -40f), new Vector2(Width, height));
            _background = AddImage(_panel, _rounded, Color.white);

            // A single ring over the whole panel. Nothing else draws an edge.
            var border = Rect(_panel, "Border", Vector2.zero, new Vector2(Width, height));
            _border = AddImage(border, _outline, Color.white);

            BuildHeader();

            float y = -HeaderHeight;
            section = null;

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];

                if (row.Section != null && row.Section != section)
                {
                    section = row.Section;
                    BuildSection(section.ToUpperInvariant(), y);
                    y -= SectionHeight;
                }

                _rows.Add(BuildRow(row, y));
                y -= RowHeight;
            }

            BuildFooter(height);
        }

        private void BuildHeader()
        {
            var band = Rect(_panel, "HeaderBand", new Vector2(1f, -1f), new Vector2(Width - 2f, HeaderHeight - 8f));
            _headerBand = AddImage(band, _solid, Color.white);

            _wordmark = Label(_panel, "Wordmark", new Vector2(PadX, -12f), new Vector2(220f, 26f),
                "LUMEN", 20f, FontStyles.Bold, Color.white, TextAlignmentOptions.Left);
            _wordmark.characterSpacing = 10f;

            _version = Label(_panel, "Version", new Vector2(PadX + 104f, -12f), new Vector2(260f, 26f),
                "v" + LumenPlugin.Version, 11f, FontStyles.Normal, Color.white, TextAlignmentOptions.Left);

            // Frame rate high in the header, detail line below it, rule clear of both.
            _fps = Label(_panel, "Fps", new Vector2(Width - PadX - 190f, -8f), new Vector2(156f, 30f),
                "60", 26f, FontStyles.Bold, Color.white, TextAlignmentOptions.Right);

            _fpsUnit = Label(_panel, "FpsUnit", new Vector2(Width - PadX - 30f, -16f), new Vector2(34f, 18f),
                "fps", 11f, FontStyles.Normal, Color.white, TextAlignmentOptions.Left);

            _stats = Label(_panel, "Stats", new Vector2(Width - PadX - 240f, -36f), new Vector2(240f, 16f),
                "", 11f, FontStyles.Normal, Color.white, TextAlignmentOptions.Right);

            var rule = Rect(_panel, "HeaderRule", new Vector2(PadX, -(HeaderHeight - 8f)),
                new Vector2(Width - PadX * 2f, 1f));
            _headerRule = AddImage(rule, _solid, Color.white);
        }

        private void BuildSection(string heading, float y)
        {
            var label = Label(_panel, "Section", new Vector2(PadX, y - 4f), new Vector2(260f, 18f),
                heading, 10f, FontStyles.Bold, Color.white, TextAlignmentOptions.Left);
            label.characterSpacing = 12f;
            label.ForceMeshUpdate();

            _sectionLabels.Add(label);

            float width = label.GetPreferredValues(heading).x + 16f;

            var rule = Rect(_panel, "SectionRule", new Vector2(PadX + width, y - 12f),
                new Vector2(Width - PadX * 2f - width, 1f));
            _sectionRules.Add(AddImage(rule, _solid, Color.white));
        }

        private RowWidgets BuildRow(SettingRow row, float y)
        {
            var widgets = new RowWidgets();

            var highlight = Rect(_panel, "Highlight", new Vector2(2f, y), new Vector2(Width - 4f, RowHeight));
            widgets.Highlight = AddImage(highlight, _pill, Color.clear);

            widgets.Label = Label(_panel, "Label", new Vector2(PadX, y), new Vector2(250f, RowHeight),
                row.Label, 14f, FontStyles.Normal, Color.white, TextAlignmentOptions.Left);

            float controlX = PadX + 252f;

            if (row.IsToggle)
            {
                var pill = Rect(_panel, "Pill", new Vector2(controlX, y - (RowHeight - 20f) * 0.5f),
                    new Vector2(50f, 20f));
                widgets.PillBackground = AddImage(pill, _pill, Color.white);

                widgets.PillText = Label(pill, "PillText", Vector2.zero, new Vector2(50f, 20f),
                    "On", 12f, FontStyles.Bold, Color.white, TextAlignmentOptions.Center);
            }
            else if (row.Fraction >= 0f)
            {
                var track = Rect(_panel, "Track", new Vector2(controlX, y - (RowHeight - 4f) * 0.5f),
                    new Vector2(80f, 4f));
                widgets.TrackBackground = AddImage(track, _pill, Color.white);

                var fill = Rect(track, "Fill", Vector2.zero, new Vector2(40f, 4f));
                widgets.TrackFill = AddImage(fill, _pill, Color.white);

                var knob = Rect(track, "Knob", new Vector2(37f, 3f), new Vector2(5f, 10f));
                widgets.Knob = AddImage(knob, _pill, Color.white);

                widgets.TrackValue = Label(_panel, "TrackValue",
                    new Vector2(controlX + 90f, y), new Vector2(52f, RowHeight),
                    "Off", 12f, FontStyles.Bold, Color.white, TextAlignmentOptions.Left);
            }
            else
            {
                // A plain value, for rows that are neither a switch nor a scale.
                widgets.TrackValue = Label(_panel, "Value", new Vector2(controlX, y),
                    new Vector2(142f, RowHeight), row.Value, 13f, FontStyles.Bold,
                    Color.white, TextAlignmentOptions.Left);
            }

            widgets.Note = Label(_panel, "Note", new Vector2(controlX + 150f, y),
                new Vector2(Width - controlX - 150f - PadX + 12f, RowHeight),
                row.Note, 11f, FontStyles.Normal, Color.white, TextAlignmentOptions.Left);

            return widgets;
        }

        private void BuildFooter(float panelHeight)
        {
            float y = -(panelHeight - FooterHeight);

            var rule = Rect(_panel, "FooterRule", new Vector2(PadX, y), new Vector2(Width - PadX * 2f, 1f));
            _footerRule = AddImage(rule, _solid, Color.white);

            // Words, not arrow glyphs: TMP renders from a prebuilt atlas and the game's
            // font has no arrows in it.
            _footer = Label(_panel, "Footer", new Vector2(PadX, y - 8f), new Vector2(Width - PadX * 2f, 18f),
                "UP/DOWN  Select     LEFT/RIGHT  Change     F10  Close",
                11f, FontStyles.Normal, Color.white, TextAlignmentOptions.Left);
        }

        // -----------------------------------------------------------------------------

        internal void Refresh(Harness harness)
        {
            if (!_built || _root == null || !_root.activeSelf) return;

            try
            {
                if (_appliedTheme != LumenConfig.Theme.Value) ApplyTheme(harness);

                var theme = Themes.Current;

                float ms = harness.Stats.AverageMs(2f);
                float fps = FrameStats.ToFps(ms);

                string fpsText = fps.ToString("0");
                if (_lastFps != fpsText) { _fps.text = fpsText; _lastFps = fpsText; }
                _fps.color = fps >= 60f ? theme.Good : fps >= 45f ? theme.Warning : theme.Bad;
                _stats.text = $"{ms:0.0} ms      1% low {harness.Stats.OnePercentLowFps(10f):0}";

                var rows = harness.Settings;
                int count = Math.Min(rows.Count, _rows.Count);

                for (int i = 0; i < count; i++)
                    RefreshRow(theme, rows[i], _rows[i], i == harness.SelectedSetting);
            }
            catch (Exception ex)
            {
                LumenPlugin.Log.LogError($"Canvas panel refresh failed: {ex}");
            }
        }

        private void ApplyTheme(Harness harness)
        {
            var theme = Themes.Current;
            _appliedTheme = theme.Name;

            _background.color = theme.Backdrop;
            _border.color = new Color(theme.Accent.r, theme.Accent.g, theme.Accent.b, 0.55f);
            _headerBand.color = new Color(theme.Accent.r, theme.Accent.g, theme.Accent.b, 0.04f);
            _headerRule.color = new Color(theme.Accent.r, theme.Accent.g, theme.Accent.b, 0.22f);
            _footerRule.color = new Color(theme.Accent.r, theme.Accent.g, theme.Accent.b, 0.16f);

            _wordmark.color = theme.Ink;
            // An available update takes over the version tag and the footer rather than
            // adding a banner, so the panel never changes height and never covers more of
            // the game than the player asked it to.
            bool update = UpdateCheck.NewerVersion != null;

            _version.text = update
                ? $"v{LumenPlugin.Version}  UPDATE v{UpdateCheck.NewerVersion}"
                : "v" + LumenPlugin.Version;

            _version.color = update
                ? theme.Warning
                : new Color(theme.Secondary.r, theme.Secondary.g, theme.Secondary.b, 0.9f);

            _footer.text = update
                ? "Update at " + UpdateCheck.ReleasesUrl
                : "UP/DOWN  Select     LEFT/RIGHT  Change     F10  Close";
            _fpsUnit.color = theme.InkFaint;
            _stats.color = theme.InkFaint;
            _footer.color = update ? theme.Warning : theme.InkFaint;

            for (int i = 0; i < _sectionLabels.Count; i++)
                _sectionLabels[i].color = new Color(theme.Secondary.r, theme.Secondary.g, theme.Secondary.b, 0.92f);

            for (int i = 0; i < _sectionRules.Count; i++)
                _sectionRules[i].color = new Color(theme.Secondary.r, theme.Secondary.g, theme.Secondary.b, 0.18f);

            for (int i = 0; i < _rows.Count; i++)
            {
                var widgets = _rows[i];
                if (widgets.TrackBackground != null)
                    widgets.TrackBackground.color = new Color(theme.Ink.r, theme.Ink.g, theme.Ink.b, 0.12f);
            }
        }

        private void RefreshRow(Theme theme, SettingRow row, RowWidgets widgets, bool selected)
        {
            int selectedFlag = selected ? 1 : 0;
            if (widgets.LastSelected != selectedFlag)
            {
                widgets.Highlight.color = selected
                    ? new Color(theme.Accent.r, theme.Accent.g, theme.Accent.b, 0.10f)
                    : Color.clear;

                widgets.Label.color = selected ? theme.Ink : theme.InkDim;
                widgets.LastSelected = selectedFlag;
            }

            var tint = row.Visible ? theme.Warning : theme.Accent;

            // Assigning TextMeshPro's text forces a mesh rebuild even when the string is
            // identical, and doing that for every row every frame made the labels flicker
            // while the selection moved.
            if (widgets.LastNote != row.Note)
            {
                widgets.Note.text = row.Note;
                widgets.LastNote = row.Note;
            }

            widgets.Note.color = row.Visible
                ? new Color(theme.Warning.r, theme.Warning.g, theme.Warning.b, 0.80f)
                : theme.InkFaint;

            if (widgets.PillText != null)
            {
                bool on = row.IsOn;

                if (widgets.LastValue != row.Value)
                {
                    widgets.PillText.text = row.Value;
                    widgets.LastValue = row.Value;
                }

                widgets.PillText.color = on ? theme.Accent : theme.InkFaint;
                widgets.PillBackground.color = on
                    ? new Color(theme.Accent.r, theme.Accent.g, theme.Accent.b, 0.18f)
                    : new Color(theme.Ink.r, theme.Ink.g, theme.Ink.b, 0.05f);
            }

            if (widgets.TrackValue == null) return;

            if (widgets.LastValue != row.Value)
            {
                widgets.TrackValue.text = row.Value;
                widgets.LastValue = row.Value;
            }

            widgets.TrackValue.color = tint;

            if (widgets.TrackFill == null) return;

            float fraction = Mathf.Clamp01(row.Fraction);

            widgets.TrackFill.color = tint;
            widgets.Knob.color = tint;

            if (widgets.LastFraction != fraction)
            {
                widgets.TrackFill.rectTransform.sizeDelta = new Vector2(80f * fraction, 4f);
                widgets.Knob.rectTransform.anchoredPosition = new Vector2(80f * fraction - 2.5f, 3f);
                widgets.LastFraction = fraction;
            }
        }

        internal void Destroy()
        {
            try
            {
                if (_root != null) UnityEngine.Object.Destroy(_root);
            }
            catch (Exception) { }

            _root = null;
            _built = false;
            _rows.Clear();
            _sectionLabels.Clear();
            _sectionRules.Clear();
        }

        // -----------------------------------------------------------------------------
        // Hierarchy helpers. Everything anchors top-left so positions read top-down.
        // -----------------------------------------------------------------------------

        private static RectTransform Rect(Transform parent, string name, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            return rect;
        }

        private static Image AddImage(RectTransform rect, Sprite sprite, Color color)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.color = color;
            image.type = Image.Type.Sliced;
            image.raycastTarget = false;   // nothing here may ever swallow a click
            return image;
        }

        private static TextMeshProUGUI Label(Transform parent, string name, Vector2 position,
            Vector2 size, string text, float fontSize, FontStyles style, Color color,
            TextAlignmentOptions alignment)
        {
            var rect = Rect(parent, name, position, size);

            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.fontStyle = style;
            label.color = color;
            label.alignment = alignment;
            label.raycastTarget = false;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Overflow;

            var font = UiAssets.TmpFont;
            if (font != null) label.font = font;

            return label;
        }
    }
}
