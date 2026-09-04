// AnimatorRebindFix — general fix for the bundle-loaded Animator binding bug.
//
// On the Android/IL2CPP build, Animators that ship DISABLED inside an asset
// bundle (the common "pay to open / appear" props: bellway floor gates, toll
// machines, benches, doors, ...) come up WITHOUT their generic curve bindings
// built. When a script later does `animator.enabled = true; animator.Play(...)`
// the state machine advances but NO curves apply, so the object never moves —
// it only "snaps" to its saved end-state on the next room reload. Calling
// Animator.Rebind() rebuilds those bindings and the animation plays correctly.
//
// This is fixed by rebinding every animator exactly once, but HOW depends on what
// the animator is doing at the time, because the props divide into two populations
// and only one of them was ever covered.
//
//   IDLE animators — component disabled AND on an inactive GameObject. A plain
//   Rebind() is safe here: nothing is being displayed, so rebuilding the binding
//   has no visible effect, and the prop plays correctly whenever the game gets
//   round to enabling it.
//
//   POSED animators — component disabled but the GameObject is ACTIVE, i.e. the
//   frozen pose is on screen. AnimatorControlSequence puts a great many of the
//   game's props here: it enables the animator, Plays a state at t=0, waits one
//   frame for the curves to apply, then disables the animator to freeze the
//   result. With no curve bindings that one frame writes nothing, so the prop
//   keeps its AUTHORED pose — and for the Slab's spear traps that pose is fully
//   deployed, which is issue #19. A bare Rebind() rebuilds the binding and then
//   never evaluates, so it repairs nothing and (being deduped) prevents any
//   later pass from trying. These need the same capture-rebind-restore-sample
//   treatment as the live ones.
//
//   LIVE animators — enabled AND on an active GameObject, from the moment the room
//   loads, forever. The bellway toll machine is one of these, and it is why this
//   bug kept coming back: the sweep below used to skip live animators entirely, so
//   the machine was never rebound, and an "enable edge" watcher never fires for it
//   either because there is no edge to see. Measured on device:
//
//       found Bellway Toll Machine  enabled=True active=True
//       Bellway Toll Machine  state -1->1292283929  t=1.00  everIdle=False
//
//   That t=1.00 is the game putting an already-paid machine into its final,
//   retracted pose. The Play lands, but with no curve bindings nothing moves, so
//   the machine renders in its authored pose and is still standing there after you
//   have paid for it. bellway_floor_gate shows the same signature. The bench's own
//   bell_toll_machine comes up enabled=False, which is why THAT one always worked.
//
//   A live animator cannot simply be Rebind()ed — that resets it to its entry
//   state, which on a machine you have already paid for replays the whole toll
//   sequence. So its state is captured first, restored after the rebind, and
//   sampled once (RebindPreservingState). A healthy animator is left visually
//   untouched; only a decayed binding is repaired. This is the same treatment
//   WormAnimatorFix already applies to the sand centipedes, where it is proven.
//   Animators mid-transition are skipped and picked up by a later sweep, since a
//   blend between two states cannot be captured faithfully.
//
// The sweeps also have to KEEP running rather than stop shortly after scene entry:
// props stream in later, and a bench or bellway is enabled by player interaction
// long after the room settles.
//
// This replaces a sed patch in port.sh step 5 that spliced an Animator.Rebind()
// into PlayMaker's SetAnimator action (backup commit 0814fba). That patch was
// deleted along with the whole AssetRipper-era decompile-and-patch pipeline, which
// is why the bug returned. Rebinding here covers more ground than it did:
// SetAnimator was only one of the ways a prop gets enabled, while ~160 of the
// game's own classes (Gate, BellBench, ...) enable+play their animators DIRECTLY
// in C#, bypassing PlayMaker entirely.
//
// The sweeps ALSO re-run after a hazard respawn. A hazard respawn does NOT reload
// the scene (no sceneLoaded), but it routes through the scene-entry flow
// (GameManager.OnFinishedEnteringScene) which re-activates scene objects. Props
// that gate their own one-time init on a flag (e.g. SandCentipede's `warmup`)
// skip it on re-enable and come back up UNBOUND — the "Blasted Steps sand
// centipedes freeze after you get hit and respawn" bug. Re-binding on
// OnFinishedEnteringScene restores them. We clear the per-instance dedupe first
// because, unlike a real scene load, the objects keep their instance ids across a
// respawn.
//
// Cost: one FindObjectsByType every couple of seconds, and the rebinds themselves
// are deduped per instance id, so a settled room does no work at all.

