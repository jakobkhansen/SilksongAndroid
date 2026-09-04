// TrapProbe — on-device diagnosis for "traps in The Slab start deployed and
// never revert" (issue #19).
//
// The traps are BasicSpikes: a coroutine that waits for something to be inside
// activateTrigger, sets an Animator BOOL to true, holds it while anything is
// still inside, then sets it false again. So "always out" has exactly three
// possible causes, and they need completely different fixes:
//
//   A  the animator PARAMETER is wrong — the bool reads true while the
//      coroutine believes it is false. That is what our own AnimatorRebindFix
//      would do to a prop like this: Animator.Rebind() resets every parameter
//      to its authored default, and RebindPreservingState only restores the
//      STATE afterwards. The game only writes activeBool on an edge, so a
//      clobbered parameter is never corrected and the spikes sit out forever.
//
//   B  the TRIGGER is wrong — activateTrigger.InsideCount never falls back to
//      zero, so the hold loop never ends. TrackTriggerObjects seeds itself from
//      a Physics2D overlap when the hero comes into position, into a SHARED
//      static buffer of ten colliders, so a stale or wrongly-seeded entry keeps
//      the trap held down and the coroutine is behaving perfectly.
//
//   C  the BINDING is wrong — the classic bundle-loaded animator with no
//      generic curve bindings (see AnimatorRebindFix's header). The state
//      machine advances, no curves apply, and the prop renders in its authored
//      pose. If that pose is "deployed", it looks exactly like A and B.
//
// Guessing between them costs a ~7 minute build each time, so this does not
// guess. It dumps the three signals side by side — the parameter and its
// default, the trigger's occupants by name, and the state/clip actually
// playing — and then lets each candidate fix be applied to the live scene from
// adb, so the answer costs a room re-entry rather than a rebuild.
//
// It is OFF unless asked for, and the knob file is re-read every second, so
// turning it on does not even need a restart:
//
//     F=/sdcard/Android/data/com.jakobkhansen.silksong/files/trap_probe
//     adb shell "echo 'on=1' > $F"          # dump every 2s
//     adb logcat -d | grep TrapProbe
//
//     adb shell "echo 'on=1 do=setfalse' > $F"   # A? force the bool false
//     adb shell "echo 'on=1 do=rebind'   > $F"   # C? rebuild the bindings
//     adb shell "echo 'on=1 do=restart'  > $F"   # re-run the game's coroutine
//     adb shell "echo 'on=1 no_animator_rebind=1' > $F"   # A/B our own fix
//
// `match=` widens all of this to any animator whose path contains the given
// text, which is what the Slab traps needed: they are not BasicSpikes at all
// but an AnimatorControlSequence prop, posed by a one-frame Play-then-freeze.
//
//     adb shell "echo 'on=1 match=spike_trap_slab_jail' > $F"
//     adb shell "echo 'on=1 match=spike_trap_slab_jail do=resample' > $F"
//
// with do= then applying to the matched animators: `rebind` (bindings only),
// `sample` (evaluate without rebinding), `resample` (both, preserving state --
// the fix AnimatorRebindFix now applies), `enable`, `replay`.
//
// `do=` runs once per distinct value; repeat an action by changing the value
// (`do=rebind2`, `do=rebind3`, ...). Everything here is diagnosis only: with no
// knob file present nothing is scanned, nothing is logged and nothing is
// touched.

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

public class TrapProbe : MonoBehaviour
{
    const string FILE = "trap_probe";
    const string Tag = "[TrapProbe] ";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
    static void Bootstrap()
    {
        var go = new GameObject("TrapProbe");
        DontDestroyOnLoad(go);
        go.AddComponent<TrapProbe>();
    }

    // ─── the knob file ──────────────────────────────────────────────────────
    //
    // Re-read on a timer rather than once per process. DsConfig reads its file
    // at startup, which costs an app restart per hypothesis; here the whole
    // point is to change the experiment while standing in the room with the
    // trap on screen, so a stale value would defeat the tool.

