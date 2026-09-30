# NPC scan regression checks

This standalone .NET 8 console project compiles the actual `NpcOptimizer.cs` and `CharacterRenderers.cs` sources against small Unity/game/config stubs. It has no package dependencies, plugin project reference, installation target or game access.

From the repository root:

```powershell
dotnet run --project Lumen/tests/Lumen.Regression/Lumen.Regression.csproj
```

The scheduling scenarios cover stable 180/190-character crowds at 5/10/15 FPS, a 60-to-10 FPS approach, turning distance culling off, a bounded spawn/reorder, list shrink/empty/refill, master OFF without a camera, shadow-option changes and normal 60 FPS behavior.

The optional named-NPC checks cover protection OFF without lookups; story, working and dialogue classifications even with inactive label UI; an already culled character becoming protected; changing working/dialogue state; independent shadow cleanup; missing, destroyed or failing metadata, including a failing warning logger; and avoiding name lookups when distance culling or the master switch is off. The fixtures explicitly configure the option rather than assuming a production default.

The runner prints each of the 15 scenarios and returns a nonzero exit code on failure.

These tests establish managed scheduling and renderer transition behavior through stubs. They do not establish IL2CPP hook behavior, visual appearance, gameplay continuity or performance improvement in the game. They do not promise fair visits during unlimited registry churn, test pool-activity guards or model asynchronous Unity LOD updates.

Run this test project directly. Building the plugin project invokes its separate installation target and is unnecessary for these checks.
