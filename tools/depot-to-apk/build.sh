#!/usr/bin/env bash
# Assemble the APK: manifest, resources, dex, sign.
#
# The APK contains no game content and nothing Unity-made. It is the app that
# builds the game, and it does that on the device: the engine, the player
# classes, the toolchain and the user's own depot are all fetched there, and
# libil2cpp.so is compiled there from the depot's own assemblies.
#
# This once did that work on a PC too, as steps 1-4 -- stage the assemblies,
# IL -> C++, compile, build the player image. The device does all of it now,
# so those steps were dead weight and are gone; what is left is the two steps
# that make the APK itself.
#
# Usage:
#   bash tools/depot-to-apk/build.sh              # both steps
#   STEPS=6 bash tools/depot-to-apk/build.sh      # repackage only
#
#   5  apk_shell         manifest + resources + classes.dex
#   6  package           zip + align + sign
#
# Required (override any of these):
#   AP           Unity's Android player module (see `make player`)
#   ANDROID_SDK  build-tools + platforms
#
# Everything lands in $OUT, except the signed APK, which lands in $APK_DIR.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

ANDROID_SDK="${ANDROID_SDK:-$HOME/Android/Sdk}"
# Outside the checkout: the staging tree carries game data lifted out of the
# depot. dev.sh exports this, so the default here only matters when build.sh
# is run on its own.
BUILD_ROOT="${BUILD_ROOT:-${XDG_CACHE_HOME:-$HOME/.cache}/silksong/build}"
OUT="${OUT:-$BUILD_ROOT/depot-apk}"
# Where the signed APK is dropped, as opposed to where it is assembled. The
# working tree above holds the staging directory, which carries game data
# lifted out of the depot -- that cannot live in the checkout. The finished
# APK has no game data in it at all, so it can, and it is the one artefact
# anybody actually wants to reach for.
APK_DIR="${APK_DIR:-$REPO_ROOT/build}"
STEPS="${STEPS:-5,6}"

PKG="${PKG:-com.jakobkhansen.silksong}"
APP_LABEL="${APP_LABEL:-Hollow Knight: Silksong}"
# The version the stock engine reports. The depot is stamped with an internal
# branch build of the same numeric version and must be normalised to this.
UNITY_VERSION="${UNITY_VERSION:-6000.0.50f1}"
# GraphicsDeviceType: 21=Vulkan. Android has no OpenGLCore(17), which is what a
# Linux build leaves behind.
GFX_APIS="${GFX_APIS:-21}"
ANDROID_API="${ANDROID_API:-33}"

# targetSdkVersion, deliberately *not* ANDROID_API (which is the native
# compile target and stays current).
#
# The on-device build has to run a compiler, and Android decides whether a
# process may exec() a file it wrote itself from the app's targetSdkVersion.
# The mapping lives in seapp_contexts: targetSdk >= 29 lands in the
# "untrusted_app" SELinux domain, 26-28 in "untrusted_app_27". Only the latter
# carries
#
#     allow untrusted_app_27 app_data_file:file execute_no_trans;
#
# so at 29 or above every exec of a fetched toolchain binary fails with
# EACCES, no matter what the file's mode bits say. 28 is the highest value
# that keeps the permission -- the same reason Termux pins targetSdk 28.
#
# The alternative is shipping the toolchain inside lib/arm64-v8a/, which the
# installer extracts to an executable directory; that would mean carrying
# a few hundred MB of clang in an APK whose whole point is to be small.
TARGET_SDK="${TARGET_SDK:-28}"

# Debug builds get android:debuggable, which is what lets `run-as` open a
# shell inside the app's own uid and SELinux domain -- the only practical way
# to iterate on the toolchain staging without a rebuild per attempt.
DEBUGGABLE="${DEBUGGABLE:-0}"

