// ResolutionConfigurator — the frame cap, and the resolution the player chose.
//
// ── the resolution ──────────────────────────────────────────────────────────
//
// It is the player's, and keeping it is the whole job. Nothing here picks one:
// a device that has never run this renders at whatever the engine hands it,
// which is the window's own size. The only resolution this file ever applies is
// one the player asked for in the game's own video options.
//
// It has to be kept here, because the mechanism that looks like it does this
// already does not. Unity writes
//
//     Screenmanager Resolution Width  = 1920
//     Screenmanager Resolution Height = 1080
//     Screenmanager Fullscreen mode   = 1
//
// into the player prefs, and on a desktop the engine reads them back at
// startup. On Android that is not a promise: the player is handed the window's
// size on every launch and the pref is overwritten with it, so a resolution
// chosen in the menu lasted exactly as long as the process did and every launch
// came back at the panel's full size. An earlier version of this file believed
// the desktop behaviour, wrote a one-time 720p default and then left the
// resolution alone forever -- which is why it looked like the default had
// stopped working: it had not, nothing was keeping anything.
//
// So there is a key of our own, and what goes in it is the SHORT SIDE alone --
// 720, 900, 1080 -- rather than a pair of numbers. The short side is the
// choice: how many pixels to draw is what the player was picking. The width
// that goes with it belongs to the window and is derived from it every time, so
// a size chosen on a foldable's cover screen still means something on the inner
// one and a rotation leaves no stale width behind. It is capped at the window's
// own short side, because rendering more pixels than are displayed costs
// battery and buys nothing.
//
// A change is the PLAYER'S when it happens while the game's video options are
// on screen -- ResolutionMenuOptions is already watching that pane -- and that
// is the only thing that writes the key. Every other change is the engine's,
// and gets put back. There is no third thing that legitimately resizes a render
// target behind the player's back.
//
// ── the shape ───────────────────────────────────────────────────────────────
//
// Every size here is derived from the shape of the WINDOW, asked of Android,
// and never from Screen.resolutions. That array describes the DISPLAY, and the
// two are not the same rectangle: a foldable's inner screen, a large-screen
// device that letterboxes us, split-screen, all give a window smaller and a
// different shape from the panel behind it.
//
// Rendering at a shape the window does not have is the bug this replaces --
// black bars all the way round on a 4:3 handheld and on a Galaxy Fold, on top
// of whatever the game does. Matching the window means the engine scales our
// frame to it and nothing is added.
//
// What is NOT ours to fix is the game's own limit. ForceCameraAspect clamps the
// viewport it renders into to 1.6 : 1 at the narrow end
//
//     AutoScaleViewportShared:
//         MinMaxFloat(1.6f, 2.3916667f).GetClampedBetween(w / (float)h)
//
// and letterboxes whatever is left, so a 4:3 screen keeps ~8% bars and a Fold's
// ~1.16:1 inner screen keeps ~14%, exactly as they would on a PC monitor of the
// same shape. That is Team Cherry's framing decision, it is the same on every
// platform, and the game already ships the control that overrides it: the
// Overscan slider in its own video options grows the viewport past the screen
// edges, trading the bars for cropped sides.

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine;

