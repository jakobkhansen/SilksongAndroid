#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using TmpText = TMProOld.TextMeshProUGUI;

public sealed class DsHudView : MonoBehaviour
{
    readonly List<Transform> _roots = new List<Transform>();
    readonly List<Renderer> _renderers = new List<Renderer>();
    readonly List<Renderer> _scratch = new List<Renderer>();
    readonly List<GameObject> _targets = new List<GameObject>();
    readonly List<PlayMakerFSM> _healthFsms = new List<PlayMakerFSM>();
    readonly List<BlueHealth> _blueHealth = new List<BlueHealth>();
    readonly List<Vector3> _maskPositions = new List<Vector3>();
    readonly List<ToolHudIcon> _toolIcons = new List<ToolHudIcon>();
    readonly List<Canvas> _toolCanvases = new List<Canvas>();
    readonly List<Graphic> _toolGraphics = new List<Graphic>();
    readonly List<GameObject> _canvasTargets = new List<GameObject>();
    readonly DsHudRenderScope<GameObject> _scope = new DsHudRenderScope<GameObject>(
        go => go != null, go => go.layer, (go, layer) => go.layer = layer);
    readonly DsHudRenderScope<GameObject> _canvasScope = new DsHudRenderScope<GameObject>(
        go => go != null, go => go.layer, (go, layer) => go.layer = layer);
    Coroutine _canvasCleanup;

    Camera _capture, _scopedCamera;
    RenderTexture _texture;
    RawImage _image;
    TmpText _fallback;
    GameCameras _gameCameras;
    Transform _hudRoot, _health, _barParent, _capRAnchor, _tools;
    SilkSpool _spool;
    BindOrbHudFrame _bindFrame;
    Bounds _bounds;
    Matrix4x4 _worldToFrame;
    int _savedMask, _capturedFrame = -1, _hiddenFrame = -1;
    int _canvasFrame = -1;
    int _activeTools, _activeToolCanvases;
    bool _wanted, _failed, _stopped, _submitted, _showTop, _probed, _presented;
    float _zoom, _nextBind, _nextDiagnostic, _maskPixelPitch, _waitingSince = -1f;
    string _waitingReason;

    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly FieldInfo JitterActive = typeof(JitterSelf).GetField("isActive", PrivateInstance);
    static readonly FieldInfo JitterOrigin = typeof(JitterSelf).GetField("initialPosition", PrivateInstance);
    static readonly FieldInfo JitterTransform = typeof(JitterSelf).GetField("overrideTransform", PrivateInstance);

    public void Build(RectTransform host, float width, float height)
    {
        var rect = DsWidgets.Rect(host, "health");
        float w = Mathf.Min(width - DsTheme.Pad * 2f, width * 0.74f);
        // Tool charge rings sit below the silk row. Use the existing header's
        // bottom padding too, without changing the health scale or body bounds.
        float h = height - DsTheme.Pad;
        DsWidgets.Place(rect, DsTheme.Pad, DsTheme.Pad, w, h);
        _image = rect.gameObject.AddComponent<RawImage>();
        _image.raycastTarget = false;
        _image.color = Color.clear;
        _fallback = DsWidgets.Label(host, "health-status", "", DsTheme.SmallSize, DsTheme.InkDim);
        DsWidgets.Place(_fallback.rectTransform, DsTheme.Pad, DsTheme.Pad, w, h);
        DsWidgets.SetActive(_fallback, false);

        try
        {
            _showTop = DsConfig.Bool("hud_show_top",
                SilksongPatches.Settings.GetBool("dualscreen_show_top_hud", false));
            _zoom = DsConfig.Int("hud_zoom", 100) / 100f;
            if (_zoom < 0.5f || _zoom > 2f)
                throw new ArgumentOutOfRangeException("hud_zoom", "Use a percentage from 50 to 200");

            _texture = new RenderTexture(Mathf.CeilToInt(w), Mathf.CeilToInt(h), 24,
                                          RenderTextureFormat.Default);
            _texture.name = "DsHealthRT";
            _texture.antiAliasing = 1;
            _texture.filterMode = FilterMode.Bilinear;
            if (!_texture.Create()) throw new InvalidOperationException("Could not create the health render texture");
            _image.texture = _texture;

            var cameraObject = new GameObject("DsHealthCamera");
            cameraObject.transform.SetParent(host, false);
            _capture = cameraObject.AddComponent<Camera>();
            _capture.enabled = false;
            _capture.orthographic = true;
            _capture.targetTexture = _texture;
            _capture.cullingMask = DsHudRouting.CaptureMask;
            _capture.clearFlags = CameraClearFlags.SolidColor;
            _capture.backgroundColor = DsTheme.Ground;
            _capture.nearClipPlane = 0.01f;
            _capture.farClipPlane = 100f;
            _capture.depth = -100f;
            _capture.allowHDR = false;
            _capture.allowMSAA = false;
            _capture.useOcclusionCulling = false;
            Debug.Log("[DsHud] capture created; show-top=" + _showTop);
        }
        catch (Exception e) { Fail(e); }
    }

