// PerfOverlay — top-left OnGUI overlay showing FPS, CPU/GPU load,
// RAM, and battery during gameplay. Driven by the
// `perf_overlay` boolean in the launcher's SharedPreferences
// (file `launcher_settings`), which the Settings screen flips. We
// read the pref ONCE at startup via JNI on the main thread; if
// it's false the overlay never instantiates itself and there's
// zero per-frame cost. Flipping the toggle from Settings only
// takes effect on the NEXT game launch (the launcher tells the
// user this).
//
// We deliberately use legacy OnGUI rather than UI Toolkit / uGUI:
// OnGUI doesn't participate in the game's normal canvas stack so
// there's no Z-order surgery to wedge a debug overlay in, and the
// CPU cost is trivial relative to what we're measuring (~50µs per
// frame for the text draw).
//
// Counters refresh once per second to avoid creating GC pressure
// from string allocation every frame; the actual numbers are
// sampled every frame and averaged over the second. The /proc, /sys
// and battery readings are taken on a worker thread, never the main
// thread (see Sample).
//
// Data sources (Android-specific, all read via plain file I/O —
// no extra permissions needed; we sample /proc and /sys entries
// that any app can read for its own process or the GPU device):
//
//   FPS            : Time.unscaledDeltaTime, smoothed.
//   CPU %          : /proc/stat overall device usage delta /
//                    total ticks delta (jiffies-based, kernel-supplied).
//   GPU %          : /sys/class/kgsl/kgsl-3d0/gpubusy — Adreno
//                    driver exposes "active total" ratio; safe to
//                    open as a regular user. Returns -1 on
//                    non-Adreno SoCs (Mali, Xclipse) — we just
//                    hide the GPU line there.
//   RAM            : Application.totalReservedMemoryRecorder, used
//                    + total. Captures Unity-side allocations only;
//                    matches what the Profiler reports.
//   Battery        : BatteryManager via AndroidJavaObject; CAPACITY
//                    int property + CURRENT_NOW (µA, instantaneous
//                    draw — negative when discharging, by Android
//                    convention).

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Threading;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

public class PerfOverlay : MonoBehaviour
{
    const string PREFS_NAME = "launcher_settings";
    const string KEY_PERF_OVERLAY = "perf_overlay";
    // Separate from perf_overlay so a benchmark run can LOG without DRAWING:
    // the OnGUI overlay costs ~50µs/frame which would skew the watts we're
    // trying to measure. When perf_log is on we write a config-stamped CSV
    // to persistentDataPath/perf/ that makes each A/B lever config comparable.
    const string KEY_PERF_LOG = "perf_log";
    const float REFRESH_INTERVAL = 1.0f;

    // Bootstrap before any scene loads so the overlay can attach
    // to a permanent GameObject. RuntimeInitializeLoadType.AfterAssembliesLoaded
    // is the earliest hook that still gives us a working scene
    // graph + valid Activity reference; running before that misses
    // the first second of gameplay and (more importantly) loses
    // the AndroidJavaObject context.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
    static void Bootstrap()
    {
        // See Settings.cs: exported to a file by the launcher rather than
        // read across processes from its SharedPreferences.
        bool draw = SilksongPatches.Settings.GetBool(KEY_PERF_OVERLAY, false);
        bool log = SilksongPatches.Settings.GetBool(KEY_PERF_LOG, false);
        // Instantiate if EITHER drawing or logging is requested.
        if (!draw && !log) return;

        s_drawOverlay = draw;
        s_logToFile = log;
        var go = new GameObject("__PerfOverlay__");
        DontDestroyOnLoad(go);
        go.AddComponent<PerfOverlay>();
        Debug.Log($"[PerfOverlay] enabled (draw={draw} log={log})");
    }

    // Config passed from Bootstrap into the instance. AddComponent calls Awake
    // synchronously, so we can't set instance fields between construct and
    // Awake — stash on statics and read them in Awake.
    static bool s_drawOverlay, s_logToFile;

    // Per-frame samples — we accumulate over REFRESH_INTERVAL and
    // emit once. Keeping just the running aggregate avoids the
    // per-frame string allocations a "redraw every frame" approach
    // would cause.
    int _frameAccum;
    float _timeAccum;
    string _displayText = "";
    // OnGUI runs at least twice a frame; measuring the text only when it
    // changes keeps the overlay from allocating every frame.
    readonly GUIContent _content = new GUIContent();
    string _measuredText;
    Vector2 _measuredSize;

    // Everything read from /proc, /sys or the battery service. The
    // power-supply nodes and BatteryManager both go to the fuel gauge,
    // ~0.3 ms per read on the Thor and far slower on other devices; on the
    // main thread that was a hitch every second (issue #33). A worker
    // takes the readings and Refresh shows the latest set, so the device
    // stats lag the frame stats by one refresh.
    struct Sample
    {
        public float Cpu, CoreMax, Gpu, VoltageV, PowerW, BattTempC;
        public long CurHz, CurrentUa;
        public int BattLevel;
        public string BattError;
    }
    static readonly Sample NoSample = new Sample
    {
        Cpu = -1f, CoreMax = -1f, Gpu = -1f, BattLevel = -1, BattTempC = float.NaN,
    };
    readonly object _sampleLock = new object();
    Sample _sample = NoSample;
    readonly AutoResetEvent _sampleWake = new AutoResetEvent(false);
    volatile bool _samplerStop;