public static class ResolutionConfigurator
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Apply()
    {
        ApplyFrameRate();
        PinLandscape();
        RestoreChosenResolution();
    }

    /**
     * Landscape, either way up. Never portrait.
     *
     * The manifest already says android:screenOrientation="sensorLandscape",
     * which is exactly this, and on its own it is not enough. Unity's player
     * calls setRequestedOrientation itself, from the orientation in the
     * player settings it was built with -- and those settings came out of a
     * DESKTOP build of the game, where the question was never asked and the
     * answer is whatever the default happened to be. When Unity's answer and
     * the manifest's disagree, the last call wins, and Unity's is the last
     * call.
     *
     * That is the portrait nobody asked for in the bug report: not the system
     * rotating a landscape-locked activity, which it will not do, but the
     * engine asking for it.
     *
     * So it is said again, in the engine's own terms. AutoRotation with only
     * the two landscape flags set is Unity's way of spelling sensorLandscape:
     * the device may be held either way round, 180 degrees apart, and neither
     * portrait is reachable. The flags are set BEFORE the mode, because
     * AutoRotation starts honouring them the moment it is assigned.
     */
    static void PinLandscape()
    {
        try
        {
            Screen.autorotateToPortrait = false;
            Screen.autorotateToPortraitUpsideDown = false;
            Screen.autorotateToLandscapeLeft = true;
            Screen.autorotateToLandscapeRight = true;
            Screen.orientation = ScreenOrientation.AutoRotation;
            Debug.Log("[ResolutionConfigurator] orientation pinned to landscape (either way up)");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[ResolutionConfigurator] couldn't pin the orientation: " + ex.Message);
        }
    }

    /**
     * The frame cap, held rather than set, and vsync held off with it.
     *
     * The game stores its cap in PlayerPrefs as VidTFR and offers four values:
     * -1 ("off"), 30, 60 and 120, plus the panel's own rate when that exceeds
     * 120. -1 means "uncapped" on a desktop, but Unity reads a negative target
     * as its mobile default of 30, so the option meant to remove the limit is
     * the one that imposes the worst one.
     *
     * The other half is VSync, and it is the larger one. The game's VSync
     * option does not merely enable vsync -- MenuSetting.UpdateSetting, case
     * VSync, does this when it is switched on:
     *
     *     Platform.Current.VSyncCount = 1;
     *     Application.targetFrameRate = -1;
     *     UIManager.instance.DisableFrameCapSetting();
     *
     * So turning vsync on sets the same -1 sentinel, and disables the frame cap
     * control so it cannot be set back. On Android that is a request for 30 fps
     * whatever the cap says -- and vsync is ON by default, both in
     * GameSettings (LoadInt("VidVSync", ref vSync, 1)) and in the project's own
     * QualitySettings.asset (vSyncCount: 1).
     *
     * There is no vsync-on path that reaches 120 here, because Unity ignores
     * targetFrameRate entirely while vSyncCount is non-zero and the game only
     * ever pairs vsync with -1. Holding vsync off and driving the rate from
     * targetFrameRate is the only arrangement in which the cap means anything,
     * which is why this does not try to preserve a vsync choice.
     *
     * THREE approaches failed before this one. Setting targetFrameRate once at
     * startup loses, because the game re-applies its own settings from menu
     * code. Writing VidTFR instead loses too: measured on the device, the patch
     * logged "frame cap -1 -> 120" every launch and the prefs file still read
     * -1 afterwards, because the game writes its in-memory value back after
     * ours. So both values are GUARDED instead -- see FrameCapHolder, which
     * needs no stored state and no timer to do it. The prefs are still written,
     * because it costs nothing and makes the menu agree when it is read fresh.
     *
     * A deliberate 30 or 60 is still respected -- that is a real choice about
     * battery. Only the sentinel is corrected.
     */
    const string PREF_FRAME_CAP = "VidTFR";
    const string PREF_VSYNC = "VidVSync";

    /**
     * The largest cap the game will accept, worked out the way it works it out.
     *
     * Its list is a fixed set topped at 120, plus the panel's own rate when
     * that is higher -- and it derives that rate by taking the maximum over
     * every mode in Screen.resolutions, not the mode currently in use. Those
     * two can differ: a 120 Hz panel can report a 60 Hz current mode while
     * still offering 120. Asking the same question the same way is what keeps
     * our answer inside the set it will accept, on hardware nobody here has.
     */
    static int LargestAcceptedCap()
    {
        int max = 0;
        foreach (var r in Screen.resolutions)
        {
            int hz = (int)Mathf.Round((float)r.refreshRateRatio.value);
            if (hz > max) max = hz;
        }
        if (max <= 0) max = (int)Mathf.Round((float)Screen.currentResolution.refreshRateRatio.value);
        if (max <= 0) max = 60;
        // Above 120 the panel's exact rate joins the list; at or below, the
        // list stops at 120 and the game clamps to it anyway.
        return max > 120 ? max : 120;
    }

    static void ApplyFrameRate()
    {
        try
        {
            int cap = LargestAcceptedCap();

            int stored = PlayerPrefs.GetInt(PREF_FRAME_CAP, -1);
            if (stored <= 0)
            {
                // Still written, so the options menu shows something true when
                // it reads the pref fresh. Not relied on: see the note above.
                PlayerPrefs.SetInt(PREF_FRAME_CAP, cap);
                Debug.Log($"[ResolutionConfigurator] frame cap {stored} -> {cap} (held at {cap})");
            }
            else
            {
                Debug.Log($"[ResolutionConfigurator] frame cap {stored} (kept; largest is {cap})");
            }

            // Likewise cosmetic, and for a better reason: with vsync showing as
            // on, the menu disables the frame cap control, so a player cannot
            // change the cap without first turning off a setting we are already
            // ignoring.
            if (PlayerPrefs.GetInt(PREF_VSYNC, 1) != 0) PlayerPrefs.SetInt(PREF_VSYNC, 0);
            PlayerPrefs.Save();

            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = stored > 0 ? stored : cap;

            FrameCapHolder.Install(cap);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[ResolutionConfigurator] couldn't set the frame rate: " + ex.Message);
        }
    }

    /**
     * The resolution the player last chose, put back.
     *
     * Nothing is chosen here. Without a stored choice this does nothing at all
     * and the game renders at the size the engine handed us, which is the
     * window's -- a fresh install looks exactly like the game with no patches
     * in it, and the first resolution anyone sees is one they picked.
     *
     * The stored short side is capped at the window's, and the width is derived
     * from the window rather than stored beside it. Both are for the same
     * reason: the window is not the same rectangle on every launch -- a
     * foldable opens, a phone is put in split screen, a handheld is docked --
     * and a pixel count means something in all of those while a pair of numbers
     * does not.
     *
     * ALWAYS landscape. Android reports some panels as 1080x1920 -- portrait,
     * the orientation the hardware is mounted in -- so deriving the target's
     * orientation from the panel produces a portrait render target for a game
     * that only ever runs landscape. The long side is the width here, full
     * stop, and the same assumption is what ResolutionGuard enforces for the
     * resolutions the game's own menu offers.
     */
    static void RestoreChosenResolution()
    {
        try
        {
            ResolutionGuard.Install();
            ResolutionMenuOptions.Install();

            int chosen = ResolutionChoice.ShortSide;
            if (chosen <= 0)
            {
                Debug.Log($"[ResolutionConfigurator] no resolution chosen yet; "
                          + $"rendering at the window's own {Screen.width}x{Screen.height}");
                return;
            }

            int winLong, winShort;
            if (!ResolutionMenuOptions.TryWindow(out winLong, out winShort))
            {
                // No usable geometry this early. ResolutionGuard is installed
                // and polls, so the choice is applied a moment later instead of
                // being lost.
                Debug.LogWarning("[ResolutionConfigurator] no window geometry yet; "
                                 + $"the guard will restore {chosen}p");
                return;
            }

            int shortSide = Mathf.Min(chosen, winShort);
            int longSide = ResolutionMenuOptions.WidthFor(winLong, winShort, shortSide);
            if (longSide == Screen.width && shortSide == Screen.height)
            {
                Debug.Log($"[ResolutionConfigurator] already at the chosen {longSide}x{shortSide}");
                return;
            }

            Screen.SetResolution(longSide, shortSide, true);
            Debug.Log($"[ResolutionConfigurator] restoring the chosen {longSide}x{shortSide} "
                      + $"(was {Screen.width}x{Screen.height}, window {winLong}x{winShort})");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[ResolutionConfigurator] couldn't set the resolution: " + ex.Message);
        }
    }
}

