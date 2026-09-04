// AndroidRumble — make the game's vibration actually reach the device.
//
// Vibration is not broken in this port; it was never implemented for Android at
// all, and the reason is three lines deep in the game's own code:
//
//     VibrationManager.GetMixer()      -> DesktopPlatform's GamepadVibrationMixer
//     PlatformVibrationHelper.Update() -> InputManager.ActiveDevice.Vibrate(s, l)
//     InControl.InputDevice.Vibrate(float, float) { }        // empty virtual
//
// The game mixes its haptics correctly every frame and then hands the result to
// an InControl InputDevice. Only the console and native-input devices override
// Vibrate; a UnityInputDevice -- which is what every Android controller and the
// AYN Thor's own built-in pad becomes -- inherits the empty base method, so the
// values are computed and dropped on the floor. On top of that the APK did not
// request android.permission.VIBRATE, so even a correct call would have been a
// silent no-op.
//
// The device itself is perfectly capable: the AYN Thor exposes "qcom-hv-haptics"
// as a kernel input device, reachable through Android's ordinary Vibrator
// service. So this reads the values the game already computed and plays them.
//
// It deliberately reads the mixer rather than hooking anything. CurrentValues is
// public, DesktopPlatform.Update() already advances the mixer every frame, and
// VibrationManager.VibrationSetting already reflects the player's choice in the
// options menu -- so the game stays in charge of what it wants to play, how
// strongly, and whether it plays at all. Nothing here decides policy.
//
// Two motors become one amplitude, because a phone has one actuator. The larger
// of the two wins rather than their sum, so a rumble that is already at full on
// one motor does not clip.
//
// Knobs, live, for tuning without a rebuild (see TrapProbe for the same idiom):
//
//     F=/sdcard/Android/data/com.jakobkhansen.silksong/files/rumble
//     adb shell "echo 'log=1' > $F"        # log what is being played
//     adb shell "echo 'off=1' > $F"        # disable entirely
//     adb shell "echo 'scale=1.5' > $F"    # amplitude multiplier
//     adb shell "echo 'test=1' > $F"       # play a pulse now, to prove the path

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class AndroidRumble : MonoBehaviour
{
    const string FILE = "rumble";
    const string Tag = "[AndroidRumble] ";

    // How often we are willing to talk to the vibrator. Every frame would be a
    // JNI call at 120 Hz for no benefit: the actuator cannot follow that, and
    // each vibrate() call restarts the effect. 60 ms is well under the shortest
    // haptic the game plays and cheap enough to be invisible.
    const float RefreshInterval = 0.06f;

    // Each effect is asked for slightly longer than the refresh interval so that
    // a continuous rumble has no audible gap between refreshes; the next call
    // supersedes it before it ends.
    const long EffectMs = 90;

    // Below this the actuator either does nothing or buzzes unpleasantly, and
    // the game frequently mixes down to a very small residual value.
    const float MinAmplitude = 0.02f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var go = new GameObject("AndroidRumble");
        DontDestroyOnLoad(go);
        go.AddComponent<AndroidRumble>();
    }

    AndroidJavaObject _vibrator;
    AndroidJavaClass _effectClass;
    bool _hasAmplitude;
    bool _dead;                 // JNI unavailable: stay quiet forever

    float _nextRefresh;
    float _playing = -1f;       // last amplitude handed to the device
    GamepadVibrationMixer _mixer;

    void OnDestroy()
    {
        try { Stop(); } catch (Exception) { }
        if (_vibrator != null) { _vibrator.Dispose(); _vibrator = null; }
        if (_effectClass != null) { _effectClass.Dispose(); _effectClass = null; }
    }

    // ─── the device ─────────────────────────────────────────────────────────

    bool EnsureVibrator()
    {
        if (_dead) return false;
        if (_vibrator != null) return true;

        try
        {
            AndroidJavaObject ctx = Context();
            if (ctx == null) { _dead = true; return false; }

            int sdk;
            using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                sdk = version.GetStatic<int>("SDK_INT");

            if (sdk >= 31)
            {
                // Android 12 deprecated the bare VIBRATOR_SERVICE in favour of a
                // manager that can address several actuators; the default one is
                // the handset's.
                using (var mgr = ctx.Call<AndroidJavaObject>("getSystemService", "vibrator_manager"))
                {
                    if (mgr == null) { _dead = true; return false; }
                    _vibrator = mgr.Call<AndroidJavaObject>("getDefaultVibrator");
                }
            }
            else
            {
                _vibrator = ctx.Call<AndroidJavaObject>("getSystemService", "vibrator");
            }
            ctx.Dispose();

            if (_vibrator == null) { _dead = true; return false; }

            if (!_vibrator.Call<bool>("hasVibrator"))
            {
                Debug.Log(Tag + "device reports no vibrator; disabled");
                _dead = true;
                return false;
            }

            // Without amplitude control every effect plays at full strength, so
            // a gentle rumble would be indistinguishable from a heavy one. Still
            // better than silence, so we play on/off instead of giving up.
            _hasAmplitude = _vibrator.Call<bool>("hasAmplitudeControl");
            _effectClass = new AndroidJavaClass("android.os.VibrationEffect");

            Debug.Log(Tag + "vibrator ready (sdk=" + sdk + " amplitudeControl=" + _hasAmplitude + ")");
            return true;
        }
        catch (Exception e)
        {
            // Missing permission, no service, a stripped JNI binding: all mean
            // the same thing here, which is that the game plays without haptics.
            Debug.LogWarning(Tag + "unavailable, vibration disabled: " + e.Message);
            _dead = true;
            return false;
        }
    }

    // The application context, taken from the framework rather than from Unity's
    // UnityPlayer.currentActivity: it does not depend on which player class this
    // build happens to use, and it is valid before and after the activity exists.
    static AndroidJavaObject Context()
    {
        try
        {
            using (var thread = new AndroidJavaClass("android.app.ActivityThread"))
            {
                var app = thread.CallStatic<AndroidJavaObject>("currentApplication");
                if (app != null) return app;
            }
        }
        catch (Exception) { }

        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                return player.GetStatic<AndroidJavaObject>("currentActivity");
        }
        catch (Exception) { }

        return null;
    }

    // ─── the game's side ────────────────────────────────────────────────────

    // The mixer is created with the Platform, which does not exist at assembly
    // load, and is replaced if the platform is ever rebuilt -- so re-resolve it
    // whenever we do not have a live one rather than caching it once.
    GamepadVibrationMixer Mixer()
    {
        if (_mixer != null) return _mixer;
        try { _mixer = VibrationManager.GetMixer() as GamepadVibrationMixer; }
        catch (Exception) { _mixer = null; }
        return _mixer;
    }

    void LateUpdate()
    {
        RefreshFlags();
        if (Flag("off", false)) { Stop(); return; }

        if (Flag("test", false) && !_testedThisFlagSet)
        {
            _testedThisFlagSet = true;
            if (EnsureVibrator()) { Play(1f); Debug.Log(Tag + "test pulse"); }
            return;
        }

        if (Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + RefreshInterval;

        // The player's own setting, straight from the options menu. Off means
        // off: no call, no cost, and any effect already playing is cancelled.
        if (VibrationManager.VibrationSetting == VibrationManager.VibrationSettings.Off)
        {
            Stop();
            return;
        }

        var mixer = Mixer();
        if (mixer == null) { Stop(); return; }

        GamepadVibrationMixer.GamepadVibrationEmission.Values v = mixer.CurrentValues;

        // One actuator, two motors: take the louder rather than the sum, so a
        // clip that already drives one motor to full is not distorted.
        float amplitude = Mathf.Max(v.Small, v.Large) * Num("scale", 1f);
        amplitude = Mathf.Clamp01(amplitude);

        if (amplitude < MinAmplitude) { Stop(); return; }

        // Re-issuing an identical effect would restart it and produce a stutter,
        // so only speak up when the value has actually moved or the previous
        // effect is due to expire.
        if (_playing >= 0f && Mathf.Abs(amplitude - _playing) < 0.05f) return;

        if (EnsureVibrator()) Play(amplitude);
    }

    bool _testedThisFlagSet;

    void Play(float amplitude)
    {
        try
        {
            // Android takes 1..255; 0 means "stop", which is Stop()'s job.
            int amp = Mathf.Clamp(Mathf.RoundToInt(amplitude * 255f), 1, 255);
            using (var effect = _effectClass.CallStatic<AndroidJavaObject>(
                       "createOneShot", EffectMs, _hasAmplitude ? amp : -1))
            {
                _vibrator.Call("vibrate", effect);
            }
            _playing = amplitude;
            if (Flag("log", false))
                Debug.Log(Tag + "amp=" + amplitude.ToString("0.00") + " (" + amp + "/255)");
        }
        catch (Exception e)
        {
            Debug.LogWarning(Tag + "vibrate failed, disabling: " + e.Message);
            _dead = true;
        }
    }

    void Stop()
    {
        if (_playing < 0f) return;
        _playing = -1f;
        try { if (_vibrator != null) _vibrator.Call("cancel"); }
        catch (Exception) { }
    }

    // ─── knobs ──────────────────────────────────────────────────────────────

    static Dictionary<string, string> _flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static float _nextFlagRead;

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

        if (next.Count == 0 && _flags.Count == 0) return;
        _flags = next;
        if (!Flag("test", false)) _testedThisFlagSet = false;
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
}
#endif
