# NPC restoration regression checks

Using a .NET 8 SDK, run this isolated project from the repository root:

```powershell
dotnet run --project Lumen/tests/Lumen.Regression/Lumen.Regression.csproj -c Release
```

Verified: **22 tests, 56 assertions, 0 failures**, exit code 0. A failed assertion
returns a nonzero exit code.

The console project has no package dependencies or plugin project reference. It
links the production optimizer, renderer ledger, renderer selection, configuration
and plugin sources. Building it does not build or install the plugin. The plugin's
existing post-build installation target is outside this test project's build.

The checks cover OFF without a camera or registry, changes to cleanup settings,
preservation of originally disabled renderers, lost owners and detached renderers,
partial writes and retryable restoration failures, blocked writes during recovery,
reentrant callbacks, nested restoration and the actual `Unload` method.

The fixture sets cleanup options explicitly; it does not assume or change the
production defaults. Existing bound configuration values are also checked.
Unity objects, registry callbacks, config entries and host lifecycle are stubbed.
These tests check production control flow and saved-value recovery, not native
IL2CPP behavior, visual results or performance in the running game.
