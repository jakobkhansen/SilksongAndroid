#!/system/bin/sh
#
# Fetch everything the build needs that is not ours and not the user's game.
#
# The builder ships none of this. Unity's engine, toolchain and packages are
# Unity's to distribute, so the device gets them from Unity, and the phone's
# owner obtains them the same way any Unity developer does. Nothing here is
# redistributed by us, and nothing here is game-derived -- the game comes from
# the user's own Steam depot and never leaves their device.
#
# What is fetched, and why it cannot simply be committed:
#
#   Unity editor        il2cpp (the IL->C++ converter and the libil2cpp runtime
#                       sources) and the unityaot BCL. Only ~155 MB is kept.
#                       The archive is a single xz block, so it cannot be
#                       seeked into -- but tar is sequential and Unity packs
#                       both wanted trees near the front, so a 640 MiB prefix
#                       of the 4.29 GB archive is streamed and the rest is
#                       never asked for.
#
#   Android module      libunity.so, libmain.so, classes.jar, baselib.a, the
#                       engine's built-in resources and the stock managed
#                       assemblies. For this Unity version the module is
#                       published only as a macOS .pkg, which is a xar archive
#                       wrapping a cpio payload -- bsdtar reads both.
#
#   Unity packages      com.unity.inputsystem. The depot's copy is the desktop
#                       build, with the Android backend compiled out, so the
#                       game cannot see a gamepad. Packages ship as SOURCE, so
#                       this is compiled on device (see compile-packages.sh).
#                       Its licence, the Unity Companion License, states that
#                       use is acceptance, so there is nothing to click.
#
#   JDK + d8            Unity's classes.jar is Java bytecode and the built APK
#                       needs it as dex, so a dexer has to run here. d8 is an
#                       ordinary Java program; the JDK is Termux's.
#
# Everything lands under $ROOT and is skipped if already present, so an
# interrupted first run is resumed by running this again.
set -e

ROOT="${ROOT:-/data/local/tmp/unity}"
UNITY_VERSION="${UNITY_VERSION:-6000.0.52f1}"
# The changeset is part of every download URL. It is the hash the player
# reports at startup ("Version '6000.0.52f1 (9e4086222921)'").
UNITY_CHANGESET="${UNITY_CHANGESET:-9e4086222921}"
INPUTSYSTEM_VERSION="${INPUTSYSTEM_VERSION:-1.14.2}"
BUILD_TOOLS="${BUILD_TOOLS:-r34}"

DL="https://download.unity3d.com/download_unity/$UNITY_CHANGESET"
mkdir -p "$ROOT" "$ROOT/dl"

# Expected digests. Nothing is used before it has been checked against one of
# these: a download that does not match is a download we have not tested
# against, whether the cause is a corrupted transfer, a CDN that has quietly
# republished, or something worse.
#
# Unity's own value, published per artifact by
# services.api.unity.com/unity/editor/release/v1/releases?version=<version>,
# is MD5 and is used as-is for the Android module. The rest are SHA-256 taken
# from the files this pipeline was built against.
#
# All are pinned to $UNITY_VERSION / $INPUTSYSTEM_VERSION / $BUILD_TOOLS above.
# Changing any of those versions means replacing the digest beside it.
ANDROID_PKG_MD5="cf56c6b46324f8e95f14063b4c5cfea8"
INPUTSYSTEM_SHA256="875008478396009708fdcd333d8b3108097a575652e36e9f3ecde66e0af21f26"
BUILD_TOOLS_SHA256="e858c4b60069d0431051b225d384413b1643e1289b00a4825aed347f25bd510f"

# The editor archive is verified per extracted file rather than over the bytes
# they arrived in. That survives Unity repacking the archive, and it checks
# what actually runs; Unity's published digest covers the whole 4.29 GB, which
# is no use when most of it is never fetched.
EDITOR_FILES="Editor/Data/il2cpp/build/deploy/il2cpp.dll 02d9d225cc8968fe39284dfbf2a9912796b3b0666d274294cfa6b90cf5e946bb
Editor/Data/il2cpp/libil2cpp/il2cpp-config.h 38d4d2855d372bb2a12de7dce3cde110d1ec9780232a6a298153bee96c352259
Editor/Data/MonoBleedingEdge/lib/mono/unityaot-linux/mscorlib.dll 488cadc98f9d6596ae954e6b9bbfbc72f6e1142ff9fa98de6cc9e8baf2034988"

# Where to stop reading the editor archive, as a first guess. This is not a
# constant the result depends on: if the wanted trees have not all arrived by
# then, the guess doubles and the fetch is retried, up to the whole archive.
# It is a starting point that saves 3.6 GB when it is right, not an assumption
# that breaks when it is wrong.
EDITOR_FIRST_TRY_MB="${EDITOR_FIRST_TRY_MB:-640}"

