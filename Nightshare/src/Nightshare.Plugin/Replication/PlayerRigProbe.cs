using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Nightshare.Replication
{
    /// <summary>
    /// Works out which transform in the player's hierarchy actually carries the facing.
    /// <para>
    /// Built after guessing that <c>PlayerGameObject.transform</c> held the yaw and being
    /// wrong: it reported a constant 48 degrees through an entire walk that obviously
    /// included turns. A first person rig usually splits the body, the yaw pivot and the
    /// camera across different nodes, and which one is which is not guessable from a type
    /// dump.
    /// </para>
    /// <para>
    /// Rather than guess a second time, this samples every transform in and around the
    /// player for a few seconds and reports which ones moved. Turn the character while it
    /// runs and the answer falls out of the numbers.
    /// </para>
    /// </summary>
    internal sealed class PlayerRigProbe
    {
        private sealed class Node
        {
            public string Path;
            public Transform Transform;
            public Vector3 FirstPosition;
            public float MaxPositionDelta;
            public int Samples;

            /// <summary>
            /// Tested in Nightshare.Core rather than reimplemented here. The previous
            /// version used max minus min, which reports a full circle as zero rotation
            /// and cost a whole test round.
            /// </summary>
            private Nightshare.Core.YawAccumulator _yaw;

            public float YawTravel => _yaw.Travel;
            public float PeakYawStep => _yaw.PeakStep;

            public void Sample(float yaw, Vector3 position)
            {
                _yaw.Add(yaw);

                var moved = (position - FirstPosition).magnitude;
                if (moved > MaxPositionDelta) MaxPositionDelta = moved;

                Samples++;
            }
        }

        private const float DurationSeconds = 6f;
        private const int MaxDepth = 4;

        private readonly List<Node> _nodes = new();
        private float _elapsed;
        private bool _running;

        public bool IsRunning => _running;

        /// <summary>Begin a probe. Safe to call again; it restarts.</summary>
        public void Begin(GameObject playerObject)
        {
            _nodes.Clear();
            _elapsed = 0f;
            _running = false;

            if (playerObject == null)
            {
                Log("Rig probe: no player object, is a save loaded?");
                return;
            }

            try
            {
                // Start from the topmost parent so the yaw pivot is included even if it
                // sits above the object PlayerManager hands us.
                var root = playerObject.transform;
                while (root.parent != null) root = root.parent;

                Collect(root, root.name, 0);

                // The camera is a strong candidate and may live outside the player's
                // hierarchy entirely.
                var cam = Camera.main;
                if (cam != null) Add(cam.transform, $"[Camera.main] {cam.name}");

                _running = _nodes.Count > 0;

                Log($"Rig probe: watching {_nodes.Count} transform(s) for {DurationSeconds:0}s. " +
                    $"TURN YOUR CHARACTER NOW.");
            }
            catch (Exception ex)
            {
                Log($"Rig probe failed to start: {ex.Message}");
                _running = false;
            }
        }

        private void Collect(Transform t, string path, int depth)
        {
            Add(t, path);
            if (depth >= MaxDepth) return;

            var count = t.childCount;
            for (int i = 0; i < count; i++)
            {
                var child = t.GetChild(i);
                if (child == null) continue;
                Collect(child, $"{path}/{child.name}", depth + 1);
            }
        }

        private void Add(Transform t, string path)
        {
            if (t == null) return;
            _nodes.Add(new Node
            {
                Path = path,
                Transform = t,
                FirstPosition = t.position,
            });
        }

        public void Tick(float deltaTime)
        {
            if (!_running) return;

            _elapsed += deltaTime;

            foreach (var n in _nodes)
            {
                if (n.Transform == null) continue;

                try
                {
                    n.Sample(n.Transform.eulerAngles.y, n.Transform.position);
                }
                catch (Exception)
                {
                    // A destroyed transform mid-probe is not worth aborting over.
                }
            }

            if (_elapsed >= DurationSeconds)
            {
                _running = false;
                Report();
            }
        }

        private void Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Nightshare player rig probe");
            sb.AppendLine($"sampled {_nodes.Count} transform(s) over {_elapsed:0.0}s");
            sb.AppendLine();
            sb.AppendLine("Total angular travel, summed between consecutive samples.");
            sb.AppendLine("A full 360 turn reads as ~360. Animation wobble reads as a few degrees.");
            sb.AppendLine();
            sb.AppendLine($"{"yaw travel",11}  {"peak step",10}  {"moved",8}  path");
            sb.AppendLine(new string('-', 92));

            _nodes.Sort((a, b) => b.YawTravel.CompareTo(a.YawTravel));

            var shown = 0;
            foreach (var n in _nodes)
            {
                if (n.Samples == 0) continue;
                if (shown++ >= 25) break;

                sb.AppendLine($"{n.YawTravel,11:0.0}  {n.PeakYawStep,10:0.00}  " +
                              $"{n.MaxPositionDelta,8:0.00}  {n.Path}");
            }

            // The headline, so the answer is in the BepInEx log and not only in a file.
            Node best = null;
            foreach (var n in _nodes)
                if (n.Samples > 0 && (best == null || n.YawTravel > best.YawTravel)) best = n;

            // A deliberate turn covers tens of degrees. Idle animation drifts a few. The
            // threshold sits well above the 5 to 6 degrees of wobble seen on the rig.
            if (best != null && best.YawTravel > 45f)
            {
                Log($"Rig probe: facing is on '{best.Path}' " +
                    $"(turned {best.YawTravel:0} degrees total, peak step {best.PeakYawStep:0.0})");
            }
            else
            {
                Log($"Rig probe: NOTHING rotated meaningfully (best was " +
                    $"{(best == null ? "nothing" : $"{best.YawTravel:0.0} degrees on '{best.Path}'")}). " +
                    $"Either the character was not turned, or the facing is not on a transform.");
            }

            try
            {
                var path = Path.Combine(NightsharePlugin.ArtifactsDir, "player-rig-probe.txt");
                File.WriteAllText(path, sb.ToString());
                Log($"Rig probe: full report written to {path}");
            }
            catch (Exception ex)
            {
                Log($"Rig probe: could not write the report ({ex.Message}), dumping inline:");
                Log(sb.ToString());
            }
        }

        private static void Log(string m) => NightsharePlugin.Logger?.LogInfo(m);
    }
}
