// AspectGate — lets the game render into screens it was never shaped for.
//
// ── the problem ────────────────────────────────────────────────────────────
//
// Silksong clamps the shape it renders into. ForceCameraAspect:
//
//     AutoScaleViewportShared:
//         MinMaxFloat(1.6f, 2.3916667f).GetClampedBetween(w / (float)h)
//
// and letterboxes whatever is left over. On a 16:9 monitor neither end ever
// comes up. On Android both do, and a single device can hit both:
//
//     Galaxy Z Fold inner screen   2160x1856  = 1.164 : 1   below the floor
//     4:3 handheld                 1600x1200  = 1.333 : 1   below the floor
//     Galaxy Z Fold cover screen   2376x968   = 2.455 : 1   above the ceiling
//
// Below the floor the bars are top and bottom; above the ceiling they are left
// and right. Measured on a Fold 6 unfolded: 253 px top and bottom, 13.6%,
// which is exactly (1 - 1.164/1.6) / 2.
//
// Those bars are not a bug in this port and they are not ours to call wrong.
// They are the framing the art was made for, and they are what a PC player
// with an unusual monitor sees too.
//
// ── the mechanism ──────────────────────────────────────────────────────────
//
// A constant in IL cannot be changed at runtime, because by the time the game
// runs there is no IL: il2cpp turned it into C++ and then into a .so. So the
// build weaves it instead -- ModWeaver's Builtin.cs replaces those two loads
// with calls to a gate injected beside them:
//
//     public static class SilksongAspectGate
//     {
//         public static float Floor, Ceiling;        // 0 until somebody answers
//         public static float GetFloor()   => Floor   < 1f ? 1.6f       : Floor;
//         public static float GetCeiling() => Ceiling < 1f ? 2.3916667f : Ceiling;
//     }
//
// This is the somebody. The weave is unconditional and costs nothing; the
// answer is given here, at startup, from the launcher's setting -- so turning
// this on and off is a relaunch rather than the twenty minutes an il2cpp
// conversion costs.
//
// ── why it is off unless asked for ─────────────────────────────────────────
//
// Because it is a real trade and only the player can make it. The clamp also
// drives the camera: heightMult is 1.7777778 / clamped, so opening the floor
// from 1.6 to 1.164 takes the multiplier from 1.111 to 1.527 and the camera
// shows about half again as much world vertically as the artists framed for.
// On a Fold that is a screen with no bars; it is also a wider view than
// anybody at Team Cherry ever looked at, and scenes can show their edges. That
// is the same bargain the PC ultrawide mods make horizontally, and it is the
// reason this is a switch rather than a decision.
//
// Nothing here is reached at all on an ordinary phone: 16:9 is 1.777 and 19.5:9
// is 2.167, both inside the stock range, so the clamp does not bite and setting
// the gate changes precisely nothing. That is why it is safe to leave on.
//
// ── how it fails ───────────────────────────────────────────────────────────
//
// Into stock behaviour, every way round. An unwoven build has no gate type and
// the reflection below finds nothing. A woven build whose gate is never set
// reads zero, and the gate itself turns anything below 1.0 back into the
// game's own number. A setting that cannot be read is a setting at its
// default, which is off.

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Reflection;
using UnityEngine;

public static class AspectGate
{
    /// Matches ModWeaver.Builtin. Reached by name because the type does not
    /// exist until the weave puts it there, so there is nothing for this
    /// assembly to reference at compile time.
    const string GateTypeName = "SilksongAspectGate";
    const string FloorFieldName = "Floor";
    const string CeilingFieldName = "Ceiling";

    /// The launcher's key. See SettingsStore.KEY_WIDE_ASPECT.
    const string SETTING = "wide_aspect";

    /// The widest and narrowest shapes we will ask the game to render into.
    ///
    /// Not zero and not infinity, and neither is any particular device's
    /// number -- the game clamps the screen's own aspect into this range, so
    /// every shape BETWEEN these two renders at its true proportions and only
    /// the extremes are refused.
    ///
    /// 1.0 is square. Below it the window is portrait, and the height
    /// multiplier would pull the camera back further than anything in the game
    /// is built to survive, so portrait keeps the stock letterbox. 3.0 is
    /// past every real Android panel, including a folded cover screen at 2.455
    /// and a 32:9 desktop monitor at 3.55 being generous to the former without
    /// pretending the latter is a phone.
    const float FLOOR = 1.0f;
    const float CEILING = 3.0f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Apply()
    {
        try
        {
            if (!SilksongPatches.Settings.GetBool(SETTING, false))
            {
                Debug.Log("[AspectGate] off; the game keeps its own 1.60 - 2.39 range");
                return;
            }

            Type gate = GateType();
            if (gate == null)
            {
                // A build whose weave did not take. Worth one line, because the
                // setting is on and the player will otherwise wonder why
                // nothing changed.
                Debug.LogWarning("[AspectGate] on, but this build has no "
                                 + GateTypeName + "; the range is unchanged");
                return;
            }

            bool floor = Set(gate, FloorFieldName, FLOOR);
            bool ceiling = Set(gate, CeilingFieldName, CEILING);
            if (!floor && !ceiling)
            {
                Debug.LogWarning("[AspectGate] on, but " + GateTypeName
                                 + " has neither field; the range is unchanged");
                return;
            }

            float aspect = Screen.width / (float)Mathf.Max(Screen.height, 1);
            Debug.Log($"[AspectGate] on; range {FLOOR:0.00} - {CEILING:0.00} "
                      + $"(screen is {Screen.width}x{Screen.height}, {aspect:0.000}:1)");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[AspectGate] leaving the game's own range alone: " + e.Message);
        }
    }

    static bool Set(Type gate, string fieldName, float value)
    {
        FieldInfo field = gate.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
        if (field == null) return false;
        field.SetValue(null, value);
        return true;
    }

    /// <summary>The woven gate, or null if this build has none.</summary>
    static Type GateType()
    {
        // The gate is injected into Assembly-CSharp, which this assembly
        // already references, so its own assembly is the first place to look
        // and almost always the only one. The scan behind it costs nothing on
        // the path that matters and saves the patch from caring which assembly
        // the weaver chose.
        Type type = Type.GetType(GateTypeName + ", Assembly-CSharp", false);
        if (type != null) return type;

        type = Type.GetType(GateTypeName, false);
        if (type != null) return type;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(GateTypeName, false);
            if (type != null) return type;
        }
        return null;
    }
}
#endif
