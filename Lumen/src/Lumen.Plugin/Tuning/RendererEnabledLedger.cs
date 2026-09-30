using System;
using System.Collections.Generic;

namespace Lumen.Tuning
{
    internal interface IRendererEnabledTarget
    {
        int Id { get; }
        bool IsAlive { get; }
        bool Enabled { get; set; }
    }

    // Keep the actual target, not merely an ID that requires a later registry/hierarchy lookup.
    internal sealed class RendererEnabledLedger
    {
        private sealed class Entry
        {
            internal readonly int OwnerId;
            internal readonly IRendererEnabledTarget Target;
            internal readonly bool Original;
            internal bool RestoreRequested, Writing, Restoring;
            internal Entry(int ownerId, IRendererEnabledTarget target, bool original)
            { OwnerId = ownerId; Target = target; Original = original; }
        }

        private readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();
        private readonly Dictionary<int, int> _ownerCounts = new Dictionary<int, int>();
        internal int Count => _entries.Count;
        internal int PendingRestoreCount
        {
            get
            {
                int count = 0;
                foreach (var entry in _entries.Values) if (entry.RestoreRequested) count++;
                return count;
            }
        }

        internal void Hide(int ownerId, IRendererEnabledTarget target)
        {
            if (!_entries.TryGetValue(target.Id, out var entry))
            {
                if (!target.IsAlive) throw new InvalidOperationException("Renderer no longer exists.");
                entry = new Entry(ownerId, target, target.Enabled);
                _entries.Add(target.Id, entry);
                _ownerCounts.TryGetValue(ownerId, out int count);
                _ownerCounts[ownerId] = count + 1;
            }
            if (entry.OwnerId != ownerId || entry.RestoreRequested || entry.Writing || entry.Restoring)
                throw new InvalidOperationException("Renderer is already owned or awaiting restoration.");

            entry.Writing = true;
            try
            {
                if (!entry.Target.IsAlive) throw new InvalidOperationException("Renderer identity changed.");
                if (entry.Target.Enabled) entry.Target.Enabled = false;
                if (entry.Target.Enabled) throw new InvalidOperationException("Renderer disable was not retained.");
            }
            catch
            {
                entry.RestoreRequested = true;
                throw;
            }
            finally
            {
                entry.Writing = false;
                if (entry.RestoreRequested) TryRestore(entry);
            }
            if (entry.RestoreRequested)
                throw new InvalidOperationException("Renderer was released during the update.");
        }

        internal bool RestoreRenderer(int rendererId)
        {
            if (!_entries.TryGetValue(rendererId, out var entry)) return true;
            entry.RestoreRequested = true;
            return TryRestore(entry);
        }

        internal bool RestoreOwner(int ownerId)
        {
            if (!_ownerCounts.ContainsKey(ownerId)) return true;
            bool complete = true;
            foreach (var entry in new List<Entry>(_entries.Values))
            {
                if (entry.OwnerId != ownerId) continue;
                entry.RestoreRequested = true;
                if (!TryRestore(entry)) complete = false;
            }
            return complete;
        }

        internal bool RestoreAll()
        {
            if (_entries.Count == 0) return true;
            foreach (var entry in new List<Entry>(_entries.Values))
            {
                entry.RestoreRequested = true;
                TryRestore(entry);
            }
            return _entries.Count == 0;
        }

        private bool TryRestore(Entry entry)
        {
            // A nested restoration may already have released a later entry in our snapshot.
            if (!_entries.TryGetValue(entry.Target.Id, out var owned) || !ReferenceEquals(owned, entry)) return true;
            if (entry.Writing || entry.Restoring) return false;
            entry.Restoring = true;
            try
            {
                if (entry.Target.IsAlive)
                {
                    if (entry.Target.Enabled != entry.Original) entry.Target.Enabled = entry.Original;
                    if (entry.Target.Enabled != entry.Original) return false;
                }
                // Another release may have run from inside the Unity setter. Remove only this entry.
                if (_entries.TryGetValue(entry.Target.Id, out var current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(entry.Target.Id);
                    int remaining = _ownerCounts[entry.OwnerId] - 1;
                    if (remaining == 0) _ownerCounts.Remove(entry.OwnerId);
                    else _ownerCounts[entry.OwnerId] = remaining;
                }
                return true;
            }
            catch { return false; }
            finally { entry.Restoring = false; }
        }
    }
}
