using Lumen;
using Lumen.Tuning;
using Nivalis;
using UnityEngine;

int passed = 0, failed = 0, assertions = 0;
void Check(bool condition, string message) { assertions++; if (!condition) throw new InvalidOperationException(message); }
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}

Test("stable crowds receive complete scans at low FPS", () =>
{
    foreach (int count in new[] { 180, 190 })
    foreach (int rate in new[] { 5, 10, 15 })
    {
        using var f = new Fixture(count);
        f.Run(rate, 3);
        Check(CharacterRegistry.Copies >= 3, "Fixture must cross repeated registry refreshes");
        Check(f.Hidden == count, $"At {rate} FPS only {f.Hidden}/{count} far NPCs hidden; trailing NPCs were starved");
    }
});

Test("already hidden tail reappears after FPS falls and player approaches", () =>
{
    using var f = new Fixture(180);
    f.Run(60, 1);
    Check(f.Hidden == 180, "60 FPS preparation must hide the full crowd");
    f.MoveNear();
    f.Run(10, 3);
    Check(f.Hidden == 0, $"{f.Hidden} trailing NPCs stayed hidden after approaching at 10 FPS");
    Check(f.Optimizer.CulledCount == 0, "Culled count follows actual restoration");
});

Test("disabling distance culling restores the whole crowd at low FPS", () =>
{
    using var f = new Fixture(190);
    f.Run(60, 1);
    Check(f.Hidden == 190, "Setup must hide the non-divisible crowd");
    LumenConfig.NpcCullDistance.Value = 0;
    f.Run(10, 3);
    Check(f.Hidden == 0 && f.Optimizer.CulledCount == 0, "Distance zero must revisit and restore the tail");
});

Test("a bounded spawn and reorder is followed by complete coverage", () =>
{
    using var f = new Fixture(180);
    f.Run(60, 1);
    var spawned = f.Add(40);
    f.Run(10, 3);
    Check(spawned.All(c => !c.Renderers[0].enabled), $"Only {spawned.Count(c => !c.Renderers[0].enabled)}/40 newly spawned tail NPCs visited");
    CharacterRegistry.Entries.Reverse();
    f.MoveNear();
    f.Run(10, 3);
    Check(f.Hidden == 0, $"{f.Hidden} reordered survivors stayed hidden");
});

Test("shrinking below the cursor, empty snapshots and refill remain safe", () =>
{
    using var f = new Fixture(180);
    f.Run(60, 1);
    var removed = CharacterRegistry.Entries.Take(160).ToArray();
    foreach (var c in removed) CharacterRegistry.Disable(c);
    Check(removed.All(c => c.Renderers[0].enabled), "Disable callback restores removed NPCs");
    f.MoveNear();
    f.Run(10, 3);
    Check(f.Hidden == 0 && f.Optimizer.CulledCount == 0, "A cursor past the new list end wraps safely");
    foreach (var c in CharacterRegistry.Entries.ToArray()) CharacterRegistry.Disable(c);
    f.Run(10, 1);
    Check(CharacterRegistry.Entries.Count == 0 && f.Optimizer.CulledCount == 0, "Empty snapshot does no work");
    var refill = f.Add(40);
    f.Run(10, 1);
    Check(refill.All(c => !c.Renderers[0].enabled), "A refilled registry resumes scanning");
});

Test("OFF restores immediately without a camera", () =>
{
    using var f = new Fixture(180);
    f.Run(60, 1);
    Check(f.Hidden == 180, "Setup hides every NPC");
    Camera.main = null;
    LumenConfig.NpcOptimizerEnabled.Value = false;
    f.Optimizer.Tick(0.1f);
    Check(f.Hidden == 0 && f.Optimizer.CulledCount == 0, "OFF must restore before camera lookup");
});

Test("shadow option change restores and reapplies current setting", () =>
{
    using var f = new Fixture(40, shadows: true);
    LumenConfig.NpcCullDistance.Value = 0;
    LumenConfig.RemoveShadowProxies.Value = true;
    f.Run(60, 1);
    Check(f.Characters.All(c => c.Renderers[0].enabled && !c.Renderers[1].enabled), "Only shadow proxies are suppressed");
    LumenConfig.RemoveShadowProxies.Value = false;
    f.Run(10, 1);
    Check(f.Characters.All(c => c.Renderers.All(r => r.enabled)), "Option OFF restores old shadows");
});

