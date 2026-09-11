// DsPresentation — the second screen itself: one display, one camera, one
// canvas, and the discipline that keeps all three from touching the game.
//
// This replaces the whole of V1's transport. There is no RenderTexture, no
// AsyncGPUReadback or shared memory. Unity presents to the panel
// directly, measured at no cost to the main screen: 120.2 fps with p99 and max
// present intervals both equal to the 8.33 ms refresh and zero missed vsyncs
// over 253 frames, with a camera and canvas live on display 1.
//
// Three things about Unity's Android multi-display are not obvious, and each
// one cost a debugging session during M0:
//
//   * Activate() is ASYNCHRONOUS. Rendering to the display a few frames after
//     it returns took the graphics thread down with SIGBUS (BUS_ADRALN).
//     Waiting about a second first has been reliable.
//
//   * There is NO readiness flag to wait on instead. Display.renderingWidth
//     and renderingHeight read 0x0 forever on this platform, even while Unity
//     is demonstrably rendering to the panel at 1240x1080. A guard that waited
//     for them left the panel black. systemWidth/Height do eventually populate,
//     but only AFTER rendering starts, so they cannot be waited on either.
//
//   * Size and input come from the actual Android SurfaceView. Display metrics
//     can include system bars that are outside that view, and guessed heights
//     shift every hit target by the difference.
//
// Isolation from the game is enforced at both ends. Our camera renders exactly
// one layer, and that layer is cleared from every other camera's mask -- on a
// slow sweep, because Unity has no "a camera was created" event and the game
// spawns cameras for cutscenes and bosses. The reverse direction (the game
// drawing our layer) is the one that would be visible to the player, so it is
// the one that gets swept.

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class DsPresentation
{
    /// <summary>Unity's index for the second panel.</summary>
    public const int DISPLAY = 1;

    // Layers 3 and 6 are unnamed in the game's TagManager, so 6 is ours. The
    // game renders its HUD and menus on layer 5 ("UI") and its world on the
    // rest; nothing of the game's is ever moved here. DsHudView separately
    // borrows layer 3 only within individual camera render callbacks.
    public const int LAYER = 6;

    public Camera Camera { get; private set; }
    public Canvas Canvas { get; private set; }
    /// <summary>Where screens build their UI. Fills the panel.</summary>
    public RectTransform Root { get; private set; }
    public bool Ready { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>
    /// The input bridge's reference size, latched at bringup. The canvas may
    /// have different logical dimensions; frame geometry uses LayoutSize.
    /// </summary>
    public static int PanelW { get; private set; }
    public static int PanelH { get; private set; }
    static RectTransform _uiRoot;

    public static Vector2 LayoutSize => _uiRoot != null ? _uiRoot.rect.size
        : new Vector2(PanelW > 0 ? PanelW : 1240, PanelH > 0 ? PanelH : 1080);

    public static Vector2 FromSurface(Vector2 normalized)
    {
        return DsTouch.MapToCanvas(normalized, _uiRoot.rect.size, PanelH);
    }

    /// <summary>Panel touch point -> layout point (origin top-left, y down).</summary>
    public static Vector2 ToLayout(Vector2 panelPoint)
    {
        return new Vector2(panelPoint.x, PanelH - panelPoint.y);
    }

    /// <summary>
    /// The render camera. Input deliberately does not use screen-point
    /// projection: Unity's secondary-display pixel dimensions can be zero.
    /// </summary>
    public static Camera UiCamera { get; private set; }

    readonly Transform _parent;
    CanvasScaler _scaler;
    float _nextSweep;

    public DsPresentation(Transform parent) { _parent = parent; }

    /// <summary>
    /// Bring up the display and build the rig. Yields until the panel is live.
    /// </summary>
    public IEnumerator Bringup(Func<bool> canRender)
    {
        var displays = Display.displays;
        if (displays.Length <= DISPLAY)
        {
            // Not an error: a device with one screen is a device with one
            // screen, and everything here simply does not run.
            Debug.Log("[DualScreen] only " + displays.Length + " display(s); second screen not available");
            yield break;
        }

        if (!DsTouch.Begin()) yield break;
        try { displays[DISPLAY].Activate(); }
        catch (Exception e)
        {
            Debug.LogError("[DualScreen] could not activate display " + DISPLAY + ": " + e);
            DsTouch.Stop();
            yield break;
        }

        // Unity creates the Android presentation lazily, on the first render.
        // Warm up the display with a blank camera after the usual settling
        // period; waiting for a SurfaceView before rendering deadlocks startup.
        // The UI is built only after that surface's input has been captured.
        float settle = Mathf.Max(0, DsConfig.Int("settle_ms", 1500)) / 1000f;
        float elapsed = 0f, waited = 0f;
        while (true)
        {
            if (!canRender())
            {
                Suspend();
                elapsed = waited = 0f;
            }
            else if (elapsed < settle)
            {
                elapsed += Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            }
            else
            {
                if (Camera == null) BuildCamera();
                Camera.enabled = true;
                if (DsTouch.Ready) break;
                waited += Mathf.Min(Time.unscaledDeltaTime, 0.1f);
                if (waited > 5f)
                {
                    Debug.LogError("[DualScreen] secondary surface/input did not become ready");
                    Suspend();
                    DsTouch.Stop();
                    yield break;
                }
            }
            yield return null;
        }

        MeasurePanel();

        if (Canvas == null)
        {
            BuildCanvas();
        }
        else
        {
            // Re-acquiring a panel we have already built for -- an unplug and
            // replug. Build() must NOT run twice: it creates the camera and the
            // canvas outright, so a second call leaves two of each, both
            // drawing, and the screens' UI attached to the orphaned one.
            // Keeping the rig also keeps everything built on it.
            if (_scaler != null) _scaler.referenceResolution = new Vector2(Width, Height);
            SweepCameras(force: true);
        }

        UnityEngine.Canvas.ForceUpdateCanvases();
        Ready = true;

        Debug.Log("[DualScreen] second screen up: " + Width + "x" + Height +
                  " (" + ((float)Width / Height).ToString("F2") + ":1)" +
                  " canvas=" + Canvas.renderMode + " rect=" + Root.rect.size +
                  " scale=" + Canvas.scaleFactor + " layer=" + LAYER);
    }

    void MeasurePanel()
    {
        Vector2 size = DsTouch.SurfaceSize;
        int w = (int)size.x, h = (int)size.y;
        Width = w;
        Height = h;
        PanelW = w;
        PanelH = h;
    }

    void BuildCamera()
    {
        var camGo = new GameObject("DsCamera");
        camGo.transform.SetParent(_parent, false);
        camGo.layer = LAYER;
        Camera = camGo.AddComponent<Camera>();
        Camera.targetDisplay = DISPLAY;
        Camera.clearFlags = CameraClearFlags.SolidColor;
        Camera.backgroundColor = Color.black;
        Camera.cullingMask = 1 << LAYER;    // ours and only ours
        Camera.orthographic = true;
        Camera.nearClipPlane = -100f;
        Camera.farClipPlane = 100f;
        Camera.allowHDR = false;
        Camera.allowMSAA = false;
        Camera.useOcclusionCulling = false;
        Camera.depth = -50f;
    }

    void BuildCanvas()
    {
        var canvasGo = new GameObject("DsCanvas");
        canvasGo.transform.SetParent(_parent, false);
        canvasGo.layer = LAYER;
        Canvas = canvasGo.AddComponent<Canvas>();
        Canvas.targetDisplay = DISPLAY;

        // ScreenSpaceCamera by default rather than Overlay, because the map
        // screen will need world-space content composited with the UI and only
        // a camera-rendered canvas can do that. Overlay is kept one flag away
        // in case a device disagrees.
        if (DsConfig.Str("canvasmode", "camera") == "overlay")
        {
            Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            Canvas.targetDisplay = DISPLAY;
            UiCamera = null;            // overlay canvases hit-test against null
        }
        else
        {
            Canvas.renderMode = RenderMode.ScreenSpaceCamera;
            Canvas.worldCamera = Camera;
            Canvas.planeDistance = 1f;
            UiCamera = Camera;
        }

        // Authored at the panel's own size, so a different second screen scales
        // rather than clips. matchWidthOrHeight 0.5 splits the difference on a
        // panel with a different aspect.
        var scaler = canvasGo.AddComponent<DsPanelScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(Width, Height);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        _scaler = scaler;

        // No GraphicRaycaster and no EventSystem: we own every rect on this
        // screen and hit-test them ourselves, so a raycaster would only add a
        // way to collide with the game's event system.

        var rootGo = new GameObject("DsRoot");
        rootGo.layer = LAYER;
        Root = rootGo.AddComponent<RectTransform>();
        _uiRoot = Root;
        Root.SetParent(canvasGo.transform, false);
        Root.anchorMin = Vector2.zero;
        Root.anchorMax = Vector2.one;
        Root.offsetMin = Vector2.zero;
        Root.offsetMax = Vector2.zero;

        SweepCameras(force: true);
    }

    /// <summary>
    /// Keep our layer off every other camera.
    ///
    /// The game hard-assigns culling masks in places -- GameCameras.MoveMenuToHUDCamera
    /// sets hudCamera.cullingMask = 32 outright, on a delayed Invoke -- and
    /// spawns cameras during cutscenes and boss fights. Since Unity offers no
    /// notification when a Camera appears, this runs on a slow sweep. A camera
    /// created between two sweeps could show our layer for a frame; that is the
    /// residual risk, and it is why the sweep is cheap enough to run often.
    /// </summary>
    public void SweepCameras(bool force = false)
    {
        if (!force && Time.unscaledTime < _nextSweep) return;
        _nextSweep = Time.unscaledTime + DsConfig.Int("sweep_ms", 500) / 1000f;

        // Camera.allCameras allocates a fresh array on every read; the
        // count-plus-buffer form does not, and this runs forever.
        int count = Camera.allCamerasCount;
        if (count <= 0) return;
        if (_camBuf == null || _camBuf.Length < count) _camBuf = new Camera[count + 4];
        Camera.GetAllCameras(_camBuf);

        int mask = 1 << LAYER;
        for (int i = 0; i < count; i++)
        {
            var c = _camBuf[i];
            if (c == null || c == Camera) continue;
            if ((c.cullingMask & mask) == 0) continue;
            c.cullingMask &= ~mask;
            Debug.Log("[DualScreen] cleared layer " + LAYER + " from camera '" + c.name + "'");
        }
    }

    Camera[] _camBuf;

    /// <summary>Stop drawing without tearing the rig down (pause, panel lost).</summary>
    public void SetVisible(bool visible)
    {
        if (Camera != null && Camera.enabled != visible) Camera.enabled = visible;
        if (Canvas != null && Canvas.enabled != visible) Canvas.enabled = visible;
    }

    public void Suspend()
    {
        SetVisible(false);
        Ready = false;
    }

    public void Destroy()
    {
        if (Canvas != null) UnityEngine.Object.Destroy(Canvas.gameObject);
        if (Camera != null) UnityEngine.Object.Destroy(Camera.gameObject);
        Canvas = null; Camera = null; Root = null;
        _uiRoot = null;
        UiCamera = null;
        Ready = false;
    }
}

// Stock CanvasScaler replaces renderingDisplaySize with Display.renderingWidth/
// Height for secondary canvases, even when those are both zero on Android.
public class DsPanelScaler : CanvasScaler
{
    protected override void HandleScaleWithScreenSize()
    {
        Vector2 size = DsTouch.SurfaceSize;
        if (size.x <= 0f || size.y <= 0f) return; // surface not attached yet
        SetScaleFactor(Mathf.Sqrt(size.x / referenceResolution.x * size.y / referenceResolution.y));
        SetReferencePixelsPerUnit(referencePixelsPerUnit);
    }
}
#endif