    void OnEnable()
    {
        if (_stopped) return;
        Camera.onPreCull += BeforeCamera;
        Camera.onPostRender += AfterCamera;
        _canvasCleanup = StartCoroutine(RestoreCanvasAtFrameEnd());
    }

    void OnDisable()
    {
        Camera.onPreCull -= BeforeCamera;
        Camera.onPostRender -= AfterCamera;
        StopCanvasCleanup();
        SetVisible(false);
    }

    public void SetVisible(bool visible)
    {
        _wanted = visible && !_stopped;
        if (!_wanted) Suspend();
    }

    public void Stop()
    {
        _stopped = true;
        _wanted = false;
        Camera.onPreCull -= BeforeCamera;
        Camera.onPostRender -= AfterCamera;
        StopCanvasCleanup();
        Suspend();
    }

    void StopCanvasCleanup()
    {
        if (_canvasCleanup == null) return;
        StopCoroutine(_canvasCleanup);
        _canvasCleanup = null;
    }

    IEnumerator RestoreCanvasAtFrameEnd()
    {
        var endOfFrame = new WaitForEndOfFrame();
        while (true)
        {
            yield return endOfFrame;
            try { _canvasScope.Restore(); }
            catch (Exception e) { Fail(e); }
        }
    }

    void Suspend()
    {
        if (_capture != null) _capture.enabled = false;
        _capturedFrame = -1;
        _submitted = false;
        _presented = false;
        if (_image != null) _image.color = Color.clear;
        try { RestoreAllScopes(); }
        catch (Exception e) { Fail(e); }
    }

    void LateUpdate()
    {
        try
        {
            if (_scope.Active)
                throw new InvalidOperationException("A camera did not finish its HUD render scope");
            _canvasScope.Restore();
            if (_failed || !_wanted || !DsTouch.Ready || !DsGameData.InGame)
            {
                Suspend();
                return;
            }
            if (!CanPresent)
            {
                Suspend();
                Waiting("Secondary canvas is not ready");
                return;
            }
            if (!TryBind())
            {
                Suspend();
                Waiting("Native health HUD is not available");
                return;
            }
            if (!NativeVisible)
            {
                Suspend();
                _waitingSince = -1f;
                DsWidgets.SetActive(_fallback, false);
                return;
            }
            if (!_texture.IsCreated())
            {
                _capturedFrame = -1;
                if (!_texture.Create()) throw new InvalidOperationException("Could not restore the health render texture");
            }
            // RawImage colour changes after a capture reach the canvas rebuild
            // on the next frame. Keep the top HUD during that first hand-off.
            _presented = DsHudRouting.HasPresentedFrame(_image.color.a > 0f, _capturedFrame, Time.frameCount);
            PrepareCanvasScope();
            _capture.enabled = true;
            Diagnostic();
        }
        catch (Exception e) { Fail(e); }
    }