Test("normal 60 FPS scanning and transition writes remain unchanged", () =>
{
    using var f = new Fixture(190);
    f.Run(60, 1);
    Check(f.Hidden == 190, "Normal 60 FPS crowd is covered");
    Check(f.Characters.All(c => c.Renderers[0].Writes.Count == 1), "Repeated wraps do not repeat unchanged renderer writes");
    f.MoveNear();
    f.Run(60, 1);
    Check(f.Hidden == 0 && f.Characters.All(c => c.Renderers[0].Writes.Count == 2), "Normal approach restores once per renderer");
});

Test("named protection OFF preserves legacy culling without name lookups", () =>
{
    using var f = new Fixture(3);
    ((Character)f.Characters[0]).NameDisplay.hasStory = true;
    ((Character)f.Characters[1]).NameDisplay.Working = true;
    ((Character)f.Characters[2]).NameDisplay.randomDialogue = true;
    foreach (Character c in f.Characters.Take(3))
    {
        c.NameDisplay.gameObject.activeInHierarchy = false;
        c.NameDisplay.isActiveAndEnabled = false;
    }
    f.Run(60, 0.5);
    Check(f.Hidden == 3, "OFF keeps legacy distance culling for named characters");
    Check(f.Characters.All(c => c.CastLookups == 0), "OFF must not query name or working state");
});

Test("named protection ON protects story, workers and dialogue but permits unnamed culling", () =>
{
    using var f = new Fixture(4);
    LumenConfig.ProtectNamedNpcs.Value = true;
    ((Character)f.Characters[0]).NameDisplay.hasStory = true;
    ((Character)f.Characters[1]).NameDisplay.Working = true;
    ((Character)f.Characters[2]).NameDisplay.randomDialogue = true;
    foreach (Character c in f.Characters.Take(3))
    {
        c.NameDisplay.gameObject.activeInHierarchy = false;
        c.NameDisplay.isActiveAndEnabled = false;
    }
    f.Run(60, 0.5);
    Check(f.Characters.Take(3).All(c => c.Renderers[0].enabled), "Named classifications stay protected even when their label UI is inactive");
    Check(!f.Characters[3].Renderers[0].enabled && f.Hidden == 1, "Unnamed far character remains eligible");
});

Test("enabling named protection restores an already culled character", () =>
{
    using var f = new Fixture(1);
    var c = (Character)f.Characters[0];
    c.NameDisplay.hasStory = true;
    f.Run(60, 0.5);
    Check(!c.Renderers[0].enabled, "Setup uses protection OFF");
    LumenConfig.ProtectNamedNpcs.Value = true;
    f.Run(10, 0.5);
    Check(c.Renderers[0].enabled && f.Optimizer.CulledCount == 0, "ON revisits and restores existing distance overrides");
    LumenConfig.ProtectNamedNpcs.Value = false;
    f.Run(10, 0.5);
    Check(!c.Renderers[0].enabled, "OFF returns to the old distance behavior");
});

Test("working and dialogue state changes are re-evaluated while far", () =>
{
    using var f = new Fixture(1);
    LumenConfig.ProtectNamedNpcs.Value = true;
    var c = (Character)f.Characters[0];
    f.Run(10, 0.5);
    Check(!c.Renderers[0].enabled, "Initially unnamed character can be culled");
    c.NameDisplay.Working = true;
    f.Run(10, 0.5);
    Check(c.Renderers[0].enabled, "Becoming a working staff member restores the character");
    c.NameDisplay.Working = false;
    f.Run(10, 0.5);
    Check(!c.Renderers[0].enabled, "End of working state removes that protection");
    c.NameDisplay.randomDialogue = true;
    f.Run(10, 0.5);
    Check(c.Renderers[0].enabled, "Dialogue state restores the character even if name display itself is suppressed");
    c.NameDisplay.randomDialogue = false;
    f.Run(10, 0.5);
    Check(!c.Renderers[0].enabled, "Ending dialogue removes that temporary protection");
});

