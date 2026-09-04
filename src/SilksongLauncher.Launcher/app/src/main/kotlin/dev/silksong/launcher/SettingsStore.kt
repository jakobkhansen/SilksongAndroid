// SettingsStore — thin SharedPreferences wrapper for the launcher's
// user-tweakable behaviour flags. Kept deliberately tiny: one prefs
// file, raw key/value access, no observer plumbing — Settings UI
// reads/writes directly and the consumers (LauncherActivity) re-read
// on every relevant lifecycle event.
//
// Default for both flags is OFF so the launcher behaves identically
// to its current manual-only operation until the user opts in. We
// store them under a dedicated `launcher_settings` prefs file rather
// than mixing into TokenStore's `launcher_tokens` to keep secrets
// and behaviour flags on separate disk-level lifecycles (clearing
// the token shouldn't wipe sync preferences, and vice-versa).

package dev.silksong.launcher

import android.content.Context
import android.content.SharedPreferences
import java.io.File

class SettingsStore(context: Context) {

    private val prefs: SharedPreferences =
        context.applicationContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    var autoPull: Boolean
        get() = prefs.getBoolean(KEY_AUTO_PULL, false)
        set(value) { prefs.edit().putBoolean(KEY_AUTO_PULL, value).apply() }

    var autoPush: Boolean
        get() = prefs.getBoolean(KEY_AUTO_PUSH, false)
        set(value) { prefs.edit().putBoolean(KEY_AUTO_PUSH, value).apply() }

    /**
     * When enabled, an OnGUI overlay rendered in the Unity game shows
     * live FPS / CPU / GPU / battery stats in the top-left corner.
     * Read from the launcher process here for the UI; the actual
     * overlay lives in PerfOverlay.cs (Unity side) which reads the
     * SAME SharedPreferences key via JNI at startup. We share the
     * underlying prefs file because the launcher process
     * (`:launcher`) and the game process (default) share package
     * data even though they run in separate processes.
     */
    var perfOverlay: Boolean
        get() = prefs.getBoolean(KEY_PERF_OVERLAY, false)
        set(value) { prefs.edit().putBoolean(KEY_PERF_OVERLAY, value).apply() }

    /**
     * When enabled, skip Silksong's startup intro (studio logos +
     * opening quote) and go straight to loading the main menu. Read
     * via JNI from the game process at boot from this SAME prefs file;
     * the actual skip lives in IntroSkipper.cs (Unity side) which
     * short-circuits StartManager's intro animator to the loading
     * state. The unavoidable loading/save-icon screen still shows
     * briefly while the menu finishes loading. Default OFF (vanilla
     * intro). Takes effect on next game launch.
     */
    var skipIntro: Boolean
        get() = prefs.getBoolean(KEY_SKIP_INTRO, false)
        set(value) { prefs.edit().putBoolean(KEY_SKIP_INTRO, value).apply() }

    /**
     * Dual-screen support. When enabled, the Unity side (DualScreenV2, one of
     * the patches compiled on the device) activates a secondary display and
     * draws its own inventory / crest / tasks / journal / map screens on it,
     * built from the game's own data and driven by touch. Read via JNI from the
     * game process at boot from this SAME prefs file. Defaults ON, so the
     * switch is for turning it OFF; it is inert on a device with no second
     * display.
     */
    var dualScreen: Boolean
        get() = prefs.getBoolean(KEY_DUAL_SCREEN, true)
        set(value) { prefs.edit().putBoolean(KEY_DUAL_SCREEN, value).apply() }

    /**
     * Unlock the aspect ratio.
     *
     * Silksong clamps the shape it renders into to between 1.6 : 1 and
     * 2.3916667 : 1 and letterboxes the rest, which never shows on a 16:9
     * monitor and always shows on the screens Android actually has -- a
     * foldable's inner screen is about 1.16 : 1, its cover screen about
     * 2.46 : 1, and a 4:3 handheld 1.33 : 1. When this is on, the build's
     * woven gate widens that range so the game renders at the screen's own
     * proportions and the bars go away.
     *
     * Default OFF, and deliberately so: the same clamp drives the camera, so
     * opening it also pulls the camera back and shows more of the world than
     * the art was framed for. On a foldable that is a full screen; it can also
     * look wrong. Only the player can make that trade.
     *
     * Costs a relaunch, not a rebuild -- the weave is always in the build and
     * this only decides whether the gate is opened. Inert on an ordinary
     * phone, whose aspect is already inside the game's own range.
     */
    var wideAspect: Boolean
        get() = prefs.getBoolean(KEY_WIDE_ASPECT, false)
        set(value) { prefs.edit().putBoolean(KEY_WIDE_ASPECT, value).apply() }

    /**
     * Hands the game the settings it needs, as a file it can read.
     *
     * The launcher runs in its own process, so the game cannot read these
     * preferences directly -- cross-process SharedPreferences means
     * MODE_MULTI_PROCESS, which is deprecated because it does not reliably
     * work. Written to the app's external files directory because that is
     * what Unity reports as Application.persistentDataPath on this platform,
     * so the patch side needs no package name and no path convention.
     *
     * Only what the GAME acts on. The cloud-save toggles are the launcher's
     * own business and are deliberately not here: a setting the game cannot
     * use is a setting somebody will eventually try to make it use.
     *
     * Written whole every time, immediately before launch, so it cannot drift
     * from what the user last chose.
     */
    fun exportForGame(context: Context) {
        val dir = context.getExternalFilesDir(null) ?: return
        val text = buildString {
            append(KEY_PERF_OVERLAY).append('=').append(perfOverlay).append('\n')
            append(KEY_SKIP_INTRO).append('=').append(skipIntro).append('\n')
            append(KEY_DUAL_SCREEN).append('=').append(dualScreen).append('\n')
            append(KEY_WIDE_ASPECT).append('=').append(wideAspect).append('\n')
        }
        try {
            val out = File(dir, "game-settings.txt")
            val tmp = File(dir, "game-settings.txt.part")
            tmp.writeText(text)
            if (!tmp.renameTo(out)) {
                tmp.delete()
                throw java.io.IOException("rename to $out")
            }
            LauncherLog.log("settings for the game: ${text.replace('\n', ' ').trim()}")
        } catch (t: Throwable) {
            // Not fatal: the patches fall back to defaults, which is the
            // behaviour the game had before any of this existed.
            LauncherLog.log("could not write the game's settings", t)
        }
    }

    private companion object {
        const val PREFS_NAME = "launcher_settings"
        const val KEY_AUTO_PULL = "auto_pull"
        const val KEY_AUTO_PUSH = "auto_push"
        const val KEY_PERF_OVERLAY = "perf_overlay"
        const val KEY_SKIP_INTRO = "skip_intro"
        const val KEY_DUAL_SCREEN = "dualscreen_enabled"
        const val KEY_WIDE_ASPECT = "wide_aspect"
    }
}