    bool NativeVisible => _hudRoot != null && _hudRoot.gameObject.activeInHierarchy &&
                          _gameCameras != null && _gameCameras.IsHudVisible && !HudGlobalHide.IsHidden;

    bool CanPresent
    {
        get
        {
            if (!_wanted || !DsTouch.Ready || _image == null || !_image.gameObject.activeInHierarchy) return false;
            Vector2 size = DsPresentation.LayoutSize;
            if (!(size.x > 0f && size.y > 0f)) return false;
            var canvas = _image.canvas;
            if (canvas == null) return false;
            var camera = canvas.worldCamera;
            return DsHudRouting.CanvasCanPresent(canvas.isActiveAndEnabled, canvas.renderMode, canvas.targetDisplay,
                camera != null && camera.isActiveAndEnabled, camera != null ? camera.targetDisplay : -1,
                DsPresentation.DISPLAY);
        }
    }

    bool TryBind()
    {
        var cameras = GameCameras.SilentInstance;
        if (cameras != null && cameras == _gameCameras && _hudRoot != null &&
            _health != null && _spool != null && _bindFrame != null && _barParent != null &&
            _capRAnchor != null && _tools != null)
            return true;
        if (Time.unscaledTime < _nextBind) return false;
        _nextBind = Time.unscaledTime + 1f;
        _capturedFrame = -1;
        _roots.Clear();
        if (cameras == null || cameras.hudCanvasSlideOut == null || cameras.silkSpool == null) return false;

        var root = cameras.hudCanvasSlideOut.transform;
        var health = root.Find("Health");
        var spool = cameras.silkSpool;
        var frame = spool.GetComponentInChildren<BindOrbHudFrame>(true);
        var bar = spool.transform.Find("Thread Spool/Parent");
        var cap = spool.transform.Find("Thread Spool/Cap R Anchored");
        var tools = root.Find("Tool Icons");
        if (health == null || frame == null || bar == null || cap == null || tools == null ||
            !spool.transform.IsChildOf(root)) return false;

        if (!string.IsNullOrEmpty(LayerMask.LayerToName(DsHudRouting.CaptureLayer)))
            throw new InvalidOperationException("The health capture layer is already named by the game");
        foreach (var renderer in Resources.FindObjectsOfTypeAll<Renderer>())
        {
            if (renderer != null && renderer.gameObject.scene.IsValid() &&
                renderer.gameObject.layer == DsHudRouting.CaptureLayer)
                throw new InvalidOperationException("The health capture layer is in use by " + renderer.name);
        }
        foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>())
        {
            if (canvas != null && canvas.gameObject.scene.IsValid() &&
                canvas.gameObject.layer == DsHudRouting.CaptureLayer)
                throw new InvalidOperationException("The health capture layer is in use by canvas " + canvas.name);
        }