/**
 * The one number this port remembers about the resolution: the short side the
 * player chose, in pixels.
 *
 * It is a key of our own rather than Unity's "Screenmanager Resolution Height"
 * because Unity's is not ours to trust on Android -- the engine overwrites it
 * with the window's size on launch, which is what made a chosen resolution look
 * like it was being ignored. This one is written by exactly one caller,
 * ResolutionGuard, and only for a change made while the player was in the
 * game's video options.
 *
 * Zero, the default, means "never chosen". That is a state with a meaning:
 * nothing is applied at boot and the engine's own size stands. It is not the
 * same as a player who chose the panel's full size, which is stored like any
 * other choice and put back like any other choice.
 *
 * Cached, because it is read on every poll and PlayerPrefs on Android is a
 * JNI call into SharedPreferences.
 */
static class ResolutionChoice
{
    const string Pref = "SilksongAndroidResShort";
    // The marker the one-time 720p default used to be gated on. Removed rather
    // than left lying about, so that a prefs file dumped off a device does not
    // suggest a mechanism that no longer exists.
    const string LegacyDefaultApplied = "SilksongAndroidDefaultRes";
    // Below this is not a resolution anybody picked, it is a transient: the
    // game's own display-switch hack drops to 800x600 for a single frame.
    const int Floor = 240;

    static int _shortSide = -1;

    /// <summary>The chosen short side, or 0 if nobody has chosen one.</summary>
    public static int ShortSide
    {
        get
        {
            if (_shortSide < 0)
            {
                try
                {
                    _shortSide = PlayerPrefs.GetInt(Pref, 0);
                    if (PlayerPrefs.HasKey(LegacyDefaultApplied))
                    {
                        PlayerPrefs.DeleteKey(LegacyDefaultApplied);
                        PlayerPrefs.Save();
                    }
                }
                catch (System.Exception e)
                {
                    _shortSide = 0;
                    Debug.LogWarning("[ResolutionChoice] couldn't read the stored resolution: " + e.Message);
                }
            }
            return _shortSide;
        }
    }

    /// <summary>Records a short side the player asked for. Ignores nonsense.</summary>
    public static void Remember(int shortSide)
    {
        if (shortSide < Floor) return;
        if (shortSide == ShortSide) return;

        _shortSide = shortSide;
        try
        {
            PlayerPrefs.SetInt(Pref, shortSide);
            PlayerPrefs.Save();
            Debug.Log($"[ResolutionChoice] remembering {shortSide}p as the chosen resolution");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[ResolutionChoice] couldn't store the resolution: " + e.Message);
        }
    }
}

/**
 * The size of the window the game is drawing into, in pixels, asked of Android.
 *
 * Unity cannot answer this. Screen.resolutions and Screen.currentResolution
 * describe the DISPLAY, which on Android is a different rectangle from the
 * window whenever the window does not cover it -- a large-screen device that
 * letterboxes a non-resizeable activity, split-screen, a foldable whose two
 * screens have genuinely different shapes. And Screen.width/height stop being
 * an answer the moment anything calls Screen.SetResolution, because from then
 * on they report the render target we asked for rather than the window we were
 * given. Unity's own Display.main.systemWidth has the same problem on Android.
 *
 * So the window is asked for directly. getCurrentWindowMetrics is the API whose
 * documented contract is exactly the question -- "the size of the area the
 * window would occupy with MATCH_PARENT width and height", which is the
 * letterboxed rectangle when we are being letterboxed, not the panel behind it.
 * It is API 30, so two fallbacks sit behind it: the decor view, which is the
 * window's own root and therefore the same rectangle once it has been laid out,
 * and finally the display, which is only right when the window covers it and is
 * still better than nothing.
 *
 * Every failure is soft. A device where none of this works falls back to what
 * the code did before, which is Unity's own view of the screen.
 */
