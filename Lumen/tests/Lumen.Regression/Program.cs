using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using Lumen;
using Lumen.Diagnostics;
using Lumen.Tuning;
using Nivalis;
using UnityEngine;
using Object = UnityEngine.Object;

internal static class Program
{
    private static int _passed, _failed, _assertions;
    private static int Main()
    {
        Run("Production config binding preserves existing choices", ExistingConfiguration);
        Run("Master OFF restores with absent camera and registry", OffWithoutCamera);
        Run("Originally disabled renderers remain disabled", OriginalFalse);
        Run("Cleaned LOD option changes are applied both ways", LodOptionChanges);
        Run("Cleaned shadow option changes are applied both ways", ShadowOptionChanges);
        Run("Option reset restores before camera lookup", OptionChangeWithoutCamera);
        Run("Lost registry restores retained live targets", LostRegistry);
        Run("Release restores without current hierarchy access", ReleaseWithoutHierarchy);
        Run("Culled to Cleaned restores detached renderer", DetachedRenderer);
        Run("Partial Apply failure restores earlier writes and retains failure", PartialApply);
        Run("Pending recovery blocks new mutations", PendingStopsMutations);
        Run("Disable callback during setter cannot commit completed state", ReentrantDisable);
        Run("Ledger retains original false through repeated Hide", LedgerOriginalFalse);
        Run("Ledger recovers mutation before setter exception", LedgerMutateThenThrow);
        Run("Ledger retains failed restoration and retries", LedgerRetry);
        Run("Ledger requires successful readback", LedgerReadback);
        Run("Ledger restores owner independently of other owners", LedgerOwnerIsolation);
        Run("Ledger forgets destroyed target without writing", LedgerDestroyed);
        Run("Ledger handles restoration requested inside Hide setter", LedgerReentrantHide);
        Run("Nested restoration does not overwrite externally changed released target", LedgerStaleRestoreSnapshot);
        Run("Actual Unload keeps recovery alive until restoration succeeds", UnloadPending);
        Run("Actual Unload returns false and retains state on exception", UnloadException);
        Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed; {_assertions} assertions.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action action)
    {
        CharacterRegistry.Reset();
        LumenConfig.Bind(new ConfigFile());
        LumenPlugin.Log = new ManualLogSource();
        LumenBehaviour.Optimizer = null;
        LumenBehaviour.Harness = null;
        Camera.main = new Camera();
        Object.DestroyCalls = 0;
        try { action(); _passed++; Console.WriteLine("PASS: " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL: " + name + " :: " + ex.Message); }
    }

    private static void Check(bool condition, string message)
    { _assertions++; if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(Action action, string message)
    {
        bool threw = false;
        try { action(); } catch (InvalidOperationException) { threw = true; }
        Check(threw, message);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly NpcOptimizer Optimizer = new NpcOptimizer();
        internal readonly BaseCharacter Character = new BaseCharacter();
        internal Fixture(params Renderer[] renderers)
        {
            Character.Renderers = renderers;
            CharacterRegistry.Entries.Add(Character);
            LumenConfig.NpcOptimizerEnabled.Value = true;
            LumenConfig.NpcCullDistance.Value = 0;
            LumenConfig.CollapseStackedLods.Value = false;
            LumenConfig.RemoveShadowProxies.Value = false;
        }
        internal void Tick(float delta = 0.5f) => Optimizer.Tick(delta);
        public void Dispose() { Optimizer.Dispose(); }
    }
    private sealed class Target : IRendererEnabledTarget
    {
        private readonly Renderer _renderer;
        public int Id { get; }
        internal Target(Renderer renderer) { _renderer = renderer; Id = renderer.GetInstanceID(); }
        public bool IsAlive => _renderer.Alive;
        public bool Enabled { get => _renderer.enabled; set => _renderer.enabled = value; }
    }

    private static void ExistingConfiguration()
    {
        var config = new ConfigFile();
        config.Bind("NPCs","CollapseStackedLods",true,"existing");
        config.Bind("NPCs","RemoveShadowProxies",true,"existing");
        config.Bind("NPCs","CullDistance",42f,"existing");
        LumenConfig.Bind(config);
        Check(LumenConfig.CollapseStackedLods.Value && LumenConfig.RemoveShadowProxies.Value, "Existing user options were reset");
        Check(LumenConfig.NpcCullDistance.Value == 42f, "Existing distance was reset");
    }
    private static void OffWithoutCamera()
    {
        var a = new Renderer("body_LOD_0"); var b = new Renderer("body_LOD_1");
        using var f = new Fixture(a,b); LumenConfig.CollapseStackedLods.Value=true; f.Tick();
        Check(!b.enabled && f.Optimizer.CleanedCount==1, "Expected initial Cleaned override");
        CharacterRegistry.Entries.Clear(); Camera.main=null; LumenConfig.NpcOptimizerEnabled.Value=false; f.Tick(0);
        Check(a.enabled && b.enabled, "OFF failed without registry/camera");
        Check(f.Optimizer.PendingRestoreCount==0 && f.Optimizer.CleanedCount==0 && f.Optimizer.CulledCount==0, "OFF left bookkeeping");
    }
    private static void OriginalFalse()
    {
        var a = new Renderer("body_LOD_0"); var b = new Renderer("body_LOD_1",false);
        using var f = new Fixture(a,b); LumenConfig.CollapseStackedLods.Value=true; f.Tick();
        LumenConfig.NpcOptimizerEnabled.Value=false; Camera.main=null; f.Tick(0);
        Check(!b.enabled && !b.Writes.Any(value=>value), "Originally false target was enabled");
        Check(a.enabled && f.Optimizer.PendingRestoreCount==0, "Original states failed restoration");
    }
    private static void LodOptionChanges()
    {
        var a=new Renderer("body_LOD_0"); var b=new Renderer("body_LOD_1");
        using var f=new Fixture(a,b); f.Tick();
        Check(b.enabled && f.Optimizer.CleanedCount==1, "Expected initially Cleaned character");
        LumenConfig.CollapseStackedLods.Value=true; f.Tick(0);
        Check(!b.enabled && a.enabled, "Changing options did not revisit Cleaned state");
        LumenConfig.CollapseStackedLods.Value=false; f.Tick(0);
        Check(b.enabled && f.Optimizer.CleanedCount==1, "Disabling LOD collapse did not restore/reapply");
    }
    private static void ShadowOptionChanges()
    {
        var shadow=new Renderer("body_Shadow");
        using var f=new Fixture(shadow); f.Tick();
        LumenConfig.RemoveShadowProxies.Value=true; f.Tick(0); Check(!shadow.enabled,"Shadow option ON did not apply");
        LumenConfig.RemoveShadowProxies.Value=false; f.Tick(0); Check(shadow.enabled,"Shadow option OFF did not restore");
    }
    private static void OptionChangeWithoutCamera()
    {
        var shadow=new Renderer("body_Shadow");
        using var f=new Fixture(shadow); LumenConfig.RemoveShadowProxies.Value=true; f.Tick();
        Camera.main=null; LumenConfig.RemoveShadowProxies.Value=false; f.Tick(0);
        Check(shadow.enabled && f.Optimizer.CleanedCount==0, "Option restoration incorrectly depended on camera");
    }
    private static void LostRegistry()
    {
        var renderer=new Renderer("body");
        using var f=new Fixture(renderer); LumenConfig.NpcCullDistance.Value=10; f.Character.transform.position=new Vector3(20,0,0); f.Tick();
        Check(!renderer.enabled,"Expected culling"); CharacterRegistry.Entries.Clear(); f.Tick();
        Check(renderer.enabled && f.Optimizer.CulledCount==0 && f.Optimizer.PendingRestoreCount==0,"Lost registry owner did not restore");
    }
    private static void ReleaseWithoutHierarchy()
    {
        var renderer=new Renderer("body");
        using var f=new Fixture(renderer); LumenConfig.NpcCullDistance.Value=10; f.Character.transform.position=new Vector3(20,0,0); f.Tick();
        f.Character.ThrowEnumeration=true; CharacterRegistry.Disable(f.Character);
        Check(renderer.enabled && f.Optimizer.CulledCount==0,"Release wrongly required current hierarchy");
    }
    private static void DetachedRenderer()
    {
        var retained=new Renderer("old"); var current=new Renderer("new");
        using var f=new Fixture(retained); LumenConfig.NpcCullDistance.Value=10; f.Character.transform.position=new Vector3(20,0,0); f.Tick();
        f.Character.Renderers=new[]{current}; f.Character.transform.position=new Vector3(1,0,0); f.Tick(0);
        Check(retained.enabled && current.enabled,"Near transition left detached renderer overridden");
        Check(f.Optimizer.CleanedCount==1 && f.Optimizer.CulledCount==0,"Near transition counters wrong");
    }
    private static void MakeRestoreFailAfterHide(Renderer renderer)
    {
        renderer.FailAfterWrites=1;
        renderer.OnWrite=value=> { if (!value) { renderer.OnWrite=null; renderer.FailBeforeWrites=int.MaxValue; } };
    }
    private static void PartialApply()
    {
        var first=new Renderer("first"); var second=new Renderer("second"); MakeRestoreFailAfterHide(second);
        using var f=new Fixture(first,second); LumenConfig.NpcCullDistance.Value=10; f.Character.transform.position=new Vector3(20,0,0); f.Tick();
        Check(first.enabled && !second.enabled,"Partial failure lost changed targets or failed rollback");
        Check(f.Optimizer.PendingRestoreCount==1 && f.Optimizer.CulledCount==0,"Partial apply was committed or failure forgotten");
        CharacterRegistry.Entries.Clear(); Camera.main=null; LumenConfig.NpcOptimizerEnabled.Value=false; f.Tick(0);
        Check(f.Optimizer.PendingRestoreCount==1,"Failed restore must remain retryable");
        second.FailBeforeWrites=0; f.Tick(0); Check(second.enabled && f.Optimizer.PendingRestoreCount==0,"Retry did not restore original target");
    }
    private static void PendingStopsMutations()
    {
        var failed=new Renderer("failed"); MakeRestoreFailAfterHide(failed);
        using var f=new Fixture(failed); LumenConfig.NpcCullDistance.Value=10; f.Character.transform.position=new Vector3(20,0,0); f.Tick();
        var fresh=new Renderer("fresh"); var next=new BaseCharacter {Renderers=new[]{fresh}}; next.transform.position=new Vector3(30,0,0);
        CharacterRegistry.Entries.Clear(); CharacterRegistry.Entries.Add(next); f.Tick();
        Check(fresh.enabled && fresh.Writes.Count==0 && f.Optimizer.PendingRestoreCount==1,"Pending recovery allowed new mutations");
        failed.FailBeforeWrites=0; LumenConfig.NpcOptimizerEnabled.Value=false; Camera.main=null; f.Tick(0);
        Check(failed.enabled && fresh.enabled && f.Optimizer.PendingRestoreCount==0,"OFF retry failed");
    }
    private static void ReentrantDisable()
    {
        var renderer=new Renderer("body");
        using var f=new Fixture(renderer); LumenConfig.NpcCullDistance.Value=10; f.Character.transform.position=new Vector3(20,0,0);
        renderer.OnWrite=value=> { if (!value) { renderer.OnWrite=null; CharacterRegistry.Disable(f.Character); } }; f.Tick();
        Check(renderer.enabled && f.Optimizer.PendingRestoreCount==0,"Disable from setter lost restoration");
        Check(f.Optimizer.CulledCount==0 && f.Optimizer.CleanedCount==0,"Released owner was committed after setter");
    }
    private static void LedgerOriginalFalse()
    {
        var r=new Renderer("target",false);var ledger=new RendererEnabledLedger();var target=new Target(r);
        ledger.Hide(1,target);ledger.Hide(1,target);Check(ledger.Count==1,"Repeated Hide replaced/duplicated entry");
        Check(ledger.RestoreAll() && !r.enabled && !r.Writes.Any(v=>v),"Original false was not preserved");
    }
    private static void LedgerMutateThenThrow()
    {
        var r=new Renderer("target"){FailAfterWrites=1};var ledger=new RendererEnabledLedger();
        Throws(()=>ledger.Hide(1,new Target(r)),"Mutate-then-throw must not be silent");
        Check(r.enabled && ledger.Count==0,"Stored original was not used for immediate recovery");
    }
    private static void LedgerRetry()
    {
        var r=new Renderer("target");MakeRestoreFailAfterHide(r);var ledger=new RendererEnabledLedger();
        Throws(()=>ledger.Hide(1,new Target(r)),"Expected failed apply");Check(!r.enabled && ledger.Count==1 && ledger.PendingRestoreCount==1,"Failure was not retained");
        Check(!ledger.RestoreAll() && ledger.Count==1,"Failed restore should remain incomplete");
        r.FailBeforeWrites=0;Check(ledger.RestoreAll() && r.enabled && ledger.Count==0,"Restoration retry lost original");
        Check(ledger.RestoreOwner(1),"Owner count was not cleared after recovery");
    }
    private static void LedgerReadback()
    {
        var r=new Renderer("target"){IgnoreWrites=true};var ledger=new RendererEnabledLedger();
        Throws(()=>ledger.Hide(1,new Target(r)),"Ignored disable setter requires readback rejection");
        Check(r.enabled && ledger.Count==0,"Ignored write should recover original");
        r.IgnoreWrites=false;ledger.Hide(1,new Target(r));r.IgnoreWrites=true;
        Check(!ledger.RestoreAll() && ledger.PendingRestoreCount==1,"Ignored restore setter was marked complete");
        r.IgnoreWrites=false;Check(ledger.RestoreAll() && r.enabled,"Readback failure was not retryable");
    }
    private static void LedgerOwnerIsolation()
    {
        var a=new Renderer("a");var b=new Renderer("b");var ledger=new RendererEnabledLedger();ledger.Hide(1,new Target(a));ledger.Hide(2,new Target(b));
        Check(ledger.RestoreOwner(77) && !a.enabled && !b.enabled,"Untracked owner affected targets");
        Check(ledger.RestoreOwner(1) && a.enabled && !b.enabled && ledger.Count==1,"Owner restore crossed ownership");
        Check(ledger.RestoreAll() && b.enabled,"Remaining owner failed restoration");
    }
    private static void LedgerDestroyed()
    {
        var r=new Renderer("target");var ledger=new RendererEnabledLedger();ledger.Hide(1,new Target(r));int writes=r.Writes.Count;r.Alive=false;
        Check(ledger.RestoreAll() && ledger.Count==0 && r.Writes.Count==writes,"Destroyed target was written or retained forever");
    }
    private static void LedgerReentrantHide()
    {
        var r=new Renderer("target");var ledger=new RendererEnabledLedger();
        r.OnWrite=value=> {if(!value){r.OnWrite=null;ledger.RestoreAll();}};
        Throws(()=>ledger.Hide(1,new Target(r)),"Reentrant release must reject completed Hide");
        Check(r.enabled && ledger.Count==0 && ledger.PendingRestoreCount==0,"Reentrant Hide lost deferred restoration");
    }
    private static void LedgerStaleRestoreSnapshot()
    {
        var a=new Renderer("a");var b=new Renderer("b");var ledger=new RendererEnabledLedger();ledger.Hide(1,new Target(a));ledger.Hide(2,new Target(b));int afterExternal=-1;
        a.OnWrite=value=> {if(value){a.OnWrite=null;ledger.RestoreAll();b.enabled=false;afterExternal=b.Writes.Count;}};
        Check(ledger.RestoreAll() && a.enabled && ledger.Count==0,"Nested restore did not clear ownership");
        Check(!b.enabled && b.Writes.Count==afterExternal,"Stale outer restore overwrote released external state");
        Check(ledger.RestoreOwner(1) && ledger.RestoreOwner(2),"Nested removals left owner count residue");
    }
    private static LumenPlugin PluginWithHost(out GameObject host)
    {
        var plugin=new LumenPlugin();host=new GameObject("offline-host");
        typeof(LumenPlugin).GetField("_host",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(plugin,host);
        return plugin;
    }
    private static void UnloadPending()
    {
        var renderer=new Renderer("body");using var f=new Fixture(renderer);LumenConfig.NpcCullDistance.Value=10;f.Character.transform.position=new Vector3(20,0,0);f.Tick();
        renderer.FailBeforeWrites=int.MaxValue;LumenBehaviour.Optimizer=f.Optimizer;var harness=new Harness();LumenBehaviour.Harness=harness;var plugin=PluginWithHost(out var host);
        Check(!plugin.Unload(),"Unload accepted failed restoration");
        Check(ReferenceEquals(LumenBehaviour.Optimizer,f.Optimizer) && ReferenceEquals(LumenBehaviour.Harness,harness),"Unload dropped retained recovery objects");
        Check(host.Alive && Object.DestroyCalls==0 && CharacterRegistry.Subscribers==1 && f.Optimizer.PendingRestoreCount==1,"Unload destroyed host/unsubscribed while pending");
        renderer.FailBeforeWrites=0;Check(plugin.Unload() && renderer.enabled,"Unload retry failed");
        Check(LumenBehaviour.Optimizer==null && LumenBehaviour.Harness==null && !host.Alive && CharacterRegistry.Subscribers==0,"Successful unload did not release host/hooks");
    }
    private static void UnloadException()
    {
        using var f=new Fixture(new Renderer("body"));LumenBehaviour.Optimizer=f.Optimizer;var harness=new Harness{ThrowRestore=true};LumenBehaviour.Harness=harness;var plugin=PluginWithHost(out var host);
        Check(!plugin.Unload() && ReferenceEquals(LumenBehaviour.Optimizer,f.Optimizer),"Unload exception did not keep optimizer");
        Check(host.Alive && Object.DestroyCalls==0 && CharacterRegistry.Subscribers==1,"Exception destroyed recovery host/hooks");
        harness.ThrowRestore=false;Check(plugin.Unload() && !host.Alive,"Unload exception recovery did not retry");
    }
}
