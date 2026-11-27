// IntroSkipper — optionally skips Silksong's startup intro sequence
// (the studio logos + opening quote) and drops straight into loading
// the main menu, controlled by a launcher Settings toggle.
//
// Why a runtime hook (and not a source patch):
//
//   The port deliberately doesn't patch the game's own code — it only ADDS
//   MonoBehaviours (ResolutionConfigurator, PerfOverlay, AspectGate, …).
//   This stays in keeping with that: a small bootstrapped behaviour that drives
//   the game's OWN animator instead of editing StartManager.
//
// How the intro works (StartManager.Start, the startup coroutine):
//
//   StartManager kicks off the core-manager load + Menu_Title scene
//   load via a callback (DoActionAfterAllLoadsComplete sets
//   `startedLoadingMenu`), THEN — gated by a hardcoded
//   `showIntroSequence = true` — plays `startManagerAnimator`:
//     SetBool("WillShowQuote", true); SetTrigger("Start");
//     while (anim state shortNameHash != hash("LoadingIcon")) yield;
//   i.e. it just waits for the animator to reach the "LoadingIcon"
//   state (logos → quote → loading-icon) before activating the menu.
//   Crucially the actual menu LOAD is independent of this animation —
//   it's driven by the load callback, not by any animation event — so
//   short-circuiting the animator to "LoadingIcon" is safe: only the
//   wait loop is affected; the menu still loads and activates normally.
//
// What we do:
//
//   While StartManager is alive (skip enabled), force its animator
//   straight to the "LoadingIcon" state every frame. That makes
//   StartManager's wait loop exit immediately, skipping the logos +
//   quote (and their animation/audio events, since those states never
//   play). A speed bump is set as a SAFETY NET only — if Play() can't
//   land the state directly on some animator layout, the natural
//   transitions still fast-forward to "LoadingIcon" within a fraction
//   of a second rather than hanging.
//
//   Caveat (documented in the launcher Settings subtitle): the
//   loading / save-icon screen — the "LoadingIcon" state — still shows
//   briefly while the menu finishes loading. Only the logos + quote
//   cutscene is skipped, not the unavoidable load wait.
//
//   Reads the `skip_intro` flag from the shared `launcher_settings`
//   SharedPreferences via JNI, the same cross-process pattern as
//   ResolutionConfigurator / PerfOverlay (the launcher `:launcher`
//   process writes it; the game process reads it).

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine;

public class IntroSkipper : MonoBehaviour
{
    // Cross-process pref protocol. Keep in lock-step with the Kotlin
    // SettingsStore.PREFS_NAME / KEY_SKIP_INTRO constants.
    const string PREFS_NAME = "launcher_settings";
    const string KEY_SKIP_INTRO = "skip_intro";

    // The state StartManager.Start waits for. It compares
    // GetCurrentAnimatorStateInfo(0).shortNameHash against
    // Animator.StringToHash("LoadingIcon"), so the same hash both
    // satisfies that wait and (via Animator.Play) lands the state.
    static readonly int LoadingIconHash = Animator.StringToHash("LoadingIcon");

    // Fast-forward multiplier used only as a fallback if Play() can't
    // jump straight to the state — accelerates the animator's own
    // transitions to "LoadingIcon" instead of hanging. Reset to 1 once
    // we're in "LoadingIcon" so the loading spinner runs at normal speed.
    const float FastForwardSpeed = 10f;

    // Safety stop: give up polling if StartManager never appears so we
    // don't FindAnyObjectByType forever in some unexpected state. The
    // launcher extracts the game bundles and only starts the Unity
    // process once that's finished, so StartManager (loaded via
    // Addressables) shows within a few seconds of boot — 30s is a
    // comfortable margin. (Worst case if it ever does time out: the
    // intro just isn't skipped that once.)
    const float MaxWaitSeconds = 30f;

    bool _engaged;          // have we ever seen StartManager?
    float _bootTime;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!ReadSkipPref())
        {
            Debug.Log("[IntroSkip] disabled (skip_intro off)");
            return;
        }
        var go = new GameObject("__IntroSkipper__");
        DontDestroyOnLoad(go);
        go.AddComponent<IntroSkipper>();
        Debug.Log("[IntroSkip] enabled — will skip the startup intro");
    }

    void Awake()
    {
        _bootTime = Time.realtimeSinceStartup;
    }

    void Update()
    {
        var sm = Object.FindAnyObjectByType<StartManager>();
        if (sm == null)
        {
            // StartManager gone after we engaged it ⇒ intro is over and
            // the menu is loading: our job is done. If it never appeared
            // within the timeout, bail too (avoid perpetual polling).
            if (_engaged || Time.realtimeSinceStartup - _bootTime > MaxWaitSeconds)
            {
                if (!_engaged)
                    Debug.Log("[IntroSkip] StartManager never appeared — standing down");
                Destroy(gameObject);
            }
            return;
        }

        var anim = sm.startManagerAnimator;
        if (anim == null)
            return;

        if (!_engaged)
        {
            _engaged = true;
            Debug.Log("[IntroSkip] StartManager found — short-circuiting intro to LoadingIcon");
        }

        if (anim.GetCurrentAnimatorStateInfo(0).shortNameHash == LoadingIconHash)
        {
            // Reached the loading state — StartManager's wait loop is
            // satisfied; let the spinner run at its authored speed while
            // the menu finishes loading.
            if (!Mathf.Approximately(anim.speed, 1f))
                anim.speed = 1f;
            return;
        }

        // Jump straight to LoadingIcon (skips logos/quote and their
        // events). Speed bump is the safety net if Play can't land it.
        anim.Play(LoadingIconHash, 0, 0f);
        anim.speed = FastForwardSpeed;
    }

    // SharedPreferences boolean read via JNI from the Unity process —
    // same cross-process access as ResolutionConfigurator. MODE_PRIVATE = 0.
    static bool ReadSkipPref()
    {
        // Was a JNI read of the launcher's SharedPreferences. The launcher
        // now exports what the game needs to a file beside the save data,
        // which needs no UnityPlayer.currentActivity -- this player's
        // activity is not Unity's own, so that lookup is not something to
        // depend on. See Settings.cs.
        return SilksongPatches.Settings.GetBool(KEY_SKIP_INTRO, false);
    }
}
#endif