static class AndroidWindow
{
    static bool _dead;
    static int _sdk = -1;

    /// <summary>The window's pixel size. False if Android will not say.</summary>
    public static bool TrySize(out int width, out int height)
    {
        width = 0;
        height = 0;
        if (_dead) return false;

        try
        {
            using (var activity = Activity())
            {
                // Not dead: this is the ordinary state before the activity
                // exists, and it exists a moment later.
                if (activity == null) return false;

                if (Sdk() >= 30 && FromMetrics(activity, out width, out height)) return true;
                if (FromDecor(activity, out width, out height)) return true;
                if (FromDisplay(activity, out width, out height)) return true;
            }
        }
        catch (System.Exception e)
        {
            // Once, and then never again: a JNI surface that is not there is
            // not going to appear, and this is called twice a second.
            _dead = true;
            Debug.LogWarning("[AndroidWindow] cannot measure the window; "
                             + "using Unity's view of the screen instead: " + e.Message);
        }

        width = 0;
        height = 0;
        return false;
    }

    static AndroidJavaObject Activity()
    {
        using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            return player.GetStatic<AndroidJavaObject>("currentActivity");
    }

    static int Sdk()
    {
        if (_sdk >= 0) return _sdk;
        try
        {
            using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                _sdk = version.GetStatic<int>("SDK_INT");
        }
        catch (System.Exception)
        {
            _sdk = 0;
        }
        return _sdk;
    }

    static bool FromMetrics(AndroidJavaObject activity, out int width, out int height)
    {
        width = 0;
        height = 0;
        using (var wm = activity.Call<AndroidJavaObject>("getWindowManager"))
        {
            if (wm == null) return false;
            using (var metrics = wm.Call<AndroidJavaObject>("getCurrentWindowMetrics"))
            {
                if (metrics == null) return false;
                using (var bounds = metrics.Call<AndroidJavaObject>("getBounds"))
                {
                    if (bounds == null) return false;
                    width = bounds.Call<int>("width");
                    height = bounds.Call<int>("height");
                }
            }
        }
        return width > 0 && height > 0;
    }

    // Zero until the window has been laid out once, which is why this is a
    // fallback and not the answer.
    static bool FromDecor(AndroidJavaObject activity, out int width, out int height)
    {
        width = 0;
        height = 0;
        using (var window = activity.Call<AndroidJavaObject>("getWindow"))
        {
            if (window == null) return false;
            using (var decor = window.Call<AndroidJavaObject>("getDecorView"))
            {
                if (decor == null) return false;
                width = decor.Call<int>("getWidth");
                height = decor.Call<int>("getHeight");
            }
        }
        return width > 0 && height > 0;
    }

    // The panel, not the window. Right only when the window covers it -- which
    // is the common case, and the one this whole file used to assume.
    static bool FromDisplay(AndroidJavaObject activity, out int width, out int height)
    {
        width = 0;
        height = 0;
        using (var wm = activity.Call<AndroidJavaObject>("getWindowManager"))
        {
            if (wm == null) return false;
            using (var display = wm.Call<AndroidJavaObject>("getDefaultDisplay"))
            {
                if (display == null) return false;
                using (var point = new AndroidJavaObject("android.graphics.Point"))
                {
                    display.Call("getRealSize", point);
                    width = point.Get<int>("x");
                    height = point.Get<int>("y");
                }
            }
        }
        return width > 0 && height > 0;
    }
}

/**
 * Puts the resolutions the player actually wants into the game's own menu.
 *
 * Silksong builds its resolution list from Screen.resolutions
 * (MenuResolutionSetting.RefreshAvailableResolutions). On Android that array
 * describes the panel as it is mounted rather than as it is held -- one entry,
 * 1080x1920, portrait -- so the menu offers a single unusable option. The one
 * thing that saves it is a fallback in RefreshCurrentIndex: a current
 * resolution missing from the list gets prepended. That is why the menu showed
 * exactly two entries, whichever resolution happened to be running plus the
 * portrait one, and why choosing 1080p made 720p disappear.
 *
 * So the list is replaced with the sizes this WINDOW can sensibly render: the
 * window's own size, then a ladder of smaller ones, every entry holding the
 * window's exact shape. Building them from the window rather than from
 * Screen.resolutions is what keeps a 4:3 handheld and a foldable from being
 * offered 16:9 rows that can only be displayed with bars around them.
 * Everything downstream is the game's own code and needs no help --
 * PushUpdateOptionList formats the labels, ApplySettings indexes the same array
 * we wrote, and Unity persists the result.
 *
 * The array is private, so it is set by reflection. The alternative was to
 * rewrite the menu, and this is the smaller lie: one field, restored to the
 * shape the game already expects, with every method that reads it left alone.
 * It is re-applied whenever the pane is opened, because RefreshControls runs on
 * enable and overwrites it -- and it fails silently, leaving the game's own
 * behaviour, if the field ever stops being there.
 *
 * Entries carry the CURRENT refresh rate, so the running resolution matches one
 * of them exactly (Resolution.Equals compares the rate too). Without that,
 * RefreshCurrentIndex would decide the current mode is missing and prepend a
 * near-duplicate of a row already on screen.
 */