#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class AnimatorRebindFix : MonoBehaviour
{
    // Rebind each animator at most once (tracked by instance id). Scene animators
    // get fresh ids per load, so this only suppresses redundant work within a
    // single loaded set, never across genuinely-new objects.
    static readonly HashSet<int> _rebound = new HashSet<int>();

    // The GameManager whose OnFinishedEnteringScene we're currently subscribed to
    // (re-subscribed if the singleton is ever recreated, e.g. after a menu trip).
    GameManager _subscribedGm;

    // --- idle sweep state -----------------------------------------------------
    //
    // How often to re-sweep for idle animators once the scene-entry burst is over.
    // Props stream in, and a bench sits disabled until the player interacts with
    // it, so the sweep has to keep running rather than stop a few seconds in.
    const float SweepInterval = 2f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
    static void Bootstrap()
    {
        var go = new GameObject("AnimatorRebindFix");
        DontDestroyOnLoad(go);
        go.AddComponent<AnimatorRebindFix>();
    }

    void OnEnable() { SceneManager.sceneLoaded += OnSceneLoaded; }
    void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (_subscribedGm != null)
        {
            _subscribedGm.OnFinishedEnteringScene -= OnFinishedEnteringScene;
            _subscribedGm = null;
        }
    }

    // Whatever is already loaded when we come up.
    void Start()
    {
        StartCoroutine(RebindPasses());
        StartCoroutine(IdleSweep());
        StartCoroutine(KeepSceneEntrySubscription());
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        StartCoroutine(RebindPasses());
    }

    // Keep ourselves subscribed to the current GameManager's scene-entry event.
    // GameManager is a DontDestroyOnLoad singleton, but can be recreated across a
    // return-to-menu, so we re-check and re-bind to the live instance.
    IEnumerator KeepSceneEntrySubscription()
    {
        var wait = new WaitForSeconds(2f);
        while (true)
        {
            var gm = GameManager.UnsafeInstance;
            if (gm != null && gm != _subscribedGm)
            {
                if (_subscribedGm != null)
                    _subscribedGm.OnFinishedEnteringScene -= OnFinishedEnteringScene;
                gm.OnFinishedEnteringScene += OnFinishedEnteringScene;
                _subscribedGm = gm;
            }
            yield return wait;
        }
    }

    // Fires on every scene entry INCLUDING hazard respawn (which doesn't raise
    // sceneLoaded). Clear the dedupe so animators that kept their instance ids
    // across the respawn get re-bound, then run a longer burst of idle passes to
    // catch props during whatever idle window they re-init into.
    void OnFinishedEnteringScene()
    {
        _rebound.Clear();
        StartCoroutine(EntryRebindPasses());
    }

    // A burst of passes: immediately, next frame (after scene objects' Start() has
    // run, in case a prop disables its animator there), then a couple of delayed
    // ones to catch animators spawned just after the room loads. The sweep started
    // in Start() keeps going afterwards, so nothing is left uncovered once this
    // burst finishes.
    IEnumerator RebindPasses()
    {
        RebindIdleAnimators();
        yield return null;
        RebindIdleAnimators();
        yield return new WaitForSeconds(0.5f);
        RebindIdleAnimators();
        yield return new WaitForSeconds(1.5f);
        RebindIdleAnimators();
    }

    // The steady-state sweep. Runs for the lifetime of the game, because the
    // animator that matters most — a bench, a bellway toll machine, a door — sits
    // disabled and untouched until the player walks up to it, which can be minutes
    // after the room loaded. Rebinding it at some idle moment beforehand is what
    // makes the eventual enable+Play work, and doing it while idle is what keeps us
    // from ever fighting the game over the animator's state.
    IEnumerator IdleSweep()
    {
        var wait = new WaitForSeconds(SweepInterval);
        while (true)
        {
            yield return wait;
            RebindIdleAnimators();
        }
    }

    // Longer-tailed burst for scene entry / hazard respawn: props like the sand
    // centipedes only sit idle (animator disabled) between intermittent pop-ups,
    // so we keep re-scanning for several seconds to catch each one while idle and
    // rebind it before its next pop-up.
    IEnumerator EntryRebindPasses()
    {
        RebindIdleAnimators();
        yield return null;
        RebindIdleAnimators();
        float[] delays = { 0.3f, 0.6f, 1.0f, 1.5f, 2.5f, 4.0f };
        for (int i = 0; i < delays.Length; i++)
        {
            yield return new WaitForSeconds(delays[i] - (i == 0 ? 0f : delays[i - 1]));
            RebindIdleAnimators();
        }
    }

    static void RebindIdleAnimators()
    {
        var anims = Object.FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        int idle = 0, live = 0, posed = 0;
        for (int i = 0; i < anims.Length; i++)
        {
            var a = anims[i];
            if (a == null || a.runtimeAnimatorController == null) continue;

            // Debug-only A/B switch (TrapProbe, issue #19). Off unless a knob
            // file on the device asks for it, and checked BEFORE the dedupe so
            // that turning it off again lets the sweep pick the animator up.
            if (TrapProbe.SuppressAnimatorRebind) continue;

            bool isLive = a.enabled && a.gameObject.activeInHierarchy;

            // A live animator mid-transition cannot be captured faithfully (a
            // transition blends two states and only one survives a replay), so
            // leave it and let a later sweep take it once it has settled.
            if (isLive && IsTransitioning(a)) continue;

            int id = a.GetInstanceID();
            if (!_rebound.Add(id)) continue;   // once per animator

            if (isLive)
            {
                RebindPreservingState(a);
                live++;
            }
            else if (a.gameObject.activeInHierarchy)
            {
                // Component disabled, but the object is ON SCREEN. For this
                // population the frozen pose IS the display, so a bare Rebind()
                // -- which rebuilds the bindings and then never evaluates -- is
                // not enough: the prop keeps whatever pose it was authored with.
                //
                // AnimatorControlSequence is the idiom that makes this matter,
                // and it is all over the game's props:
                //
                //     animator.enabled = true;
                //     animator.Play(stateName, 0, 0f);
                //     yield return null;        // one frame to apply the curves
                //     animator.enabled = false; // and freeze there
                //
                // On a bundle animator with no curve bindings that one frame
                // writes nothing, so the prop is left in its authored pose
                // forever -- which for the Slab's spear traps is fully deployed
                // (issue #19). Measured on device before the fix:
                //
                //     anim=spikes enabled=False/True ctrl=spikes init=True
                //     state[0] t=0 len=1.611 loop=False clip='Jail_spear_trap'
                //
                // The state the game asked for is right there and correct; only
                // the pose was never written. So rebind and SAMPLE once, at the
                // state and time the animator already holds, which writes
                // exactly the pose the game intended and nothing else.
                RebindPreservingState(a);
                posed++;
            }
            else
            {
                // Nothing is displayed, and Animator.Update does not apply to an
                // inactive GameObject anyway. Rebuilding the binding is all that
                // is wanted; the prop plays correctly when it is next enabled.
                a.Rebind();
                idle++;
            }
        }
        if (idle > 0 || live > 0 || posed > 0)
            Debug.Log("[AnimatorRebindFix] rebound " + idle + " idle + " + posed
                      + " posed + " + live + " live bundle animators");
    }

    static bool IsTransitioning(Animator a)
    {
        for (int l = 0; l < a.layerCount; l++)
            if (a.IsInTransition(l)) return true;
        return false;
    }

    // Rebind() rebuilds the generic curve bindings but resets the animator to its
    // default/entry state. For an animator that is live, capture the state on every
    // layer first, Rebind, then restore that state at its captured time and sample
    // once, so the pose the game asked for is applied immediately and no frame of
    // the entry state is ever shown.
    static void RebindPreservingState(Animator a)
    {
        int layers = a.layerCount;
        var stateHash = new int[layers];
        var normTime = new float[layers];
        for (int l = 0; l < layers; l++)
        {
            AnimatorStateInfo st = a.GetCurrentAnimatorStateInfo(l);
            stateHash[l] = st.fullPathHash;
            normTime[l] = st.normalizedTime;
        }

        a.Rebind();

        for (int l = 0; l < layers; l++)
        {
            if (stateHash[l] != 0)
                a.Play(stateHash[l], l, normTime[l]);
        }
        a.Update(0f);
    }
}
#endif