    static Dictionary<string, string> _flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static float _nextFlagRead;
    static string _lastDescribe = "";

    static void RefreshFlags()
    {
        if (Time.unscaledTime < _nextFlagRead) return;
        _nextFlagRead = Time.unscaledTime + 1f;

        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = Path.Combine(Application.persistentDataPath, FILE);
            if (File.Exists(path))
            {
                foreach (string tok in File.ReadAllText(path)
                             .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = tok.IndexOf('=');
                    if (eq > 0) next[tok.Substring(0, eq)] = tok.Substring(eq + 1);
                }
            }
        }
        catch (Exception)
        {
            // An unreadable knob file means "no knobs", never a failure.
            return;
        }
        _flags = next;

        var parts = new List<string>();
        foreach (var kv in _flags) parts.Add(kv.Key + "=" + kv.Value);
        parts.Sort(StringComparer.Ordinal);
        string desc = string.Join(" ", parts.ToArray());
        if (desc != _lastDescribe)
        {
            _lastDescribe = desc;
            Debug.Log(Tag + "flags: " + (desc.Length == 0 ? "(none)" : desc));
        }
    }

    static bool Flag(string key, bool fallback)
    {
        string v;
        if (!_flags.TryGetValue(key, out v)) return fallback;
        return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    static float Num(string key, float fallback)
    {
        string v; float parsed;
        if (!_flags.TryGetValue(key, out v)) return fallback;
        return float.TryParse(v, out parsed) ? parsed : fallback;
    }

    static string Str(string key, string fallback)
    {
        string v;
        return _flags.TryGetValue(key, out v) ? v : fallback;
    }

    /// <summary>
    /// The A/B switch for our own AnimatorRebindFix, consulted by it directly.
    /// If the traps behave once rebinding is suppressed, cause A is proven and
    /// the fix belongs in AnimatorRebindFix rather than in the game's props.
    /// Off unless the knob file says otherwise, so shipping behaviour is
    /// exactly what it was.
    /// </summary>
    public static bool SuppressAnimatorRebind
    {
        get { RefreshFlags(); return Flag("no_animator_rebind", false); }
    }

    // ─── reflection into BasicSpikes ────────────────────────────────────────
    //
    // Everything interesting on it is private: which Animator it drives, which
    // parameter name it writes, and whether it currently believes the spikes
    // are out. Compiling against the depot means these are looked up on the
    // real type rather than by name-matching a guess.

    static readonly FieldInfo FAnimator = Field("animator");
    static readonly FieldInfo FActiveBool = Field("activeBool");
    static readonly FieldInfo FTrigger = Field("activateTrigger");
    static readonly FieldInfo FIsOut = Field("isOut");
    static readonly FieldInfo FDidHit = Field("didHit");

    static FieldInfo Field(string name)
    {
        try { return typeof(BasicSpikes).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance); }
        catch (Exception) { return null; }
    }

    static T Get<T>(FieldInfo f, object target, T fallback)
    {
        try
        {
            if (f == null || target == null) return fallback;
            object v = f.GetValue(target);
            return v is T ? (T)v : fallback;
        }
        catch (Exception) { return fallback; }
    }

    // ─── the loop ───────────────────────────────────────────────────────────

    float _nextDump;
    string _lastAction = "";

    void Update()
    {
        RefreshFlags();
        if (!Flag("on", false)) return;

        // Actions first: an action and a dump in the same tick means the dump
        // shows the result, which is the comparison worth having.
        string action = Str("do", "");
        if (action.Length > 0 && action != _lastAction)
        {
            _lastAction = action;
            try { RunAction(action); }
            catch (Exception e) { Debug.LogWarning(Tag + "action '" + action + "' failed: " + e); }
        }

        if (Time.unscaledTime < _nextDump) return;
        _nextDump = Time.unscaledTime + Mathf.Max(0.25f, Num("interval", 2f));
        try { Dump(); }
        catch (Exception e) { Debug.LogWarning(Tag + "dump failed: " + e); }
    }

    // ─── the dump ───────────────────────────────────────────────────────────

    static BasicSpikes[] FindSpikes()
    {
        return UnityEngine.Object.FindObjectsByType<BasicSpikes>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
    }

    void Dump()
    {
        var spikes = FindSpikes();
        var sb = new StringBuilder();
        sb.Append(Tag).Append("scene=").Append(SceneManager.GetActiveScene().name)
          .Append(" spikes=").Append(spikes.Length);

        var hero = HeroController.SilentInstance;
        sb.Append(" heroInPosition=").Append(hero != null ? hero.isHeroInPosition.ToString() : "(no hero)");
        Debug.Log(sb.ToString());

        for (int i = 0; i < spikes.Length; i++) DumpOne(i, spikes[i]);

        // If BasicSpikes turned out to be the wrong class, one build should not
        // be wasted proving it. `match=` dumps any other animator whose object
        // name contains the given text, so the real prop can be identified in
        // the same pass.
        string match = Str("match", null);
        if (!string.IsNullOrEmpty(match)) DumpMatchingAnimators(match);
    }

    static void DumpOne(int index, BasicSpikes s)
    {
        if (s == null) return;

        var anim = Get<Animator>(FAnimator, s, null);
        var trig = Get<TrackTriggerObjects>(FTrigger, s, null);
        string activeBool = Get<string>(FActiveBool, s, null);

        var sb = new StringBuilder();
        sb.Append(Tag).Append('#').Append(index).Append(' ').Append(PathOf(s.transform));
        sb.Append(" comp=").Append(s.enabled).Append('/').Append(s.gameObject.activeInHierarchy);
        // Cause A's signature is a disagreement between these two: the game
        // thinks the spikes are in, the animator has them out.
        sb.Append(" isOut=").Append(Get<bool>(FIsOut, s, false));
        sb.Append(" didHit=").Append(Get<bool>(FDidHit, s, false));

        // Cause B's signature: this never returns to 0.
        if (trig != null)
        {
            sb.Append(" inside=").Append(trig.InsideCount);
            var names = new List<string>();
            try
            {
                foreach (var go in trig.InsideGameObjects)
                    if (go != null) names.Add(go.name);
            }
            catch (Exception) { }
            if (names.Count > 0) sb.Append(" [").Append(string.Join(", ", names.ToArray())).Append(']');
        }
        else sb.Append(" inside=(no trigger)");

        Debug.Log(sb.ToString());

        if (anim == null) { Debug.Log(Tag + "  no animator"); return; }
        DumpAnimator(anim, activeBool);
    }

    static void DumpAnimator(Animator anim, string activeBool)
    {
        var sb = new StringBuilder();
        sb.Append(Tag).Append("  anim=").Append(anim.gameObject.name)
          .Append(" enabled=").Append(anim.enabled)
          .Append('/').Append(anim.gameObject.activeInHierarchy)
          .Append(" ctrl=").Append(anim.runtimeAnimatorController != null
                                       ? anim.runtimeAnimatorController.name : "(none)")
          .Append(" culling=").Append(anim.cullingMode)
          .Append(" speed=").Append(anim.speed.ToString("0.##"));
        // Cause C's signature is here: an animator whose playables were never
        // built cannot apply a curve no matter what state it reports.
        try { sb.Append(" init=").Append(anim.isInitialized); } catch (Exception) { }
        try { sb.Append(" bound=").Append(anim.hasBoundPlayables); } catch (Exception) { }
        // The prop idiom that turned out to matter: a one-frame Play-then-freeze.
        // If this component is present, the animator being disabled is normal and
        // the pose on screen is supposed to be frame 0 of stateName.
        try
        {
            var seq = anim.GetComponent<AnimatorControlSequence>();
            if (seq != null)
            {
                sb.Append(" AnimatorControlSequence(state='");
                var f = typeof(AnimatorControlSequence).GetField("stateName",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                sb.Append(f != null ? (f.GetValue(seq) as string) : "?").Append("')");
            }
        }
        catch (Exception) { }
        Debug.Log(sb.ToString());

        for (int l = 0; l < anim.layerCount; l++)
        {
            AnimatorStateInfo st = anim.GetCurrentAnimatorStateInfo(l);
            var line = new StringBuilder();
            line.Append(Tag).Append("  state[").Append(l).Append("] hash=").Append(st.fullPathHash)
                .Append(" t=").Append(st.normalizedTime.ToString("0.###"))
                .Append(" len=").Append(st.length.ToString("0.###"))
                .Append(" loop=").Append(st.loop)
                .Append(" transition=").Append(anim.IsInTransition(l));
            try
            {
                var clips = anim.GetCurrentAnimatorClipInfo(l);
                for (int c = 0; c < clips.Length; c++)
                    if (clips[c].clip != null)
                        line.Append(" clip='").Append(clips[c].clip.name).Append('\'');
            }
            catch (Exception) { }
            Debug.Log(line.ToString());
        }

        // The decisive line for cause A. `default` is what Rebind() would reset
        // the parameter to; `cur` is what it holds now. A bool that reads true
        // while BasicSpikes reports isOut=false is a clobbered parameter, and a
        // `cur` that equals `default` right after a rebind says who clobbered it.
        AnimatorControllerParameter[] pars;
        try { pars = anim.parameters; }
        catch (Exception) { return; }

        for (int p = 0; p < pars.Length; p++)
        {
            var par = pars[p];
            var line = new StringBuilder();
            line.Append(Tag).Append("  param '").Append(par.name).Append("' ").Append(par.type);
            try
            {
                switch (par.type)
                {
                    case AnimatorControllerParameterType.Bool:
                        line.Append(" cur=").Append(anim.GetBool(par.nameHash))
                            .Append(" default=").Append(par.defaultBool);
                        break;
                    case AnimatorControllerParameterType.Float:
                        line.Append(" cur=").Append(anim.GetFloat(par.nameHash).ToString("0.###"))
                            .Append(" default=").Append(par.defaultFloat.ToString("0.###"));
                        break;
                    case AnimatorControllerParameterType.Int:
                        line.Append(" cur=").Append(anim.GetInteger(par.nameHash))
                            .Append(" default=").Append(par.defaultInt);
                        break;
                    case AnimatorControllerParameterType.Trigger:
                        line.Append(" cur=").Append(anim.GetBool(par.nameHash))
                            .Append(" default=").Append(par.defaultBool);
                        break;
                }
            }
            catch (Exception) { }
            if (!string.IsNullOrEmpty(activeBool) && par.name == activeBool) line.Append("   <-- activeBool");
            Debug.Log(line.ToString());
        }
    }

    static void DumpMatchingAnimators(string match)
    {
        var all = UnityEngine.Object.FindObjectsByType<Animator>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        int shown = 0;
        for (int i = 0; i < all.Length; i++)
        {
            var a = all[i];
            if (a == null) continue;
            if (a.gameObject.name.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0 &&
                PathOf(a.transform).IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (++shown > 24) { Debug.Log(Tag + "  ...more matches suppressed"); break; }
            Debug.Log(Tag + "match " + PathOf(a.transform));
            DumpAnimator(a, null);
        }
        if (shown == 0) Debug.Log(Tag + "no animator matching '" + match + "'");
    }

    static string PathOf(Transform t)
    {
        var sb = new StringBuilder(t.name);
        for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
        return sb.ToString();
    }

    // ─── the experiments ────────────────────────────────────────────────────
    //
    // Each one isolates a single cause, applied to the trap that is on screen
    // right now. Whichever makes the spikes retract names the bug.

    void RunAction(string action)
    {
        // Trailing digits let the same action be repeated (do=rebind2).
        string verb = action.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

        // With `match=` set the actions apply to whatever animators that names,
        // which is what makes this useful for a prop we have not written a
        // typed accessor for. Without it they apply to BasicSpikes.
        string match = Str("match", null);
        if (!string.IsNullOrEmpty(match)) { RunActionOnMatched(verb, match); return; }

        var spikes = FindSpikes();
        int touched = 0;

        for (int i = 0; i < spikes.Length; i++)
        {
            var s = spikes[i];
            if (s == null) continue;
            var anim = Get<Animator>(FAnimator, s, null);
            string activeBool = Get<string>(FActiveBool, s, null);

            switch (verb)
            {
                // A: write the parameter the coroutine believes it already
                // wrote. If the spikes retract, the state machine and the
                // bindings are both fine and the parameter was the problem.
                case "setfalse":
                    if (anim != null && !string.IsNullOrEmpty(activeBool))
                    {
                        anim.SetBool(activeBool, false);
                        touched++;
                    }
                    break;

                // A, inverted: prove the parameter still drives the prop at all.
                case "settrue":
                    if (anim != null && !string.IsNullOrEmpty(activeBool))
                    {
                        anim.SetBool(activeBool, true);
                        touched++;
                    }
                    break;

                // C: rebuild the curve bindings. If the spikes start moving
                // only after this, they were rendering an authored pose.
                case "rebind":
                    if (anim != null) { anim.Rebind(); touched++; }
                    break;

                // B and A together: re-run the game's own coroutine from the
                // top, which re-writes every parameter and clears isOut. If
                // this fixes it but `setfalse` did not, the trigger is stuck.
                case "restart":
                    s.enabled = false;
                    s.enabled = true;
                    touched++;
                    break;

                // Make an off-camera animator evaluate, in case culling is
                // what is freezing the retract.
                case "alwaysanimate":
                    if (anim != null) { anim.cullingMode = AnimatorCullingMode.AlwaysAnimate; touched++; }
                    break;
            }
        }

        Debug.Log(Tag + "action '" + verb + "' applied to " + touched + " of " + spikes.Length + " spikes");
    }

    // The same experiments, on any animator named by `match=`. This is the half
    // that actually settled issue #19: the Slab's spear traps are not
    // BasicSpikes at all, they are an AnimatorControlSequence prop, and the
    // question was whether their pose could be recovered by rebinding and
    // SAMPLING the state they already hold.
    void RunActionOnMatched(string verb, string match)
    {
        var all = UnityEngine.Object.FindObjectsByType<Animator>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        int touched = 0;

        for (int i = 0; i < all.Length; i++)
        {
            var a = all[i];
            if (a == null || a.runtimeAnimatorController == null) continue;
            if (PathOf(a.transform).IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;

            switch (verb)
            {
                // Bindings only. If nothing moves, the binding was never the
                // problem -- or it was rebuilt and then never evaluated, which
                // is the distinction `resample` below draws out.
                case "rebind":
                    a.Rebind();
                    touched++;
                    break;

                // The candidate fix, exactly as AnimatorRebindFix now applies it
                // to a disabled-but-visible animator: keep the state the game
                // asked for, rebuild the bindings under it, and write the pose.
                case "resample":
                    RebindPreservingState(a);
                    touched++;
                    break;

                // Sample without rebinding, to separate "the binding was broken"
                // from "the binding was fine and nobody ever evaluated it".
                case "sample":
                    a.Update(0f);
                    touched++;
                    break;

                // Let it run, to see the clip actually play.
                case "enable":
                    a.enabled = true;
                    touched++;
                    break;

                // Re-run the prop's own posing sequence from the top.
                case "replay":
                    var seq = a.GetComponent<AnimatorControlSequence>();
                    if (seq != null) { seq.PlayAnimatorFromStart(); touched++; }
                    break;
            }
        }

        Debug.Log(Tag + "action '" + verb + "' applied to " + touched
                  + " animator(s) matching '" + match + "'");
    }

    // Capture state, rebind, restore state, sample once — the same sequence
    // AnimatorRebindFix uses, duplicated here so an experiment can be run
    // against a build whose fix is switched off.
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
            if (stateHash[l] != 0) a.Play(stateHash[l], l, normTime[l]);
        a.Update(0f);
    }
}
#endif