public class ResolutionMenuOptions : MonoBehaviour
{
    const float CHECK_SECONDS = 0.25f;
    // Scaled-down short sides, offered when the window is taller than each. Not
    // a fixed set of sizes: the widths are derived from the window's own shape,
    // so a 21:9 cover screen and a 6:5 inner one each get their own.
    static readonly int[] ShortSides = { 1440, 1200, 1080, 900, 810, 720, 600, 540 };
    // A safety net rather than a limit: the ladder above is eight rows and the
    // window and the running size make ten.
    const int MaxEntries = 12;

    static ResolutionMenuOptions _instance;
    static System.Reflection.FieldInfo _field;
    static bool _lookedUp;
    static bool _warned;
    static float _lastOpen = -9999f;

    float _next;
    UnityEngine.UI.MenuResolutionSetting _opt;

    /// <summary>
    /// Is the resolution changing because the player is changing it?
    ///
    /// The game's resolution control lives in one pane, and this class is
    /// already watching that pane four times a second, so the question costs
    /// nothing to answer. It is the whole basis for telling a choice from an
    /// accident: a resolution that changes while these options are on screen is
    /// the player's, and one that changes at any other time is the engine's.
    ///
    /// The grace exists because applying the choice is not the end of it. The
    /// game puts up its own "keep this resolution?" prompt with a timer, and
    /// the size can still change -- to the chosen one, or back again if the
    /// prompt times out -- after the pane itself has gone.
    /// </summary>
    public static bool PlayerIsChoosing(float graceSeconds)
    {
        return Time.unscaledTime - _lastOpen <= graceSeconds;
    }

    public static void Install()
    {
        if (_instance != null) return;
        var go = new GameObject("__ResolutionMenuOptions__");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<ResolutionMenuOptions>();
    }

    void Update()
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + CHECK_SECONDS;