    // CPU baseline (jiffies). Updated each refresh tick.
    long _prevCpuActive, _prevCpuTotal;
    // Per-core CPU baselines. /proc/stat exposes "cpuN" lines for
    // each core; we track each one and report the BUSIEST core as a
    // separate stat. The averaged "cpu" line hides single-thread
    // bottlenecks — 1 thread fully saturated on an 8-core SoC shows
    // as ~12% averaged, looks fine, but is actually a hard render-
    // thread cap. Max-core % surfaces that.
    long[] _prevCoreActive;
    long[] _prevCoreTotal;
    // GPU sampling. We try several Adreno sysfs nodes in order of
    // preference:
    //
    //   1. /sys/class/kgsl/kgsl-3d0/gpu_busy_percentage
    //        Cleanest: returns the string "<N> %" directly. No
    //        math, no delta tracking.
    //
    //   2. /sys/class/kgsl/kgsl-3d0/devfreq/gpu_load
    //        Plain integer, 0-100. Also a snapshot percentage.
    //
    //   3. /sys/class/kgsl/kgsl-3d0/gpubusy
    //        Two integers "<active> <total>" — but per Adreno's
    //        driver convention, the counters RESET on read. So
    //        `active/total` from a SINGLE read is the busy
    //        fraction over the interval since the last read.
    //        DO NOT take a delta vs a previously-saved value —
    //        the previous values were already consumed by the
    //        last read. The earlier delta-based math was the
    //        source of "??" (dt <= 0) and "200%" (active in this
    //        read window > total reported for the last window).
    //
    // First-existing wins. _gpuKind tracks which one we resolved
    // to so the per-frame read code picks the right parser.
    enum GpuKind { None, Percent, Devfreq, Gpubusy }
    GpuKind _gpuKind = GpuKind.None;
    string _gpuPath = "";
    static readonly (string path, GpuKind kind)[] _gpuPathCandidates = new[]
    {
        ("/sys/class/kgsl/kgsl-3d0/gpu_busy_percentage", GpuKind.Percent),
        ("/sys/class/kgsl/kgsl-3d0/devfreq/gpu_load",    GpuKind.Devfreq),
        ("/sys/class/kgsl/kgsl-3d0/gpubusy",             GpuKind.Gpubusy),
    };

    // GPU clock readout. Adreno reports the current devfreq governor
    // pick in Hz at `/sys/class/kgsl/kgsl-3d0/devfreq/cur_freq` and
    // the device's top P-state at `max_freq`. Displaying both lets
    // the user see whether DCVS is leaving headroom on the table
    // (cur << max while frames drop) vs whether the GPU is just
    // saturated (cur ≈ max and frames still drop).
    //
    // Read once at startup for max_freq (immutable per boot), per
    // tick for cur_freq. Both files always have the same Hz integer
    // format on Adreno; null result = file missing (older Adreno or
    // non-Qualcomm SoC).
    const string GpuCurFreqPath = "/sys/class/kgsl/kgsl-3d0/devfreq/cur_freq";
    const string GpuMaxFreqPath = "/sys/class/kgsl/kgsl-3d0/devfreq/max_freq";
    long _gpuMaxHz = -1;

    // /sys/class/power_supply/battery/voltage_now reads cell
    // voltage in µV. Combined with the BatteryManager current_now
    // (µA), we can derive instantaneous power draw in watts
    // (W = A × V) without the user having to mentally convert
    // mA at "some voltage".
    const string BATTERY_VOLTAGE_PATH = "/sys/class/power_supply/battery/voltage_now";

    // Cached Android objects — building these per-sample would be
    // wasteful (every Call<> crosses the JNI boundary).
    AndroidJavaObject _activity;
    AndroidJavaObject _batteryManager;

    GUIStyle _style;
    Texture2D _bgTexture;

    // ── Per-frame breakdown ───────────────────────────────────────────────
    // FrameTimingManager gives the high-level split that answers the first
    // optimisation question: are we MAIN-thread-bound (game/scripts), RENDER-
    // thread-bound (draw submission), or GPU-bound? It reports real
    // milliseconds at runtime in a release Player once PlayerSettings
    // .enableFrameTimingStats is on (set in the build config).
    //
    // ProfilerRecorder counters add the "why": draw calls, SetPass calls,
    // batches, triangles, vertices and GC-allocated-per-frame. Some counters
    // are only populated in development Players; we probe .Valid and show /
    // log only the ones this build actually exposes.
    ProfilerRecorder _rDraw, _rSetPass, _rBatches, _rTris, _rVerts, _rGcAlloc;
    ProfilerRecorder _rMainThread, _rRenderThread, _rRenderThread2;
    ProfilerRecorder _rDynBatch, _rStaticBatch, _rInstBatch;
    readonly FrameTiming[] _frameTimings = new FrameTiming[1];
    bool _loggedRecorderAvailability;

