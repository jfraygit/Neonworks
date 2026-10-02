using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Nightshare.UI
{
    /// <summary>
    /// The Nightshare menu: host a session, join one, see who is connected.
    /// <para>
    /// Built as real Unity UI in the game's own style, the same way Lumen's settings panel
    /// is, because an IMGUI window reads as a debug tool no matter how carefully it is
    /// composed.
    /// </para>
    /// <para>
    /// <b>No GraphicRaycaster and no EventSystem, so navigation is the keyboard.</b> Mouse
    /// support would mean showing and unlocking the cursor, and this game locks the cursor
    /// for a first person view. A mod that takes the cursor mid-game is how soft-locks
    /// start, and this panel opens in play. Arrow keys and Enter cost a little discoverability
    /// and cannot strand anybody.
    /// </para>
    /// <para>
    /// The hierarchy is built once and afterwards only has its text and colours updated, so
    /// an open menu costs no allocation and no layout rebuild.
    /// </para>
    /// </summary>
    internal sealed class LobbyMenu
    {
        private const float Width = 420f;
        private const float PadX = 26f;
        private const float HeaderTop = 18f;
        private const float StatusTop = 48f;
        private const float RowsTop = 84f;
        private const float RowHeight = 34f;
        private const float FooterGap = 14f;
        private const float FooterHeight = 26f;

        private const int MaxRows = 6;

        /// <summary>Width of the selected-row stripe. Kept well clear of the labels.</summary>
        private const float StripeWidth = 3f;

        private GameObject _root;
        private RectTransform _panel;
        private Image _background, _border;
        private TextMeshProUGUI _heading, _status, _footer;

        private readonly List<Row> _rows = new List<Row>();
        private Sprite _rounded, _outline, _solid;

        private bool _built;
        private bool _visible;

        private sealed class Row
        {
            internal RectTransform Rect;
            internal Image Stripe;
            internal TextMeshProUGUI Label;
            internal TextMeshProUGUI Value;

            internal string LastLabel, LastValue;

            /// <summary>
            /// Tri-state on purpose: -1 means nothing has been drawn yet.
            /// <para>
            /// <b>This was a bool and every row drew its selection marker.</b> An unselected,
            /// enabled row compared equal to the initial <c>false</c>/<c>true</c> defaults,
            /// so the update was skipped and the marker kept whatever state it was created
            /// in, which was visible. Change detection whose starting value is a legitimate
            /// value cannot distinguish "unchanged" from "never set".
            /// </para>
            /// </summary>
            internal int LastSelected = -1;
            internal int LastEnabled = -1;
        }

        public bool Available => _built;
        public bool IsOpen => _visible;

        // ---------------------------------------------------------------- building

        public void Build()
        {
            if (_built) return;

            try
            {
                _rounded = UiAssets.RoundedRect(10);
                _outline = UiAssets.RoundedOutline(10, 1.5f);
                _solid = UiAssets.Solid();

                _root = new GameObject("NightshareLobbyMenu")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                UnityEngine.Object.DontDestroyOnLoad(_root);

                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;

                // Above the game's HUD and above our own notices. No raycaster.
                canvas.sortingOrder = 30000;

                var scaler = _root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.scaleFactor = Mathf.Max(1f, Screen.height / 1440f);

                BuildPanel();

                _built = true;
                _root.SetActive(false);

                NightsharePlugin.Logger?.LogInfo("Lobby menu built");
            }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogError($"Lobby menu failed to build: {ex}");
                _built = false;

                if (_root != null) UnityEngine.Object.Destroy(_root);
                _root = null;
            }
        }

        private void BuildPanel()
        {
            var height = RowsTop + RowHeight * MaxRows + FooterGap + FooterHeight;

            // Top left, clear of the game's own interaction prompt.
            //
            // Lumen sits at -40, which is fine for a panel you open while standing still.
            // This one is opened anywhere, and the game draws a "SHOPPING LIST / TO OPEN"
            // prompt in this corner whenever you are near something interactable. -150 put
            // the panel's edge against the bottom of that prompt; this clears it with a gap,
            // because two panels touching reads worse than either one alone.
            _panel = Rect(_root.transform, "Panel", new Vector2(40f, -205f),
                          new Vector2(Width, height));

            _background = AddImage(_panel, _rounded, Palette.Panel);

            var borderRect = Rect(_panel, "Border", Vector2.zero, new Vector2(Width, height));
            StretchToParent(borderRect);
            _border = AddImage(borderRect, _outline, Palette.Border);

            // NO FULL HEIGHT ACCENT BAR.
            //
            // The game's quest panel has one, and borrowing it worked for the sleep prompt
            // and the paused notice, which are single blocks of text. Here it ran parallel
            // to the per-row selection marker and the two read as a stack of unrelated
            // vertical lines. A menu needs the eye drawn to one row, not to the edge.

            _heading = Label(_panel, "Heading", new Vector2(PadX, -HeaderTop),
                             new Vector2(Width - PadX * 2f, 28f),
                             "Nightshare", 22f, FontStyles.Bold,
                             Palette.Gold, TextAlignmentOptions.Left);

            _status = Label(_panel, "Status", new Vector2(PadX, -StatusTop),
                            new Vector2(Width - PadX * 2f, 24f),
                            "", 16f, FontStyles.Normal,
                            Palette.TextDone, TextAlignmentOptions.Left);

            for (var i = 0; i < MaxRows; i++)
            {
                var y = -(RowsTop + RowHeight * i);

                var rowRect = Rect(_panel, $"Row{i}", new Vector2(PadX - 10f, y),
                                   new Vector2(Width - PadX * 2f + 10f, RowHeight));

                // A soft filled band behind the selected row, the way the game highlights a
                // selected save slot, rather than another vertical rule.
                var stripeRect = Rect(rowRect, "Highlight", new Vector2(0f, -3f),
                                      new Vector2(Width - PadX * 2f + 10f, RowHeight - 6f));
                var stripe = AddImage(stripeRect, _rounded, Palette.Highlight);

                var label = Label(rowRect, "Label", new Vector2(14f, -4f),
                                  new Vector2(Width * 0.55f, 24f),
                                  "", 17f, FontStyles.Normal,
                                  Palette.Text, TextAlignmentOptions.Left);

                // Right aligned, so values line up down the panel the way the game's do.
                var value = Label(rowRect, "Value",
                                  new Vector2(Width - PadX * 2f - 10f - (Width * 0.40f), -4f),
                                  new Vector2(Width * 0.40f, 24f),
                                  "", 17f, FontStyles.Normal,
                                  Palette.Gold, TextAlignmentOptions.Right);

                rowRect.gameObject.SetActive(false);
                _rows.Add(new Row { Rect = rowRect, Stripe = stripe, Label = label, Value = value });
            }

            _footer = Label(_panel, "Footer",
                            new Vector2(PadX, -(RowsTop + RowHeight * MaxRows + FooterGap)),
                            new Vector2(Width - PadX * 2f, FooterHeight),
                            "", 14f, FontStyles.Normal,
                            Palette.TextDone, TextAlignmentOptions.Left);
        }

        // ---------------------------------------------------------------- drawing

        public void SetVisible(bool visible)
        {
            if (!_built || visible == _visible) return;

            _root.SetActive(visible);
            _visible = visible;
        }

        /// <summary>
        /// Paint one frame of the menu. Nothing is assigned that has not changed, so holding
        /// the menu open is free.
        /// </summary>
        public void Render(string heading, string status, IReadOnlyList<MenuItem> items,
                           int selected, string footer)
        {
            if (!_built) return;

            if (_heading.text != heading) _heading.text = heading;
            if (_status.text != status) _status.text = status;
            if (_footer.text != footer) _footer.text = footer;

            var shown = Mathf.Min(items.Count, MaxRows);

            for (var i = 0; i < MaxRows; i++)
            {
                var row = _rows[i];

                if (i >= shown)
                {
                    if (row.Rect.gameObject.activeSelf) row.Rect.gameObject.SetActive(false);
                    continue;
                }

                if (!row.Rect.gameObject.activeSelf) row.Rect.gameObject.SetActive(true);

                var item = items[i];
                var isSelected = i == selected;

                if (row.LastLabel != item.Label)
                {
                    row.LastLabel = item.Label;
                    row.Label.text = item.Label;
                }

                var value = item.Value ?? "";
                if (row.LastValue != value)
                {
                    row.LastValue = value;
                    row.Value.text = value;
                }

                var selectedNow = isSelected ? 1 : 0;
                var enabledNow = item.Enabled ? 1 : 0;

                if (row.LastSelected != selectedNow || row.LastEnabled != enabledNow)
                {
                    row.LastSelected = selectedNow;
                    row.LastEnabled = enabledNow;

                    row.Stripe.enabled = isSelected;

                    // A disabled row is greyed exactly as the game greys a finished
                    // objective, rather than hidden: knowing an option exists and why it is
                    // unavailable is more useful than it vanishing.
                    row.Label.color = !item.Enabled ? Palette.TextDone
                                    : isSelected ? Palette.Gold
                                    : Palette.Text;

                    row.Value.color = item.Enabled ? Palette.Gold : Palette.TextDone;
                }
            }

            // Shrink the panel to the rows actually in use.
            var height = RowsTop + RowHeight * Mathf.Max(1, shown) + FooterGap + FooterHeight;
            if (!Mathf.Approximately(_panel.sizeDelta.y, height))
            {
                _panel.sizeDelta = new Vector2(Width, height);
                _border.rectTransform.sizeDelta = Vector2.zero;   // stretched; stays in step
                _footer.rectTransform.anchoredPosition =
                    new Vector2(PadX, -(RowsTop + RowHeight * Mathf.Max(1, shown) + FooterGap));
            }
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
            label.overflowMode = TextOverflowModes.Ellipsis;

            var font = UiAssets.TmpFont;
            if (font != null) label.font = font;

            return label;
        }
    }

    /// <summary>One line of the menu. A label, an optional value, and whether it can be used.</summary>
    internal sealed class MenuItem
    {
        public string Label;
        public string Value;
        public bool Enabled = true;

        /// <summary>What Enter does. Null for a row that is only being displayed.</summary>
        public Action Activate;

        /// <summary>
        /// Why the row is disabled, shown in the footer while it is selected. A greyed
        /// option with no explanation is worse than no option.
        /// </summary>
        public string DisabledReason;
    }
}
