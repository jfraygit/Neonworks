using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    public sealed class Il2CppArrayBase<T>(T[] values)
    {
        public int Length => values.Length;
        public T this[int index] => values[index];
    }
}

namespace UnityEngine
{
    public class Object
    {
        private static int _next;
        private readonly int _id = ++_next;
        public bool Alive = true;
        public string name = "stub";
        public int GetInstanceID() { if (!Alive) throw new InvalidOperationException("Destroyed object"); return _id; }
        public static bool operator ==(Object a, Object b)
        {
            bool an = ReferenceEquals(a, null) || !a.Alive;
            bool bn = ReferenceEquals(b, null) || !b.Alive;
            return an || bn ? an == bn : ReferenceEquals(a, b);
        }
        public static bool operator !=(Object a, Object b) => !(a == b);
        public override bool Equals(object other) => ReferenceEquals(this, other);
        public override int GetHashCode() => _id;
    }
    public struct Vector3(float x, float y, float z)
    {
        public float x = x, y = y, z = z;
        public float sqrMagnitude => x * x + y * y + z * z;
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static float Distance(Vector3 a, Vector3 b) => MathF.Sqrt((a - b).sqrMagnitude);
    }
    public class Transform { public Vector3 position; }
    public class GameObject { public bool activeSelf = true, activeInHierarchy = true; }
    public class Component : Object
    {
        public Transform transform = new();
        public GameObject gameObject = new();
        public bool isActiveAndEnabled = true;
    }
    public class Camera : Component { public static Camera main; }
    public sealed class Renderer(string rendererName, bool initial = true) : Component
    {
        private bool _enabled = initial;
        public readonly List<bool> Writes = new();
        public bool enabled
        {
            get { if (!Alive) throw new InvalidOperationException("Destroyed renderer"); return _enabled; }
            set { if (!Alive) throw new InvalidOperationException("Destroyed renderer"); Writes.Add(value); _enabled = value; }
        }
        public void Name() => name = rendererName;
    }
}

namespace Nivalis
{
    public class BaseCharacter : UnityEngine.Component
    {
        public UnityEngine.Renderer[] Renderers = Array.Empty<UnityEngine.Renderer>();
        public int RendererEnumerations;
        public int CastLookups;
        public bool FailCast;
        public T TryCast<T>() where T : class
        {
            CastLookups++;
            if (FailCast) throw new InvalidOperationException("Cast lookup failed");
            return this as T;
        }
        public Il2CppArrayBase<T> GetComponentsInChildren<T>(bool includeInactive)
        {
            if (!Alive) throw new InvalidOperationException("Destroyed character");
            RendererEnumerations++;
            return new((T[])(object)Renderers);
        }
    }
    public sealed class Character : BaseCharacter
    {
        private CharacterNameDisplay _display = new();
        public bool FailDisplay;
        public CharacterNameDisplay NameDisplay
        {
            get { if (FailDisplay) throw new InvalidOperationException("Name display lookup failed"); return _display; }
            set => _display = value;
        }
    }
    public sealed class CharacterNameDisplay : UnityEngine.Component
    {
        public bool hasStory, randomDialogue, Working, FailWorking;
        public bool IsWorking()
        {
            if (FailWorking) throw new InvalidOperationException("Working state lookup failed");
            return Working;
        }
    }
}

namespace BepInEx.Configuration
{
    public sealed class ConfigEntry<T>(T initial) { public T Value = initial; }
}

namespace Lumen
{
    internal static class LumenConfig
    {
        internal static BepInEx.Configuration.ConfigEntry<bool> NpcOptimizerEnabled = new(true);
        internal static BepInEx.Configuration.ConfigEntry<bool> RemoveShadowProxies = new(false);
        internal static BepInEx.Configuration.ConfigEntry<bool> ProtectNamedNpcs = new(false);
        internal static BepInEx.Configuration.ConfigEntry<float> NpcCullDistance = new(20f);
    }
    internal sealed class StubLog
    {
        internal bool ThrowWarning;
        internal int WarningCalls;
        internal void LogInfo(object message) { }
        internal void LogWarning(object message)
        {
            WarningCalls++;
            if (ThrowWarning) throw new InvalidOperationException("Warning logger failed");
        }
        internal void LogError(object message) { }
    }
    internal static class LumenPlugin { internal static StubLog Log = new(); }
    internal static class CharacterRegistry
    {
        internal static readonly List<Nivalis.BaseCharacter> Entries = new();
        internal static event Action<Nivalis.BaseCharacter> Disabled;
        internal static int Copies;
        internal static void CopyInto(List<Nivalis.BaseCharacter> into) { Copies++; into.Clear(); into.AddRange(Entries); }
        internal static void Disable(Nivalis.BaseCharacter character) { Disabled?.Invoke(character); Entries.Remove(character); }
        internal static void Reset() { Entries.Clear(); Disabled = null; Copies = 0; }
    }
}
