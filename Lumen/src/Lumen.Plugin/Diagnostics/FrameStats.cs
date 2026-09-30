using System;

namespace Lumen.Diagnostics
{
    /// <summary>
    /// Rolling frame-time statistics. The 1% low is the number that actually reflects
    /// "struggling badly". An average of 60 with a 1% low of 22 feels worse than a flat 50.
    /// </summary>
    internal sealed class FrameStats
    {
        // 4096 frames is ~28s at 144fps, comfortably more than the longest window we ask for.
        private const int Capacity = 4096;

        private readonly float[] _ms = new float[Capacity];
        private readonly float[] _at = new float[Capacity];
        private int _head;
        private int _count;

        private readonly float[] _scratch = new float[Capacity];

        private float _now;

        /// <summary>Total frames seen since load. Used to gate readings during loading screens.</summary>
        internal int TotalFrames { get; private set; }

        internal float LastMs { get; private set; }

        internal int Gen0Collections { get; private set; }
        private int _gen0AtLastSecond;
        private float _gen0WindowStart;
        internal int Gen0PerSecond { get; private set; }

        internal void Record(float unscaledDeltaTime)
        {
            float ms = unscaledDeltaTime * 1000f;
            LastMs = ms;
            _now += unscaledDeltaTime;
            TotalFrames++;

            _ms[_head] = ms;
            _at[_head] = _now;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;

            try
            {
                Gen0Collections = GC.CollectionCount(0);
            }
            catch (Exception)
            {
                // Not fatal; the counter is a hint, not a result.
            }

            if (_now - _gen0WindowStart >= 1f)
            {
                Gen0PerSecond = Gen0Collections - _gen0AtLastSecond;
                _gen0AtLastSecond = Gen0Collections;
                _gen0WindowStart = _now;
            }
        }

        /// <summary>Mean frame time in ms over the last <paramref name="seconds"/>. Zero if no samples.</summary>
        internal float AverageMs(float seconds)
        {
            int n = Collect(seconds);
            if (n == 0) return 0f;

            double sum = 0;
            for (int i = 0; i < n; i++) sum += _scratch[i];
            return (float)(sum / n);
        }

        /// <summary>
        /// Mean of the worst 1% of frames over the window, expressed as FPS. Falls back to
        /// the single worst frame when the window is too short for 1% to mean anything.
        /// </summary>
        internal float OnePercentLowFps(float seconds)
        {
            int n = Collect(seconds);
            if (n == 0) return 0f;

            Array.Sort(_scratch, 0, n);

            int worst = Math.Max(1, n / 100);
            double sum = 0;
            for (int i = n - worst; i < n; i++) sum += _scratch[i];
            double meanMs = sum / worst;

            return meanMs <= 0 ? 0f : (float)(1000.0 / meanMs);
        }

        internal static float ToFps(float ms) => ms <= 0f ? 0f : 1000f / ms;

        /// <summary>Copies the frames inside the window into <see cref="_scratch"/>, newest first order irrelevant.</summary>
        private int Collect(float seconds)
        {
            float cutoff = _now - seconds;
            int n = 0;

            for (int i = 0; i < _count; i++)
            {
                int idx = _head - 1 - i;
                if (idx < 0) idx += Capacity;
                if (_at[idx] < cutoff) break;
                _scratch[n++] = _ms[idx];
            }

            return n;
        }
    }
}
