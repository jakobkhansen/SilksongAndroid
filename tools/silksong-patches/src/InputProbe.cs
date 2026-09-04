// InputProbe — find out where a dropped input goes.
//
// Players on the AYN Thor report inputs that do not register. Everything this
// port adds on the input side is touch-only (DsTouch, DsInput,
// InventoryTouchInput) and none of it can reach a gamepad button, so the drop is
// somewhere in the chain below:
//
//     kernel gamepad -> Android InputDispatcher -> Unity -> InControl -> game
//
// Two of those links have already been ruled out by measurement rather than
// argument. Android's focused display DOES follow a touch on the second panel
// (FocusedDisplayId went 0 -> 4 -> 0 on a tap), which would have been a very
// tidy explanation, but pressing the bottom screen demonstrably does not break
// the top one, so key routing is not it.
//
// What remains, and what this exists to catch, is InControl re-enumerating its
// devices underneath the game. UnityInputDeviceManager.Update does this every
// second:
//
//     deviceRefreshTimer += deltaTime;
//     if (deviceRefreshTimer >= 1f) {
//         QueryJoystickInfo();                       // Input.GetJoystickNames()
//         if (JoystickInfoHasChanged) {
//             DetachDevices();                       // every device, including
//             AttachDevices();                       // the one being held
//         }
//     }
//
// A detach sets InputManager.ActiveDevice to InputDevice.Null and throws away
// every control's state, so a button held across the refresh is released and a
// press that lands in the same frame is lost. It only takes the hashed list of
// joystick names to wobble once, and the device has good reasons to wobble: it
// exposes an "ODIN Station Virtual Mouse" alongside the pad, and Android logs
// "InputReader: Reconfiguring input devices, changes=DISPLAY_INFO" whenever the
// second panel's viewport is republished.
//
// InControl would normally say so itself -- it logs "Change in attached Unity
// joysticks detected" -- but the game's InControlManager.LogMessage is an empty
// method, so every InControl diagnostic is swallowed before it reaches the log.
// That is why this has gone unnoticed, and it is the first thing fixed here: we
// subscribe to InControl's own public events instead.
//
// Off unless asked for; the knob file is re-read every second.
//
//     F=/sdcard/Android/data/com.jakobkhansen.silksong/files/input_probe
//     adb shell "echo 'on=1' > $F"           # attach/detach/active-device churn
//     adb shell "echo 'on=1 names=1' > $F"   # + Input.GetJoystickNames() changes
//     adb shell "echo 'on=1 edges=1' > $F"   # + every button edge, with frame gap
//     adb logcat -d | grep InputProbe
//
// `edges=1` is the one that proves a drop: it prints the frame number and the
// milliseconds since the previous frame for every press and release InControl
// reports. A press the player made that never appears, next to a device detach
// at the same timestamp, is the whole answer.

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using InControl;
using UnityEngine;