        try
        {
            // Deliberately NOT UIManager.instance. That property logs an error
            // of its own -- "Couldn't find a UIManager, make sure one exists in
            // the scene" -- every time it is read before the UI exists, which
            // is most of a session and, at four times a second, several hundred
            // lines of somebody else's error in the log we ask users to send.
            // The setting is found directly instead, and cached until the scene
            // that owns it goes away.
            if (_opt == null)
            {
                var found = Resources.FindObjectsOfTypeAll<UnityEngine.UI.MenuResolutionSetting>();
                for (int i = 0; i < found.Length; i++)
                {
                    if (found[i] == null || !found[i].gameObject.scene.IsValid()) continue;
                    _opt = found[i];
                    break;
                }
            }
            var opt = _opt;
            if (opt == null || !opt.isActiveAndEnabled) return;
            _lastOpen = Time.unscaledTime;

            var field = Field();
            if (field == null) return;

            var wanted = BuildList(opt.currentRes);
            if (wanted == null || wanted.Length == 0) return;

            // Asked of the array itself rather than of the labels beside it.
            // Comparing list LENGTHS would answer wrongly on a panel small
            // enough that our list is one entry, which is exactly the size the
            // game's own list is -- and then this would never run at all.
            if (Same(field.GetValue(opt) as Resolution[], wanted)) return;

            field.SetValue(opt, wanted);
            opt.RefreshCurrentIndex();
            opt.PushUpdateOptionList();
            // Public, and the only route to the protected UpdateText that makes
            // the new label actually appear.
            opt.SetOptionTo(opt.selectedOptionIndex);
            Debug.Log("[ResolutionMenuOptions] offering " + wanted.Length + " resolutions");
        }
        catch (System.Exception e)
        {
            // Once. A failure here leaves the game's own menu working, so it is
            // not worth a line every quarter second.
            if (_warned) return;
            _warned = true;
            Debug.LogWarning("[ResolutionMenuOptions] leaving the game's own list alone: " + e.Message);
        }
    }

    static bool Same(Resolution[] a, Resolution[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].width != b[i].width || a[i].height != b[i].height) return false;
        return true;
    }

    static System.Reflection.FieldInfo Field()
    {
        if (_lookedUp) return _field;
        _lookedUp = true;
        _field = typeof(UnityEngine.UI.MenuResolutionSetting).GetField(
            "availableResolutions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (_field == null)
            Debug.LogWarning("[ResolutionMenuOptions] no availableResolutions field; menu left as the game built it");
        return _field;
    }

    /// <summary>
    /// The window's size, as landscape. False if nothing will say.
    ///
    /// Shared with the first-run default in ResolutionConfigurator and with
    /// ResolutionGuard, so that the resolution chosen at boot, the rows in the
    /// menu and the shape the guard enforces are all derived the same way. When
    /// these drifted apart -- the default rounding one way and the menu the
    /// other -- a screen whose aspect is not tidy got a boot resolution one
    /// pixel off every row in its own menu, and the game prepended a
    /// near-duplicate to say so.
    ///
    /// ALWAYS landscape, whatever Android says. The game runs landscape only,
    /// some panels report themselves the way they are mounted rather than the
    /// way they are held, and a window that is genuinely portrait is either a
    /// rotation in progress or a split-screen a landscape-locked game cannot
    /// use. Taking max and min of the pair is right in all three cases.
    /// </summary>
    public static bool TryWindow(out int longSide, out int shortSide)
    {
        int ww, wh;
        if (AndroidWindow.TrySize(out ww, out wh))
        {
            longSide = Mathf.Max(ww, wh);
            shortSide = Mathf.Min(ww, wh);
            return longSide > 0 && shortSide > 0;
        }

        // Android would not say. The display is the next best thing, and is
        // the same rectangle whenever the window covers it.
        longSide = 0;
        shortSide = 0;

        var modes = Screen.resolutions;
        for (int i = 0; i < modes.Length; i++)
        {
            int lo = Mathf.Max(modes[i].width, modes[i].height);
            int sh = Mathf.Min(modes[i].width, modes[i].height);
            if (lo > longSide) { longSide = lo; shortSide = sh; }
        }

        var c = Screen.currentResolution;
        int clo = Mathf.Max(c.width, c.height), csh = Mathf.Min(c.width, c.height);
        if (clo > longSide) { longSide = clo; shortSide = csh; }

        return longSide > 0 && shortSide > 0;
    }

    /// <summary>
    /// The width that pairs with <paramref name="targetShort"/> in this window.
    ///
    /// Even, because the arithmetic lands on an odd number whenever the aspect
    /// is not tidy and an odd render target is legal but awkward.
    /// </summary>
    public static int WidthFor(int longSide, int shortSide, int targetShort)
    {
        int w = Mathf.RoundToInt((float)longSide * targetShort / Mathf.Max(shortSide, 1));
        if ((w & 1) != 0) w++;
        return w;
    }

    /// <summary>
    /// The window's own size, and a ladder of smaller ones with its exact shape.
    ///
    /// Deliberately NOT built from Screen.resolutions any more. Those are the
    /// display's modes, and every one of them that does not share the window's
    /// shape is a row that can only be shown with bars around it -- which was
    /// the whole complaint on a 4:3 handheld and on a foldable. Nothing is lost
    /// by dropping them: a display mode the window does not have is not a mode
    /// the window can display.
    ///
    /// The ladder is deliberately wide. This runs on everything from a 720p
    /// handheld to a 1440p foldable, the whole point of a lower resolution is
    /// battery, and only the player knows what they are trading.
    ///
    /// Folding changes the window, and this is recomputed while the pane is
    /// open, so the list follows.
    /// </summary>
    static Resolution[] BuildList(Resolution current)
    {
        int winLong, winShort;
        if (!TryWindow(out winLong, out winShort)) return null;

        var sizes = new System.Collections.Generic.List<Resolution>();

        // The window itself, first: the one entry that needs no scaling at all.
        AddSize(sizes, winLong, winShort, current);

        // Then whatever is running, which need not be any of these -- a size
        // chosen on the other screen of a foldable, or one stored by an older
        // build. It is the entry the menu cannot do without: RefreshCurrentIndex
        // prepends a duplicate of it otherwise.
        AddSize(sizes, Screen.width, Screen.height, current);

        for (int i = 0; i < ShortSides.Length; i++)
        {
            int target = ShortSides[i];
            if (target >= winShort) continue;
            int w = WidthFor(winLong, winShort, target);
            // A window whose short side is 904 would otherwise be offered
            // "2306x900" next to its own "2316x904": two names for the same
            // picture, one of them wrong. Anything within a few percent of a
            // size already in the list is not a choice, it is noise.
            if (TooClose(sizes, w, target)) continue;
            AddSize(sizes, w, target, current);
        }

        sizes.Sort(ByArea);
        if (sizes.Count > MaxEntries)
            sizes.RemoveRange(MaxEntries, sizes.Count - MaxEntries);
        return sizes.ToArray();
    }

    /// <summary>Is this size close enough to one already listed to be indistinguishable?</summary>
    static bool TooClose(System.Collections.Generic.List<Resolution> list, int width, int height)
    {
        const float Tolerance = 0.03f;
        for (int i = 0; i < list.Count; i++)
        {
            float dw = Mathf.Abs(list[i].width - width) / (float)Mathf.Max(list[i].width, 1);
            float dh = Mathf.Abs(list[i].height - height) / (float)Mathf.Max(list[i].height, 1);
            if (dw < Tolerance && dh < Tolerance) return true;
        }
        return false;
    }

    static int ByArea(Resolution a, Resolution b)
    {
        return (b.width * b.height).CompareTo(a.width * a.height);
    }

    /// <summary>Adds one size as landscape, if that size is not already there.</summary>
    static void AddSize(System.Collections.Generic.List<Resolution> into,
                        int width, int height, Resolution current)
    {
        if (width <= 0 || height <= 0) return;

        // Landscape, always: the game only runs landscape, and Android reports
        // some panels the way they are mounted rather than the way they are
        // held. See ResolutionGuard.
        int w = Mathf.Max(width, height), h = Mathf.Min(width, height);
        for (int i = 0; i < into.Count; i++)
            if (into[i].width == w && into[i].height == h) return;

        into.Add(Make(w, h, current));
    }

    static Resolution Make(int width, int height, Resolution current)
    {
        return new Resolution
        {
            width = width,
            height = height,
            refreshRateRatio = current.refreshRateRatio,
        };
    }
}

