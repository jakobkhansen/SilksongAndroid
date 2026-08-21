# Silksong Android

**Hollow Knight: Silksong** Android port with dual-screen capabilites and optional
Steam integration for game files and cloud saves.

<img width="2048" height="1536" alt="IMG_1639" src="https://github.com/user-attachments/assets/c9ddb25d-37a8-4e7e-877c-0f13eb13efed" />

## Features

- **Dual screen**: Show interactive map, inventory, crests, tasks and journal on the second screen
- **Steam integration (optional)**: Sign in to Steam if you want the app to download game files and/or your Steam cloud saves for you
- **High performance**: Compiles to native arm64 via IL2CPP
- **Fully open source and legal**: Supply your own game files either manually or through Steam sign-in (the app downloads them for you)
- **Compilation on device**: Just download the APK and supply the game files, porting happens on device (20–30 min on a Snapdragon 8 Gen 2)
- **QoL settings**: Skip intro, set resolution, auto upload/download cloud saves etc.
- **Any device**: Any Android device works, single screen as well.

## Getting started

1. Download the latest APK from
   [Releases](https://github.com/jakobkhansen/SilksongAndroid/releases/latest)
   and install it.
2. Supply your own game files. Either copy them across from your PC yourself,
   or let the app fetch them for you by signing in to Steam.
3. Press the button. The app fetches everything it needs and builds the game.
   The build takes 20–30 minutes on a Snapdragon 8 Gen 2, most of it the download and the compile.
4. Play. After the first build, launching is instant.

Any device should do (tested on the AYN Thor and the Retroid Pocket Flip 2). Open an issue
if your device doesn't work.

### Supplying the game files yourself

If you'd rather not sign in, download the game's **Linux** depot on a PC with
[DepotDownloader](https://github.com/SteamRE/DepotDownloader). The Linux files are the
ones the port is built from; the Windows depot will not do:

```sh
DepotDownloader -app 1030300 -depot 1030303 -username <your account> -password <your password> -dir silksong
```

Open the app once first, so that it creates its folder on the device, then copy what you
downloaded into `Android/data/com.jakobkhansen.silksong/files/depot/`, so that the device
ends up looking like this:

```
Android/data/com.jakobkhansen.silksong/files/
└── depot/
    ├── Hollow Knight Silksong.x86_64
    ├── UnityPlayer.so
    └── Hollow Knight Silksong_Data/
        ├── Managed/
        ├── MonoBleedingEdge/
        ├── Plugins/
        ├── Resources/
        ├── StreamingAssets/
        ├── globalgamemanagers
        ├── resources.assets
        └── ...
```

Press "I have the files" in the app once it is there.

## Steam Cloud Saves

If you sign in to Steam, you also get your Steam Cloud saves. Pull before you play, push
when you're done, or let the launcher do both automatically (see settings). Save conflicts
are handled, but back your saves up if you want to be certain nothing is lost.

Signing in, by QR code or password, goes through
[JavaSteam](https://github.com/Longi94/JavaSteam), an open-source Steam client library.
Your password is never stored: the only thing kept is the login token Steam issues in
return, encrypted, and it never leaves the device.

## AI assistance

Much of this project was written with AI assistance. Everything in it is reviewed and
tested on real hardware before release.

## Legal

This repository and the APK contain **no game content and nothing Unity-made**. Silksong
is © Team Cherry, and none of its code, art or audio is distributed here. The APK is a
build system: it downloads Unity's toolchain, takes *your* game files (supplied by hand,
or fetched with your own Steam account), and compiles a playable build on your own device.

The tooling is MIT-licensed; see [LICENSE](LICENSE). Third-party open-source
components shipped in the APK are listed in [NOTICE.md](NOTICE.md), which also
records what the APK deliberately does *not* contain.


## Building from source

You don't need Unity or any game files to build the APK:

```bash
make player     # once: fetch Unity's Android player module (~642 MB)
make surgery    # once: build bundle-surgery
make dev        # rebuild, repackage, install
```

Requires an Android SDK, JDK 17+ and the .NET 8 SDK; on Windows use Git Bash.
`make docker-apk` does the same in a container. See [COPILOT.md](COPILOT.md)
for the full development loop.