# Where the Addressables content ends up on the device. The catalog keeps its
# content root as a single length-prefixed string, stored once and shared by
# every location, so repointing that one string in step 4 moves the whole
# content set -- which is far too large to ship inside the APK.
CONTENT_ROOT="${CONTENT_ROOT:-/data/user/0/$PKG/files/aa}"

log()  { printf '\033[36m[forge]\033[0m %s\n' "$*" >&2; }
warn() { printf '\033[33m[forge] WARN:\033[0m %s\n' "$*" >&2; }
fail() { printf '\033[31m[forge] FAIL:\033[0m %s\n' "$*" >&2; exit 1; }
# Where the launcher's build output lands. Both the shell step and the
# packaging step need it -- one for the classes, the other for the native
# libraries that come with them -- so it is resolved once, here.
LAUNCHER_OUT="$REPO_ROOT/src/SilksongLauncher.Launcher/app/build/outputs"
LAUNCHER_AAR="${LAUNCHER_AAR:-$LAUNCHER_OUT/aar/app-release.aar}"
LAUNCHER_DEPS="${LAUNCHER_DEPS:-$LAUNCHER_OUT/runtime-deps}"

# ── The app's own version, as Android sees it ───────────────────────────────
#
# VERSION at the repo root is the single source of truth, so bumping a release
# is a one-line commit and CI reads the same file to name the tag.
#
# A semantic version may carry a prerelease suffix (0.2.0-rc.1). versionName
# takes it verbatim; versionCode must be a plain increasing integer, so it is
# derived as major*10000 + minor*100 + patch. That keeps increasing as long as
# minor and patch stay below 100, which is checked rather than assumed.
#
# The suffix deliberately does NOT change versionCode: 0.2.0-rc.1 and 0.2.0
# are the same integer, so installing the final build over the candidate reads
# as an update rather than the downgrade Android would refuse.
if [[ -z "${VERSION_NAME:-}" ]]; then
    if [[ -f "$REPO_ROOT/VERSION" ]]; then
        VERSION_NAME="$(tr -d ' \t\r\n' < "$REPO_ROOT/VERSION")"
    else
        VERSION_NAME="0.0.0"
    fi
fi

# The APK's filename, which is what a person downloading it sees.
#
# Named after the project and its version rather than the application id: a
# file called com.jakobkhansen.silksong.apk says nothing useful in a downloads
# folder, and says nothing at all about which build it is. VERSION at the repo
# root is the single source of truth, and dev.sh and the Makefile derive the
# same name from the same file.
APK_NAME="${APK_NAME:-SilksongAndroid-$VERSION_NAME.apk}"

if [[ -z "${VERSION_CODE:-}" ]]; then
    _core="${VERSION_NAME%%-*}"
    IFS='.' read -r _maj _min _pat <<< "$_core"
    _maj="${_maj:-0}"; _min="${_min:-0}"; _pat="${_pat:-0}"
    [[ "$_maj" =~ ^[0-9]+$ && "$_min" =~ ^[0-9]+$ && "$_pat" =~ ^[0-9]+$ ]] \
        || fail "VERSION '$VERSION_NAME' is not major.minor.patch[-prerelease]"
    (( _min < 100 && _pat < 100 )) \
        || fail "VERSION '$VERSION_NAME': minor and patch must be below 100, or versionCode stops increasing"
    VERSION_CODE=$(( _maj * 10000 + _min * 100 + _pat ))
fi

should_run() { case ",$STEPS," in *",$1,"*) return 0 ;; *) return 1 ;; esac; }

# Android's build-tools ship .bat wrappers on Windows and bare executables
# everywhere else, so the same name is not directly runnable in both places.
# Resolving it here keeps one script usable from Git Bash, Linux and Termux.
bt_tool() {
    local dir=$1 name=$2 c
    for c in "$dir/$name" "$dir/$name.exe" "$dir/$name.bat"; do
        [[ -f "$c" ]] && { printf '%s\n' "$c"; return 0; }
    done
    fail "no $name under $dir"
}