/**
 * Keeps the render target the player's: their pixel count, the window's shape.
 *
 * Three things go wrong without this, and they have the same cause -- a render
 * target nobody is keeping.
 *
 * The first is a resolution that does not survive the process. Unity's own
 * persistence is a desktop promise; on Android the engine hands the player the
 * window's size on every launch and overwrites the stored one with it. So the
 * choice is stored here instead, in ResolutionChoice, and put back here: every
 * size that is not the chosen one is corrected, whether it arrived at boot,
 * after a resume, or from whatever else in the engine decided to resize the
 * surface.
 *
 * The second is a render target that never matched the window to begin with.
 * Everything used to be derived from Screen.resolutions -- the DISPLAY's modes,
 * which on a foldable, a large screen that letterboxes us, or a 4:3 handheld
 * are a different shape from the window we are actually given. A 16:9 frame in
 * a 4:3 window is displayed with bars added around it, and those bars are on
 * top of the ones the game draws itself.
 *
 * The third is rotation, and it is the same mismatch arriving later. Rotating
 * to portrait and back left the game squashed and never recovered, because the
 * old guard did one thing -- transpose a portrait Screen into a landscape one
 * -- and then remembered the size it had CORRECTED. Screen.width and
 * Screen.height report that correction back, so the test that decided whether
 * to act was reading its own output: after one transpose the guard saw
 * landscape, returned early forever, and the window underneath could change
 * shape as often as it liked without anything noticing.
 *
 * So both questions asked here are about things our own corrections cannot
 * change: the WINDOW, asked of Android, and the choice, stored in a pref. What
 * is on screen is only ever compared against those two, never against itself.
 *
 * And the choice is only ever written in one place -- here, from a size that
 * changed while the player was in the game's video options. That is what makes
 * "put it back" safe: everything else that moves the resolution is something
 * the player did not ask for.
 */
public class ResolutionGuard : MonoBehaviour
{
    const float CHECK_SECONDS = 0.5f;
    // Two frames whose aspects are this close are the same picture; correcting
    // between them would be a rounding error with a Screen.SetResolution
    // attached to it.
    const float TOLERANCE = 0.02f;
    // The narrowest shape the GAME will render into, from
    // ForceCameraAspect.AutoScaleViewportShared:
    //     MinMaxFloat(1.6f, 2.3916667f).GetClampedBetween(w / (float)h)
    // Anything narrower is letterboxed by the game itself, on every platform.
    // Not ours to change; worth reporting, so that bars which are Team Cherry's
    // are not mistaken for bars which are ours.
    const float GAME_FLOOR_ASPECT = 1.6f;
    // How long after the video options were last on screen a change is still
    // the player's doing. It covers the game's own "keep this resolution?"
    // prompt, which HIDES the video pane while it counts down and can change
    // the size twice -- once on apply, once more if it rolls back.
    const float MENU_GRACE_SECONDS = 30f;
    // Corrections for one window and one target before giving up on it. If the
    // engine is going to overwrite us whatever we do, a correction every half
    // second is a flicker rather than a fix. Reset the moment one takes.
    const int MAX_ATTEMPTS = 3;
    const float RETRY_SECONDS = 2f;

    static ResolutionGuard _instance;
    float _next;
    bool _reported;
    // What the attempts below are about: a window, and the short side wanted in
    // it. Any change to either is a different problem and gets its own budget.
    int _forLong, _forShort, _forTarget;
    int _attempts;
    float _lastAttempt;
    bool _gaveUp;
    // The short side seen on the previous poll, for as long as the player is
    // the one changing it. Two polls agreeing is what makes a size a choice.
    int _settledShort;

    public static void Install()
    {
        if (_instance != null) return;
        var go = new GameObject("__ResolutionGuard__");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<ResolutionGuard>();
    }

    void Update()
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + CHECK_SECONDS;

        int winLong, winShort;
        if (!ResolutionMenuOptions.TryWindow(out winLong, out winShort)) return;

        if (!_reported)
        {
            _reported = true;
            Report(winLong, winShort);
        }

        int haveLong = Mathf.Max(Screen.width, Screen.height);
        int haveShort = Mathf.Max(Mathf.Min(Screen.width, Screen.height), 1);

        // Portrait is never a choice and never right: the game only runs
        // landscape, and a portrait render target is what a rotation in
        // progress and the game's own unpatched menu can both hand it.
        bool portrait = Screen.height > Screen.width;

        // The player's, while the player is the one changing it. Recorded only
        // once the size has held for two polls: the game's display option drops
        // to 800x600 for a single frame on its way somewhere else, and a
        // transient is not a choice.
        bool theirs = !portrait && ResolutionMenuOptions.PlayerIsChoosing(MENU_GRACE_SECONDS);
        if (theirs)
        {
            if (haveShort == _settledShort) ResolutionChoice.Remember(haveShort);
            _settledShort = haveShort;
        }
        else
        {
            _settledShort = 0;
        }

        // What is on screen is the target while the player is working the menu,
        // which leaves only the shape to correct: a stored choice is the LAST
        // thing they asked for, and putting it back over the one they are
        // asking for now would make the menu unusable. Outside the menu the
        // stored choice is the target, and with nothing stored the pixel count
        // on screen is the engine's and stands -- a device that has never been
        // told what to render at renders at its own size.
        int chosen = ResolutionChoice.ShortSide;
        int wanted = (theirs || chosen <= 0) ? haveShort : chosen;
        int target = Mathf.Min(wanted, winShort);
        int longSide = ResolutionMenuOptions.WidthFor(winLong, winShort, target);

