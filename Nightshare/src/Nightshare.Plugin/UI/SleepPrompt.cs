using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Nightshare.UI
{
    /// <summary>
    /// The panel shown while a player is in bed waiting for the others.
    /// <para>
    /// Built as real Unity UI to match the game's own quest panel: a dark rounded panel, a
    /// gold bar down the left edge, a gold heading and one diamond-marked row per player.
    /// Players already at a bed are dimmed with a filled diamond, exactly as the game greys
    /// a completed objective, so the mapping is the game's own idiom rather than an
    /// invented one.
    /// </para>
    /// <para>
    /// <b>No GraphicRaycaster and no EventSystem.</b> This must never intercept a click,
    /// because that would mean needing a cursor, and a mod that grabs the cursor mid-game is
    /// how soft-locks start. The panel is informational and keyboard-free.
    /// </para>
    /// <para>
    /// Built once and then only has its text and colours updated, so an open panel costs no
    /// allocation and no layout rebuild.
    /// </para>
    /// </summary>
    internal sealed class SleepPrompt
    {
        private const float Width = 360f;
        private const float PadX = 26f;
        private const float HeaderTop = 20f;
        private const float RowHeight = 34f;
        private const float RowsTop = 62f;
        private const float BottomPad = 18f;

        private const int MaxRows = 6;

        private GameObject _root;
        private RectTransform _panel;
        private Image _background, _border, _accent;
        private TextMeshProUGUI _heading;

        private readonly List<Row> _rows = new List<Row>();

        private Sprite _rounded, _outline, _solid;

        /// <summary>
        /// Near-square sprites for the markers.
        /// <para>
        /// The panel's own corner radius cannot be reused here: a radius of 10 on a 13
        /// pixel box IS a circle, so rotating it 45 degrees produces a circle rather than a
        /// diamond. The marker needs corners sharp relative to its size to read as one.
        /// </para>
        /// </summary>
        private Sprite _diamondFill, _diamondOutline;
        private bool _built;
        private bool _visible;
        private string _lastSignature;

        private sealed class Row
        {
            internal RectTransform Rect;
            internal Image Diamond;
            internal TextMeshProUGUI Label;
        }

        public bool Available => _built;

        // ---------------------------------------------------------------- building

        public void Build()
        {
            if (_built) return;

            try
            {
                _rounded = UiAssets.RoundedRect(10);
                _outline = UiAssets.RoundedOutline(10, 1.5f);
                _solid = UiAssets.Solid();

                // A whisper of a radius, so the corners read as points when turned.
                _diamondFill = UiAssets.RoundedRect(2);
                _diamondOutline = UiAssets.RoundedOutline(2, 2.5f);

                _root = new GameObject("NightshareSleepPrompt")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                UnityEngine.Object.DontDestroyOnLoad(_root);

                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;

                // Above the game's HUD. No raycaster, so it cannot swallow input.
                canvas.sortingOrder = 29000;

                // Constant pixel size. Scaling against a 1080p reference inflates the panel
                // by a third on a 1440p display; the floor of 1 keeps it readable on 4K.
                var scaler = _root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.scaleFactor = Mathf.Max(1f, Screen.height / 1440f);

                BuildPanel();

                _built = true;
                _root.SetActive(false);

                NightsharePlugin.Logger?.LogInfo("Sleep prompt built");
            }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogError($"Sleep prompt failed to build: {ex}");
                _built = false;

                if (_root != null) UnityEngine.Object.Destroy(_root);
                _root = null;
            }
        }

        private void BuildPanel()
        {
            var height = RowsTop + RowHeight * 2 + BottomPad;

            // Centred horizontally, a little below the middle, clear of the top bar and the
            // quest panel on the right.
            _panel = Rect(_root.transform, "Panel", Vector2.zero, new Vector2(Width, height));
            _panel.anchorMin = new Vector2(0.5f, 0.5f);
            _panel.anchorMax = new Vector2(0.5f, 0.5f);
            _panel.pivot = new Vector2(0.5f, 0.5f);
            _panel.anchoredPosition = new Vector2(0f, -170f);

            _background = AddImage(_panel, _rounded, Palette.Panel);

            var borderRect = Rect(_panel, "Border", Vector2.zero, new Vector2(Width, height));
            StretchToParent(borderRect);
            _border = AddImage(borderRect, _outline, Palette.Border);

            // The gold bar down the left edge, the most recognisable part of the game's
            // own panels.
            var accentRect = Rect(_panel, "Accent", new Vector2(10f, -14f), new Vector2(3f, height - 28f));
            _accent = AddImage(accentRect, _solid, Palette.Gold);

            _heading = Label(_panel, "Heading", new Vector2(PadX, -HeaderTop),
                             new Vector2(Width - PadX * 2f, 28f),
                             "Waiting To Sleep", 21f, FontStyles.Bold,
                             Palette.Gold, TextAlignmentOptions.Left);

            for (var i = 0; i < MaxRows; i++)
            {
                var y = -(RowsTop + RowHeight * i);

                var rowRect = Rect(_panel, $"Row{i}", new Vector2(PadX, y),
                                   new Vector2(Width - PadX * 2f, RowHeight));

                // A diamond is a square turned 45 degrees. With a RectTransform that is a
                // rotation on the object, which cannot leak into anything drawn later.
                var diamondRect = Rect(rowRect, "Diamond", new Vector2(2f, -8f), new Vector2(12f, 12f));
                diamondRect.localEulerAngles = new Vector3(0f, 0f, 45f);
                var diamond = AddImage(diamondRect, _diamondOutline, Palette.Gold);

                var label = Label(rowRect, "Label", new Vector2(30f, -3f),
                                  new Vector2(Width - PadX * 2f - 30f, 24f),
                                  "", 17f, FontStyles.Normal,
                                  Palette.Text, TextAlignmentOptions.Left);

                rowRect.gameObject.SetActive(false);
                _rows.Add(new Row { Rect = rowRect, Diamond = diamond, Label = label });
            }
        }

        // ---------------------------------------------------------------- updating

        /// <summary>
        /// Show the panel listing who is and is not at a bed. Nothing is touched unless the
        /// content actually changed, so an open panel is free.
        /// </summary>
        public void Show(IReadOnlyList<string> atBed, IReadOnlyList<string> notAtBed)
        {
            if (!_built) return;

            var signature = $"{string.Join("|", atBed)}#{string.Join("|", notAtBed)}";
            var changed = signature != _lastSignature;

            if (!_visible)
            {
                _root.SetActive(true);
                _visible = true;
                changed = true;
            }

            if (!changed) return;
            _lastSignature = signature;

            try
            {
                var index = 0;

                // Waiting on, first and bright.
                for (var i = 0; i < notAtBed.Count && index < MaxRows; i++, index++)
                    SetRow(index, notAtBed[i], done: false);

                // Already in bed, dimmed, as the game greys a finished objective.
                for (var i = 0; i < atBed.Count && index < MaxRows; i++, index++)
                    SetRow(index, atBed[i], done: true);

                for (var i = index; i < MaxRows; i++)
                    _rows[i].Rect.gameObject.SetActive(false);

                // Grow the panel to the rows actually in use.
                var used = Mathf.Max(1, index);
                var height = RowsTop + RowHeight * used + BottomPad;
                _panel.sizeDelta = new Vector2(Width, height);
            }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogWarning($"Sleep prompt update failed: {ex.Message}");
            }
        }

        private void SetRow(int index, string name, bool done)
        {
            var row = _rows[index];
            row.Rect.gameObject.SetActive(true);

            row.Label.text = name;
            row.Label.color = done ? Palette.TextDone : Palette.Text;

            // Filled for someone already in bed, outlined for someone still up, exactly as
            // the game marks a finished objective against an outstanding one.
            row.Diamond.sprite = done ? _diamondFill : _diamondOutline;
            row.Diamond.color = done ? Palette.TextDone : Palette.Gold;
        }

        public void Hide()
        {
            if (!_built || !_visible) return;

            _root.SetActive(false);
            _visible = false;
            _lastSignature = null;
        }

        public void Destroy()
        {
            if (_root == null) return;

            try { UnityEngine.Object.Destroy(_root); } catch (Exception) { }
            _root = null;
            _built = false;
            _visible = false;
            _rows.Clear();
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>Everything anchors top-left, so positions read top-down.</summary>
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

        private static void StretchToParent(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
        }

        private static Image AddImage(RectTransform rect, Sprite sprite, Color colour)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.color = colour;
            image.type = Image.Type.Sliced;
            image.raycastTarget = false;   // nothing here may ever swallow a click
            return image;
        }

        private static TextMeshProUGUI Label(Transform parent, string name, Vector2 position,
                                             Vector2 size, string text, float fontSize,
                                             FontStyles style, Color colour,
                                             TextAlignmentOptions alignment)
        {
            var rect = Rect(parent, name, position, size);

            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.fontStyle = style;
            label.color = colour;
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
