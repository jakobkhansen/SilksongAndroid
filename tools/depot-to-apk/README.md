# depot-to-apk

Assembles the APK: manifest, resources, dex, sign. **No Unity Editor, no
decompilation.**

The APK contains no game content and nothing Unity-made. It is the app that
builds the game, and it does that on the phone.

## Why this works

A shipped Unity game already contains almost everything an Android player
needs. The serialized player image, the Addressables catalog and the asset
bundles are all reusable once retargeted. Only two artifacts are genuinely
game-derived — `libil2cpp.so` and `global-metadata.dat` — and both can be
regenerated from the depot's own managed assemblies using Unity's IL2CPP
converter, which is portable .NET. Everything else is stock Unity, byte-identical
for every game.

That insight is what the whole port rests on, but none of it happens here any
more: the device does it, from the user's own depot. The notes below are kept
because the same constraints apply there, and each of them cost real debugging
time.

| Component | Comes from |
| --- | --- |
| `libil2cpp.so`, `global-metadata.dat` | generated on the device from depot assemblies |
| `libunity.so`, `libmain.so`, `classes.jar`, `unity default resources`, `baselib.a` | stock Unity Android player, fetched on the device |
| player image, `unity_builtin_extra`, Addressables catalog, bundles | the user's depot |

Nothing game-derived is redistributed: a user supplies their own depot and this
produces their own APK.

## Usage

```bash
bash tools/depot-to-apk/build.sh          # both steps
STEPS=6 bash tools/depot-to-apk/build.sh  # repackage only
```

| Step | Name | Does |
| --- | --- | --- |
| 5 | `apk_shell` | manifest, resources, `classes.dex` |
| 6 | `package` | zip, align, sign |

The numbering starts at 5 because steps 1-4 used to do on a PC what the device
does now — stage the assemblies, IL → C++, compile, build the player image.
They were removed rather than renumbered, so the names in the logs and in the
notes below still line up with what they meant.

Inputs (all overridable by environment variable):

- `AP` — Unity's Android player module (see `make player`)
- `ANDROID_SDK` — build-tools + platforms

The working tree lands in `$OUT`; the signed APK in `$APK_DIR` (default
`build/`).

## Things that are not obvious

Each of these cost real debugging time; they are enforced by the script but
worth knowing if you change it.

**The depot's `Managed/` is the wrong BCL.** It holds the *Mono* class library
(a 4.5 MB `mscorlib` plus a `netstandard` facade). IL2CPP needs `unityaot-linux`.
Feeding it the Mono one fails opaquely — a `NullReferenceException` inside
`BuildSharedEnumTypes` printing `[System.Byte, ]`. The correct input set is
unityaot-linux BCL + the Android player's `Variations/il2cpp/Managed` +
depot-only assemblies.

**The depot is stamped with an internal Unity branch** (`6000.0.50f1-uum-100966-branch1`)
that the stock engine rejects. The version string is NUL-terminated inside the
metadata, so shortening it shifts every subsequent offset — it has to be
re-serialized, not patched in place. `BuildSettings.m_Version` carries a second
copy that must agree.

**The engine is 6000.0.52f1, not the game's 6000.0.50f1.** The game was built
with a private Unity build (`6000.0.50f1-uum-100966-branch1`, revision
`16f07294548e`, made for an audio fix, UUM-100966). It is not on Unity's
download servers or in its release API, so the closest engine that can be
fetched is a public one. Stock 6000.0.50f1 has UUM-107650: an Animator
disabled and re-enabled from script keeps running its state machine but writes
only its default pose until it is rebound. This game toggles animators that
way all over: paid bellway toll machines stood again on every visit, already
opened gates came up shut, and sand worms froze. 6000.0.52f1 is the first
6000.0 release with the fix (UUM-109250). Its release notes, and 6000.0.51f1's,
carry no serialization or IL2CPP changes, and its `il2cpp.dll` is
byte-identical to 6000.0.50f1's. The game's data is re-stamped with
whichever version the engine is (above), so it loads unchanged.

**A built player has no `m_BuildTargetGraphicsAPIs`.** The resolved list lives in
`BuildSettings.m_GraphicsAPIs`, and a Linux build leaves `[17 OpenGLCore, 21 Vulkan]`
behind. Android needs `[21]` alone.

**`unity default resources` is an engine built-in, not game data.** Use the
Android player's copy (3.57 MB), not the depot's Linux one (5.59 MB), or the
engine reports `File's Build target is: 5`.

**Unity 6's `classes.jar` has `UnityPlayer` but no `UnityPlayerActivity`.** The
Java source ships at `PlaybackEngines/AndroidPlayer/Source/`; it must be compiled
with `javac`, jarred, then run through `d8`. It also requires a string resource
named `game_view_content_description` or it throws at startup.

**`resources.arsc` must be stored uncompressed** for Android R and later.

Serialized-data edits are all performed by `tools/bundle-surgery`.

## Content: catalog and bundles

The game's ~2000 asset bundles (7.5 GB) cannot go in the APK — ZIP32 stops at
4 GB, and they are the user's own game data regardless. They live in the app's
private storage, and two edits connect them to the player.

**The catalog is repointed, not rewritten.** A shipped catalog does not store
absolute paths. Bundle locations are a token plus a relative path,
`{UnityEngine.AddressableAssets.Addressables.RuntimePath}/<platform>/x.bundle`,
resolved at load time. The token is stored once and referenced by every
location, so replacing that one string moves the entire content set:

```bash
BundleSurgery patch-catalog-path catalog.bin out/catalog.bin /data/user/0/<pkg>/files/aa
```

Strings in the binary catalog are length-prefixed rather than terminated, so a
replacement of exactly the same byte length keeps the prefix valid and leaves
every following offset untouched. The path is padded to length with trailing
slashes, which POSIX collapses. `catalog.bin` itself stays in the APK at
`assets/aa/`, together with `settings.json` — `settings.json` points at the
catalog through the same token, which correctly resolves inside the APK.

**The bundles are retargeted.** A bundle records the platform it was built for,
and the engine refuses one that does not match (`File's Build target is: 24`).
`retarget-tree` stamps every inner serialized file as Android and strips each
shader to its Vulkan slice:

```bash
BundleSurgery retarget-tree <depot>/StreamingAssets/aa/StandaloneLinux64 out/aa/StandaloneLinux64
```

Note this walks the tree **recursively**. A shipped Addressables tree groups
content into subdirectories, and in this game 911 of the 2068 bundles — 590 of
them scenes — live below the top level. Processing only the top level leaves
that content targeted at the wrong platform, to fail at load time instead.

The platform directory keeps its `StandaloneLinux64` name end to end: depot,
retargeted output, device storage, and catalog references all agree, so no
path rewriting is needed anywhere.

## Status

**The game boots and plays**, with no Unity Editor and no decompilation
anywhere: the player image, catalog and bundles come from the user's own
depot, `libil2cpp.so` and `global-metadata.dat` are regenerated from the
depot's assemblies, and everything else is stock Unity fetched at run time.

## Where the rest happens

`tools/ondevice-il2cpp/` holds what runs on the phone: `fetch-unity.sh` for
the toolchain, and `build-il2cpp.sh` for the native compile, which ships as an
APK asset. The conversion, the player image and the content retarget are in
the launcher's own Kotlin — `Il2cppConverter`, `PlayerImage`,
`PackageCompiler`.