        float want = winLong / (float)winShort;
        float have = haveLong / (float)haveShort;
        bool wrongShape = Mathf.Abs(want - have) / want > TOLERANCE;

        if (!portrait && !wrongShape && haveShort == target)
        {
            // Fits, and is what was asked for. Forget the attempts it took to
            // get here, so that a size the engine takes away later -- on a
            // resume, a fold, a rotation -- is put back with a full budget
            // rather than with what an earlier window had left.
            _attempts = 0;
            _gaveUp = false;
            return;
        }

        if (winLong != _forLong || winShort != _forShort || target != _forTarget)
        {
            _forLong = winLong;
            _forShort = winShort;
            _forTarget = target;
            _attempts = 0;
            _gaveUp = false;
        }

        if (_attempts >= MAX_ATTEMPTS)
        {
            if (_gaveUp) return;
            _gaveUp = true;
            Debug.LogWarning($"[ResolutionGuard] {longSide}x{target} would not stick after "
                             + $"{MAX_ATTEMPTS} tries; leaving {Screen.width}x{Screen.height} "
                             + "until the window or the choice changes");
            return;
        }
        if (Time.unscaledTime - _lastAttempt < RETRY_SECONDS) return;

        _attempts++;
        _lastAttempt = Time.unscaledTime;

        Debug.Log($"[ResolutionGuard] {Screen.width}x{Screen.height} is not "
                  + (theirs || chosen <= 0 ? "the right shape" : $"the chosen {chosen}p")
                  + $" for a {winLong}x{winShort} window; using {longSide}x{target}");
        Screen.SetResolution(longSide, target, true);
    }

    /**
     * The whole geometry, once, into the log the launcher already captures.
     *
     * This exists because the devices that get this wrong are the ones nobody
     * here owns -- a Galaxy Fold, a 4:3 handheld -- and "there are black bars"
     * has three different causes that look identical on a photograph:
     *
     *   window smaller than the display  the SYSTEM is letterboxing us, which
     *                                    is resizeableActivity in the manifest
     *   frame a different shape from the window  WE are, which is this file
     *   window narrower than 1.6:1       the GAME is, which is by design and
     *                                    is what its Overscan slider is for
     *
     * One line separates them, so a report can be answered from a log instead
     * of from the hardware.
     */
    void Report(int winLong, int winShort)
    {
        float aspect = winLong / (float)winShort;
        var display = Screen.currentResolution;
        string bars = aspect < GAME_FLOOR_ASPECT
            ? $"{(1f - aspect / GAME_FLOOR_ASPECT) * 50f:0.0}% top and bottom, by the game's own "
              + $"{GAME_FLOOR_ASPECT:0.00}:1 floor"
            : "none";

        Debug.Log($"[ResolutionGuard] window {winLong}x{winShort} ({aspect:0.000}:1), "
                  + $"rendering {Screen.width}x{Screen.height}, "
                  + $"display {display.width}x{display.height}, "
                  + $"{Screen.resolutions.Length} display mode(s); the game will letterbox: {bars}");
    }
}

/**
 * Keeps vsync off and the frame cap off the broken sentinel.
 *
 * Neither value can be set once and left. The game re-applies both from menu
 * code (MenuSetting.UpdateSetting), from Platform.SetTargetFrameRate, and from
 * Platform.RestoreFrameRate after a video; QualitySettings.SetQualityLevel also
 * resets vSyncCount to the project default, which is 1. None of those is
 * reachable from here -- the patches are an additional assembly compiled
 * against the game, not a rewrite of it -- so there is nothing to subscribe to
 * and the values have to be guarded rather than assigned.
 *
 * The guard is two engine property reads per frame and nothing else. There is
 * deliberately no timer and no PlayerPrefs lookup: an earlier version polled
 * the pref every three seconds, which was both more expensive per check and
 * wrong, because it read the stored cap to decide whether to act and our own
 * startup code had just written a positive value into it -- so it returned
 * early forever and never corrected anything.
 *
 * The test that works needs no stored state at all. The game applies the
 * player's chosen cap by assigning Application.targetFrameRate directly, so a
 * positive value already IS their choice, whatever it is, and is left alone. A
 * value at or below zero can only be the "off" sentinel, which Unity reads on
 * Android as 30. Replacing just that is the whole job, and it happens within a
 * frame rather than within three seconds.
 */
public class FrameCapHolder : MonoBehaviour
{
    static FrameCapHolder _instance;
    int _cap;

    public static void Install(int cap)
    {
        if (_instance != null) { _instance._cap = cap; return; }

        var go = new GameObject("__FrameCapHolder__");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<FrameCapHolder>();
        _instance._cap = cap;
    }

    void Update()
    {
        // Off, always. The game's vsync option is not a vsync option: switching
        // it on also sets the target to -1 and disables the frame cap control,
        // so on Android it means 30 fps and no way back to the menu that would
        // change it.
        if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;

        if (Application.targetFrameRate <= 0) Application.targetFrameRate = _cap;
    }
}
#endif