# The Android player module. dev.sh resolves this -- from a Unity install if
# there is one, otherwise from the module fetched by `make player` -- and
# exports it, because the two are interchangeable here and only the caller
# knows which exists. Falls back to the Unity layout when run directly.
AP="${AP:-$UNITY_DIR/PlaybackEngines/AndroidPlayer}"
VARIATION="$AP/Variations/il2cpp/Release"
# ─── Step 5: the APK shell ──────────────────────────────────────────────────
#
# Unity 6's classes.jar contains UnityPlayer but NOT UnityPlayerActivity — the
# Editor generates that into the Gradle project. Unity does ship its source, so
# it can be compiled directly and no Editor run is needed.
#
# The launcher is merged in here too, when its AAR has been built. There is no
# Gradle in this pipeline, so the parts AGP would normally do are done by hand:
# unpack the AAR, compile its resources alongside ours, generate the R class
# for its package with the same IDs (aapt2 --extra-packages), and dex its
# classes together with the runtime JARs its dependencies need. Build it with:
#
#   cd src/SilksongLauncher.Launcher && gradle :app:assembleRelease :app:collectRuntimeDeps
#
# Without it the APK still builds and boots straight into the game, which is
# what the on-device pipeline wants -- it has no Kotlin toolchain.
step_5_apk_shell() {
    log "step 5: building the APK shell"
    local sh="$OUT/shell" bt android_jar jdk launcher_aar launcher_deps
    bt=$(find "$ANDROID_SDK/build-tools" -maxdepth 1 -mindepth 1 -type d | sort -V | tail -1)
    [[ -d "$bt" ]] || fail "no build-tools under $ANDROID_SDK/build-tools"
    android_jar=$(find "$ANDROID_SDK/platforms" -name android.jar | sort -V | tail -1)
    [[ -f "$android_jar" ]] || fail "no android.jar under $ANDROID_SDK/platforms"
    jdk="${JDK:-$AP/OpenJDK}/bin"

    launcher_aar="$LAUNCHER_AAR"
    launcher_deps="$LAUNCHER_DEPS"

    rm -rf "$sh"; mkdir -p "$sh/res/values" "$sh/javaout" "$sh/dex"

    local have_launcher=0
    if [[ -f "$launcher_aar" ]]; then
        have_launcher=1
        mkdir -p "$sh/aar"
        ( cd "$sh/aar" && unzip -qo "$launcher_aar" )
        log "  launcher: $launcher_aar ($(ls "$launcher_deps"/*.jar 2>/dev/null | wc -l) runtime JARs)"
    else
        log "  launcher: not built, the APK will boot straight into the game"
    fi

    # With the launcher present it owns the app icon and the game is reached
    # through it; without it the game has to be launchable on its own.
    local game_filter="" launcher_block="" app_attrs=""
    if (( DEBUGGABLE )); then
        app_attrs='android:debuggable="true"'
    fi
    if (( have_launcher )); then
        app_attrs="$app_attrs"' android:name="dev.silksong.launcher.SilksongApp" android:appCategory="game"'
        # The launcher screens sit in their own process so that the game
        # calling System.exit on quit does not take them with it.
        launcher_block=$(cat <<'XML'
        <meta-data android:name="android.game_mode_config"
                   android:resource="@xml/game_mode_config" />
        <activity android:name="dev.silksong.launcher.SetupActivity"
            android:exported="true" android:process=":launcher"
            android:configChanges="orientation|screenSize|screenLayout|keyboardHidden"
            android:theme="@android:style/Theme.DeviceDefault.NoActionBar">
            <intent-filter>
                <action android:name="android.intent.action.MAIN" />
                <category android:name="android.intent.category.LAUNCHER" />
            </intent-filter>
        </activity>
        <activity android:name="dev.silksong.launcher.LauncherActivity"
            android:exported="false" android:process=":launcher"
            android:configChanges="orientation|screenSize|screenLayout|keyboardHidden"
            android:theme="@android:style/Theme.DeviceDefault.NoActionBar" />
        <activity android:name="dev.silksong.launcher.LoginActivity"
            android:exported="false" android:process=":launcher"
            android:configChanges="orientation|screenSize|screenLayout|keyboardHidden"
            android:theme="@android:style/Theme.DeviceDefault.NoActionBar" />
        <activity android:name="dev.silksong.launcher.SettingsActivity"
            android:exported="false" android:process=":launcher"
            android:configChanges="orientation|screenSize|screenLayout|keyboardHidden"
            android:theme="@android:style/Theme.DeviceDefault.NoActionBar" />
        <activity android:name="dev.silksong.launcher.LogActivity"
            android:exported="false" android:process=":launcher"
            android:configChanges="orientation|screenSize|screenLayout|keyboardHidden"
            android:theme="@android:style/Theme.DeviceDefault.NoActionBar" />
        <activity android:name="dev.silksong.launcher.ModsActivity"
            android:exported="false" android:process=":launcher"
            android:configChanges="orientation|screenSize|screenLayout|keyboardHidden"
            android:theme="@android:style/Theme.DeviceDefault.NoActionBar" />
        <!--
          The two builder processes. See MonoService: the .NET runtime needs a
          process of its own, cannot be started twice in one, and a run
          therefore ends by killing the process it ran in. There are two so
          that the next run never asks for a process whose predecessor is
          still being reaped.
        -->
        <service android:name="dev.silksong.launcher.MonoService"
            android:exported="false" android:process=":builder" />
        <service android:name="dev.silksong.launcher.MonoServiceAlt"
            android:exported="false" android:process=":builder2" />
XML
)
        # The heredoc above is quoted, so that the XML is taken literally and
        # cannot be surprised by a $ in it. Nothing in there varies with the
        # package any more.
    else
        game_filter=$(cat <<'XML'
            <intent-filter>
                <action android:name="android.intent.action.MAIN" />
                <category android:name="android.intent.category.LAUNCHER" />
            </intent-filter>
XML
)
    fi

    cat > "$sh/AndroidManifest.xml" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<manifest xmlns:android="http://schemas.android.com/apk/res/android"
    package="$PKG" android:versionCode="$VERSION_CODE" android:versionName="$VERSION_NAME">
    <uses-sdk android:minSdkVersion="26" android:targetSdkVersion="$TARGET_SDK" />
    <uses-feature android:glEsVersion="0x00030002" android:required="true" />
    <uses-permission android:name="android.permission.INTERNET" />
    <!--
      Haptics. The game mixes vibration every frame and hands it to InControl,
      whose UnityInputDevice.Vibrate is an empty method on Android, so
      AndroidRumble plays it through the platform Vibrator instead, and that
      needs this. A normal permission: granted at install, never prompted for,
      and inert if the device has no actuator.
    -->
    <uses-permission android:name="android.permission.VIBRATE" />
    <!--
      Ending a builder process when a build is cancelled goes down the
      binding now, but this is still what clears a straggler left by a run
      that would not stop. A normal permission: granted at install, never
      prompted for.
    -->
    <uses-permission android:name="android.permission.KILL_BACKGROUND_PROCESSES" />
    <!--
      Storage, for the folder a user may pick to say where their copy of the
      game already is. The picker hands back a URI; what every step after it
      needs is a path, because the catalog is repointed at a symlink to the
      depot's own content tree and the retarget rewrites that tree in place
      through a .NET process. Neither of those can go through a provider.

      Plain file access to a picked folder is available because this app
      targets SDK 28, which SELinux forces on us anyway (see TARGET_SDK
      above): the platform gives an app below 29 the legacy storage view,
      which is the whole volume once READ_EXTERNAL_STORAGE is granted. The two
      constraints happen to want the same thing.

      Requested at runtime, and refusing costs nothing. The app's own external
      directory needs no permission, is still where a download goes, and is
      still the first place a hand copied depot is looked for.
    -->
    <uses-permission android:name="android.permission.READ_EXTERNAL_STORAGE" />
    <uses-permission android:name="android.permission.WRITE_EXTERNAL_STORAGE" />
    <!--
      Steam's CDN serves depot chunks over plain HTTP, and its own clients
      download them that way: each chunk is encrypted with the depot key and
      checksummed before anything is written, so the transport adds nothing
      the format does not already provide.

      This is deliberately the blunt attribute rather than a network security
      config naming Valve's hosts. Declaring any <domain-config> switches the
      platform to a domain-aware TrustManager, and any TLS stack that calls
      the two-argument checkServerTrusted then fails with "Domain specific
      configurations require that hostname aware checkServerTrusted is used".
      That is what the Steam login connection does, so the scoped version
      fixed the chunks and broke the sign-in.

      Nothing is downgraded by this: it permits cleartext where a URL asks for
      it, and every other connection here (the Steam login, the fetches from
      Unity) asks for https and stays encrypted.

      hasFragileUserData puts a "Keep app data?" checkbox on the uninstall
      dialog (Android 10+). Without it an uninstall silently takes the depot
      with it, and that is a multi-gigabyte download and a ~27 minute rebuild
      to get back, along with any save that was not synced to Steam Cloud.
      The data this app accumulates is far more expensive than the app.

      (Note for editors: this is an XML comment, so it may not contain a
      double hyphen anywhere. One in the prose above cost a build.)
    -->
    <application android:label="@string/app_name" android:hasCode="true"
                 android:icon="@mipmap/ic_launcher"
                 android:roundIcon="@mipmap/ic_launcher"
                 android:extractNativeLibs="true"
                 android:usesCleartextTraffic="true"
                 android:largeHeap="true"
                 android:hasFragileUserData="true" $app_attrs>
$launcher_block
        <activity android:name="dev.silksong.shell.GameActivity"
            android:exported="true" android:launchMode="singleTask"
            android:configChanges="mcc|mnc|locale|touchscreen|keyboard|keyboardHidden|navigation|orientation|screenLayout|uiMode|screenSize|smallestScreenSize|fontScale|layoutDirection|density"
            android:screenOrientation="sensorLandscape"
            android:resizeableActivity="false"
            android:theme="@android:style/Theme.NoTitleBar.Fullscreen">
$game_filter
            <meta-data android:name="unityplayer.UnityActivity" android:value="true" />
        </activity>
    </application>
</manifest>
EOF

    # game_view_content_description is required: UnityPlayer looks it up during
    # construction and throws Resources\$NotFoundException without it.
    cat > "$sh/res/values/strings.xml" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<resources>
    <string name="app_name">$APP_LABEL</string>
    <string name="game_view_content_description">Game view</string>
</resources>
EOF

    mkdir -p "$sh/res/xml"
    cp -f "$SCRIPT_DIR/shell/res/xml/game_mode_config.xml" "$sh/res/xml/"

    # The launcher icon, at every density plus the adaptive variant. Copied
    # rather than generated: they are ours, they do not change per build, and
    # aapt2 wants them in the tree it compiles.
    for d in "$SCRIPT_DIR"/shell/res/mipmap-*; do
        [[ -d "$d" ]] || continue
        mkdir -p "$sh/res/$(basename "$d")"
        cp -f "$d"/* "$sh/res/$(basename "$d")/"
    done

    local link_res=(-R "$sh/res.zip") extra_pkg=()
    "$(bt_tool "$bt" aapt2)" compile -o "$sh/res.zip" --dir "$sh/res"
    if (( have_launcher )); then
        # The launcher's own layouts and strings. Its compiled code refers to
        # them through dev.silksong.launcher.R, which AGP would normally
        # regenerate at app-build time with the final IDs; --extra-packages is
        # the same thing, emitting a second R.java for that package.
        "$(bt_tool "$bt" aapt2)" compile -o "$sh/res-launcher.zip" --dir "$sh/aar/res"
        link_res+=(-R "$sh/res-launcher.zip")
        extra_pkg=(--extra-packages dev.silksong.launcher)
    fi
    "$(bt_tool "$bt" aapt2)" link -o "$sh/base.apk" -I "$android_jar" \
        --manifest "$sh/AndroidManifest.xml" "${link_res[@]}" --auto-add-overlay \
        --java "$sh/gen" "${extra_pkg[@]}"


    # GameActivity and the activity that hosts the player (shell/*.java) are
    # ours, and are compiled here together with the generated R classes the
    # launcher's code resolves against.
    #
    # Unity's classes.jar is on the compile classpath and is NOT an input to
    # the dexer: com.unity3d.player.* resolves at runtime from a dex the app
    # builds on the device out of the module it downloads. That is the whole
    # point -- the APK links against those types without carrying them.
    # Unity's own UnityPlayerActivity.java used to be compiled in here too;
    # PlayerActivity replaces it for the same reason.
    mkdir -p "$sh/src"
    cp -f "$SCRIPT_DIR/shell/GameActivity.java" "$sh/src/"
    cp -f "$SCRIPT_DIR/shell/PlayerActivity.java" "$sh/src/"

    local unity_classes="$VARIATION/Classes/classes.jar"
    local cp="$unity_classes:$android_jar"
    # fixed: never changes between rebuilds. changed: rebuilt every time.
    local fixed=() changed=()
    local srcs=("$sh/src/PlayerActivity.java" "$sh/src/GameActivity.java")
    if (( have_launcher )); then
        cp="$cp:$sh/aar/classes.jar"
        changed+=("$sh/aar/classes.jar")
        local jar
        for jar in "$launcher_deps"/*.jar; do
            [[ -f "$jar" ]] || continue
            cp="$cp:$jar"
            fixed+=("$jar")
        done
        while IFS= read -r f; do srcs+=("$f"); done < <(find "$sh/gen" -name 'R.java')
    fi

    # --release 17 pins the bytecode level to what d8 accepts. Without it
    # javac targets whatever JDK was found -- a JDK 25 emits class file major
    # 69 and d8 refuses it. Unity's bundled JDK happened to be 17, which is
    # why this only surfaced once the build stopped depending on Unity.
    "$jdk/javac" --release 17 -nowarn -cp "$cp" -d "$sh/javaout" "${srcs[@]}"
    "$jdk/jar" --create --file "$sh/activity.jar" -C "$sh/javaout" .
    changed+=("$sh/activity.jar")

    # Dexing dominates a rebuild, and almost none of its input moves: the
    # launcher's runtime JARs are the same bytes every time. They are dexed
    # once into a cache keyed by that input set, and later builds merge the
    # cache with the jars that actually changed -- d8 takes dex as input, so
    # this is a merge rather than a recompile.
    #
    # --lib on Unity's classes.jar, on every d8 call: our activities reference
    # com.unity3d.player.*, and the dexer has to resolve those types to
    # desugar and verify against them. A --lib is a compile-time reference
    # only; nothing from it lands in the output. That is the difference
    # between linking against the player classes and shipping them.
    #
    # Native multidex: min-api 26 means the platform loads classes2.dex and
    # beyond on its own, which this needs -- JavaSteam alone brings in ktor,
    # protobuf and the Kotlin runtime.
    local cache key
    if (( ${#fixed[@]} )); then
        key=$(for f in "${fixed[@]}"; do stat -c '%n %s %Y' "$f"; done | sha256sum | cut -c1-16)
        cache="$OUT/dexcache/$key"
        if [[ ! -f "$cache/.done" ]]; then
            log "  dexing the fixed input set (cached as $key)"
            rm -rf "$OUT/dexcache"; mkdir -p "$cache"
            "$(bt_tool "$bt" d8)" --min-api 26 --lib "$android_jar" --lib "$unity_classes" \
                --output "$cache" "${fixed[@]}"
            touch "$cache/.done"
        fi
        changed+=("$cache"/classes*.dex)
    fi
    "$(bt_tool "$bt" d8)" --min-api 26 --lib "$android_jar" --lib "$unity_classes" \
        --output "$sh/dex" "${changed[@]}"

    log "  dex: $(ls "$sh/dex"/classes*.dex | wc -l) file(s), $(du -ch "$sh/dex"/classes*.dex | tail -1 | cut -f1)"

    # D8, dexed, so the device can run it.
    #
    # The device has to turn Unity's classes.jar into dex, because that jar is
    # downloaded there rather than shipped here. A dexer is an ordinary Java
    # program, and ART runs dex -- so d8 dexed with itself runs on the phone
    # with no JVM to install. Verified on device: it dexes Unity's classes.jar
    # in about two seconds.
    #
    # This is Google's tool under Apache-2.0, not Unity's and not the game's,
    # so unlike everything else here it can travel in the APK. ~3.5 MB.
    #
    # Cached on the jar's identity: dexing it takes ~40s and it never changes
    # between builds.
    local d8jar="$bt/lib/d8.jar" d8key d8cache
    if [[ -f "$d8jar" ]]; then
        d8key=$(stat -c '%s %Y' "$d8jar" | sha256sum | cut -c1-16)
        d8cache="$OUT/d8dex/$d8key"
        if [[ ! -f "$d8cache/d8.zip" ]]; then
            log "  dexing d8 for the device (cached as $d8key)"
            rm -rf "$OUT/d8dex"; mkdir -p "$d8cache/raw"
            "$(bt_tool "$bt" d8)" --release --min-api 26 --lib "$android_jar" \
                --output "$d8cache/raw" "$d8jar"
            ( cd "$d8cache/raw" && "$jdk/jar" --create --file "$d8cache/d8.zip" \
                --no-manifest classes*.dex )
            rm -rf "$d8cache/raw"
        fi
        cp -f "$d8cache/d8.zip" "$sh/d8.zip"
        log "  d8 for the device: $(du -h "$sh/d8.zip" | cut -f1)"
    else
        fail "no d8.jar under $bt/lib -- the device cannot dex Unity's classes without it"
    fi
}

# ─── Step 6: package, align, sign ───────────────────────────────────────────
step_6_package() {
    log "step 6: packaging"
    local sh="$OUT/shell" stage="$OUT/staging" bt unsigned="$OUT/unsigned.apk" jdk
    bt=$(find "$ANDROID_SDK/build-tools" -maxdepth 1 -mindepth 1 -type d | sort -V | tail -1)
    jdk="${JDK:-$AP/OpenJDK}/bin"

    # Rebuilt in place rather than from scratch: the player image is ~60 MB and
    # re-copying it is most of the packaging time, while everything else here
    # is small enough to just replace. Only the parts that can change are
    # cleared out.
    mkdir -p "$stage/lib/arm64-v8a" "$stage/assets/bin/Data"
    # Everything the AAR provides is cleared before it is copied back in.
    # Merging over what is already there looks equivalent and is not: a
    # packaging run that did not refresh one of these files shipped a stale
    # copy of the on-device build script, and the only symptom was the game
    # crashing on launch twenty minutes later.
    rm -rf "$stage/lib" "$stage/assets/aa" "$stage/assets/ondevice"
    rm -f "$stage"/classes*.dex "$stage/AndroidManifest.xml" "$stage/resources.arsc"
    mkdir -p "$stage/lib/arm64-v8a"
    # Nothing Unity-made and nothing game-derived is staged here, and there is
    # no switch to make it happen: libunity.so, libmain.so and the player
    # classes are fetched on the device, libil2cpp.so is compiled there, and
    # the player image and data package are built there. This used to be a
    # choice (ENGINE=/DATA=apk put them in the APK, external left them out)
    # back when a PC could produce them. The PC cannot any more -- steps 1-4
    # are gone -- so the switch had one reachable setting and is better as a
    # property of the build than an option someone can get wrong.
    #
    # classes.dex, classes2.dex, ... -- the launcher's dependencies push this
    # well past one file.
    cp -f "$sh/dex"/classes*.dex "$stage/"
    # The dexer the device needs to turn Unity's downloaded classes.jar into
    # dex. Google's tool, Apache-2.0, so it can ship here.
    if [[ -f "$sh/d8.zip" ]]; then
        mkdir -p "$stage/assets"
        cp -f "$sh/d8.zip" "$stage/assets/d8.zip"
    fi
    # The launcher's own assets, and the native halves of its dependencies.
    # These are ours, not Unity's, so they travel in the APK.
    if [[ -d "$sh/aar" ]]; then
        cp -f "$sh/aar/jni/arm64-v8a"/*.so "$stage/lib/arm64-v8a/" 2>/dev/null || true
        if [[ -d "$sh/aar/assets" ]]; then cp -r "$sh/aar/assets/." "$stage/assets/"; fi
    fi
    # And the native halves of the launcher's dependencies -- zstd-jni, which
    # JavaSteam needs to decompress depot chunks. Without these the download
    # dies on the first zstd chunk with a NoClassDefFoundError.
    if [[ -d "$LAUNCHER_DEPS/jni/arm64-v8a" ]]; then
        cp -f "$LAUNCHER_DEPS/jni/arm64-v8a"/*.so "$stage/lib/arm64-v8a/" 2>/dev/null || true
    fi
    ( cd "$stage" && unzip -qo "$sh/base.apk" )

    # Everything is stored rather than deflated: Android R+ wants
    # resources.arsc uncompressed and 4-byte aligned, the natives have to stay
    # mappable, and the assets are already-compressed bundles that deflate
    # would only make slower. The JDK's jar does that with --no-compress, so
    # packaging needs nothing step 5 does not already need -- one less thing to
    # install on the device, where this pipeline also has to run.
    rm -f "$unsigned" "$OUT/aligned.apk"
    "$jdk/jar" --create --file "$unsigned" --no-manifest --no-compress -C "$stage" .

    "$(bt_tool "$bt" zipalign)" -p -f 4 "$unsigned" "$OUT/aligned.apk"

    # Signing.
    #
    # The default is the local debug keystore, which is what makes a dev build
    # installable with no setup. A RELEASE must not use it: the signing key is
    # the identity Android checks on every update, so a key that differs
    # between releases -- or one everybody has a copy of -- means users cannot
    # upgrade in place. CI therefore passes its own keystore through KEYSTORE,
    # with the passwords from secrets.
    #
    # KEY_ALIAS is optional. apksigner picks the only key in a single-key
    # store by itself, which is true of debug.keystore; a release store with
    # more than one needs to be told.
    local ks="${KEYSTORE:-$HOME/.android/debug.keystore}"
    [[ -f "$ks" ]] || fail "no keystore at $ks (set KEYSTORE=)"
    mkdir -p "$APK_DIR"
    local -a sign_args=(sign --ks "$ks"
        --ks-pass "pass:${KS_PASS:-android}"
        --key-pass "pass:${KEY_PASS:-android}")
    [[ -n "${KEY_ALIAS:-}" ]] && sign_args+=(--ks-key-alias "$KEY_ALIAS")
    "$(bt_tool "$bt" apksigner)" "${sign_args[@]}" \
        --out "$APK_DIR/$APK_NAME" "$OUT/aligned.apk"

    log "  APK: $APK_DIR/$APK_NAME ($(du -h "$APK_DIR/$APK_NAME" | cut -f1))"
    log "  install: adb install -r $APK_DIR/$APK_NAME"
}

# ─── Driver ─────────────────────────────────────────────────────────────────
mkdir -p "$OUT"
log "player: $AP"
log "out:    $OUT"
log "steps:  $STEPS"
log "version: $VERSION_NAME (code $VERSION_CODE)"

should_run 5 && step_5_apk_shell
should_run 6 && step_6_package
log "done"
