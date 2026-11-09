# Mod weaver

This Cecil executable rewrites staged managed assemblies before IL2CPP. It
does not load plugins into a running game.

The `builtin` command also adds Android PlayStation menu aliases after
`InputHandler.MapControllerButtons` rebuilds the game's bindings: Options
opens pause, and Back/Share/Create open inventory. Existing bindings remain
in place. The aliases are checked before insertion so controller reconnects
do not accumulate duplicates. This runs on normal builds without a plugin.

## Composition and rejection

Each patched target has one original body, one shared `__result`, and one
postfix path. Prefixes run in reverse weave order; postfixes run in weave
order. A false prefix skips the original and remaining prefixes, but **all
enabled postfixes still run**. Each plugin's prefix and postfix keep separate
gate checks. `__state` remains local to each paired prefix/postfix; multiple
pairs in one class still use positional pairing and report that limitation.
Harmony priorities and finalizers remain unsupported.

All input DLLs are checked before any gate or target is changed. Missing
assemblies, types, and definite non-generic method/field references reject the
DLL. Rejection cascades through assembly references, independent of input
order; rejected assemblies are also removed from target lookup. Reports retain
the existing `Failed` status and per-plugin issues, and rejected DLLs are not
staged. An unsupported patch shape alone remains `Partial`, since the rest of
the plugin can still be useful.

Cecil's uncertain generic-member resolution and array intrinsics remain
best-effort, not definite rejection. This is not a general IL verifier or a
guarantee that every accepted plugin will convert successfully.

## Focused regressions

From the repository root:

```powershell
dotnet run --project .\tools\mod-weaver\tests\ModWeaver.RegressionTests.csproj --configuration Release
dotnet build .\tools\mod-weaver\ModWeaver.csproj --configuration Release --no-restore
```

The package-free console tests use the production weaver and its existing
Cecil dependency. They generate synthetic staged assemblies and plugins, then
execute the rewritten IL in collectible .NET load contexts. No game or Unity
content is needed. Fixtures live under the test build output and are removed
after each case. Test sources are excluded from the production executable.

Coverage includes cross-plugin skip/postfix ordering, per-plugin gates,
shared results, ref arguments, paired state, instance injection, multiple
returns, exception regions, direct missing members and mismatched signatures,
dependency cascades, genuine generic-resolution uncertainty, and builtin
weave coexistence.
Controller-menu regressions execute the woven IL across multiple return paths,
repeated remapping, replacement action sets, missing actions, and a second
weave; an incompatible input API leaves the original assembly unchanged.