        _gameCameras = cameras;
        _hudRoot = root;
        _health = health;
        _spool = spool;
        _bindFrame = frame;
        _barParent = bar;
        _capRAnchor = cap;
        _tools = tools;
        _roots.Add(health);
        _roots.Add(spool.transform);
        _roots.Add(tools);
        foreach (string name in new[] {
            "Crest Get Effects", "Blue_Health_Overblue_HUD_burst", "Blue_Health_Overblue_HUD_drips",
        })
        {
            var effects = root.Find(name);
            if (effects != null) _roots.Add(effects);
        }
        Debug.Log("[DsHud] bound live Health, Spool and Tool Icons under '" + root.name + "'; " + CanvasState());
        if (!_probed && DsConfig.Bool("hud_probe", false))
        {
            _probed = true;
            DsProbe.DumpHud(_roots);
        }
        return true;
    }

    void BeforeCamera(Camera camera)
    {
        bool running = !_failed && CanPresent && _capture != null;
        bool canvasBatch = _canvasFrame == Time.frameCount;
        if (!running && !canvasBatch) return;
        try
        {
            if (_scope.Active || _scopedCamera != null)
                throw new InvalidOperationException("Nested camera rendering interrupted the health capture");

            bool capture = running && camera == _capture;
            bool primary = DsHudRouting.IsPrimaryCamera(camera.targetDisplay, camera.targetTexture != null,
                camera.cullingMask, _gameCameras != null &&
                (camera == _gameCameras.hudCamera || camera == _gameCameras.mainCamera));
            bool hide = running && primary && DsHudRouting.HideOnPrimary(_showTop, _presented,
                NativeVisible, _capturedFrame, Time.frameCount);
            if (!capture && !hide && !(primary && canvasBatch)) return;
            if (capture && !NativeVisible) return;

            if (capture || hide) CollectRenderers();
            if (capture)
            {
                _submitted = false;
                if (!FrameCamera())
                {
                    _capturedFrame = -1;
                    _image.color = Color.clear;
                    Waiting("Native health artwork is not ready");
                    return;
                }
            }

            _scopedCamera = camera;
            _savedMask = camera.cullingMask;
            camera.cullingMask = capture ? DsHudRouting.CaptureMask
                                         : DsHudRouting.PrimaryMask(_savedMask, hide, canvasBatch);
            if (capture || hide) _scope.Begin(_targets);
            _submitted = capture && _scope.Count > 0;
            if (hide) _hiddenFrame = Time.frameCount;
        }
        catch (Exception e) { Fail(e); }
    }

    void AfterCamera(Camera camera)
    {
        if (_scopedCamera != camera) return;
        try
        {
            bool captured = camera == _capture && _submitted;
            RestoreScope();
            if (!captured) return;
            _capturedFrame = Time.frameCount;
            _image.color = Color.white;
            _waitingSince = -1f;
            _waitingReason = null;
            DsWidgets.SetActive(_fallback, false);
        }
        catch (Exception e) { Fail(e); }
    }

    void RestoreScope()
    {
        try { _scope.Restore(); }
        finally
        {
            if (_scopedCamera != null)
                _scopedCamera.cullingMask = DsHudRouting.RestoreMask(_scopedCamera.cullingMask, _savedMask);
            _scopedCamera = null;
        }
    }

    void RestoreAllScopes()
    {
        try { RestoreScope(); }
        finally { _canvasScope.Restore(); }
    }

    void CollectRenderers()
    {
        _renderers.Clear();
        _targets.Clear();
        foreach (var root in _roots)
        {
            if (root == null) continue;
            _scratch.Clear();
            root.GetComponentsInChildren(true, _scratch);
            _renderers.AddRange(_scratch);
        }
        foreach (var renderer in _renderers)
        {
            if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy &&
                renderer.gameObject.layer == DsHudRouting.SourceLayer)
                _targets.Add(renderer.gameObject);
        }
    }

    void PrepareCanvasScope()
    {
        // uGUI batches before camera callbacks. Keep these layers in place for
        // the whole render phase, then restore them at WaitForEndOfFrame.
        _canvasTargets.Clear();
        _activeToolCanvases = 0;
        _toolCanvases.Clear();
        _tools.GetComponentsInChildren(true, _toolCanvases);
        foreach (var canvas in _toolCanvases)
        {
            if (canvas == null || !canvas.isActiveAndEnabled) continue;
            if (canvas.renderMode != RenderMode.WorldSpace)
                throw new InvalidOperationException("Native tool HUD canvas is not world-space: " + canvas.name);
            if (canvas.gameObject.layer == DsHudRouting.SourceLayer)
            {
                _canvasTargets.Add(canvas.gameObject);
                _activeToolCanvases++;
            }
        }
        _toolGraphics.Clear();
        _tools.GetComponentsInChildren(true, _toolGraphics);
        foreach (var graphic in _toolGraphics)
        {
            if (graphic != null && graphic.isActiveAndEnabled && graphic.canvas != null &&
                graphic.canvas.renderMode == RenderMode.WorldSpace &&
                graphic.gameObject.layer == DsHudRouting.SourceLayer)
                _canvasTargets.Add(graphic.gameObject);
        }
        _canvasScope.Begin(_canvasTargets);
        if (_canvasScope.Count > 0) _canvasFrame = Time.frameCount;
    }

    bool FrameCamera()
    {
        if (_targets.Count == 0 || _bindFrame == null || _capRAnchor == null) return false;
        _worldToFrame = Matrix4x4.TRS(_hudRoot.position, _hudRoot.rotation, Vector3.one).inverse;
        Transform first = null, second = null;
        var pd = PlayerData.instance;
        _healthFsms.Clear();
        _maskPositions.Clear();
        _health.GetComponentsInChildren(true, _healthFsms);
        foreach (var fsm in _healthFsms)
        {
            if (fsm == null || fsm.FsmName != "health_display") continue;
            var number = fsm.FsmVariables.FindFsmInt("Health Number");
            if (number == null) continue;
            if (number.Value == 1) first = fsm.transform;
            if (number.Value == 2) second = fsm.transform;
            if (number.Value > 0 && number.Value <= pd.CurrentMaxHealth)
                _maskPositions.Add(LayoutPosition(fsm.transform));
        }
        if (first == null || second == null || !HasArtwork(first.GetComponent<Renderer>()) ||
            !HasArtwork(_bindFrame.GetComponent<Renderer>())) return false;

        Vector3 anchor = LayoutPosition(first);
        float pitch = LayoutPosition(second).x - anchor.x;
        if (pitch <= 0f) return false;
        float rightmost = anchor.x;
        foreach (var point in _maskPositions) rightmost = Mathf.Max(rightmost, point.x);
        _blueHealth.Clear();
        _health.GetComponentsInChildren(true, _blueHealth);
        foreach (var blue in _blueHealth)
        {
            if (blue != null && blue.gameObject.activeInHierarchy)
                rightmost = Mathf.Max(rightmost, LayoutPosition(blue.transform).x);
        }
        _activeTools = 0;
        _toolIcons.Clear();
        _tools.GetComponentsInChildren(true, _toolIcons);
        foreach (var icon in _toolIcons)
        {
            if (icon == null || !icon.gameObject.activeInHierarchy || icon.CurrentTool == null) continue;
            _activeTools++;
            rightmost = Mathf.Max(rightmost, LayoutPosition(icon.transform).x);
        }

        // Slot origins and the unanimated spool cap define layout. Sampling
        // sprite meshes made damage/appear frames permanently shrink the HUD.
        rightmost = Mathf.Max(rightmost, LayoutPosition(_capRAnchor).x);
        var framing = new DsHudFrame(anchor, pitch, rightmost,
            new Vector2(_texture.width, _texture.height), DsLayout.Current.Hud.height / DsLayout.HudHeight, _zoom);
        _bounds = framing.LayoutBounds;
        _maskPixelPitch = framing.PixelPitch;

        var position = framing.Position;
        position.x += DsConfig.Int("hud_x", 0) / 100f;
        position.y += DsConfig.Int("hud_y", 0) / 100f;
        var t = _capture.transform;
        t.localScale = Vector3.one;
        var scale = t.lossyScale;
        if (scale.x == 0f || scale.y == 0f || scale.z == 0f)
            throw new InvalidOperationException("The health camera inherited an empty canvas scale");
        t.localScale = new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z);
        t.SetPositionAndRotation(_hudRoot.position + _hudRoot.rotation * position, _hudRoot.rotation);
        _capture.orthographicSize = framing.HalfHeight;
        _capture.aspect = (float)_texture.width / _texture.height;
        return true;
    }

    Vector3 LayoutPosition(Transform target)
    {
        Vector3 position = target.position;
        var jitter = target.GetComponent<JitterSelf>();
        if (jitter != null)
        {
            if (JitterActive == null || JitterOrigin == null || JitterTransform == null)
                throw new MissingFieldException("Native HUD jitter layout fields are unavailable");
            var moved = JitterTransform.GetValue(jitter) as Transform;
            if ((moved == null || moved == target) && (bool)JitterActive.GetValue(jitter))
            {
                var local = (Vector3)JitterOrigin.GetValue(jitter);
                position = target.parent != null ? target.parent.TransformPoint(local) : local;
            }
        }
        position = _worldToFrame.MultiplyPoint3x4(position);
        if (!DsHudFrame.Finite(position)) throw new InvalidOperationException("Native HUD layout is not finite");
        return position;
    }

    static bool HasArtwork(Renderer renderer)
    {
        if (renderer == null || !renderer.gameObject.activeInHierarchy) return false;
        var sprite = renderer as SpriteRenderer;
        if (sprite != null) return sprite.sprite != null;
        var filter = renderer.GetComponent<MeshFilter>();
        return filter != null && filter.sharedMesh != null && filter.sharedMesh.vertexCount > 0;
    }

    void Waiting(string reason)
    {
        if (_waitingSince < 0f) _waitingSince = Time.unscaledTime;
        if (Time.unscaledTime - _waitingSince < 5f) return;
        if (_waitingReason != reason)
        {
            _waitingReason = reason;
            Debug.LogWarning("[DsHud] " + reason + "; " + CanvasState() + "; keeping the primary HUD");
        }
        _fallback.text = "Health HUD unavailable";
        DsWidgets.SetActive(_fallback, true);
    }

    void Diagnostic()
    {
        if (!DsConfig.Bool("hud_diag", false) || Time.unscaledTime < _nextDiagnostic) return;
        _nextDiagnostic = Time.unscaledTime + 2f;
        var pd = PlayerData.instance;
        Debug.Log("[DsHud] frame=" + Time.frameCount + " captured=" + _capturedFrame +
                  " hidden-top=" + _hiddenFrame + " renderers=" + _targets.Count +
                  " health=" + pd.health + "/" + pd.CurrentMaxHealth + " blue=" + pd.healthBlue +
                  " silk=" + pd.silk + "/" + pd.CurrentSilkMax +
                  " tool-icons=" + _activeTools + " radial-canvases=" + _activeToolCanvases +
                  " layout=" + _bounds + " mask-pitch-px=" + _maskPixelPitch.ToString("F1") +
                  " ortho=" + _capture.orthographicSize + " show-top=" + _showTop);
    }

    string CanvasState()
    {
        var canvas = _image != null ? _image.canvas : null;
        var camera = canvas != null ? canvas.worldCamera : null;
        return "view-active=" + (_image != null && _image.gameObject.activeInHierarchy) +
               " size=" + DsPresentation.LayoutSize +
               " canvas=" + (canvas != null ? canvas.name : "none") +
               " canvas-active=" + (canvas != null && canvas.isActiveAndEnabled) +
               " mode=" + (canvas != null ? canvas.renderMode.ToString() : "none") +
               " canvas-display=" + (canvas != null ? canvas.targetDisplay : -1) +
               " camera=" + (camera != null ? camera.name : "none") +
               " camera-active=" + (camera != null && camera.isActiveAndEnabled) +
               " camera-display=" + (camera != null ? camera.targetDisplay : -1);
    }

    void Fail(Exception error)
    {
        _failed = true;
        _capturedFrame = -1;
        _submitted = false;
        _presented = false;
        if (_capture != null) _capture.enabled = false;
        if (_image != null) _image.color = Color.clear;
        try { RestoreAllScopes(); }
        catch (Exception restore) { Debug.LogError("[DsHud] restoration failed: " + restore); }
        if (_fallback != null)
        {
            _fallback.text = "Health HUD unavailable";
            DsWidgets.SetActive(_fallback, true);
        }
        Debug.LogError("[DsHud] capture disabled; retaining the primary HUD: " + error);
    }

    void OnDestroy()
    {
        Stop();
        if (_texture == null) return;
        if (_capture != null) _capture.targetTexture = null;
        _texture.Release();
        Destroy(_texture);
        _texture = null;
    }
}
#endif