public class InputProbe : MonoBehaviour
{
    const string FILE = "input_probe";
    const string Tag = "[InputProbe] ";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var go = new GameObject("InputProbe");
        DontDestroyOnLoad(go);
        go.AddComponent<InputProbe>();
    }

    bool _subscribed;
    string[] _lastNames;
    string _lastActive = "";
    float _nextNameCheck;
    float _lastFrameTime;

    // Button states from the previous frame, per device, so an edge can be
    // reported with the frame it happened on.
    readonly Dictionary<int, bool[]> _lastButtons = new Dictionary<int, bool[]>();

    void OnEnable() { Subscribe(); }

    void OnDisable()
    {
        if (!_subscribed) return;
        InputManager.OnDeviceAttached -= OnAttached;
        InputManager.OnDeviceDetached -= OnDetached;
        InputManager.OnActiveDeviceChanged -= OnActiveChanged;
        _subscribed = false;
    }

    // InControl is set up by the game's own InControlManager, which may not have
    // run yet, and it tears its events down on reset -- so keep checking rather
    // than subscribing once and assuming it held.
    void Subscribe()
    {
        if (_subscribed || !InputManager.IsSetup) return;
        InputManager.OnDeviceAttached += OnAttached;
        InputManager.OnDeviceDetached += OnDetached;
        InputManager.OnActiveDeviceChanged += OnActiveChanged;
        _subscribed = true;
        Debug.Log(Tag + "subscribed to InControl device events");
    }

    void OnAttached(InputDevice d)
    {
        if (!On) return;
        Debug.Log(Tag + "ATTACH   " + Describe(d) + Where());
    }

    // The line that matters. A detach immediately before a missed input is the
    // signature we are looking for.
    void OnDetached(InputDevice d)
    {
        if (!On) return;
        Debug.LogWarning(Tag + "DETACH   " + Describe(d) + Where());
    }

    void OnActiveChanged(InputDevice d)
    {
        if (!On) return;
        Debug.Log(Tag + "ACTIVE-> " + Describe(d) + Where());
    }

    static string Describe(InputDevice d)
    {
        if (d == null) return "(null)";
        var sb = new StringBuilder();
        sb.Append('\'').Append(d.Name).Append('\'');
        try { sb.Append(" attached=").Append(d.IsAttached); } catch (Exception) { }
        try { sb.Append(" style=").Append(d.DeviceStyle); } catch (Exception) { }
        return sb.ToString();
    }

    string Where()
    {
        return "  frame=" + Time.frameCount +
               " t=" + Time.unscaledTime.ToString("0.000") +
               " devices=" + (InputManager.Devices != null ? InputManager.Devices.Count : -1);
    }

    void Update()
    {
        RefreshFlags();
        Subscribe();
        if (!On) return;

        float now = Time.unscaledTime;
        float gapMs = (now - _lastFrameTime) * 1000f;
        _lastFrameTime = now;

        if (Flag("names", false) && now >= _nextNameCheck)
        {
            _nextNameCheck = now + 0.25f;
            CheckJoystickNames();
        }

        // The ACTIVE device by name, sampled rather than only evented, because a
        // device can go null without OnActiveDeviceChanged firing in the order
        // one expects during a detach/attach pair.
        string active = InputManager.ActiveDevice != null ? InputManager.ActiveDevice.Name : "(null)";
        if (active != _lastActive)
        {
            Debug.Log(Tag + "active device: '" + _lastActive + "' -> '" + active + "'" + Where());
            _lastActive = active;
        }

        if (Flag("edges", false)) ReportEdges(gapMs);
    }

    // Unity's own view of what is plugged in. This is the exact input to the
    // hash InControl compares each second, so if it wobbles, this shows the
    // wobble and names the device that caused it.
    void CheckJoystickNames()
    {
        string[] names;
        try { names = Input.GetJoystickNames(); }
        catch (Exception) { return; }

        bool changed = _lastNames == null || _lastNames.Length != names.Length;
        if (!changed)
            for (int i = 0; i < names.Length; i++)
                if (names[i] != _lastNames[i]) { changed = true; break; }

        if (!changed) return;
        _lastNames = names;

        var sb = new StringBuilder();
        sb.Append(Tag).Append("joystick names (").Append(names.Length).Append("):");
        for (int i = 0; i < names.Length; i++)
            sb.Append(" [").Append(i).Append("]='").Append(names[i]).Append('\'');
        Debug.LogWarning(sb.ToString() + Where());
    }

    // Every press and release InControl reports, with the frame it landed on and
    // how long that frame was. A long gap beside a missing press separates "the
    // game never saw it" from "the game saw it late".
    //
    // An explicit control list rather than an enumeration: InputDevice exposes
    // its controls by InputControlType, and these are the ones the game is
    // actually played with, so the log stays readable during a real session.
    static readonly InputControlType[] Watched =
    {
        InputControlType.Action1, InputControlType.Action2,
        InputControlType.Action3, InputControlType.Action4,
        InputControlType.LeftBumper, InputControlType.RightBumper,
        InputControlType.LeftTrigger, InputControlType.RightTrigger,
        InputControlType.DPadUp, InputControlType.DPadDown,
        InputControlType.DPadLeft, InputControlType.DPadRight,
        InputControlType.LeftStickUp, InputControlType.LeftStickDown,
        InputControlType.LeftStickLeft, InputControlType.LeftStickRight,
        InputControlType.Command, InputControlType.Menu,
    };

    void ReportEdges(float gapMs)
    {
        var devices = InputManager.Devices;
        if (devices == null) return;

        for (int i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            if (d == null || !d.IsAttached) continue;

            int id = d.GetHashCode();
            bool[] last;
            if (!_lastButtons.TryGetValue(id, out last) || last.Length != Watched.Length)
            {
                last = new bool[Watched.Length];
                _lastButtons[id] = last;
            }

            for (int b = 0; b < Watched.Length; b++)
            {
                bool nowDown;
                try
                {
                    var control = d.GetControl(Watched[b]);
                    nowDown = control != null && control.IsPressed;
                }
                catch (Exception) { continue; }

                if (nowDown == last[b]) continue;
                last[b] = nowDown;
                Debug.Log(Tag + (nowDown ? "DOWN " : "UP   ") + Watched[b] +
                          " on '" + d.Name + "'  frame=" + Time.frameCount +
                          " gap=" + gapMs.ToString("0.0") + "ms");
            }
        }
    }

    // ─── knobs ──────────────────────────────────────────────────────────────

    static Dictionary<string, string> _flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static float _nextFlagRead;

    static bool On { get { return Flag("on", false); } }

    void RefreshFlags()
    {
        if (Time.unscaledTime < _nextFlagRead) return;
        _nextFlagRead = Time.unscaledTime + 1f;

        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = Path.Combine(Application.persistentDataPath, FILE);
            if (File.Exists(path))
                foreach (string tok in File.ReadAllText(path)
                             .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = tok.IndexOf('=');
                    if (eq > 0) next[tok.Substring(0, eq)] = tok.Substring(eq + 1);
                }
        }
        catch (Exception) { return; }
        _flags = next;
    }

    static bool Flag(string key, bool fallback)
    {
        string v;
        if (!_flags.TryGetValue(key, out v)) return fallback;
        return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
#endif
