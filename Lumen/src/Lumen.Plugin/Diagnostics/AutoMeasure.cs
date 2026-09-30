using System;
using System.Collections.Generic;

namespace Lumen.Diagnostics
{
    /// <summary>
    /// Turns every probe flip into an A/B reading automatically: the before value comes
    /// from the rolling window, the after value from a fixed settle-then-sample window, and
    /// the result goes to both the overlay and the log.
    /// </summary>
    internal sealed class AutoMeasure
    {
        private enum State { Idle, Settling, Measuring }

        private readonly FrameStats _stats;
        private State _state = State.Idle;

        private Probe _probe;
        private bool _wentActive;
        private float _beforeMs;
        private float _timer;

        private double _sum;
        private int _frames;

        private readonly List<string> _log = new List<string>();
        private const int LogCapacity = 14;

        internal AutoMeasure(FrameStats stats) { _stats = stats; }

        internal bool Busy => _state != State.Idle;

        /// <summary>Progress text for the overlay header while a reading is in flight.</summary>
        internal string BusyText
        {
            get
            {
                switch (_state)
                {
                    case State.Settling: return $"settling {_timer:0.0}s";
                    case State.Measuring: return $"measuring {_timer:0.0}s";
                    default: return null;
                }
            }
        }

        internal IReadOnlyList<string> Log => _log;

        /// <summary>
        /// Called immediately after a probe was flipped. <paramref name="wentActive"/> is
        /// the probe's new state, so the log line says which direction was measured.
        /// </summary>
        internal void Begin(Probe probe, bool wentActive)
        {
            _probe = probe;
            _wentActive = wentActive;

            // The before value comes from the window that ended the instant the key was hit.
            _beforeMs = _stats.AverageMs(2f);

            _state = State.Settling;
            _timer = Math.Max(0.1f, LumenConfig.SettleSeconds.Value);
            _sum = 0;
            _frames = 0;
        }

        internal void Tick(float unscaledDeltaTime)
        {
            if (_state == State.Idle) return;

            _timer -= unscaledDeltaTime;

            if (_state == State.Settling)
            {
                if (_timer > 0f) return;

                _state = State.Measuring;
                _timer = Math.Max(0.25f, LumenConfig.MeasureSeconds.Value);
                return;
            }

            _sum += unscaledDeltaTime * 1000.0;
            _frames++;

            if (_timer > 0f) return;

            Finish();
        }

        /// <summary>Drop a reading in flight without reporting it, e.g. when everything is reset.</summary>
        internal void Abort()
        {
            _state = State.Idle;
            _probe = null;
        }

        private void Finish()
        {
            _state = State.Idle;

            if (_probe == null || _frames == 0) return;

            float afterMs = (float)(_sum / _frames);
            float deltaMs = afterMs - _beforeMs;

            float beforeFps = FrameStats.ToFps(_beforeMs);
            float afterFps = FrameStats.ToFps(afterMs);
            float deltaFps = afterFps - beforeFps;

            string direction = _wentActive ? "ON " : "OFF";

            // The status records what the probe actually did, so a reading of zero is not
            // ambiguous between "free" and "never applied".
            string status = string.IsNullOrEmpty(_probe.Status) ? "" : $"  [{_probe.Status}]";

            string line =
                $"{_probe.Name} {direction}  " +
                $"{_beforeMs:0.0}ms ({beforeFps:0.0} fps) -> {afterMs:0.0}ms ({afterFps:0.0} fps)  " +
                $"d {deltaMs:+0.0;-0.0;0.0}ms  {deltaFps:+0.0;-0.0;0.0} fps{status}";

            Push(line);
            LumenPlugin.Log.LogInfo($"[A/B] {line}");

            _probe = null;
        }

        private void Push(string line)
        {
            _log.Add(line);
            if (_log.Count > LogCapacity) _log.RemoveAt(0);
        }

        internal void PushNote(string note)
        {
            Push(note);
            LumenPlugin.Log.LogInfo($"[A/B] {note}");
        }
    }
}
