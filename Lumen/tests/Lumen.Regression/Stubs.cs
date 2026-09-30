using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    public sealed class Il2CppArrayBase<T>
    {
        private readonly T[] _values;
        public Il2CppArrayBase(T[] values) { _values = values; }
        public int Length => _values.Length;
        public T this[int index] => _values[index];
    }
}

namespace UnityEngine
{
    public class Object
    {
        private static int _nextId;
        private readonly int _id = ++_nextId;
        public bool Alive = true;
        public string name = "object";
        public int GetInstanceID() { if (!Alive) throw new InvalidOperationException("Destroyed target"); return _id; }
        public static bool operator ==(Object a, Object b)
        {
            bool aNull = ReferenceEquals(a, null) || !a.Alive;
            bool bNull = ReferenceEquals(b, null) || !b.Alive;
            return aNull || bNull ? aNull == bNull : ReferenceEquals(a, b);
        }
        public static bool operator !=(Object a, Object b) => !(a == b);
        public override bool Equals(object other) => ReferenceEquals(this, other);
        public override int GetHashCode() => _id;
        public static int DestroyCalls;
        public static void Destroy(Object target) { DestroyCalls++; target.Alive = false; }
        public static void DontDestroyOnLoad(Object target) { }
    }
    public class Transform : Object { public Vector3 position; }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float xValue, float yValue, float zValue) { x = xValue; y = yValue; z = zValue; }
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x-b.x, a.y-b.y, a.z-b.z);
        public float sqrMagnitude => x*x+y*y+z*z;
    }
    public class Component : Object { public Transform transform = new Transform(); public bool isActiveAndEnabled = true; }
    public class Camera : Component { public static Camera main; }
    public class LODGroup : Component { }
    public sealed class Renderer : Component
    {
        private bool _enabled;
        public Renderer(string objectName, bool original = true) { name = objectName; _enabled = original; }
        public int FailBeforeWrites;
        public int FailAfterWrites;
        public int FailReads;
        public bool IgnoreWrites;
        public Action<bool> OnWrite;
        public readonly List<bool> Writes = new List<bool>();
        public bool enabled
        {
            get
            {
                if (!Alive || FailReads > 0) { if (FailReads > 0) FailReads--; throw new InvalidOperationException("Read failed"); }
                return _enabled;
            }
            set
            {
                Writes.Add(value);
                if (!Alive || FailBeforeWrites > 0) { if (FailBeforeWrites > 0) FailBeforeWrites--; throw new InvalidOperationException("Setter failed before write"); }
                if (!IgnoreWrites) _enabled = value;
                OnWrite?.Invoke(value);
                if (FailAfterWrites > 0) { FailAfterWrites--; throw new InvalidOperationException("Setter mutated then threw"); }
            }
        }
    }
    public enum HideFlags { HideAndDontSave }
    public class GameObject : Object
    {
        public GameObject(string objectName) { name = objectName; }
        public HideFlags hideFlags;
        public T AddComponent<T>() where T : new() => new T();
    }
    public static class Application { public static string unityVersion = "stub"; }
    public static class SystemInfo
    {
        public static string graphicsDeviceName = "stub", graphicsDeviceType = "stub", graphicsDeviceVersion = "stub", processorType = "stub";
        public static int graphicsMemorySize, processorCount, systemMemorySize;
    }
}

namespace Nivalis
{
    public class BaseCharacter : UnityEngine.Component
    {
        public UnityEngine.Renderer[] Renderers = Array.Empty<UnityEngine.Renderer>();
        public UnityEngine.LODGroup[] ChildGroups = Array.Empty<UnityEngine.LODGroup>();
        public UnityEngine.LODGroup[] ParentGroups = Array.Empty<UnityEngine.LODGroup>();
        public bool ThrowEnumeration, ThrowGroups, NullGroups;
        public Il2CppArrayBase<T> GetComponentsInChildren<T>(bool inactive)
        {
            if (ThrowEnumeration) throw new InvalidOperationException("Character enumeration failed");
            if (typeof(T) == typeof(UnityEngine.Renderer)) return new Il2CppArrayBase<T>((T[])(object)Renderers);
            if (typeof(T) == typeof(UnityEngine.LODGroup))
            {
                if (ThrowGroups) throw new InvalidOperationException("Group query failed");
                if (NullGroups) return null;
                return new Il2CppArrayBase<T>((T[])(object)ChildGroups);
            }
            return new Il2CppArrayBase<T>(Array.Empty<T>());
        }
        public Il2CppArrayBase<T> GetComponentsInParent<T>(bool inactive)
        {
            if (ThrowGroups) throw new InvalidOperationException("Parent group query failed");
            if (NullGroups) return null;
            return new Il2CppArrayBase<T>((T[])(object)ParentGroups);
        }
    }
}

