# Dev loop

Everything runs through `tools/depot-to-apk/dev.sh`. One command rebuilds the
launcher, repackages the APK and installs it, in about three minutes.

The APK lands in `build/`. Everything else the build produces or downloads
lives under `~/.cache/silksong` (override with `SILKSONG_CACHE`, `BUILD_ROOT`
or `PLAYER_ROOT`).

## Setup

```sh
make surgery      # build bundle-surgery; Gradle stages it into the APK
make weaver       # build mod-weaver; Gradle stages it into the APK
make player       # fetch Unity's Android player module (~642 MB, once)
```

Beyond that you need an Android SDK and a JDK 17+, both discovered
automatically. Unity does not have to be installed. Override anything unusual:

```sh
ANDROID_SDK=/path/to/sdk AP=/path/to/AndroidPlayer make dev
```

A `tools/depot-to-apk/dev.env` file, if present, wins over discovery.

**On Windows, use Git Bash, not WSL** — `bash` on PATH is WSL, which cannot see
the Windows-side toolchain:

```sh
'C:\Program Files\Git\bin\bash.exe' -c 'make dev'
```

## Everyday

```sh
make dev          # rebuild, repackage, install
make dev-fast     # same, skipping Gradle (shell or build.sh changes only)
make device-wipe  # uninstall and delete app storage (then make install)
make logcat       # our logs
make game-logcat  # Unity's logs
make build-log    # the on-device compile log
```

After installing, drive the app on the device: it asks where your copy of the
game is, then does everything else behind one progress bar.

## In a container

For CI or a clean machine — needs nothing installed but Docker:

```sh
make docker-image
make docker-apk   # APK lands in build/; install it with `make install`
```

