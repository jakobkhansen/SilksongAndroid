.DEFAULT_GOAL := help

# The dev loop is tools/depot-to-apk/dev.sh; these targets are a thin wrapper
# around it so there is one place to look. Paths are discovered, not
# configured. See COPILOT.md.
#
# On Windows run this from Git Bash. `bash` on PATH is WSL, which cannot read
# the Windows paths this uses. Or avoid the question entirely: `make
# docker-apk` builds on Linux in a container and needs nothing but Docker.
#
# Not from PowerShell or cmd: make then runs recipes through cmd.exe, which
# fails on the first `VAR=value cmd` with "'DEBUGGABLE' is not recognized".
# From PowerShell, hand the whole thing to Git Bash's own bash.exe:
#
#   & 'C:\Program Files\Git\bin\bash.exe' -lc 'cd "$(cygpath "$PWD")" && make dev'

DEV     := tools/depot-to-apk/dev.sh
ADB     ?= adb
# Nothing the build produces or fetches lives in the checkout except the APK
# itself: the staging tree carries game data lifted out of the depot, and the
# player module is Unity's redistributable. Those go under a cache root; the
# finished APK, which contains neither, lands in build/.
#
# `cd ~ && pwd` rather than $(HOME): under Git Bash make, $(HOME) arrives as a
# Windows path whose backslashes are already eaten -- C:Usersjakobhansen --
# which is a valid-looking relative path that silently writes to the wrong
# place. This yields /c/Users/... on Windows and $HOME everywhere else.
SILKSONG_CACHE ?= $(shell cd ~ && pwd)/.cache/silksong
BUILD_ROOT  ?= $(SILKSONG_CACHE)/build
PLAYER_ROOT ?= $(SILKSONG_CACHE)/unity-player
# The one build output that belongs in the repo.
APK_DIR ?= build
export SILKSONG_CACHE BUILD_ROOT PLAYER_ROOT APK_DIR
# Kept in step with dev.sh; only used by the targets that talk to the device.
PKG     ?= com.jakobkhansen.silksong
# The APK is named for the project and its version, not the application id, so
# that a downloaded file says what it is. Same VERSION file build.sh reads.
VERSION ?= $(shell tr -d ' \t\r\n' < VERSION 2>/dev/null)
APK     ?= $(APK_DIR)/SilksongAndroid-$(VERSION).apk
FILES   := /sdcard/Android/data/$(PKG)/files

.PHONY: help dev dev-fast device-wipe install logcat game-logcat build-log \
        game-reset check mod-check surgery player devices clean \
        docker-image docker-apk docker-up docker-dev docker-down docker-shell

help: ## Show this help
	@echo "Targets:"
	@grep -E '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-14s\033[0m %s\n", $$1, $$2}'

# ─── the loop ───────────────────────────────────────────────────────────────

dev: ## Rebuild the launcher, repackage the APK, install (~3 min)
	DEBUGGABLE=1 bash $(DEV)

dev-fast: ## Same, but skip Gradle (for shell/ or build.sh changes only)
	LAUNCHER=0 DEBUGGABLE=1 bash $(DEV)

# Wiping the device has nothing to do with building the APK. Keeping them
# separate means a wipe costs seconds rather than a three-minute rebuild, and
# leaves exactly one way to do it.
device-wipe: ## Uninstall and delete app storage, without rebuilding
	-$(ADB) shell "rm -rf /sdcard/Android/obb/$(PKG) /sdcard/Android/data/$(PKG)"
	-$(ADB) uninstall $(PKG)
	@echo "device wiped; 'make install' puts the last-built APK back"

install: ## Install the APK that was built last, without rebuilding
	$(ADB) install -r "$(APK)"

# ─── the container ──────────────────────────────────────────────────────────
#
# Same build, on Linux, with nothing installed on the host but Docker. No
# Unity: the Android player module is fetched at run time into build/, exactly
# as the phone fetches it. The container has no USB, so it builds the APK and
# `make install` puts it on the device.

COMPOSE := docker compose -f tools/docker/compose.yaml

docker-image: ## Build the container image
	$(COMPOSE) build apk

docker-apk: ## One-shot APK build in a fresh container (cold, hermetic)
	$(COMPOSE) run --rm apk