namespace BepInEx.Configuration
{
    public sealed class ConfigEntry<T> { public T Value; public ConfigEntry(T initial) { Value = initial; } }
    public sealed class ConfigFile
    {
        private readonly Dictionary<string, object> _entries = new Dictionary<string, object>();
        public ConfigEntry<T> Bind<T>(string section, string key, T value, object description)
        {
            string name = section + "/" + key;
            if (_entries.TryGetValue(name, out object existing)) return (ConfigEntry<T>)existing;
            var entry = new ConfigEntry<T>(value); _entries.Add(name, entry); return entry;
        }
    }
    public sealed class ConfigDescription { public ConfigDescription(string text, object acceptable = null) { } }
    public sealed class AcceptableValueRange<T> { public AcceptableValueRange(T min, T max) { } }
    public sealed class AcceptableValueList<T> { public AcceptableValueList(params T[] values) { } }
}
namespace BepInEx
{
    public sealed class BepInPlugin : Attribute { public BepInPlugin(string id, string title, string version) { } }
    public static class Paths { public static string BepInExRootPath = "offline-stub"; }
}
namespace BepInEx.Logging
{
    public sealed class ManualLogSource
    {
        public readonly List<string> Messages = new List<string>();
        public void LogInfo(object text) { Messages.Add(text.ToString()); }
        public void LogWarning(object text) { Messages.Add(text.ToString()); }
        public void LogError(object text) { Messages.Add(text.ToString()); }
    }
}
namespace BepInEx.Unity.IL2CPP
{
    public class BasePlugin
    {
        public BepInEx.Logging.ManualLogSource Log = new BepInEx.Logging.ManualLogSource();
        public BepInEx.Configuration.ConfigFile Config = new BepInEx.Configuration.ConfigFile();
        public virtual void Load() { }
        public virtual bool Unload() => true;
    }
}
namespace HarmonyLib { public sealed class Harmony { public Harmony(string id) { } } }
namespace Il2CppInterop.Runtime.Injection { public static class ClassInjector { public static void RegisterTypeInIl2Cpp<T>() { } } }
namespace Lumen.Diagnostics
{
    internal static class Themes { internal static string[] Names = { "Synthwave" }; }
    internal sealed class Harness
    {
        internal bool ThrowRestore;
        internal int RestoreCalls;
        internal void RestoreAll() { RestoreCalls++; if (ThrowRestore) throw new InvalidOperationException("Harness restore failed"); }
    }
}
namespace Lumen
{
    internal static class CharacterRegistry
    {
        internal static readonly List<Nivalis.BaseCharacter> Entries = new List<Nivalis.BaseCharacter>();
        internal static event Action<Nivalis.BaseCharacter> Disabled;
        internal static int Subscribers => Disabled?.GetInvocationList().Length ?? 0;
        internal static void CopyInto(List<Nivalis.BaseCharacter> into) { into.Clear(); into.AddRange(Entries); }
        internal static void Install(HarmonyLib.Harmony harmony) { }
        internal static void Disable(Nivalis.BaseCharacter character) { Disabled?.Invoke(character); Entries.Remove(character); }
        internal static void Reset() { Entries.Clear(); Disabled = null; }
    }
    internal static class DisplayMode { internal static void Install(HarmonyLib.Harmony harmony) { } }
    internal sealed class LumenBehaviour
    {
        internal static Tuning.NpcOptimizer Optimizer;
        internal static Diagnostics.Harness Harness;
    }
}
