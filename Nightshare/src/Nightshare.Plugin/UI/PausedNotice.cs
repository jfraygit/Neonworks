using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Nightshare.UI
{
    /// <summary>
    /// Shown on a guest while the host has the game paused.
    /// <para>
    /// <b>Why this is not optional.</b> A guest whose world has stopped with nothing on
    /// screen looks like a guest whose game has hung. The city keeps rendering, the HUD
    /// stays up, and nothing moves; that is indistinguishable from a freeze, and the honest
    /// reaction is to force-quit. One line of text is the difference between a mod that
    /// paused and a mod that broke.
    /// </para>
    /// <para>
    /// Same visual language as <see cref="SleepPrompt"/>: dark rounded panel, gold bar down
    /// the left edge, gold heading in the game's own font. Deliberately smaller, because
    /// this is a status notice rather than something that needs reading.
    /// </para>
    /// <para>
    /// <b>No GraphicRaycaster and no EventSystem</b>, for the same reason as the sleep
    /// prompt: a mod that grabs the cursor mid-game is how soft-locks start.
    /// </para>
    /// </summary>
    internal sealed class PausedNotice
    {
        private const float Width = 300f;
        private const float Height = 84f;
        private const float PadX = 24f;

        /// <summary>
        /// Distance below the top edge.
        /// <para>
        /// Has to clear two things, not one: the day/clock/money bar across the top, and
        /// the large zone name banner that drops beneath it on entering an area. A first
        /// attempt cleared only the bar and landed straight on top of the zone name.
        /// </para>
        /// </summary>
        private const float TopOffset = 320f;

        private GameObject _root;
        private bool _built;
        private bool _visible;

        public bool Available => _built;

        public void Build()
        {
            if (_built) return;

            try
            {
                var rounded = UiAssets.RoundedRect(10);
                var outline = UiAssets.RoundedOutline(10, 1.5f);
                var solid = UiAssets.Solid();

                _root = new GameObject("NightsharePausedNotice")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                UnityEngine.Object.DontDestroyOnLoad(_root);

                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 29000;

                var scaler = _root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.scaleFactor = Mathf.Max(1f, Screen.height / 1440f);

                // Top centre, below the zone name banner.
                //
                // The middle of the screen is where the player is looking and where their
                // character stands, so a panel there sits on top of the thing the notice is
                // about. The top strip is already the game's own status area: day, clock,
                // money and the zone name all live there, so a session status reads as
                // belonging with them.
                var panel = Rect(_root.transform, "Panel", Vector2.zero, new Vector2(Width, Height));
                panel.anchorMin = new Vector2(0.5f, 1f);
                panel.anchorMax = new Vector2(0.5f, 1f);
                panel.pivot = new Vector2(0.5f, 1f);
                panel.anchoredPosition = new Vector2(0f, -TopOffset);

                AddImage(panel, rounded, Palette.Panel);

                var borderRect = Rect(panel, "Border", Vector2.zero, new Vector2(Width, Height));
                StretchToParent(borderRect);
                AddImage(borderRect, outline, Palette.Border);

                var accentRect = Rect(panel, "Accent", new Vector2(10f, -14f),
                                      new Vector2(3f, Height - 28f));
                AddImage(accentRect, solid, Palette.Gold);

                Label(panel, "Heading", new Vector2(PadX, -18f),
                      new Vector2(Width - PadX * 2f, 28f),
                      "Host Paused", 21f, FontStyles.Bold,
                      Palette.Gold, TextAlignmentOptions.Left);

                Label(panel, "Detail", new Vector2(PadX, -48f),
                      new Vector2(Width - PadX * 2f, 24f),
                      "The City Is Waiting", 16f, FontStyles.Normal,
                      Palette.Text, TextAlignmentOptions.Left);

                _built = true;
                _root.SetActive(false);

                NightsharePlugin.Logger?.LogInfo("Paused notice built");
            }
            catch (Exception ex)
            {
                NightsharePlugin.Logger?.LogError($"Paused notice failed to build: {ex}");
                _built = false;

                if (_root != null) UnityEngine.Object.Destroy(_root);
                _root = null;
            }
        }

        /// <summary>Match the panel to whether the host is paused. Cheap to call every frame.</summary>
        public void SetVisible(bool visible)
        {
            if (!_built || visible == _visible) return;

            _root.SetActive(visible);
            _visible = visible;
        }

        public void Destroy()
        {
            if (_root == null) return;

            try { UnityEngine.Object.Destroy(_root); } catch (Exception) { }
            _root = null;
            _built = false;
            _visible = false;
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
            image.raycastTarget = false;
            return image;
        }

        private static void Label(Transform parent, string name, Vector2 position,
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
        }
    }
}