    // ── CSV benchmark logging ─────────────────────────────────────────────
    // Independent of the on-screen overlay: `perf_log` writes an aggregated,
    // config-stamped CSV to persistentDataPath/perf/perf_<ts>.csv (adb-pullable
    // at /storage/emulated/0/Android/data/<pkg>/files/perf/). One file per
    // run = one comparable A/B data point. AutoFlush keeps rows durable even
    // if the process is killed.
    bool _drawOverlay = true;     // mirror of s_drawOverlay (set in Awake)
    bool _logToFile;              // mirror of s_logToFile
    StreamWriter _csv;
    string _csvPath;
    float _logStartTime;          // realtimeSinceStartup at first row
    bool _summaryFlushed;
    // Per-frame frame-time samples (ms) within the current 1s window, for
    // percentiles + "% of frames that hit the cap". Fixed buffer = zero
    // per-second allocation; overflow (sustained >512fps) just drops the
    // extra samples that second, which never happens at a 120Hz cap.
    readonly float[] _frameMs = new float[512];
    int _frameMsCount;
    readonly float[] _sortScratch = new float[512];
    float _targetMs = 1000f / 120f;   // resolved in OpenLog() from targetFrameRate
    // Last per-frame breakdown values, captured by AppendFrameBreakdown so
    // WriteCsvRow can emit them without recomputing.
    double _lastMainMs, _lastRndMs, _lastGpuMs;
    long _lastDc = -1, _lastSp = -1, _lastBt = -1;
    // Running session aggregates for the summary line.
    int _sumRows;
    double _sumFps, _sumWatts, _sumMj, _sumPctCap, _sumP99, _worstP99;
    float _minFps = float.MaxValue;

    // Battery temperature (tenths of °C) — a reliable, permission-free
    // whole-device thermal proxy on handhelds (case temp tracks cell temp).
    const string BATTERY_TEMP_PATH = "/sys/class/power_supply/battery/temp";