# The incremental loop. `run --rm` discards the Gradle daemon, its
# configuration cache and Kotlin's incremental state every time; a container
# that outlives the build keeps them.
docker-up: ## Start the persistent build container
	$(COMPOSE) up -d devbox

docker-dev: ## Rebuild the APK in the running container (incremental)
	$(COMPOSE) exec devbox /usr/local/bin/silksong-apk apk

docker-down: ## Stop the persistent build container
	$(COMPOSE) down

docker-shell: ## Open a shell in the build container
	$(COMPOSE) run --rm apk shell

# ─── the device ─────────────────────────────────────────────────────────────

# The game is built ON THE PHONE. This drops what it produced so the app will
# build it again; the compiler, .NET, Unity's tools and the Steam depot are all
# left alone, so it is minutes rather than the full run.
game-reset: ## Make the app rebuild the game (keeps tools and depot)
	$(ADB) shell run-as $(PKG) rm -f files/pkg/.built files/pkg/data.apk files/pkg/lib/arm64/libil2cpp.so
	@echo "Press Start porting in the app."

logcat: ## Stream our logs
	$(ADB) logcat -v time SilksongLauncher:V DualScreen:V AndroidRuntime:E *:S

game-logcat: ## Stream Unity's logs from the running game
	$(ADB) logcat -v brief Unity:V *:S

build-log: ## Show the on-device compile log
	$(ADB) shell cat $(FILES)/build/compile.log

devices: ## List connected devices
	$(ADB) devices -l

# ─── inputs to the build ────────────────────────────────────────────────────

surgery: ## Build bundle-surgery (Gradle stages it into the APK)
	dotnet build -c Release tools/bundle-surgery/BundleSurgery.csproj

weaver: ## Build mod-weaver (Gradle stages it into the APK)
	dotnet build -c Release tools/mod-weaver/ModWeaver.csproj

# The one thing the APK build still needs from Unity: the Android player
# module, which carries the player classes compiled into the dex,
# UnityPlayerActivity's source, Gradle, and libunity/libmain. This is the same
# module the app downloads on the device, from the same URL and digest, so a
# machine with no Unity install can build the APK. ~670 MB, fetched once.
player: ## Fetch Unity's Android player module (instead of installing Unity)
	WHAT=android ROOT="$(PLAYER_ROOT)" bash tools/ondevice-il2cpp/fetch-unity.sh

# The compile check that matters.
#
# The patches ship as SOURCE and are compiled on the device against YOUR depot,
# so this runs the same compile here, with the same split of Unity's player
# assemblies for the engine and the depot's own for the game. It catches the
# mistakes that actually happen: a renamed field, an enum value that does not
# exist, a missing assembly reference. Ten seconds here against seven minutes
# on the phone.
#
# There used to be a second target that built the patches against stock Unity
# alone, for a machine with no game. It was deleted rather than fixed: every
# patch is guarded by `#if UNITY_ANDROID`, so it compiled 2 of 31 files and
# proved nothing about the rest, and the DLL it produced went unused once the
# device started compiling the sources itself.
check: ## Compile-check the patch sources against your depot (fast, thorough)
	@pwsh -NoProfile -File tools/silksong-patches/check.ps1

# The same question, asked about somebody else's mod.
#
# A plugin DLL references BepInEx and 0Harmony by name, and here those are our
# shims. This compiles them against your depot and runs the real weaver over
# the plugin, so "will this mod work" is answered in seconds rather than by a
# twenty-minute build on the phone that fails at the end.
mod-check: ## Check a plugin against the shims (PLUGIN=a.dll[,b.dll])
	@test -n "$(PLUGIN)" || { echo "usage: make mod-check PLUGIN=path/to/Plugin.dll"; exit 2; }
	@pwsh -NoProfile -File tools/bepinex-shim/check.ps1 -Plugin $(PLUGIN)

clean: ## Remove build outputs
	rm -rf "$(BUILD_ROOT)" "$(APK_DIR)" src/SilksongLauncher.Launcher/app/build tools/bundle-surgery/bin tools/bundle-surgery/obj tools/mod-weaver/bin tools/mod-weaver/obj