# The editor archive is 4.29 GB and only about 155 MB of it is wanted. tar is
# sequential and Unity packs it by walking a directory tree, so each
# directory's entries are contiguous: bsdtar is told which members to take and
# quits as soon as it has them, and the transfer stops there -- roughly 600 MB
# in, without either end having to know that number in advance.
#
# Verification is per extracted file rather than over the bytes they arrived
# in. That survives Unity repacking the archive, and it checks what actually
# runs; Unity's published digest covers the whole 4.29 GB, which is no use when
# most of it is never fetched.


have() { [ -e "$1" ]; }
say()  { echo "[fetch] $*"; }
die()  { echo "[fetch] $*" >&2; exit 1; }

# digest <algo> <file> -- md5 or sha256, lowercase hex, no filename.
digest() {
    case "$1" in
        md5)    md5sum    "$2" | cut -d' ' -f1 ;;
        sha256) sha256sum "$2" | cut -d' ' -f1 ;;
    esac
}

# verify <algo> <file> <expected> -- removes the file on mismatch so that
# re-running fetches it again rather than getting stuck on a bad copy.
verify() {
    got=$(digest "$1" "$2")
    [ "$got" = "$3" ] && { say "  $1 ok"; return 0; }
    rm -f "$2"
    die "$(basename "$2"): expected $1 $3 but got $got -- refusing to use it"
}

# Android's toolbox tar reads gzip but not xz, and knows nothing of xar. The
# editor archive is .tar.xz and the Android module is a .pkg, so libarchive's
# bsdtar is required for both -- fail here with something legible rather than
# part-way through a multi-gigabyte download.
#
# It goes by several names. Windows 10 and later ship libarchive as
# System32\tar.exe while Git Bash's own `tar` is GNU's, so the name alone
# decides nothing: what matters is whether the binary says libarchive.
find_bsdtar() {
    local c
    for c in bsdtar /c/Windows/System32/tar.exe "$SYSTEMROOT/System32/tar.exe" tar; do
        [ -n "$c" ] || continue
        command -v "$c" >/dev/null 2>&1 || continue
        "$c" --version 2>&1 | head -1 | grep -qi libarchive && { echo "$c"; return; }
    done
}
BSDTAR=$(find_bsdtar)
command -v curl >/dev/null 2>&1 || { echo "[fetch] curl not found." >&2; exit 1; }
[ -n "$BSDTAR" ] || {
    echo "[fetch] no libarchive tar found." >&2
    echo "        On Android it comes from the Termux libarchive package;" >&2
    echo "        on Linux and macOS from libarchive; on Windows 10+ it is" >&2
    echo "        already at System32\\tar.exe." >&2
    exit 1
}

# Which pieces to fetch. The device wants all of them; a machine building the
# APK wants only the Android module, which is where Unity's player classes,
# its Gradle and the engine libraries come from -- see COPILOT.md on why that
# is the one thing the host still needs from Unity.
WHAT="${WHAT:-all}"
wanted() { [ "$WHAT" = all ] || [ "$WHAT" = "$1" ]; }

fetch() {
    url="$1"; out="$2"; algo="$3"; want="$4"
    if have "$out"; then
        say "have $(basename "$out")"
    else
        say "downloading $(basename "$out")"
        # The destination directory may not exist yet: with WHAT= selecting a
        # single section, whichever section ran first and created it is skipped.
        mkdir -p "$(dirname "$out")"
        # -C - resumes a partial transfer, which matters for multi-gigabyte
        # downloads over a phone's connection.
        curl -fL -C - --retry 5 --retry-delay 5 -o "$out.part" "$url"
        mv "$out.part" "$out"
    fi
    [ -n "$want" ] && verify "$algo" "$out" "$want"
}

