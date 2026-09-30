using System;
using UnityEngine;

namespace Lumen.Diagnostics
{
    /// <summary>
    /// A colour scheme for the panel. Every colour the UI uses comes from here, so a theme
    /// change recolours the whole thing without touching layout.
    /// </summary>
    internal sealed class Theme
    {
        internal string Name;

        /// <summary>Border, selection, toggles that are on, the frame rate when it is good.</summary>
        internal Color Accent;

        /// <summary>Section headings and the version tag. The second neon.</summary>
        internal Color Secondary;

        /// <summary>Settings that cost something visible.</summary>
        internal Color Warning;

        internal Color Good;
        internal Color Bad;

        internal Color Backdrop;
        internal Color Ink;
        internal Color InkDim;
        internal Color InkFaint;
    }

    internal static class Themes
    {
        private static readonly Theme[] All =
        {
            new Theme
            {
                Name = "Neon Noir",
                Accent = new Color(0.20f, 0.95f, 1.00f),
                Secondary = new Color(1.00f, 0.22f, 0.58f),
                Warning = new Color(1.00f, 0.66f, 0.22f),
                Good = new Color(0.36f, 1.00f, 0.62f),
                Bad = new Color(1.00f, 0.32f, 0.42f),
                Backdrop = new Color(0.028f, 0.032f, 0.055f, 0.965f),
                Ink = new Color(0.90f, 0.94f, 0.98f),
                InkDim = new Color(0.58f, 0.64f, 0.74f),
                InkFaint = new Color(0.40f, 0.46f, 0.56f)
            },
            new Theme
            {
                Name = "Ion Blue",
                Accent = new Color(0.35f, 0.70f, 1.00f),
                Secondary = new Color(0.55f, 0.88f, 1.00f),
                Warning = new Color(1.00f, 0.72f, 0.35f),
                Good = new Color(0.45f, 0.95f, 0.85f),
                Bad = new Color(1.00f, 0.40f, 0.45f),
                Backdrop = new Color(0.020f, 0.038f, 0.062f, 0.965f),
                Ink = new Color(0.92f, 0.96f, 1.00f),
                InkDim = new Color(0.60f, 0.70f, 0.82f),
                InkFaint = new Color(0.40f, 0.50f, 0.62f)
            },
            new Theme
            {
                Name = "Synthwave",
                Accent = new Color(1.00f, 0.28f, 0.66f),
                Secondary = new Color(0.62f, 0.40f, 1.00f),
                Warning = new Color(1.00f, 0.78f, 0.30f),
                Good = new Color(0.45f, 1.00f, 0.80f),
                Bad = new Color(1.00f, 0.35f, 0.38f),
                Backdrop = new Color(0.055f, 0.022f, 0.070f, 0.965f),
                Ink = new Color(1.00f, 0.94f, 0.99f),
                InkDim = new Color(0.76f, 0.62f, 0.80f),
                InkFaint = new Color(0.54f, 0.42f, 0.60f)
            },
            new Theme
            {
                Name = "Hazard",
                Accent = new Color(1.00f, 0.72f, 0.14f),
                Secondary = new Color(1.00f, 0.44f, 0.16f),
                Warning = new Color(1.00f, 0.32f, 0.28f),
                Good = new Color(0.72f, 1.00f, 0.40f),
                Bad = new Color(1.00f, 0.30f, 0.26f),
                Backdrop = new Color(0.050f, 0.040f, 0.022f, 0.965f),
                Ink = new Color(1.00f, 0.97f, 0.90f),
                InkDim = new Color(0.78f, 0.72f, 0.58f),
                InkFaint = new Color(0.56f, 0.50f, 0.38f)
            },
            new Theme
            {
                Name = "Viridian",
                Accent = new Color(0.30f, 1.00f, 0.58f),
                Secondary = new Color(0.20f, 0.85f, 0.75f),
                Warning = new Color(1.00f, 0.80f, 0.30f),
                Good = new Color(0.40f, 1.00f, 0.55f),
                Bad = new Color(1.00f, 0.42f, 0.42f),
                Backdrop = new Color(0.018f, 0.045f, 0.035f, 0.965f),
                Ink = new Color(0.90f, 1.00f, 0.94f),
                InkDim = new Color(0.58f, 0.80f, 0.68f),
                InkFaint = new Color(0.38f, 0.58f, 0.48f)
            },
            new Theme
            {
                Name = "Ghost",
                Accent = new Color(0.92f, 0.94f, 0.98f),
                Secondary = new Color(0.62f, 0.68f, 0.78f),
                Warning = new Color(1.00f, 0.70f, 0.35f),
                Good = new Color(0.85f, 0.92f, 1.00f),
                Bad = new Color(1.00f, 0.45f, 0.50f),
                Backdrop = new Color(0.035f, 0.038f, 0.045f, 0.965f),
                Ink = new Color(0.96f, 0.97f, 1.00f),
                InkDim = new Color(0.62f, 0.66f, 0.74f),
                InkFaint = new Color(0.42f, 0.46f, 0.54f)
            }
        };

        internal static string[] Names
        {
            get
            {
                var names = new string[All.Length];
                for (int i = 0; i < All.Length; i++) names[i] = All[i].Name;
                return names;
            }
        }

        internal static Theme Current => Find(LumenConfig.Theme.Value);

        internal static Theme Find(string name)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (string.Equals(All[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return All[i];
            }

            return All[0];
        }

        /// <summary>Steps to the next or previous theme, wrapping at both ends.</summary>
        internal static string Step(string from, int direction)
        {
            int index = 0;
            for (int i = 0; i < All.Length; i++)
            {
                if (!string.Equals(All[i].Name, from, StringComparison.OrdinalIgnoreCase)) continue;
                index = i;
                break;
            }

            index = (index + direction + All.Length) % All.Length;
            return All[index].Name;
        }
    }
}
