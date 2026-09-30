using System;
using BepInEx.Configuration;

namespace Lumen.Diagnostics
{
    /// <summary>
    /// One line in the settings panel, bound directly to a config entry so changes apply
    /// immediately and persist without a save step.
    /// </summary>
    internal abstract class SettingRow
    {
        internal abstract string Label { get; }
        internal abstract string Value { get; }

        /// <summary>Short plain-language consequence, shown beside the value.</summary>
        internal abstract string Note { get; }

        /// <summary>True when this setting visibly changes the game, so the note reads as a caution.</summary>
        internal virtual bool Visible => false;

        /// <summary>Heading this row sits under. Null continues the previous group.</summary>
        internal string Section { get; set; }

        /// <summary>
        /// 0..1 for rows that should draw as a track rather than a toggle pill; negative to
        /// draw as a pill. A setting with a range reads better as a position on a scale than
        /// as a bare number.
        /// </summary>
        internal virtual float Fraction => -1f;

        /// <summary>True for on/off rows, which draw as a filled or hollow pill.</summary>
        internal virtual bool IsToggle => false;

        /// <summary>True when an on/off row is currently on.</summary>
        internal virtual bool IsOn => false;

        internal abstract void Adjust(int direction);
    }

    internal sealed class BoolRow : SettingRow
    {
        private readonly ConfigEntry<bool> _entry;
        private readonly string _label;
        private readonly string _onNote;

        internal BoolRow(ConfigEntry<bool> entry, string label, string onNote)
        {
            _entry = entry;
            _label = label;
            _onNote = onNote;
        }

        internal override string Label => _label;
        internal override string Value => _entry.Value ? "On" : "Off";
        internal override string Note => _entry.Value ? _onNote : "disabled";
        internal override bool IsToggle => true;
        internal override bool IsOn => _entry.Value;

        internal override void Adjust(int direction) => _entry.Value = !_entry.Value;
    }

    /// <summary>
    /// The NPC cull distance, stepped rather than free-scrolling so it lands on values that
    /// were actually measured. Right goes more aggressive.
    /// </summary>
    internal sealed class CullDistanceRow : SettingRow
    {
        // 0 is "off" at the left-hand end, so the row reads as one axis from "changes
        // nothing" to "most frames, most popping".
        private static readonly float[] Steps =
            { 0f, 150f, 120f, 100f, 80f, 60f, 50f, 40f, 30f, 25f, 20f, 15f };

        private readonly ConfigEntry<float> _entry;

        internal CullDistanceRow(ConfigEntry<float> entry) { _entry = entry; }

        internal override string Label => "Hide Faraway Crowds";
        internal override string Value => _entry.Value <= 0f ? "Off" : $"{_entry.Value:0} m";
        internal override bool Visible => _entry.Value > 0f;

        // Index rather than metres, so the steps are evenly spaced on screen.
        internal override float Fraction => NearestStep(_entry.Value) / (float)(Steps.Length - 1);

        internal override string Note
        {
            get
            {
                float value = _entry.Value;
                if (value <= 0f) return "Off - nothing changes";
                if (value >= 100f) return "Hard to spot";
                if (value >= 60f) return "Slight fade-in, small boost";
                if (value >= 40f) return "Some fade-in, good boost";
                if (value >= 25f) return "Clear fade-in, big boost";
                return "Obvious fade-in, biggest boost";
            }
        }

        internal override void Adjust(int direction)
        {
            int index = NearestStep(_entry.Value);
            index = Math.Max(0, Math.Min(Steps.Length - 1, index + direction));
            _entry.Value = Steps[index];
        }

        private static int NearestStep(float value)
        {
            int best = 0;
            float bestGap = float.MaxValue;

            for (int i = 0; i < Steps.Length; i++)
            {
                float gap = Math.Abs(Steps[i] - value);
                if (gap >= bestGap) continue;
                bestGap = gap;
                best = i;
            }

            return best;
        }
    }

    /// <summary>Cycles the panel's colour scheme. Applies live, no rebuild.</summary>
    internal sealed class ThemeRow : SettingRow
    {
        private readonly ConfigEntry<string> _entry;

        internal ThemeRow(ConfigEntry<string> entry) { _entry = entry; }

        internal override string Label => "Colour Theme";
        internal override string Value => _entry.Value;
        internal override string Note => "Press Left or Right to preview";

        internal override void Adjust(int direction) =>
            _entry.Value = Themes.Step(_entry.Value, direction);
    }
}
