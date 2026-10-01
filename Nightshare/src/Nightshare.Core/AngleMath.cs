using System;

namespace Nightshare.Core
{
    /// <summary>
    /// Angle helpers, in Core so they can be tested without Unity.
    /// </summary>
    public static class AngleMath
    {
        /// <summary>
        /// Shortest signed difference between two angles in degrees, in the range
        /// (-180, 180]. Going 359 to 1 is +2, not -358.
        /// </summary>
        public static float ShortestDelta(float from, float to)
        {
            var delta = (to - from) % 360f;
            if (delta > 180f) delta -= 360f;
            if (delta < -180f) delta += 360f;
            return delta;
        }
    }

    /// <summary>
    /// Measures how far an angle has turned in total.
    /// <para>
    /// <b>This exists because max-minus-min is wrong and it cost a test round.</b> A full
    /// circle sweeps 0 to 360, so the range is 360, and any wraparound correction cancels
    /// it to zero: a probe built that way reports a complete turn as no rotation at all.
    /// Accumulating the absolute shortest delta between consecutive samples handles
    /// partial turns, turns through north, and full circles alike.
    /// </para>
    /// </summary>
    public struct YawAccumulator
    {
        private float _previous;
        private bool _hasPrevious;

        /// <summary>Sum of absolute angular change across every sample so far.</summary>
        public float Travel { get; private set; }

        /// <summary>Largest single-sample change. Separates a deliberate turn from jitter.</summary>
        public float PeakStep { get; private set; }

        public int Samples { get; private set; }

        public void Add(float angleDegrees)
        {
            if (_hasPrevious)
            {
                var step = Math.Abs(AngleMath.ShortestDelta(_previous, angleDegrees));
                Travel += step;
                if (step > PeakStep) PeakStep = step;
            }

            _previous = angleDegrees;
            _hasPrevious = true;
            Samples++;
        }

        public void Reset()
        {
            _previous = 0f;
            _hasPrevious = false;
            Travel = 0f;
            PeakStep = 0f;
            Samples = 0;
        }
    }
}
