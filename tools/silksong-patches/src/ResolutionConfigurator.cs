// ResolutionConfigurator — the frame cap, and a sensible starting resolution.
//
// ── the resolution ──────────────────────────────────────────────────────────
//
// This used to be a launcher setting: pick 720p/900p/1080p/native in Settings,
// and every boot would force it with Screen.SetResolution. That is gone, and
// the reason it could go is worth writing down, because it was not obvious.
//
// Unity persists the resolution ON ANDROID. After a Screen.SetResolution the
// player prefs carry
//
//     Screenmanager Resolution Width  = 1280
//     Screenmanager Resolution Height = 720
//     Screenmanager Fullscreen mode   = 1
//
// and the engine restores them on the next launch. So there is nothing to
// re-apply: forcing it every boot was not making it stick, it was overwriting
// whatever the player had chosen since.
//
// What is left is a default. 720p, applied exactly once, on a device that has
// never run this before -- roughly half the pixels of a 1080p panel, which is
// most of a battery saving on art that tolerates the downscale. After that the
// game owns it, including through its own resolution menu, and nothing here
// touches it again.
//
// Nothing is clamped any more either. The old code refused to set anything at
// or above the panel's short dimension, which meant the highest modes were
// unreachable by design; the panel's own modes are exactly what the game's
// menu offers, and they should work.
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
        ApplyDefaultResolution();
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
     * 720p, once, on a device that has never run this before.
     *
     * The marker is ours rather than Unity's. Unity writes its own
     * "Screenmanager Resolution Width" on first run too -- with the panel's
     * native size -- so its presence says nothing about whether anybody has
     * chosen anything. A key only this code writes is the only way to tell
     * "never been here" from "been here, and the player picked native".
     *
     * After this has run once it never runs again, and the resolution belongs
     * to the game: its own menu writes Screenmanager Resolution Width/Height,
     * Unity restores them at boot, and nothing here interferes.
     *
     * ALWAYS landscape. Android reports some panels as 1080x1920 -- portrait,
     * the orientation the hardware is mounted in -- so deriving the target's
     * orientation from the panel produces a portrait render target for a game
     * that only ever runs landscape. The long side is the width here, full
     * stop, and the same assumption is what ResolutionGuard enforces for the
     * resolutions the game's own menu offers.
     */
    const string PREF_DEFAULT_APPLIED = "SilksongAndroidDefaultRes";
    const int DEFAULT_SHORT_SIDE = 720;

    static void ApplyDefaultResolution()
    {
        try
        {
            ResolutionGuard.Install();
            ResolutionMenuOptions.Install();

            if (PlayerPrefs.GetInt(PREF_DEFAULT_APPLIED, 0) != 0)
            {
                Debug.Log($"[ResolutionConfigurator] resolution is the game's: {Screen.width}x{Screen.height}");
                return;
            }

            int longSide, shortSide;
            if (!ResolutionMenuOptions.TryWindow(out longSide, out shortSide))
            {
                // No usable geometry yet. Not marked as decided, so the next
                // launch gets another go rather than silently keeping whatever
                // Unity picked.
                Debug.LogWarning("[ResolutionConfigurator] no window geometry yet; leaving the resolution alone");
                return;
            }

            // A panel already at or below the default is left alone: there is
            // nothing to save, and scaling UP would be worse than doing nothing.
            if (shortSide > DEFAULT_SHORT_SIDE)
            {
                // Derived through the same pair of helpers the menu uses, so
                // this lands exactly on one of its rows rather than a pixel
                // beside it.
                int width = ResolutionMenuOptions.WidthFor(longSide, shortSide, DEFAULT_SHORT_SIDE);
                Screen.SetResolution(width, DEFAULT_SHORT_SIDE, true);
                Debug.Log(
                    $"[ResolutionConfigurator] first run: defaulting to {width}x{DEFAULT_SHORT_SIDE} " +
                    $"(window {longSide}x{shortSide}). Change it in the game's video options.");
            }
            else
            {
                Debug.Log(
                    $"[ResolutionConfigurator] first run: window is {longSide}x{shortSide}, " +
                    "already at or below the default; leaving it alone");
            }

            // Written whether or not the resolution was changed. The question
            // it answers is "has the default been decided", and it has been.
            PlayerPrefs.SetInt(PREF_DEFAULT_APPLIED, 1);
            PlayerPrefs.Save();
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[ResolutionConfigurator] couldn't set the resolution: " + ex.Message);
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

    float _next;
    UnityEngine.UI.MenuResolutionSetting _opt;

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
 * Keeps the render target the same shape as the window.
 *
 * Two things go wrong without this, and they have the same cause.
 *
 * The first is a render target that never matched the window to begin with.
 * Everything used to be derived from Screen.resolutions -- the DISPLAY's modes,
 * which on a foldable, a large screen that letterboxes us, or a 4:3 handheld
 * are a different shape from the window we are actually given. A 16:9 frame in
 * a 4:3 window is displayed with bars added around it, and those bars are on
 * top of the ones the game draws itself.
 *
 * The second is rotation, and it is the same mismatch arriving later. Rotating
 * to portrait and back left the game squashed and never recovered, because the
 * old guard did one thing -- transpose a portrait Screen into a landscape one
 * -- and then remembered the size it had CORRECTED. Screen.width and
 * Screen.height report that correction back, so the test that decided whether
 * to act was reading its own output: after one transpose the guard saw
 * landscape, returned early forever, and the window underneath could change
 * shape as often as it liked without anything noticing.
 *
 * So the question asked here is about the WINDOW, which is the one thing our
 * own corrections cannot change. Every window that is a different shape from
 * the frame we are rendering gets exactly one correction, and a window that
 * changes again -- rotated back, unfolded, resized -- is a different window and
 * gets its own. That is what makes it recover.
 *
 * The short side is left alone: how many pixels to render is the player's
 * choice, and only the shape is ours to fix. It is capped at the window's own,
 * because rendering more pixels than are displayed costs battery and buys
 * nothing.
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

    static ResolutionGuard _instance;
    float _next;
    int _triedLong, _triedShort;
    bool _reported;

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

        // Portrait is always wrong: the game only runs landscape, and a
        // portrait render target is what the old menu could hand it.
        bool portrait = Screen.height > Screen.width;
        float want = winLong / (float)winShort;
        float have = haveLong / (float)haveShort;
        bool wrongShape = Mathf.Abs(want - have) / want > TOLERANCE;

        if (!portrait && !wrongShape)
        {
            // Fits. Forget any attempt made for an earlier window, so that a
            // window which comes back to a shape we once failed on is tried
            // again rather than written off.
            _triedLong = 0;
            _triedShort = 0;
            return;
        }

        // Tried already, for this exact window. Either the correction did not
        // take or something is re-applying it, and repeating it every half
        // second would turn a cosmetic problem into a flickering one.
        if (winLong == _triedLong && winShort == _triedShort) return;
        _triedLong = winLong;
        _triedShort = winShort;

        int shortSide = Mathf.Min(haveShort, winShort);
        int longSide = ResolutionMenuOptions.WidthFor(winLong, winShort, shortSide);
        if (longSide == Screen.width && shortSide == Screen.height) return;

        Debug.Log($"[ResolutionGuard] {Screen.width}x{Screen.height} does not fit a "
                  + $"{winLong}x{winShort} window; using {longSide}x{shortSide}");
        Screen.SetResolution(longSide, shortSide, true);
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