    void Awake()
    {
        _drawOverlay = s_drawOverlay;
        _logToFile = s_logToFile;
        try
        {
            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            _activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            // Context.BATTERY_SERVICE = "batterymanager".
            _batteryManager = _activity.Call<AndroidJavaObject>("getSystemService", "batterymanager");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PerfOverlay] battery service unavailable: " + ex.Message);
        }
        foreach (var (path, kind) in _gpuPathCandidates)
        {
            if (File.Exists(path))
            {
                _gpuPath = path;
                _gpuKind = kind;
                break;
            }
        }
        _gpuMaxHz = TryReadHz(GpuMaxFreqPath);
        SampleCpuOnce();
        StartRecorders();
        if (_logToFile) OpenLog();
        new Thread(SamplerLoop) { IsBackground = true, Name = "PerfOverlaySampler" }.Start();
        _sampleWake.Set();
    }

    void SamplerLoop()
    {
        // BatteryManager is a JNI object; a thread Unity did not create
        // has to attach to the VM before calling it.
        AndroidJNI.AttachCurrentThread();
        try
        {
            while (true)
            {
                _sampleWake.WaitOne();
                if (_samplerStop) return;
                var s = TakeSample();
                lock (_sampleLock) _sample = s;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PerfOverlay] sampler stopped: " + ex.Message);
        }
        finally
        {
            AndroidJNI.DetachCurrentThread();
        }
    }

    Sample TakeSample()
    {
        var s = NoSample;
        s.Cpu = SampleCpu();
        // Sample every tick (not just when cpu>=0) so the per-core baselines
        // keep advancing and the CSV always has a peak-core value.
        s.CoreMax = SampleMaxCore();
        s.Gpu = _gpuKind != GpuKind.None ? SampleGpu() : -1f;
        s.CurHz = TryReadHz(GpuCurFreqPath);
        if (_batteryManager != null)
        {
            try
            {
                // BATTERY_PROPERTY_CAPACITY=4 (int %),
                // BATTERY_PROPERTY_CURRENT_NOW=2 (long µA — negative
                // while discharging on most OEMs, positive on a few).
                s.BattLevel = _batteryManager.Call<int>("getIntProperty", 4);
                s.CurrentUa = _batteryManager.Call<long>("getLongProperty", 2);
                s.VoltageV = ReadBatteryVoltageVolts();
                s.PowerW = s.VoltageV > 0f ? (Math.Abs(s.CurrentUa) / 1e6f) * s.VoltageV : 0f;
            }
            catch (Exception ex)
            {
                s.BattLevel = -1;
                s.BattError = ex.GetType().Name;
            }
        }
        if (_logToFile) s.BattTempC = ReadBatteryTempC();
        return s;
    }

    // Built-in Profiler counters. StartNew never throws for an unknown name —
    // it returns an invalid recorder — so we just probe .Valid before reading.
    void StartRecorders()
    {
        _rDraw       = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
        _rSetPass    = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
        _rBatches    = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
        _rTris       = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
        _rVerts      = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count");
        _rGcAlloc    = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
        // FrameTimingManager returns nothing on this Vulkan/Turnip Android setup,
        // so the CPU thread split comes from these System timing counters instead
        // (nanoseconds). "Render Thread" lives under different categories across
        // Unity versions, so probe both and use whichever reports valid.
        _rMainThread    = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread");
        _rRenderThread  = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Render Thread");
        _rRenderThread2 = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Render Thread");
        // Batching breakdown: how many of the draw calls are actually merged. If
        // these are ~0 while Draw Calls ~= SetPass, nothing is batching and there's
        // a real opportunity; if draws are already batched, there is not.
        _rDynBatch    = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Dynamic Batched Draw Calls Count");
        _rStaticBatch = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Static Batched Draw Calls Count");
        _rInstBatch   = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Instanced Batched Draw Calls Count");
    }

    void OnDestroy()
    {
        _samplerStop = true;
        _sampleWake.Set();
        _rDraw.Dispose(); _rSetPass.Dispose(); _rBatches.Dispose();
        _rTris.Dispose(); _rVerts.Dispose(); _rGcAlloc.Dispose();
        _rMainThread.Dispose(); _rRenderThread.Dispose(); _rRenderThread2.Dispose();
        _rDynBatch.Dispose(); _rStaticBatch.Dispose(); _rInstBatch.Dispose();
        if (_logToFile && !_summaryFlushed) { _summaryFlushed = true; FlushSummary(); }
    }

    // App backgrounded (home / recents / power button) is the reliable
    // "session end" hook on Android — OnDestroy isn't guaranteed before a
    // process kill. Rows are already durable (AutoFlush); this just appends
    // the summary line. Rows keep accumulating if the app resumes.
    void OnApplicationPause(bool paused)
    {
        if (paused && _logToFile && _csv != null && !_summaryFlushed)
        {
            _summaryFlushed = true;
            FlushSummary();
        }
    }

    void Update()
    {
        // Must be called once per frame for FrameTimingManager to advance its
        // ring buffer; the values are read in Refresh().
        FrameTimingManager.CaptureFrameTimings();
        _frameAccum++;
        float dtMs = Time.unscaledDeltaTime * 1000f;
        _timeAccum += Time.unscaledDeltaTime;
        // Record per-frame ms for percentiles + %frames-at-cap (logging only).
        if (_logToFile && _frameMsCount < _frameMs.Length)
            _frameMs[_frameMsCount++] = dtMs;
        if (_timeAccum >= REFRESH_INTERVAL)
            Refresh();
    }

    void Refresh()
    {
        float fps = _frameAccum / _timeAccum;
        float frameMs = (_timeAccum / _frameAccum) * 1000f;
        _frameAccum = 0;
        _timeAccum = 0f;

        // Frame-time distribution over the window (logging only). p99 + max
        // surface stutter the 1Hz average hides; pct_at_cap answers the core
        // question "did we actually hold the target this second?".
        float msP50 = 0f, msP99 = 0f, msMax = 0f, pctCap = 0f;
        if (_logToFile && _frameMsCount > 0)
        {
            int n = _frameMsCount;
            Array.Copy(_frameMs, _sortScratch, n);
            Array.Sort(_sortScratch, 0, n);
            msP50 = _sortScratch[(int)(0.50f * (n - 1))];
            msP99 = _sortScratch[Mathf.Clamp((int)Mathf.Ceil(0.99f * n) - 1, 0, n - 1)];
            msMax = _sortScratch[n - 1];
            float cap = _targetMs * 1.05f;
            int hit = 0;
            for (int i = 0; i < n; i++) if (_frameMs[i] <= cap) hit++;
            pctCap = 100f * hit / n;
        }
        _frameMsCount = 0;

        var sb = new StringBuilder(192);
        sb.Append("FPS ").Append(fps.ToString("F0")).Append("  (")
          .Append(frameMs.ToString("F1")).Append(" ms)\n");

        AppendFrameBreakdown(sb);

        Sample s;
        lock (_sampleLock) s = _sample;
        _sampleWake.Set();

        if (s.Cpu >= 0f)
        {
            sb.Append("CPU ").Append(s.Cpu.ToString("F0")).Append(" %");
            if (s.CoreMax >= 0f)
                sb.Append("  (peak ").Append(s.CoreMax.ToString("F0")).Append("%)");
            sb.Append('\n');
        }

        // Always emit the GPU line — even when we can't read a
        // number — so the overlay's layout stays stable across
        // frames and the user can tell "unavailable" from "0%".
        sb.Append("GPU ");
        if (s.Gpu >= 0f) sb.Append(s.Gpu.ToString("F0")).Append(" %");
        else sb.Append("??");

        // GPU clock: cur / max in MHz. Lets the user see whether
        // DCVS is holding the GPU below its top P-state (low cur
        // while frames drop → DCVS heuristic mismatch) vs whether
        // it's saturated (cur ≈ max and still dropping frames →
        // genuinely GPU-bound). This is the readout we'll watch when
        // A/B-testing a GPU max-frequency cap.
        if (s.CurHz > 0)
        {
            sb.Append("  ");
            sb.Append((s.CurHz / 1_000_000)).Append('/');
            if (_gpuMaxHz > 0) sb.Append((_gpuMaxHz / 1_000_000)).Append(" MHz");
            else sb.Append("? MHz");
        }
        sb.Append('\n');

        long totalMem = Profiler.GetTotalReservedMemoryLong();
        long usedMem = Profiler.GetTotalAllocatedMemoryLong();
        sb.Append("RAM ").Append(usedMem / (1024 * 1024)).Append(" / ")
          .Append(totalMem / (1024 * 1024)).Append(" MB\n");

        if (_batteryManager != null)
        {
            if (s.BattError != null)
                sb.Append("BAT ?? (").Append(s.BattError).Append(")");
            else if (s.BattLevel < 0)
                sb.Append("BAT ??");
            else
            {
                string dir = s.CurrentUa <= 0 ? "↓" : "↑";
                sb.Append("BAT ").Append(s.BattLevel).Append("%  ").Append(dir);
                if (s.VoltageV > 0f) sb.Append(s.PowerW.ToString("F2")).Append(" W");
                else sb.Append((Math.Abs(s.CurrentUa) / 1000f).ToString("F0")).Append(" mA");
            }
        }

        // Only materialise the display string when we'll actually draw it —
        // saves a per-second allocation in log-only mode.
        if (_drawOverlay) _displayText = sb.ToString();

        if (_logToFile && _csv != null)
            WriteCsvRow(fps, frameMs, msP50, msP99, msMax, pctCap, s.Cpu, s.CoreMax, s.Gpu, s.CurHz,
                        usedMem / (1024 * 1024), s.BattLevel, s.BattTempC,
                        s.VoltageV, s.CurrentUa, s.PowerW);
    }

    // Appends the per-frame breakdown to the overlay text and, on benchmark
    // runs (perf_log), logs the same to logcat (tag [PerfBreakdown]) so
    // trends can be pulled offline without watching the screen. Called once
    // per refresh tick (1 Hz).
    void AppendFrameBreakdown(StringBuilder sb)
    {
        // Prefer FrameTimingManager (gives GPU ms too) but it reports nothing on
        // this Vulkan/Turnip setup, so fall back to the System timing counters for
        // the CPU main / render thread split (nanoseconds -> ms).
        uint count = 0;
        double mainMs = 0, rndMs = 0, gpuMs = 0;
        try
        {
            count = FrameTimingManager.GetLatestTimings(1, _frameTimings);
            if (count > 0)
            {
                mainMs = _frameTimings[0].cpuMainThreadFrameTime;
                rndMs = _frameTimings[0].cpuRenderThreadFrameTime;
                gpuMs = _frameTimings[0].gpuFrameTime;
            }
        }
        catch { }

        if (count == 0)
        {
            if (_rMainThread.Valid) mainMs = _rMainThread.LastValue * 1e-6;
            if (_rRenderThread.Valid) rndMs = _rRenderThread.LastValue * 1e-6;
            else if (_rRenderThread2.Valid) rndMs = _rRenderThread2.LastValue * 1e-6;
        }

        _lastMainMs = mainMs; _lastRndMs = rndMs; _lastGpuMs = gpuMs;
        sb.Append("MAIN ").Append(mainMs.ToString("F1"))
          .Append("  RND ").Append(rndMs.ToString("F1"));
        if (gpuMs > 0) sb.Append("  GPU ").Append(gpuMs.ToString("F1"));
        sb.Append(" ms\n");

        long dc = _rDraw.Valid ? _rDraw.LastValue : -1;
        long sp = _rSetPass.Valid ? _rSetPass.LastValue : -1;
        long bt = _rBatches.Valid ? _rBatches.LastValue : -1;
        long tris = _rTris.Valid ? _rTris.LastValue : -1;
        long verts = _rVerts.Valid ? _rVerts.LastValue : -1;
        long gc = _rGcAlloc.Valid ? _rGcAlloc.LastValue : -1;
        _lastDc = dc; _lastSp = sp; _lastBt = bt;

        if (dc >= 0 || sp >= 0 || bt >= 0)
            sb.Append("DC ").Append(F(dc)).Append("  SP ").Append(F(sp))
              .Append("  BT ").Append(F(bt)).Append('\n');
        if (tris >= 0 || verts >= 0)
            sb.Append("TRI ").Append(Kfmt(tris)).Append("  VRT ").Append(Kfmt(verts)).Append('\n');
        if (gc >= 0)
            sb.Append("GCa ").Append((gc / 1024f).ToString("F1")).Append(" KB/f\n");

        if (!_loggedRecorderAvailability)
        {
            _loggedRecorderAvailability = true;
            Debug.Log("[PerfOverlay] frameTimings=" + count +
                      " recorders: draw=" + _rDraw.Valid + " setpass=" + _rSetPass.Valid +
                      " batches=" + _rBatches.Valid + " tris=" + _rTris.Valid +
                      " verts=" + _rVerts.Valid + " gc=" + _rGcAlloc.Valid +
                      " mainThread=" + _rMainThread.Valid +
                      " renderThread=" + _rRenderThread.Valid + "/" + _rRenderThread2.Valid +
                      " dynB=" + _rDynBatch.Valid + " statB=" + _rStaticBatch.Valid + " instB=" + _rInstBatch.Valid);
        }
        // Debug.Log captures a stack trace in this player, which is not free
        // once a second on the main thread; only benchmark runs want it.
        if (_logToFile)
            Debug.Log("[PerfBreakdown] main=" + mainMs.ToString("F1") + " rnd=" + rndMs.ToString("F1") +
                  " gpu=" + gpuMs.ToString("F1") + " ms | DC=" + dc + " SP=" + sp + " BT=" + bt +
                  " | dynB=" + (_rDynBatch.Valid ? _rDynBatch.LastValue : -1) +
                  " statB=" + (_rStaticBatch.Valid ? _rStaticBatch.LastValue : -1) +
                  " instB=" + (_rInstBatch.Valid ? _rInstBatch.LastValue : -1) +
                  " | tris=" + tris + " verts=" + verts +
                  " GCa=" + (gc >= 0 ? (gc / 1024f).ToString("F1") : "?") + "KB");
    }

    static string F(long v) => v < 0 ? "?" : v.ToString();

    static string Kfmt(long v)
    {
        if (v < 0) return "?";
        if (v >= 1_000_000) return (v / 1_000_000f).ToString("F1") + "M";
        if (v >= 1_000) return (v / 1_000f).ToString("F0") + "k";
        return v.ToString();
    }

    void OnGUI()
    {
        if (!_drawOverlay) return;
        if (string.IsNullOrEmpty(_displayText)) return;
        EnsureStyle();
        if (!ReferenceEquals(_measuredText, _displayText))
        {
            _measuredText = _displayText;
            _content.text = _displayText;
            _measuredSize = _style.CalcSize(_content);
        }
        // Anchor top-right so the overlay sits away from the
        // health/geo HUD that lives top-left in Silksong's UI.
        const int pad = 8;
        var size = _measuredSize;
        float w = size.x + pad * 2;
        float h = size.y + pad * 2;
        var rect = new Rect(Screen.width - w - pad, pad, w, h);
        GUI.DrawTexture(rect, _bgTexture);
        GUI.Label(new Rect(rect.x + pad, rect.y + pad, size.x, size.y), _content, _style);
    }

    void EnsureStyle()
    {
        if (_style != null) return;
        _style = new GUIStyle(GUI.skin.label)
        {
            fontSize = 16,
            normal = { textColor = new Color(0.95f, 0.95f, 0.95f, 1f) },
            wordWrap = false,
            richText = false,
        };
        _bgTexture = new Texture2D(1, 1);
        _bgTexture.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.55f));
        _bgTexture.Apply();
    }

    // /proc/stat first line: "cpu user nice system idle iowait irq
    // softirq steal guest guest_nice" in jiffies since boot, summed
    // across all cores. Active = total - idle.
    void SampleCpuOnce()
    {
        try
        {
            ReadCpu(out _prevCpuActive, out _prevCpuTotal);
        }
        catch
        {
            // /proc/stat unreadable (SELinux on some OEM images);
            // SampleCpu will keep returning -1.
        }
    }

    float SampleCpu()
    {
        try
        {
            ReadCpu(out long active, out long total);
            long da = active - _prevCpuActive;
            long dt = total - _prevCpuTotal;
            _prevCpuActive = active;
            _prevCpuTotal = total;
            if (dt <= 0) return -1f;
            return (100f * da) / dt;
        }
        catch
        {
            return -1f;
        }
    }

    static void ReadCpu(out long active, out long total)
    {
        active = 0; total = 0;
        using var sr = new StreamReader("/proc/stat");
        string line = sr.ReadLine();
        if (string.IsNullOrEmpty(line) || !line.StartsWith("cpu ")) return;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // parts[0] == "cpu", parts[1..] are the per-state counters.
        long idle = 0;
        for (int i = 1; i < parts.Length; i++)
        {
            long v = long.Parse(parts[i]);
            total += v;
            if (i == 4 || i == 5) idle += v; // idle + iowait
        }
        active = total - idle;
    }

    // Returns the busiest single CPU core's utilization since the
    // last SampleMaxCore call (or -1 if /proc/stat can't be parsed).
    // Reads every "cpuN " line, computes delta(active)/delta(total)
    // for each, returns the max. We cache prev jiffies arrays so
    // back-to-back calls give per-interval values.
    float SampleMaxCore()
    {
        try
        {
            // First-run init: enumerate cores by reading /proc/stat.
            // CPU set is fixed per boot; sizing once is fine.
            if (_prevCoreActive == null)
            {
                int n = 0;
                using (var sr = new StreamReader("/proc/stat"))
                {
                    string l;
                    while ((l = sr.ReadLine()) != null)
                    {
                        if (l.StartsWith("cpu") && l.Length > 3 &&
                            char.IsDigit(l[3])) n++;
                    }
                }
                if (n == 0) return -1f;
                _prevCoreActive = new long[n];
                _prevCoreTotal  = new long[n];
                // Seed baselines so the first sample isn't bogus.
                ReadCoresInto(_prevCoreActive, _prevCoreTotal);
                return -1f;
            }

            int cores = _prevCoreActive.Length;
            var curActive = new long[cores];
            var curTotal  = new long[cores];
            ReadCoresInto(curActive, curTotal);

            float maxPct = -1f;
            for (int i = 0; i < cores; i++)
            {
                long da = curActive[i] - _prevCoreActive[i];
                long dt = curTotal[i]  - _prevCoreTotal[i];
                _prevCoreActive[i] = curActive[i];
                _prevCoreTotal[i]  = curTotal[i];
                if (dt <= 0) continue;
                float p = (100f * da) / dt;
                if (p > maxPct) maxPct = p;
            }
            return maxPct;
        }
        catch
        {
            return -1f;
        }
    }

    static void ReadCoresInto(long[] active, long[] total)
    {
        using var sr = new StreamReader("/proc/stat");
        string line;
        while ((line = sr.ReadLine()) != null)
        {
            if (!line.StartsWith("cpu") || line.Length < 4) continue;
            if (!char.IsDigit(line[3])) continue;
            int spaceIdx = line.IndexOf(' ', 3);
            if (spaceIdx < 0) continue;
            if (!int.TryParse(line.Substring(3, spaceIdx - 3), out int n)) continue;
            if (n < 0 || n >= active.Length) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long sum = 0, idle = 0;
            for (int i = 1; i < parts.Length; i++)
            {
                long v = long.Parse(parts[i]);
                sum += v;
                if (i == 4 || i == 5) idle += v;
            }
            total[n]  = sum;
            active[n] = sum - idle;
        }
    }

    float SampleGpu()
    {
        try
        {
            var text = File.ReadAllText(_gpuPath).Trim();
            switch (_gpuKind)
            {
                case GpuKind.Percent:
                    // "9 %" — take the digits before the space.
                    var sp = text.IndexOf(' ');
                    return float.Parse(sp > 0 ? text.Substring(0, sp) : text);
                case GpuKind.Devfreq:
                    // Plain integer 0-100.
                    return float.Parse(text);
                case GpuKind.Gpubusy:
                    // "<active> <total>" — RESET on read, so a
                    // single read's active/total is already the
                    // fraction for the interval since the previous
                    // read. No deltas.
                    var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) return -1f;
                    long active = long.Parse(parts[0]);
                    long total = long.Parse(parts[1]);
                    if (total <= 0) return -1f;
                    return Mathf.Clamp((100f * active) / total, 0f, 100f);
                default:
                    return -1f;
            }
        }
        catch
        {
            // One-off read error — keep the kind/path resolved so
            // we retry next tick. Permanent SELinux denials would
            // show up as repeated "??" entries, which the user can
            // act on; flipping to None forever on a single
            // transient failure would hide a recoverable
            // condition.
            return -1f;
        }
    }

    // Reads cell voltage in volts. Returns 0 (treated as "unknown")
    // if the sysfs node is unreadable — some OEMs hide it behind
    // SELinux. Most Snapdragon devices expose it freely.
    static float ReadBatteryVoltageVolts()
    {
        try
        {
            var text = File.ReadAllText(BATTERY_VOLTAGE_PATH).Trim();
            // voltage_now is in µV (microvolts) on the standard
            // power_supply node convention.
            return long.Parse(text) / 1e6f;
        }
        catch
        {
            return 0f;
        }
    }

    // ── CSV logging implementation ────────────────────────────────────────

    void OpenLog()
    {
        try
        {
            string dir = Path.Combine(Application.persistentDataPath, "perf");
            Directory.CreateDirectory(dir);
            _csvPath = Path.Combine(dir, "perf_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
            _csv = new StreamWriter(_csvPath, append: false) { AutoFlush = true };

            // Resolve the frame-time cap from the actual target rate so
            // pct_at_cap is meaningful whatever the panel/cap is.
            int tf = Application.targetFrameRate;
            if (tf <= 0)
                tf = (int)Mathf.Round((float)Screen.currentResolution.refreshRateRatio.value);
            if (tf <= 0) tf = 120;
            _targetMs = 1000f / tf;

            WriteConfigStamp(tf);
            _csv.WriteLine(
                "t_s,scene,fps,ms_avg,ms_p50,ms_p99,ms_max,pct_at_cap," +
                "main_ms,rnd_ms,gpu_ms,cpu_pct,cpu_peak_pct,gpu_pct,gpu_mhz," +
                "dc,sp,bt,ram_mb,batt_pct,batt_temp_c,volt_v,curr_ma,watts,mj_per_frame");
            Debug.Log("[PerfOverlay] logging to " + _csvPath);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PerfOverlay] couldn't open CSV — logging disabled: " + ex.Message);
            _logToFile = false;
            _csv = null;
        }
    }

    // Self-describing header so each file is attributable to one lever config
    // without an external notebook. '#' lines are comments; spreadsheet/pandas
    // importers skip them.
    void WriteConfigStamp(int targetFps)
    {
        var c = _csv;
        c.WriteLine("# Silksong perf log " + DateTime.Now.ToString("o"));
        c.WriteLine("# app=" + Application.version + " unity=" + Application.unityVersion);
        c.WriteLine("# device=" + SystemInfo.deviceModel + " soc=" + SystemInfo.processorType +
                    " mem=" + SystemInfo.systemMemorySize + "MB");
        c.WriteLine("# gfx=" + SystemInfo.graphicsDeviceName + " | " + SystemInfo.graphicsDeviceVersion +
                    " | type=" + SystemInfo.graphicsDeviceType + " mt=" + SystemInfo.graphicsMultiThreaded);
        var res = Screen.currentResolution;
        c.WriteLine("# panel=" + res.width + "x" + res.height + "@" +
                    ((float)res.refreshRateRatio.value).ToString("F0") + "Hz" +
                    " render=" + Screen.width + "x" + Screen.height);
        c.WriteLine("# targetFps=" + targetFps + " vSyncCount=" + QualitySettings.vSyncCount +
                    " qualityLevel=" + QualitySettings.GetQualityLevel() +
                    " antiAliasing=" + QualitySettings.antiAliasing +
                    " gpuMaxMHz=" + (_gpuMaxHz > 0 ? (_gpuMaxHz / 1_000_000).ToString() : "?"));
        c.WriteLine("# driver=" + (ReadPref("vulkan_driver_source") ?? "system") +
                    " overlay=" + s_drawOverlay + " log=" + s_logToFile);
        c.WriteLine("# pct_at_cap = % of frames with frame_ms <= " +
                    (_targetMs * 1.05f).ToString("F2") + "ms (target " +
                    _targetMs.ToString("F2") + "ms +5%); mj_per_frame = watts*1000/fps (lower=better)");
    }

    void WriteCsvRow(float fps, float msAvg, float msP50, float msP99, float msMax, float pctCap,
                     float cpu, float coreMax, float gpu, long curHz, long usedMemMb,
                     int battLevel, float battTempC, float voltageV, long currentUa, float powerW)
    {
        if (_logStartTime <= 0f) _logStartTime = Time.realtimeSinceStartup;
        float t = Time.realtimeSinceStartup - _logStartTime;
        float mj = fps > 0f ? powerW * 1000f / fps : 0f;

        var sb = new StringBuilder(176);
        sb.Append(t.ToString("F1")).Append(',')
          .Append(Csv(SceneManager.GetActiveScene().name)).Append(',')
          .Append(fps.ToString("F1")).Append(',')
          .Append(msAvg.ToString("F2")).Append(',').Append(msP50.ToString("F2")).Append(',')
          .Append(msP99.ToString("F2")).Append(',').Append(msMax.ToString("F2")).Append(',')
          .Append(pctCap.ToString("F1")).Append(',')
          .Append(_lastMainMs.ToString("F2")).Append(',').Append(_lastRndMs.ToString("F2")).Append(',')
          .Append(_lastGpuMs.ToString("F2")).Append(',')
          .Append(cpu >= 0f ? cpu.ToString("F1") : "").Append(',')
          .Append(coreMax >= 0f ? coreMax.ToString("F1") : "").Append(',')
          .Append(gpu >= 0f ? gpu.ToString("F1") : "").Append(',')
          .Append(curHz > 0 ? (curHz / 1_000_000).ToString() : "").Append(',')
          .Append(_lastDc).Append(',').Append(_lastSp).Append(',').Append(_lastBt).Append(',')
          .Append(usedMemMb).Append(',')
          .Append(battLevel >= 0 ? battLevel.ToString() : "").Append(',')
          .Append(float.IsNaN(battTempC) ? "" : battTempC.ToString("F1")).Append(',')
          .Append(voltageV > 0f ? voltageV.ToString("F3") : "").Append(',')
          .Append((currentUa / 1000f).ToString("F0")).Append(',')
          .Append(powerW > 0f ? powerW.ToString("F3") : "").Append(',')
          .Append(mj > 0f ? mj.ToString("F3") : "");

        try { _csv.WriteLine(sb.ToString()); }
        catch (Exception ex) { Debug.LogWarning("[PerfOverlay] CSV write failed: " + ex.Message); }

        _sumRows++;
        _sumFps += fps; _sumWatts += powerW; _sumMj += mj; _sumPctCap += pctCap;
        _sumP99 += msP99; if (msP99 > _worstP99) _worstP99 = msP99;
        if (fps < _minFps) _minFps = fps;
    }

    void FlushSummary()
    {
        if (_csv == null) return;
        try
        {
            if (_sumRows > 0)
            {
                string line = "# SUMMARY rows=" + _sumRows +
                    " fps_avg=" + (_sumFps / _sumRows).ToString("F1") +
                    " fps_min=" + (_minFps >= float.MaxValue ? 0f : _minFps).ToString("F1") +
                    " watts_avg=" + (_sumWatts / _sumRows).ToString("F3") +
                    " mj_per_frame_avg=" + (_sumMj / _sumRows).ToString("F3") +
                    " pct_at_cap_avg=" + (_sumPctCap / _sumRows).ToString("F1") +
                    " p99_avg=" + (_sumP99 / _sumRows).ToString("F2") +
                    " p99_worst=" + _worstP99.ToString("F2");
                _csv.WriteLine(line);
                Debug.Log("[PerfOverlay] " + line + " -> " + _csvPath);
            }
            _csv.Flush();
            _csv.Dispose();
        }
        catch { }
        _csv = null;
    }

    // Quote a CSV field only if it contains a comma/quote (scene names are
    // normally bare identifiers, so this is almost always a no-op).
    static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0)
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    // Battery temperature in °C (sysfs reports tenths). NaN when unreadable.
    static float ReadBatteryTempC()
    {
        try { return long.Parse(File.ReadAllText(BATTERY_TEMP_PATH).Trim()) / 10f; }
        catch { return float.NaN; }
    }

    // One-shot SharedPreferences string read via JNI (config-stamp only, not
    // per frame). Same cross-process prefs file the launcher writes.
    static string ReadPref(string key)
    {
        // See Settings.cs.
        return SilksongPatches.Settings.GetString(key, null);
    }

    // Reads an integer from a sysfs file (Hz for the GPU clock nodes).
    // Returns -1 if the file is missing / unreadable / non-integer.
    // Some Adreno builds put the value on a single line with a
    // trailing newline; some on multiple lines. Trim handles both.
    static long TryReadHz(string path)
    {
        try
        {
            if (!File.Exists(path)) return -1;
            var text = File.ReadAllText(path).Trim();
            // devfreq cur_freq is sometimes "12345 governor_name" —
            // take only the leading integer.
            int space = text.IndexOf(' ');
            if (space >= 0) text = text.Substring(0, space);
            return long.Parse(text);
        }
        catch
        {
            return -1;
        }
    }
}
#endif