The image is `linux/amd64`, because Google ships no arm64 Linux build-tools —
`aapt2` and `zipalign` are x86-64 whatever the host is. On an arm64 machine
that means emulation, which works but is several times slower: fine for CI
(GitHub's runners are x86-64 and run it natively), painful as an edit-test
loop.

An arm64 host also needs the emulator installed once, or the build fails at
the first RUN with `exec format error`:

```sh
docker run --privileged --rm tonistiigi/binfmt --install amd64
```

## Releasing

The version lives in one place, `VERSION` at the repo root. `build.sh` reads it
for the manifest's `versionName`, and derives `versionCode` from it as
`major*10000 + minor*100 + patch`.

To ship one: bump `VERSION` in a commit, then run the **Release APK** workflow
from the Actions tab. It refuses to publish a version that already has a
release, so the bump is what authorises the release.

A version with a suffix — `0.2.0-rc.1` — is published as a GitHub prerelease
automatically. Its `versionCode` deliberately matches the final `0.2.0`, so
installing the release over the candidate is an update rather than a downgrade
Android would refuse.

The workflow has a **dry run** option that builds and verifies without
publishing. That path also works without any signing secret, by generating a
throwaway key; a real publish requires `ANDROID_KEYSTORE_BASE64` and refuses to
run without it, because a signing key that changes between releases strands
everyone who installed the last one. See the comments in
`.github/workflows/release.yml` for the one-time setup.

## Rebuilding the game itself

The game is built **on the phone**. `make dev` only ships the app that does the
building. To make it build the game again:

```sh
make game-reset   # drop the built engine and player image, then press the button
```

Nothing else is thrown away, so this is minutes rather than the full run — the
toolchain and the depot stay.

### What a rebuild actually redoes

Each expensive step is gated on a record of what it was made from, so pressing
the button again after a patch edit skips the ones that cannot have changed:

| Step | Stamp | Skipped when |
| --- | --- | --- |
| IL2CPP conversion | `build/cpp.done`, staged assemblies, `build/mods.stamp` + `build/mods.converted` | conversion completed and its assemblies, mod inputs and weaver are unchanged |
| Player image + `data.apk` | `build/image.stamp` | the conversion output, entry points and depot are unmoved |
| Content retarget | `build/content.stamp` | the bundle tree is the one already retargeted |

Every stamp is written only after its step finishes, so an interrupted build is
redone rather than assumed — which matters most for the retarget, since it
rewrites the content tree in place.

Delete a stamp to force that step alone. `make game-reset` drops the lot.

The installed game has a separate completion marker, `files/pkg/.built`.
It is removed before a build or reset can change installed files and written
atomically only after the engine, player image, content and installed mod
records all finish. Setup and the launch button both require it: an interrupted rebuild
must be resumed, not played with new native code and old metadata. Completed
conversion and native compilation work remain cached. Older completion markers
are not trusted and need one cached rebuild.

The launcher has filesystem-backed JVM regressions for these checkpoints.
Run `:app:testReleaseUnitTest --tests dev.silksong.launcher.BuildInstallationTest`
with the player module's Gradle, the same one used by `dev.sh`; the Android
Studio-generated wrapper is not supported.

## Editing the patches

The port's own game code lives in `tools/silksong-patches/src/` and is compiled
on the device. Two things to know:

- Every entry point must also be listed in
  `tools/silksong-patches/entrypoints.json`. One that is not listed is silently
  never called.
- Changing a patch forces the conversion and native compile to run again, so
  allow a few minutes.

## Mods

BepInEx 5 plugins are woven into the game at build time, not loaded at runtime
-- there is no IL left once il2cpp has run. Three pieces:

- `tools/mod-weaver/` — a Cecil tool that resolves each `[HarmonyPatch]`
  against the staged assemblies and writes the prefix/postfix calls into the
  game's IL. Runs between `PackageCompiler` and il2cpp, and reports per plugin.
- `tools/bepinex-shim/src/` — our `BepInEx.dll` and `0Harmony.dll`, compiled on
  the device like the patches. `Harmony.PatchAll` is a no-op; logging, config
  and `AccessTools` are real.
- `Mods.kt` — the folder (`<external files>/mods`), the enable/disable list,
  and a content stamp so an unchanged folder skips the rebuild. The stamp
  covers what is IN the folder, not what is switched on: every plugin is woven
  and each of its calls is wrapped in a gate field (`<ModGate>.Enabled`) that
  the chainloader opens at startup. So adding or replacing a file is a
  rebuild; a toggle is a line in `mods/disabled-assemblies.txt`.

Conversion and installation are recorded separately. `mods.stamp` and
`mods.converted` describe the completed conversion; `mods.installed.stamp`
and `mods.built` are promoted from that snapshot only after installation
succeeds. The snapshot includes only accepted DLLs in the built list, while its
fingerprint covers every input, so a rejected plugin is reported as not built
without prompting for the same unsuccessful mod on every launch.

Transpilers, runtime-computed targets and `Reflection.Emit` cannot work. The
weaver says so per plugin before the native compile starts.

So does a shape that does not match. A published plugin was compiled against
the real BepInEx, so the shims have to agree with it down to the signature: a
field where BepInEx has a property, or `object` where it has a type, is a
member the plugin cannot resolve. The weaver rejects definite missing members
before IL2CPP, along with plugins that require the rejected assembly or plugin.
Uncertain generic resolution remains best-effort. To check a plugin locally:

```sh
make mod-check PLUGIN=path/to/Plugin.dll
```

It compiles both shims against your depot, stages them beside the game's
assemblies and runs the real weaver over the plugin, printing the report the
launcher would show. It found five such mismatches when Configuration Manager
was first tried.

Postfixes share a continuation per target, so a prefix that skips the original
method cannot skip another mod's postfix. The synthetic-assembly regressions
exercise the actual weaver without requiring game files:

```powershell
dotnet run --project .\tools\mod-weaver\tests\ModWeaver.RegressionTests.csproj --configuration Release
```

### Check before you build

```sh
pwsh tools/silksong-patches/check.ps1
```

Compiles every patch source against your depot in about ten seconds, exactly as
the device does — same split of Unity's Android player assemblies for the engine
and the depot's own for the game.

This is worth the habit. A patch build is ~3 minutes of APK plus ~4 minutes of
on-device IL2CPP, so a mistyped API used to cost seven minutes and a logcat to
find. It has already caught a `ReadSource` value that does not exist, a
TextMeshPro property that is spelled differently in Team Cherry's fork, and a
missing `TeamCherry.TK2D` reference.

It needs Unity's player assemblies (`make player`) and a copy of the game's own.
The depot is **not** in the checkout: it is 15 GB of your game and nothing in
the repo implies it belongs there. By default this looks for
`silksong-install/` beside the repo; `SILKSONG_DEPOT` or `-Depot` point it
anywhere.

Use the **Linux** depot specifically. It ships shaders precompiled to Vulkan
SPIR-V, which Adreno's driver loads directly; the Windows depot's DX11 blobs
are no use on Android. If your Steam install is Windows or macOS,
[DepotDownloader](https://github.com/SteamRE/DepotDownloader) fetches the Linux
depot for a game you already own.

**The app itself needs none of this** — the phone downloads the depot on its
own, signed in as you, which is the point of the port. A depot on the build
machine is only for `check.ps1`; `make dev` never reads it.

### Asking the game where things are

The patches reach into the running game, and reading the game's classes is
often a poor guide to where art and state actually live — the inventory's
needle, for instance, is not a sprite on a component but one of five child
objects the game activates, and the currency icons are 2D Toolkit sprites with
no `Sprite` object at all. Guessing costs a build each time.

So the dual-screen code ships these diagnostics, switched on by a file rather
than compiled in:

```sh
F=/sdcard/Android/data/com.jakobkhansen.silksong/files/dualscreen_v2

adb shell "echo 'probe=1'    > $F"   # dump the inventory hierarchy to logcat
adb shell "echo 'testcard=1' > $F"   # draw the second screen's test card
adb shell "echo 'map_diag=1' > $F"   # log the map panel's state as it changes

# every sprite whose name contains the text, written out as PNG, plus the whole
# atlas page each one came from. `sprite_dump=1` LISTS every sprite's name, rect
# and page to the log but writes no files -- writing them all locks the device
# up hard enough to need adb to recover it.
adb shell "echo 'sprite_dump=UI_tool_slot' > $F"
adb pull /sdcard/Android/data/com.jakobkhansen.silksong/files/sprites

adb shell "rm -f $F"                 # back to normal

adb shell am force-stop com.jakobkhansen.silksong   # settings are read once per process
adb logcat -d | grep DsProbe
```

`probe=1` logs every object under the game's inventory with its components and
sprite names, which turns "where does this icon live" into a lookup.
`sprite_dump` answers the next question along — *what does that sprite actually
look like* — for art that exists only in an atlas at runtime, since most sprite
references in the decompile point at a placeholder GUID with no PNG on disk.
It writes the page as well as the sprite because a sprite's rect is the one
thing that can still be wrong, and a page is proof: the crest's locked-socket
glyph was found that way, spotted on a page and matched back to a sprite by
rect. It is called `Tool_slot_lock_ring`, while every other glyph in its family
is `UI_tool_slot_*`, so no amount of guessing at names would have reached it.

Adding a knob is one line in `DsConfig`; keeping it costs nothing and saves a
build the next time the question comes up.


## Knobs

To `dev.sh`:

| | |
| --- | --- |
| `LAUNCHER=0` | skip the Gradle build |
| `DEBUGGABLE=1` | make the app debuggable, so `run-as` works |
| `INSTALL=0` | build the APK without installing it |
| `STEPS=6` | repackage only |

To the on-device compile:

| | |
| --- | --- |
| `OPT=-Os` | optimise for size instead of speed (default `-O2`) |
| `JOBS=n` | compile parallelism (default: core count) |
| `FULL=1` | rebuild every translation unit |

## Logs

On the device, under `/sdcard/Android/data/<pkg>/files/build/`:

| | |
| --- | --- |
| `compile.log` | per-phase progress and timings |
| `err.log` | compiler diagnostics |
| `convert.log` | IL2CPP conversion |
| `inputsystem.log` | the Input System compile |

`obj/` is mode 700, so counting objects needs `adb shell run-as <pkg> ls ...`
rather than a plain `adb shell`.
