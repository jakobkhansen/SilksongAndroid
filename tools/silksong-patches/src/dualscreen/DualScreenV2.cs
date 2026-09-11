// DualScreenV2 — the second screen's entry point and its lifetime.
//
// The port's second screen used to be a mirror: a capture camera copied the
// game's HUD into a RenderTexture, a GPU readback pulled it into managed
// memory, a worker thread flipped it into a 22 MB shared file, and Java copied
// that into a Bitmap in an Android Presentation. It showed the game's inventory
// only while the inventory was open, letterboxed, because that menu is authored
// for 16:9 and this panel is 1240x1080. All of that is gone.
//
// Unity renders to the panel itself (see DsPresentation), and what it renders
// is ours: a UI built for this screen's shape, from the game's own data. This
// file owns three things and delegates the rest:
//
//   * whether the second screen runs at all,
//   * bringing the panel up and keeping it up across pause, resume and hot-plug,
//   * keeping the two screens' input apart (DsTouch).

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections;
using UnityEngine;

public class DualScreenV2 : MonoBehaviour
{
    const string KEY_DUAL_SCREEN = "dualscreen_enabled";

    public static DualScreenV2 Instance { get; private set; }

    DsPresentation _screen;
    DsShell _shell;
    DsInput _input;
    DsTestCard _card;
    bool _paused;
    bool _bringingUp;
    int _displayCount;
    float _nextFontRetry;
    bool _fontReady;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!ShouldRun()) return;
        var go = new GameObject("__DualScreenV2__");
        DontDestroyOnLoad(go);
        go.AddComponent<DualScreenV2>();
    }

    /// <summary>
    /// The second screen runs unless the launcher's setting turns it off.
    /// </summary>
    public static bool ShouldRun()
    {
        return SilksongPatches.Settings.GetBool(KEY_DUAL_SCREEN, true);
    }

    IEnumerator Start()
    {
        Instance = this;

        // Watch for displays coming and going BEFORE trying to use one, and
        // keep watching whether or not there is one now.
        //
        // This used to be subscribed only after a successful bringup, which
        // meant a device that started with no second screen never noticed one
        // being attached: the component switched itself off and the panel
        // stayed dead until the app was restarted. On a handheld whose panel
        // detaches, that is a normal thing to do rather than an edge case.
        _displayCount = Display.displays.Length;
        Display.onDisplaysUpdated += OnDisplaysUpdated;

        yield return Bringup();
    }

    /// <summary>
    /// Try to take the second panel. Safe to run again later: with no second
    /// display it does nothing at all and leaves the game exactly as it would
    /// be on a single-screen device.
    /// </summary>
    IEnumerator Bringup()
    {
        if (_paused || _bringingUp || (_screen != null && _screen.Ready)) yield break;
        _bringingUp = true;
        try
        {
            if (_screen == null) _screen = new DsPresentation(transform);
            int oldWidth = _screen.Width, oldHeight = _screen.Height;
            yield return _screen.Bringup(() => !_paused);

            if (!_screen.Ready)
            {
                Debug.Log("[DualScreen] no usable second display; dormant");
                yield break;
            }

            bool resized = oldWidth != _screen.Width || oldHeight != _screen.Height;
            if (_shell == null && _card == null)
            {
                if (DsConfig.Bool("testcard", false))
                    _card = new DsTestCard(_screen.Root, _screen.Width, _screen.Height);
                else
                {
                    _input = new DsInput();
                    BuildShell();
                }
            }
            else if (resized)
            {
                if (_card != null)
                {
                    ClearRoot();
                    _card = new DsTestCard(_screen.Root, _screen.Width, _screen.Height);
                }
                else RebuildShell();
            }

            SetActive(!_paused);
            Debug.Log("[DualScreen] ready");
        }
        finally { _bringingUp = false; }
    }

    void BuildShell()
    {
        _shell = new DsShell(_screen.Root);
        RegisterScreens(_shell);
        _shell.Finish(DsConfig.Str("screen", "map"));
        _shell.SetVisible(_screen.Ready && !_paused);
    }

    // Native icon order; the preferred screen remains Map.
    static void RegisterScreens(DsShell shell)
    {
        shell.Register(new DsInventoryScreen(), InventoryPaneList.PaneTypes.Inv);
        shell.Register(new DsLoadoutScreen(), InventoryPaneList.PaneTypes.Tools);
        shell.Register(new DsTasksScreen(), InventoryPaneList.PaneTypes.Quests);
        shell.Register(new DsJournalScreen(), InventoryPaneList.PaneTypes.Journal);
        shell.Register(new DsMapScreen(), InventoryPaneList.PaneTypes.Map);
    }

    void Update()
    {
        if (_paused || _screen == null || !_screen.Ready) return;
        if (!DsTouch.Ready)
        {
            SetActive(false);
            StartCoroutine(Reacquire());
            return;
        }

        // The game creates cameras for cutscenes and bosses, and hard-assigns
        // culling masks in places, so our layer is swept off everything else
        // continuously rather than once. Rate-limited inside.
        _screen.SweepCameras();

        float dt = Time.unscaledDeltaTime;

        if (_card != null) { _card.Tick(); return; }
        if (_shell == null) return;

        if (_shell.LayoutChanged)
        {
            if (_input != null) { _input.Cancel(); DispatchGestures(); }
            RebuildShell();
        }

        DsProbe.MaybeRun();

        // The game's fonts are not loaded when we start, so text would build
        // blank. Retry until they appear, then rebuild the shell once with them.
        if (!_fontReady && Time.unscaledTime >= _nextFontRetry)
        {
            _nextFontRetry = Time.unscaledTime + 2f;
            DsTheme.ForgetFont();
            if (DsTheme.HasFont)
            {
                _fontReady = true;
                Debug.Log("[DualScreen] fonts found — rebuilding shell");
                RebuildShell();
            }
        }

        if (_input != null)
        {
            _input.Poll();
            DispatchGestures();
        }

        // Outside a save, the panel shows the game's title instead of the tabs.
        //
        // The grace applies only to LEAVING gameplay. DsGameData.InGame goes
        // false for a few frames during any scene load -- there is no hero
        // mid-transition -- and slamming the title card up every time the player
        // walks through a door would be worse than the thing it replaces. But
        // before the first time we have ever been in game there is nothing to
        // protect, and waiting there just means opening on an empty Inventory
        // for the length of the grace. Returning to gameplay is always
        // immediate.
        bool inGame = DsGameData.InGame;
        if (inGame) { _idleSince = -1f; _everInGame = true; }
        else if (_idleSince < 0f) _idleSince = Time.unscaledTime;

        bool settled = !_everInGame || Time.unscaledTime - _idleSince >= IDLE_GRACE;
        _shell.SetIdle(!inGame && settled);

        _shell.Tick(dt);
    }

    const float IDLE_GRACE = 0.75f;
    float _idleSince = -1f;
    bool _everInGame;

    // Rebuild once the font exists. Cheaper and far simpler than teaching every
    // widget to swap its font later, and it happens at most once per run.
    void RebuildShell()
    {
        string keep = _shell != null ? _shell.ActiveId : null;
        ClearRoot();
        _shell = new DsShell(_screen.Root);
        RegisterScreens(_shell);
        _shell.Finish(keep ?? DsConfig.Str("screen", "map"));
        _shell.SetVisible(_screen.Ready && !_paused);
    }

    void ClearRoot()
    {
        if (_shell != null) { _shell.Dispose(); _shell = null; }
        var root = _screen.Root;
        for (int i = root.childCount - 1; i >= 0; i--) Destroy(root.GetChild(i).gameObject);
    }

    void DispatchGestures()
    {
        if (_input == null || _shell == null) return;
        var gestures = _input.Gestures;
        for (int i = 0; i < gestures.Count; i++) _shell.OnGesture(gestures[i]);
    }

    // The panel is driven by touch alone, deliberately. The shoulder buttons
    // were briefly wired to change tabs, and that is wrong: L1/R1 are the
    // game's own bindings and the second screen must never take an input the
    // player is using to play. Nothing here reads the gamepad.

    // Hot-plug. On a handheld whose second panel can be detached this is not an
    // edge case, and neither the old implementation nor the first draft of the
    // plan handled it: show() was one-shot, so unplugging left the game pushing
    // frames at a dead surface and replugging brought nothing back.
    void OnDisplaysUpdated()
    {
        int now = Display.displays.Length;
        if (now == _displayCount && _screen != null && _screen.Ready) return;
        Debug.Log("[DualScreen] displays changed: " + _displayCount + " -> " + now);
        _displayCount = now;

        if (now <= DsPresentation.DISPLAY)
        {
            // The panel went away. Stop drawing and stop stealing input, so the
            // game behaves exactly as it would on a single-screen device.
            SetActive(false);
        }
        else
        {
            // A panel appeared -- either back after an unplug, or for the first
            // time on a device that started without one.
            StartCoroutine(Reacquire());
        }
    }

    IEnumerator Reacquire()
    {
        // Re-activating is the same asynchronous business as the first time, so
        // it gets the same settling period before anything is drawn.
        yield return Bringup();
    }

    void SetActive(bool on)
    {
        if (!on && _input != null)
        {
            _input.Cancel();
            DispatchGestures();
        }
        if (_shell != null) _shell.SetVisible(on);
        if (_screen != null)
        {
            if (on) _screen.SetVisible(true);
            else _screen.Suspend();
        }
    }

    // A live panel over the launcher, or over whatever the user switched to,
    // looks broken. Stop drawing while backgrounded and resume on return.
    void OnApplicationPause(bool paused)
    {
        _paused = paused;
        if (paused) SetActive(false);
        else if (Instance == this) StartCoroutine(Reacquire());
    }

    void OnApplicationQuit() { Shutdown(); }
    void OnDestroy() { Shutdown(); }

    void Shutdown()
    {
        if (Instance != this) return;
        Instance = null;
        try { Display.onDisplaysUpdated -= OnDisplaysUpdated; } catch { }
        DsTouch.Stop();
        if (_shell != null) { _shell.Dispose(); _shell = null; }
        if (_screen != null) { _screen.Destroy(); _screen = null; }
        _card = null;
    }
}
#endif
