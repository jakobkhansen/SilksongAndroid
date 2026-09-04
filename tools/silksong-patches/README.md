# silksong-patches

The port's own game code: everything the Android build adds to Silksong.

## How it gets into the game

It ships as **source**. Gradle stages `src/` into the APK under
`assets/ondevice/patches/`, and the device compiles it into
`SilksongPatches.dll` against the depot's own assemblies before handing it to
IL2CPP alongside them.

That is the whole mechanism, and it is why there is no DLL in this directory to
build. Compiling on the device is what lets the patches call `GameMap` or
`PlayerData` as ordinary typed C# instead of by reflection: the game's
assemblies are right there, and they are the player's own copy rather than
anything we ship.

## Why this is not a mod framework

Every patch is self-bootstrapping: a `[RuntimeInitializeOnLoadMethod]` that
Unity calls during startup. Nothing hooks, detours or rewrites the game's own
code, so there is no BepInEx, no Harmony and no IL rewriting.

The one catch is that a player does not scan assemblies for that attribute —
the Editor does it at build time and writes the result into
`RuntimeInitializeOnLoads.json`. There is no Editor here, so the list is
written by hand in `entrypoints.json` and appended to the depot's own.

**An entry point that is added to a `.cs` file and not to `entrypoints.json` is
silently never called.** That is the single most confusing way for a patch to
fail.

## Check before you build

```sh
pwsh tools/silksong-patches/check.ps1     # or: make check
```

Compiles every source against your depot in about ten seconds, exactly as the
device does — the same split of Unity's Android player assemblies for the engine
and the depot's own for the game. It needs `make player`, and a copy of the
game: `silksong-install/` beside the repo by default, or wherever
`SILKSONG_DEPOT` or `-Depot` points. The depot is deliberately not in the
checkout — it is 15 GB of your game.

Worth the habit: a patch build is ~3 minutes of APK plus ~4 minutes of on-device
IL2CPP, so a mistyped API used to cost seven minutes and a logcat to find. It
has already caught a `ReadSource` value that does not exist, a TextMeshPro
property spelled differently in Team Cherry's fork, and a missing
`TeamCherry.TK2D` reference.

## What is here

| | |
| --- | --- |
| `ResolutionConfigurator` | render resolution and shape, matched to the window; landscape only |
| `AspectGate` | fill screens the game letterboxes — foldables, 4:3 — when switched on |
| `IntroSkipper` | optionally skip the studio logos and opening quote |
| `AnimatorRebindFix` | rebind animators that load disabled, so they play |
| `WormAnimatorFix` | the off-camera frozen sand worm in Blasted Steps |
| `TrapProbe` | live on-device diagnosis for stuck props; off unless asked for |
| `AndroidRumble` | vibration: the game mixes it, Android never played it |
| `InputProbe` | live on-device diagnosis for dropped inputs; off unless asked for |
| `ShaderWarmup` | prewarm shader variants to cut first-encounter hitches |
| `InventoryTouchInput` | touch control for the game's own inventory |
| `PerfOverlay`, `ProfilerTopMarkers` | on-device performance readouts |
| `InjectionProbe` | proves the assembly is live, and logs the settings it sees |
| `Settings` | reads the launcher's settings file |
| `dualscreen/` | the second screen — see `DUALSCREEN-V2.md` |

## Editing them

Two things to know:

- Every entry point must also be listed in `entrypoints.json`.
- Changing a patch forces IL2CPP conversion and the native compile to run
  again, so allow a few minutes.

Subdirectories are safe: Gradle copies recursively and the on-device compile
walks the tree.