Test("named distance protection leaves shadow cleanup active", () =>
{
    using var f = new Fixture(1, shadows: true);
    LumenConfig.ProtectNamedNpcs.Value = true;
    LumenConfig.RemoveShadowProxies.Value = true;
    var c = (Character)f.Characters[0];
    c.NameDisplay.hasStory = true;
    f.Run(10, 0.5);
    Check(c.Renderers[0].enabled && !c.Renderers[1].enabled, "Protected body stays visible while opted-in shadow proxy is cleaned");
    LumenConfig.RemoveShadowProxies.Value = false;
    f.Run(10, 0.5);
    Check(c.Renderers.All(r => r.enabled), "Shadow option OFF restores its own override independently");
});

Test("unknown, missing and faulted name metadata protects conservatively", () =>
{
    using var f = new Fixture(6);
    var unknown = new BaseCharacter { Renderers = f.Characters[0].Renderers };
    unknown.transform.position = new Vector3(100, 0, 0);
    CharacterRegistry.Entries[0] = unknown;
    ((Character)f.Characters[1]).NameDisplay = null;
    ((Character)f.Characters[2]).FailDisplay = true;
    ((Character)f.Characters[3]).NameDisplay.FailWorking = true;
    f.Characters[4].FailCast = true;
    ((Character)f.Characters[5]).NameDisplay.Alive = false;
    f.Run(10, 0.5);
    Check(f.Hidden == 6, "With protection OFF, even unreadable metadata uses legacy culling");
    LumenConfig.ProtectNamedNpcs.Value = true;
    LumenPlugin.Log.ThrowWarning = true;
    f.Run(10, 0.5);
    Check(CharacterRegistry.Entries.All(c => c.Renderers[0].enabled), "Missing or failed metadata restores existing hides even when warning logging also throws");
    Check(LumenPlugin.Log.WarningCalls == 1, "The actual name-fault warning path was exercised once");
});

Test("distance zero and master OFF never query name metadata", () =>
{
    using var f = new Fixture(1);
    LumenConfig.ProtectNamedNpcs.Value = true;
    LumenConfig.NpcCullDistance.Value = 0;
    f.Characters[0].FailCast = true;
    f.Run(10, 0.5);
    Check(f.Characters[0].CastLookups == 0 && f.Hidden == 0, "Distance zero bypasses named lookups");
    LumenConfig.NpcCullDistance.Value = 20;
    LumenConfig.NpcOptimizerEnabled.Value = false;
    f.Run(10, 0.5);
    Check(f.Characters[0].CastLookups == 0 && f.Hidden == 0, "Master OFF bypasses named lookups");
});

Console.WriteLine($"RESULT: {passed} passed, {failed} failed, {assertions} assertions.");
return failed == 0 ? 0 : 1;

internal sealed class Fixture : IDisposable
{
    internal readonly NpcOptimizer Optimizer;
    internal readonly List<BaseCharacter> Characters = new();
    private readonly bool _shadows;
    internal int Hidden => CharacterRegistry.Entries.Count(c => !c.Renderers[0].enabled);
    internal Fixture(int count, bool shadows = false)
    {
        CharacterRegistry.Reset();
        LumenConfig.NpcOptimizerEnabled.Value = true;
        LumenConfig.RemoveShadowProxies.Value = false;
        LumenConfig.ProtectNamedNpcs.Value = false;
        LumenPlugin.Log = new StubLog();
        LumenConfig.NpcCullDistance.Value = 20;
        Camera.main = new Camera();
        _shadows = shadows;
        Add(count);
        Optimizer = new NpcOptimizer();
    }
    internal BaseCharacter[] Add(int count)
    {
        var added = new List<BaseCharacter>();
        for (int i = 0; i < count; i++)
        {
            var body = new Renderer("Body"); body.Name();
            var shadow = new Renderer("Body_Shadow"); shadow.Name();
            var character = new Character { Renderers = _shadows ? new[] { body, shadow } : new[] { body } };
            character.transform.position = new Vector3(100, 0, 0);
            Characters.Add(character); added.Add(character); CharacterRegistry.Entries.Add(character);
        }
        return added.ToArray();
    }
    internal void Run(int fps, double seconds)
    {
        int frames = (int)Math.Ceiling(fps * seconds);
        for (int i = 0; i < frames; i++) Optimizer.Tick(1f / fps);
    }
    internal void MoveNear()
    {
        foreach (var c in CharacterRegistry.Entries) c.transform.position = new Vector3(1, 0, 0);
    }
    public void Dispose() => Optimizer.Dispose();
}