# ── Unity editor: il2cpp + the unityaot BCL ─────────────────────────────────
#
# Streamed rather than stored: ~155 MB of a 4.29 GB archive is wanted, and
# there is no reason to hold the rest on disk even briefly.
#
# tar is sequential and Unity packs it by walking a directory tree, so both
# wanted trees are complete a few hundred megabytes in and the rest is of no
# interest. bsdtar cannot be told "stop once these patterns are exhausted"
# though -- --fast-read stops at the first match of each -- so the read is cut
# at a byte count instead, and the cut then has to be checked rather than
# trusted: if anything wanted is missing the cut doubles and it tries again.
# Wrong guesses cost time, not correctness, and the common case reads 640 MiB
# instead of 4.29 GB.
if wanted editor && ! have "$ROOT/editor/Editor/Data/il2cpp"; then
    limit=$((EDITOR_FIRST_TRY_MB * 1024 * 1024))
    full=4301350560
    while :; do
        say "streaming $((limit / 1024 / 1024)) MiB of the editor archive for il2cpp + BCL"
        rm -rf "$ROOT/editor"; mkdir -p "$ROOT/editor"
        curl -fL --retry 5 -r "0-$((limit - 1))" \
            "$DL/LinuxEditorInstaller/Unity-$UNITY_VERSION.tar.xz" \
            | $BSDTAR -xf - -C "$ROOT/editor" \
                'Editor/Data/il2cpp/*' \
                'Editor/Data/MonoBleedingEdge/lib/mono/unityaot-linux/*' 2>/dev/null || true

        missing=""
        echo "$EDITOR_FILES" | while read -r path want; do
            [ -n "$path" ] || continue
            have "$ROOT/editor/$path" || echo "$path"
        done > "$ROOT/dl/.missing"
        missing=$(cat "$ROOT/dl/.missing"); rm -f "$ROOT/dl/.missing"

        [ -z "$missing" ] && break
        [ "$limit" -ge "$full" ] && die "editor archive read in full and still missing: $missing"
        limit=$((limit * 2))
        [ "$limit" -gt "$full" ] && limit=$full
        say "  not all of it arrived; retrying with a larger read"
    done

    echo "$EDITOR_FILES" | while read -r path want; do
        [ -n "$path" ] || continue
        verify sha256 "$ROOT/editor/$path" "$want"
    done
else
    say "have editor pieces"
fi

# ── Android player module ──────────────────────────────────────────────────
if wanted android && ! have "$ROOT/android/Variations"; then
    fetch "$DL/MacEditorTargetInstaller/UnitySetup-Android-Support-for-Editor-$UNITY_VERSION.pkg" \
          "$ROOT/dl/android-support.pkg" md5 "$ANDROID_PKG_MD5"
    say "extracting the Android module"
    mkdir -p "$ROOT/android"
    # A .pkg is a xar archive whose Payload is a gzipped cpio; bsdtar reads
    # the outer xar, and the payload is then read the same way.
    $BSDTAR -xf "$ROOT/dl/android-support.pkg" -C "$ROOT/dl" 2>/dev/null || true
    # A .pkg holds several component packages, each with its own Payload; the
    # first one found is a few kilobytes of install scripts. The largest is the
    # one carrying the engine.
    payload=$(find "$ROOT/dl" -name 'Payload' -exec ls -S {} + 2>/dev/null | head -1)
    [ -n "$payload" ] || die "no Payload inside the .pkg"
    $BSDTAR -xf "$payload" -C "$ROOT/android"
fi

# ── Unity package sources ──────────────────────────────────────────────────
#
# packages.unity.com serves these publicly and unauthenticated. They arrive as
# source; compile-packages.sh builds them against the assemblies fetched above.
if wanted packages && ! have "$ROOT/packages/com.unity.inputsystem"; then
    fetch "https://packages.unity.com/com.unity.inputsystem/-/com.unity.inputsystem-$INPUTSYSTEM_VERSION.tgz" \
          "$ROOT/dl/inputsystem.tgz" sha256 "$INPUTSYSTEM_SHA256"
    mkdir -p "$ROOT/packages/com.unity.inputsystem"
    # npm-style tarballs wrap everything in package/.
    $BSDTAR -xf "$ROOT/dl/inputsystem.tgz" -C "$ROOT/packages/com.unity.inputsystem" \
        --strip-components=1
fi

# ── dexer ──────────────────────────────────────────────────────────────────
#
# d8 is distributed in the Android build-tools, and is plain Java, so it runs
# on the JDK fetched below rather than needing an Android-specific build.
if wanted buildtools && ! have "$ROOT/build-tools/lib/d8.jar"; then
    fetch "https://dl.google.com/android/repository/build-tools_${BUILD_TOOLS}-linux.zip" \
          "$ROOT/dl/build-tools.zip" sha256 "$BUILD_TOOLS_SHA256"
    mkdir -p "$ROOT/build-tools"
    $BSDTAR -xf "$ROOT/dl/build-tools.zip" -C "$ROOT/build-tools" --strip-components=1
fi

say "done — $(du -sh "$ROOT" 2>/dev/null | cut -f1) under $ROOT"
# 2>/dev/null already covers the directories a partial fetch did not create.
say "kept: $(du -sh "$ROOT/editor" "$ROOT/android" "$ROOT/packages" "$ROOT/build-tools" 2>/dev/null | tr '\n' ' ')"
