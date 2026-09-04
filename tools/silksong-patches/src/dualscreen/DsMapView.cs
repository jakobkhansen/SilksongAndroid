// DsMapView — the game's map, rendered by our own cameras into our own texture.
//
// The whole screen turns on one observation. The map looks like world-space
// sprites that only the game can draw, and it is; but the game does not draw
// them to the screen either. Two orthographic cameras under
// HudCamera/In-game/Game Map Rendering render them into RenderTextures, which
// CameraRenderToMesh then shows on two quads. The pair is split by Z SLICE, not
// by layer:
//
//     Map Camera        clips [42,50]   rooms
//     Decorator Camera  clips [30,42]   pins, next-area arrows, text
//
// with the GameMap root parked at local z = 43 and pins authored ~2.5 nearer.
// Both cameras are orthographic, size 8.710664, cullingMask 32 -- layer 5, the
// UI layer, alone.
//
// A composite is a framing of a scene, and a framing is something we can do for
// ourselves. So this builds ITS OWN pair of cameras, duplicating the game's two
// exactly -- same rotation, same clip planes, same mask -- into OUR OWN
// RenderTexture. Position and orthographic size are ours, and that is the whole
// trick: framing is what zoom and pan ARE, so we get both without writing to a
// single Transform the game owns.
//
// What that avoids, all of it paid for once in V1:
//
//   * The fade. InventoryMapManager's sceneMapFade is a component on
//     "Game Map Quads", the PARENT of the two display quads, and the alpha
//     leaves are on the quads themselves. The live content is a different
//     subtree with no fade controller anywhere from its root down. The fade is
//     on the display, not on the thing displayed -- so re-rendering the thing
//     displayed is immune to it. V1 cloned every quad with a fresh material to
//     get the same immunity.
//   * The transform fight. See EnsureContent below.
//   * The layer shuffle. Nothing of the game's is moved to layer 6, ever.
//
// One thing here is NOT ours: whether the zone's objects are active. That is
// EnsureContent's problem, and it is the only place this file touches the game.

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using UnityEngine;
using GlobalEnums;
// Team Cherry ship a forked TextMeshPro under the namespace TMProOld, so the
// label on a next-area arrow is a TMProOld.TMP_Text and not a TMPro one.
using Tmp = TMProOld.TMP_Text;

public class DsMapView
{
    /// <summary>Centred on the player, or fitted to everything the player has mapped.</summary>
    public enum Frame { Area, World }

    // Layer 5 is "UI", and it is the only layer the map's sprites live on. Our
    // cameras therefore see the whole of the game's UI in principle -- and never
    // in practice, because the map subtree sits 1000 units away in x and the Z
    // slices exclude everything else. Two independent reasons, which is the
    // right number for something that would be visible if it went wrong.
    const int GAME_UI_LAYER = 5;

    readonly Transform _parent;

    GameObject _rig;
    Camera _rooms, _decor;
    RenderTexture _rt;
    int _rtW, _rtH;

    GameMap _map;
    Camera _srcRooms, _srcDecor;
    Transform _compass;

    Vector2 _pan;
    float _zoom = 1f;
    const float MinZoom = 0.25f;
    const float MaxZoom = 6f;
    float _mapUnitsPerPixel = 0.01f;
    float _nextAssert;
    float _nextCompass;
    float _nextSweep;
    float _settleUntil;
    bool _forceAssert;
    bool _contentDark;
    bool _backedOff;
    bool _contentOk;
    bool _cleared;
    int _asserts;
    bool _visible;
    bool _describedRig;
    bool _describedAim;

    public Frame Mode { get; set; }

    /// <summary>What the screen draws. Null until Build succeeds.</summary>
    public RenderTexture Texture { get { return _rt; } }

    /// <summary>Is there a live map to look at? False on menus and mid-load.</summary>
    public bool Bound { get { return _map != null; } }

    public DsMapView(Transform parent) { _parent = parent; }

    // ── the rig ─────────────────────────────────────────────────────────────

    public void Build(int width, int height)
    {
        _rtW = Mathf.Max(64, width);
        _rtH = Mathf.Max(64, height);

        _rig = new GameObject("DsMapRig");
        _rig.transform.SetParent(_parent, false);
        NormaliseScale();

        // Depth 24 rather than none: the decorator pass clears depth and keeps
        // colour, which is the standard way to stack two cameras into one
        // target, and it wants a depth buffer to clear.
        _rt = new RenderTexture(_rtW, _rtH, 24, RenderTextureFormat.Default);
        _rt.name = "DsMapRT";
        _rt.filterMode = FilterMode.Bilinear;
        _rt.antiAliasing = 1;
        _rt.Create();
        _rig.AddComponent<DsMapRigLifetime>().Texture = _rt;
        _rig.AddComponent<DsMapLateTick>().Tick = LateTick;

        // Depths 90 and 91 keep the game's own relative order between the two
        // passes; the absolute values are irrelevant to a camera that renders
        // into a texture, but matching them costs nothing and reads clearly.
        _rooms = MakeCamera("DsMapRooms", CameraClearFlags.SolidColor, 90f);
        _decor = MakeCamera("DsMapDecor", CameraClearFlags.Depth, 91f);

        SetVisible(false);
    }

    Camera MakeCamera(string name, CameraClearFlags clear, float depth)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_rig.transform, false);

        var c = go.AddComponent<Camera>();
        c.orthographic = true;
        c.clearFlags = clear;
        c.backgroundColor = ClearColour();
        c.cullingMask = 1 << GAME_UI_LAYER;
        c.depth = depth;
        c.targetTexture = _rt;          // never presents to a display
        c.allowHDR = false;
        c.allowMSAA = false;
        c.useOcclusionCulling = false;
        c.enabled = false;
        return c;
    }

    /// <summary>
    /// Show or hide, subject to the map actually being worth showing.
    ///
    /// The readiness gate is why this is not a plain assignment, and it has two
    /// halves, both of which exist to stop the panel blinking on a room change:
    ///
    ///   * `_contentDark` -- the game tore the areas down (it does this on a
    ///     scene load, via CloseQuickMap) and we have not put them back yet.
    ///     Rendering now draws an empty map.
    ///   * `_settleUntil` -- the scene has just changed and the game's own
    ///     SetupMap has not finished populating the rooms, so the map exists but
    ///     is half-built.
    ///
    /// In both cases freezing the cameras leaves the render texture holding the
    /// previous room's frame, which between adjacent rooms is very nearly the
    /// same picture. The panel pauses instead of flashing.
    /// </summary>
    public void SetVisible(bool on)
    {
        _visible = on;
        bool render = on && !_contentDark && Time.unscaledTime >= _settleUntil;
        if (_rooms != null) _rooms.enabled = render;
        if (_decor != null) _decor.enabled = render;
    }

    /// <summary>
    /// What the map camera clears to.
    ///
    /// Normally the panel's own ground colour, so the render blends into the
    /// frame around it. That is also why an empty map and an unshown texture
    /// look identical: both are a rectangle of DsTheme.Ground. `map_clear=debug`
    /// makes the render obviously itself, which separates "the cameras drew
    /// nothing" from "the texture never reached the RawImage" -- in one app
    /// restart rather than one seven-minute build.
    /// </summary>
    static Color ClearColour()
    {
        return DsConfig.Str("map_clear", "ground") == "debug"
             ? new Color(0.28f, 0.03f, 0.03f, 1f)
             : DsTheme.Ground;
    }

    /// <summary>
    /// Cancel the canvas's scale off the rig.
    ///
    /// This is the bug that made the first device build draw an empty panel, and
    /// it is worth stating plainly because nothing about it looks wrong.
    ///
    /// The rig hangs off the screen's own RectTransform, which is the right
    /// place for it -- it dies when the shell rebuilds, so nothing has to
    /// remember to tear it down. But that RectTransform is under a
    /// ScreenSpaceCamera Canvas, and such a canvas carries a localScale that maps
    /// pixels to world units: (2 * orthographicSize) / screenHeight. Our display
    /// camera never sets orthographicSize, so it is Unity's default 5, and the
    /// canvas scale is 10/1080 -- about 0.0093.
    ///
    /// A Camera's view matrix is the inverse of its transform's localToWorld,
    /// and that INCLUDES scale. So the two map cameras inherited 0.0093 and
    /// rendered a region ~108 times too small: a patch of map a twentieth of a
    /// unit across, somewhere inside a single sprite. Flat colour, no error, no
    /// warning.
    ///
    /// Setting localScale to one first makes lossyScale exactly the parent's,
    /// and the reciprocal then cancels it. Re-applied each frame because a canvas
    /// rescales when the panel does.
    /// </summary>
    void NormaliseScale()
    {
        if (_rig == null) return;
        var t = _rig.transform;
        t.localScale = Vector3.one;
        var ls = t.lossyScale;
        if (ls.x == 0f || ls.y == 0f || ls.z == 0f) return;
        if (Mathf.Approximately(ls.x, 1f) && Mathf.Approximately(ls.y, 1f) &&
            Mathf.Approximately(ls.z, 1f)) return;
        t.localScale = new Vector3(1f / ls.x, 1f / ls.y, 1f / ls.z);
    }

    public void Destroy()
    {
        SetVisible(false);
        // The lifetime component releases the texture, so the rig is the only
        // thing to destroy and the two paths cannot disagree about who owns it.
        if (_rig != null) { UnityEngine.Object.Destroy(_rig); _rig = null; }
        _rt = null;
        _rooms = null; _decor = null;
        _map = null; _srcRooms = null; _srcDecor = null; _compass = null;
    }

    // ── per frame ───────────────────────────────────────────────────────────

    /// <summary>
    /// Find the live map. Returns false when there is nothing to show.
    ///
    /// Separate from Drive so that a zone the player has no map of costs the
    /// game nothing: the screen asks this, sees HasMapHere is false, draws the
    /// No-Map symbol, and never touches the game's map state at all.
    /// </summary>
    public bool Poll()
    {
        if (_rt == null) return false;

        // Android can drop a render texture's contents on a context loss; a
        // released texture renders as garbage rather than as an error.
        if (!_rt.IsCreated()) _rt.Create();

        return Rebind();
    }

    /// <summary>
    /// Keep the content alive and point the cameras at it. Poll first.
    /// Returns false when there is no map to show for this zone.
    /// </summary>
    public bool Drive()
    {
        if (_map == null || _srcRooms == null || _srcDecor == null) return false;

        // A new zone needs the content re-framed, not just refreshed: the area
        // view is framed by TryOpenQuickMap, which frames on the zone it was
        // called in. Without this the player walks into Greymoor and keeps
        // looking at Bellhart, because the old zone's objects are still active
        // and the dark-detector is therefore perfectly happy.
        var zone = CurrentZone;
        if (zone != _lastZone)
        {
            _lastZone = zone;
            _forceAssert = true;
            _nextAssert = 0f;
            _cleared = false;
            _lastDropped = 0;
            _loggedArrows = false;
            // Area only. The full map is a place the player has deliberately
            // put the camera -- resetting it on a border wipes their pinch and
            // yanks the view back to Hornet, which reads as the map rezooming
            // itself. The area map is the one that should follow them.
            if (Mode == Frame.Area) ResetView();
        }

        if (Mode == Frame.Area)
        {
            if (!HasMapHere) { ClearContent(); return false; }

            EnsureContent();

            // TryOpenQuickMap's own answer outranks the pre-check above. It
            // returns false for a zone the player has no map of -- and,
            // crucially, enables nothing when it does, so the PREVIOUS zone's
            // rooms are still active and still being drawn. Believing the
            // pre-check alone is what left the panel showing the map of
            // somewhere else after walking into an unmapped area.
            if (!_contentOk) { ClearContent(); return false; }
        }
        else
        {
            // The full map stands on what the player has mapped ANYWHERE, which
            // has nothing to do with where they happen to be standing. Being
            // lost in an unfamiliar area is precisely when you want to look at
            // the map you do have.
            if (!HasAnyMap) { ClearContent(); return false; }
            EnsureContent();
        }

        // Every frame, not just on a content refresh: the arrows come back from
        // outside this code -- a bench FSM calling the TryOpenQuickMap PlayMaker
        // action -- and the full map has no use for any of them. The frame in
        // which that happens is handled by LateTick; this is the steady state.
        //
        // Not while the game is showing its own map, though: those arrows belong
        // to the quick map on the main screen, where they are wanted.
        if (Mode == Frame.World && !GameMapShowing) HideNextAreaArrows();

        _cleared = false;
        Aim();
        Diagnose();
        return true;
    }

    /// <summary>
    /// One line whenever the panel's state changes, and not one otherwise.
    ///
    /// Everything that can freeze or unstick this map is a boolean that flips
    /// somewhere else: the areas going dark, the game opening its own map, the
    /// dark-detector's back-off. A frozen panel and a working one look
    /// identical in a log that only reports events, so this reports the STATE,
    /// deduplicated -- which turns "it stops after a second" into a line saying
    /// which of them changed a second in.
    /// </summary>
    void Diagnose()
    {
        if (!DsConfig.Bool("map_diag", false)) return;

        string sig = (Mode == Frame.World ? "world" : "area") +
                     " dark=" + (_contentDark ? 1 : 0) +
                     " ok=" + (_contentOk ? 1 : 0) +
                     " gameMap=" + (GameMapShowing ? 1 : 0) +
                     " areas=" + CountActiveAreas() +
                     " backoff=" + (_backedOff ? 1 : 0) +
                     " render=" + ((_rooms != null && _rooms.enabled) ? 1 : 0) +
                     " settling=" + (Time.unscaledTime < _settleUntil ? 1 : 0);
        if (sig == _lastDiag) return;
        _lastDiag = sig;
        Debug.Log("[DsMap] state: " + sig + " pan=" + _pan);
    }

    string _lastDiag;

    /// <summary>
    /// Has the player mapped anything at all, anywhere?
    ///
    /// PlayerData.HasAnyMap folds together the per-zone Has&lt;Zone&gt;Map bools
    /// and the mapAllRooms override, which is what the game's own inventory uses
    /// to decide whether its Map pane exists.
    /// </summary>
    public bool HasAnyMap
    {
        get
        {
            try { return PlayerData.instance.HasAnyMap; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Take the map down, for a zone we have no map of.
    ///
    /// CloseQuickMap is the game's own teardown: it disables every area and, via
    /// DisableAllAreas, switches the display quads off too. Without it the last
    /// mapped zone stays active and the second screen keeps cheerfully drawing
    /// it. Once per entry into the no-map state, not once per tick.
    /// </summary>
    void ClearContent()
    {
        if (_cleared) return;
        _cleared = true;
        try { _map.CloseQuickMap(); }
        catch (Exception e) { Debug.LogWarning("[DsMap] could not close the map: " + e.Message); }
    }

    /// <summary>
    /// Re-find the game's map and cameras.
    ///
    /// The GameMap is Instantiated fresh on every gameplay scene load
    /// (InventoryMapManager.EnsureGameMapSpawned, driven from
    /// GameCameras.StartScene), so a handle cached once goes stale at the first
    /// door. Cheap to re-check: a field compare in the common case.
    /// </summary>
    bool Rebind()
    {
        GameMap live = null;
        string scene = null;
        try
        {
            var gm = GameManager.instance;
            if (gm != null) { live = gm.gameMap; scene = gm.sceneName; }
        }
        catch { }

        if (live == null) { _map = null; return false; }

        if (!ReferenceEquals(live, _map))
        {
            _map = live;
            _compass = null;
            _srcRooms = null;
            _srcDecor = null;
            _nextAssert = 0f;
            _pan = Vector2.zero;
            _zoom = 1f;
            _forceAssert = true;
            _zoneBoundsOk = false;
            _lastDropped = 0;
            _arrows = null;
        }

        // The SCENE is the thing that changes on a room transition, not the map.
        //
        // This was originally hung off the GameMap instance, on the assumption
        // that a gameplay scene load spawns a fresh one. It does not:
        // EnsureGameMapSpawned only spawns when there is no map yet, so the same
        // instance persists for the session and the trigger never fired. Room
        // changes within a zone therefore kept flickering while zone changes
        // appeared fixed -- the latter only because they take the separate
        // out-of-gameplay path, which holds for its own reasons.
        if (scene != _lastScene)
        {
            _lastScene = scene;
            _forceAssert = true;
            _nextAssert = 0f;
            _settleUntil = Time.unscaledTime + DsConfig.Int("map_settle_ms", 600) / 1000f;
        }

        if (_srcRooms == null || _srcDecor == null) FindSourceCameras();
        return _srcRooms != null && _srcDecor != null;
    }

    string _lastScene;

    /// <summary>
    /// The two cameras whose framing we copy.
    ///
    /// Found through `CameraRenderToMesh` rather than by object name. That
    /// component is public, and it sits on the camera GameObjects themselves, so
    /// this needs no path through the hierarchy at all -- which matters, because
    /// a scan of the shipped assembly shows "Game Map Rendering", "Map Camera"
    /// and "Decorator Camera" exist only in scene data, with no code literal
    /// anywhere to keep them honest. "Compass Icon" and the DisableAllAreas
    /// allow-list, by contrast, ARE literals in the assembly, which is why those
    /// are still matched by name.
    ///
    /// They are then told apart by far clip plane rather than by name, because
    /// the [42,50] / [30,42] split is the thing that actually distinguishes
    /// them.
    /// </summary>
    void FindSourceCameras()
    {
        Camera far = null, near = null;

        try
        {
            var owners = Resources.FindObjectsOfTypeAll<CameraRenderToMesh>();
            for (int i = 0; i < owners.Length; i++)
            {
                var o = owners[i];
                if (o == null) continue;
                // FindObjectsOfTypeAll also returns prefab assets, which have no
                // scene and must not be rendered from.
                if (!o.gameObject.scene.IsValid()) continue;
                Classify(o.GetComponent<Camera>(), ref far, ref near);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DsMap] map camera scan failed: " + e.Message);
        }

        // Fallback: walk the HUD by name, in case a future build stops putting
        // the component on the camera itself.
        if (far == null || near == null)
        {
            try
            {
                var gc = GameCameras.instance;
                if (gc != null && gc.hudCamera != null)
                {
                    var hud = gc.hudCamera.GetComponent<HUDCamera>();
                    if (hud != null && hud.GameplayChild != null)
                    {
                        var rendering = hud.GameplayChild.transform.Find("Game Map Rendering");
                        if (rendering != null)
                        {
                            var cams = rendering.GetComponentsInChildren<Camera>(true);
                            for (int i = 0; i < cams.Length; i++) Classify(cams[i], ref far, ref near);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DsMap] map camera lookup failed: " + e.Message);
            }
        }

        if (far == null || near == null) return;
        _srcRooms = far;
        _srcDecor = near;

        // The pair is only meaningful if their slices abut: the rooms camera's
        // near plane is the decorator camera's far plane, at 42. Anything else
        // means the scan picked up some other camera-to-texture user -- the
        // scan returns inactive objects too, and ActiveSources being an enum
        // says the component was built to be shared. A wrong pair renders a
        // plausible-looking wrong framing, so it is worth one comparison to make
        // that loud rather than mysterious.
        if (!Mathf.Approximately(_srcRooms.nearClipPlane, _srcDecor.farClipPlane))
            Debug.LogWarning("[DsMap] map cameras do not abut (" +
                             _srcRooms.name + " near=" + _srcRooms.nearClipPlane + ", " +
                             _srcDecor.name + " far=" + _srcDecor.farClipPlane +
                             ") -- framing may be wrong");

        if (!_describedRig)
        {
            _describedRig = true;
            Debug.Log("[DsMap] source cameras: rooms='" + _srcRooms.name + "' clip " +
                      _srcRooms.nearClipPlane + ".." + _srcRooms.farClipPlane +
                      " size " + _srcRooms.orthographicSize +
                      ", decor='" + _srcDecor.name + "' clip " +
                      _srcDecor.nearClipPlane + ".." + _srcDecor.farClipPlane);
        }
    }

    /// <summary>Keep the two deepest-clipping cameras: the far one is the rooms.</summary>
    void Classify(Camera c, ref Camera far, ref Camera near)
    {
        if (c == null || c == _rooms || c == _decor) return;
        if (far == null || c.farClipPlane > far.farClipPlane) { near = far; far = c; }
        else if (near == null || c.farClipPlane > near.farClipPlane) near = c;
    }

    /// <summary>
    /// Keep the zone content active, without joining the fight over the map's
    /// transform.
    ///
    /// Two modes, two calls, and the difference is exactly the difference
    /// between the two views:
    ///
    ///   Area   TryOpenQuickMap() -- activates ONLY the current zone and parks
    ///          the map at that zone's quick-map anchor at scale 1.4725. This is
    ///          the L1 map, reproduced by the game's own code rather than
    ///          approximated by ours.
    ///   World  WorldMap() -- activates EVERY unlocked zone and touches no
    ///          transform. That is the whole of Pharloom, which is right for the
    ///          full map and wrong for the area one.
    ///
    /// The first draft used WorldMap() for both and framed the area view on the
    /// Compass Icon with a zoom derived from the game's numbers. It rendered an
    /// empty patch: every zone was active at once, and the hand-computed window
    /// did not land where the rooms were. Reproducing a view the game already
    /// knows how to produce beats recomputing it -- and the transform write that
    /// was being avoided turns out to cost nothing, because our framing is
    /// expressed relative to that transform anyway.
    ///
    /// The sting is in EnableUnlockedAreas' tail, which both paths share. It
    /// ends with CameraRenderToMesh.SetActive(GameMap, TRUE), switching the
    /// game's OWN display quads on -- and those quads are on screen, over
    /// gameplay, on the MAIN display. The finally below is what undoes it, and
    /// it is a finally rather than a following statement because asking for a
    /// map on the second screen must never leave one on the first.
    ///
    /// It restores rather than forces, and none of this runs at all while the
    /// game has a map up of its own: the player pressing L1 is the one case
    /// where the quads being on is not our doing and not ours to undo.
    /// </summary>
    void EnsureContent()
    {
        // Counted, not just tested. "Is anything active" is the right question
        // for the area view and the wrong one for the full map: when the game
        // opens a quick map of its own it calls TryOpenQuickMap, which narrows
        // the active set back to the CURRENT ZONE ONLY. Something is still
        // active, so a boolean check stays quiet, and the full map quietly
        // degrades into the area map -- which is exactly what it must never
        // show.
        int active = CountActiveAreas();
        _contentDark = active == 0;

        // The set shrank under us. Only World cares: Area wants one zone anyway.
        if (Mode == Frame.World && _worldAreas > 0 && active < _worldAreas) _forceAssert = true;

        // ...and the set can GROW without anything going dark.
        //
        // Every trigger above notices content DISAPPEARING: a scene load
        // darkening the areas, or the game's own quick map narrowing the world
        // view back to one zone. Nothing notices the opposite. Buying a map
        // from Shakra -- or a mod unlocking a region -- adds a zone to the
        // unlocked set, and what is already on screen stays exactly as lit as
        // it was, so the count does not move and no re-assert is asked for. The
        // panel then shows the set it last enabled until something else forces
        // one, which in practice meant pressing L1: the game opens its own map,
        // CloseQuickMap darkens the areas on the way out, and the dark detector
        // above finally fires.
        //
        // A slow sweep, rather than a signal to watch, because there is no
        // cheap one: what a player may map is spread across the save's map
        // bools, the quill, and whatever a mod has decided today, and
        // EnableUnlockedAreas is the only thing that knows how to read all of
        // it. This goes through the ordinary guarded path -- it stands down
        // while the game has a map up, it is rate-limited, and it backs off
        // where there is nothing to enable -- so at this cadence it costs
        // nothing measurable and cannot fight the main screen.
        if (Time.unscaledTime >= _nextSweep)
        {
            _nextSweep = Time.unscaledTime + DsConfig.Int("map_sweep_ms", 2000) / 1000f;
            _forceAssert = true;
        }

        // Housekeeping, on its own slow tick: keep the compass on Hornet.
        if (Time.unscaledTime >= _nextCompass)
        {
            _nextCompass = Time.unscaledTime + 0.25f;
            try { _map.PositionCompassAndCorpse(); }
            catch (Exception e) { Debug.LogWarning("[DsMap] compass: " + e.Message); }
        }

        if (!_contentDark && !_forceAssert) return;
        if (Time.unscaledTime < _nextAssert) return;

        // The player pressed L1. Stand down.
        //
        // Everything below drives the game's own map machinery -- moving the
        // map to a zone anchor, narrowing or widening the active areas -- and
        // all of it is visible on the MAIN screen when the game is showing a
        // map there. Asserting over the top of that is how the quick map came
        // to flicker and vanish: in the area view the finally below switched
        // the game's display off the moment it came on, and in the full map
        // WorldMap re-widened what TryOpenQuickMap had just narrowed, so L1
        // showed the whole of Pharloom or nothing at all.
        //
        // While the game has a map up it owns the map. This panel keeps
        // rendering, because its cameras and its framing are its own and the
        // content is right there; it simply stops rearranging the furniture.
        // When the game closes its map, CloseQuickMap darkens the areas and the
        // dark-detector above brings us straight back.
        if (GameMapShowing) return;

        _nextAssert = Time.unscaledTime + DsConfig.Int("map_assert_ms", 100) / 1000f;
        _forceAssert = false;

        try
        {
            // Captured before anything below can change it. See the finally.
            bool wasShowing = GameMapShowing;
            try
            {
                // Both modes start by framing the current zone, because
                // TryOpenQuickMap is the only public call that positions the map
                // at all. World then reveals the rest WITHOUT moving it again --
                // WorldMap touches no transform - so the full map opens looking
                // at where Hornet is rather than at the middle of Pharloom.
                string displayName;
                _contentOk = _map.TryOpenQuickMap(out displayName);
                if (_contentOk)
                {
                    _zoneName = displayName;
                    // Measured HERE, in the window where TryOpenQuickMap has
                    // enabled the current zone and WorldMap has not yet enabled
                    // the rest. Whatever is drawable at this instant is the zone
                    // and nothing else, in both modes -- which is the only cheap
                    // way to get a per-zone extent, since the zone table itself
                    // is private.
                    MeasureZone();
                }
                else
                {
                    // Stale names are worse than none: without this the header
                    // kept the last mapped zone's name after walking into an
                    // unmapped one.
                    _zoneName = null;
                }

                // Unconditionally, and that is the point: the full map shows
                // everywhere the player HAS a map, which has nothing to do with
                // whether they have one of the room they are standing in. The
                // game's own menu does not hide the map in an unfamiliar area
                // either.
                if (Mode == Frame.World) { _map.WorldMap(); HideNextAreaArrows(); MeasureAll(); }
            }
            finally
            {
                // Restore, do not force.
                //
                // EnableUnlockedAreas ends by switching the game's own display
                // quads ON, and those quads are on the MAIN screen. Undoing that
                // is the whole reason this is a finally: asking for a map on the
                // second screen must never leave one on the first.
                //
                // But "undo" is not the same as "off". The game can turn them on
                // for its own reasons in the same frame -- the player pressing
                // L1 -- and a blind SetActive(false) here took the quick map
                // away as fast as the game put it up. So the state is captured
                // above and put back, which leaves the game's map alone in the
                // one case where forcing it off was wrong.
                CameraRenderToMesh.SetActive(CameraRenderToMesh.ActiveSources.GameMap, wasShowing);
            }

            _asserts++;
            // Remember how much the full map is supposed to have on screen, so
            // the check above can tell "the game narrowed it" from "this is
            // simply how many zones are unlocked".
            int after = CountActiveAreas();
            if (Mode == Frame.World) _worldAreas = after;

            // BACK OFF when the attempt achieved nothing.
            //
            // Measured on the device: 2150 re-asserts, about ten a second, for
            // as long as the panel was open. The areas were dark, TryOpenQuickMap
            // refused because the zone has no map, WorldMap added nothing, and
            // the next tick found them dark all over again. Retrying a failure at
            // the rate meant for recovering from a scene load just drives the
            // game's map machinery in a tight loop for no result.
            if (after == 0)
            {
                _nextAssert = Time.unscaledTime + 1f;
                if (!_backedOff)
                {
                    _backedOff = true;
                    Debug.Log("[DsMap] nothing to enable here; backing off");
                }
            }
            else _backedOff = false;

            // The content is up again as of this instant, so do not spend a
            // frame frozen waiting for the next check to notice.
            if (_contentOk) _contentDark = false;
            if (_asserts <= 3 || (_asserts % 50) == 0)
                Debug.Log("[DsMap] re-enabled map content (" + _asserts + ")");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DsMap] content refresh failed: " + e.Message);
        }
    }

    /// <summary>
    /// The zone's name as the game itself renders it on the quick map, including
    /// the per-zone NameOverride that lives on a private ZoneInfo and is
    /// otherwise unreachable. Empty until the map has been opened once.
    /// </summary>
    public string ZoneName { get { return _zoneName; } }

    string _zoneName;

    /// <summary>
    /// Is the game showing its own map, on the main screen, right now?
    ///
    /// CameraRenderToMesh.SetActive drives `targetCamera.enabled`, and that
    /// component's targetCamera is the very camera FindSourceCameras hands us.
    /// So the flag is already in our hand and needs no reflection: if the game's
    /// map camera is enabled, the game is drawing a map somewhere we do not
    /// own, and the polite thing is to keep our hands off the map until it is
    /// finished.
    /// </summary>
    bool GameMapShowing
    {
        get { return _srcRooms != null && _srcRooms.enabled; }
    }

    /// <summary>
    /// Turn off the "there is more this way" arrows, which the full map has no
    /// use for.
    ///
    /// They exist to say a zone continues past the edge of the quick map. On
    /// the full map the zone it points at is already drawn, right next to it,
    /// so every arrow is pointing at something the player can see -- and where
    /// zones meet, at each other.
    ///
    /// The game agrees: MapNextAreaDisplay.Refresh hides itself whenever the
    /// broadcast says this is not a quick map, and GameMap.WorldMap sends
    /// exactly that. Calling WorldMap is not enough to rely on, though, because
    /// each display subscribes to the event in Awake -- a one-time hook to one
    /// GameMap instance. Cross the main menu and back and the subscription does
    /// not necessarily survive alongside the object, so the broadcast never
    /// reaches it and it keeps the state the last quick map left it in. That is
    /// the reported symptom, and it is the same shape as every other stale-state
    /// bug on this panel: something left behind rather than maintained.
    ///
    /// So the state is set rather than requested. Only in World mode -- the area
    /// view wants its arrows, and TryOpenQuickMap turns them back on there.
    ///
    /// Asserted every frame rather than once per content refresh, because the
    /// game turns them back on from outside this code entirely: TryOpenQuickMap
    /// is a PlayMaker action, and resting at a bench runs an FSM that calls it.
    /// That broadcasts isQuickMap=true to every MapNextAreaDisplay at once, and
    /// on the full map every zone is drawn, so every arrow in Pharloom appears
    /// and they pile up on each other. Toggling to the area map and back used to
    /// be the only way out -- which is exactly the shape of a state that is set
    /// once and then hoped for.
    ///
    /// The array is cached because this now runs per frame: there are ~128 of
    /// these on the map, and a GetComponentsInChildren per frame is an
    /// allocation per frame on a phone. Rebind drops the cache when the map
    /// instance changes, which is the only thing that can invalidate it.
    /// </summary>
    void HideNextAreaArrows()
    {
        try
        {
            if (_arrows == null) _arrows = _map.GetComponentsInChildren<MapNextAreaDisplay>(true);
            for (int i = 0; i < _arrows.Length; i++)
            {
                var a = _arrows[i];
                if (a != null && a.gameObject.activeSelf) a.gameObject.SetActive(false);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DsMap] could not hide the next-area arrows: " + e.Message);
        }
    }

    MapNextAreaDisplay[] _arrows;
    bool _loggedArrows;
    readonly Vector3[] _corners = new Vector3[4];

    /// <summary>
    /// Is this world-space point inside the game's own quick-map window?
    ///
    /// Shared by the room crop and the arrow crop so the two cannot drift
    /// apart: an arrow is kept on exactly the rule that keeps its room.
    /// </summary>
    bool InWindow(Vector3 world, Vector3 winCentre, Vector2 winHalf, float slack)
    {
        Vector3 c = _map.transform.InverseTransformPoint(world);
        return Mathf.Abs(c.x - winCentre.x) <= winHalf.x + slack
            && Mathf.Abs(c.y - winCentre.y) <= winHalf.y + slack;
    }

    /// <summary>
    /// The last thing this panel does in a frame, after every FSM has had its
    /// Update and before the cameras draw. See DsMapLateTick.
    ///
    /// Deliberately not a general second tick: it does the one job that has to
    /// happen this late, and asks nothing of the game that could fail. Guarded
    /// on the map still being bound, because a scene load can take it away
    /// between our Update and this.
    /// </summary>
    void LateTick()
    {
        if (Mode != Frame.World || _map == null || !_visible) return;
        // The game's own quick map wants its arrows. See Drive.
        if (GameMapShowing) return;
        HideNextAreaArrows();
    }

    /// <summary>
    /// How many of the map's zone parents are active.
    ///
    /// DisableAllAreas' own rule, read backwards: it deactivates every direct
    /// child except these five, so anything outside the list that is still
    /// active is map content. Zero means the areas are dark; a drop means
    /// somebody narrowed them.
    /// </summary>
    int CountActiveAreas()
    {
        var t = _map.transform;
        int n = t.childCount, count = 0;
        for (int i = 0; i < n; i++)
        {
            var c = t.GetChild(i);
            if (c == null || !c.gameObject.activeSelf) continue;
            string name = c.name;
            if (name == "Compass Icon" || name == "Shade Pos" || name == "Map Markers" ||
                name == "Flea Tracker Markers" || name == "Pan Audio Loop") continue;
            count++;
        }
        return count;
    }

    int _worldAreas;

    // ── framing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Point our cameras at what we want to see.
    ///
    /// Every quantity here is derived from the map's own transform rather than
    /// from world space, because the game moves and scales that transform
    /// underneath us -- 1.4725 for the quick map, 0.39 to 1.15 across the
    /// inventory's zoom lerp. A camera at fixed world coordinates would show the
    /// right place until the player pressed L1 and the wrong one afterwards.
    /// Only x and y are ours; z and rotation are copied, so the Z slices keep
    /// meaning what they mean.
    /// </summary>
    void Aim()
    {
        NormaliseScale();

        float scale = Mathf.Abs(_map.transform.lossyScale.y);
        if (scale < 1e-4f || float.IsNaN(scale)) scale = 1f;

        Vector3 centre;
        float halfHeight;
        float areaHalf = AreaHalfHeight(scale);

        if (Mode == Frame.World)
        {
            // The area view's floor is a legibility limit for ONE zone, and the
            // full map is not one zone: opening the whole of Pharloom at the
            // Cradle's zoom shows almost none of it. So the full map keeps the
            // game's own quick-map height as its floor, which is what the area
            // view used before the fit was tightened -- this branch behaves
            // exactly as it always did.
            float worldFloor = Mathf.Max(areaHalf, GameHalfHeight(scale));
            // Latched on entry -- BOTH the height and the centre -- and then left
            // alone. The full map is a place the player has put the camera, so it
            // must not drift under them:
            //
            //   * the height was a multiple of the ZONE fit, which is per-zone by
            //     design, so crossing a border rescaled the whole world;
            //   * the centre was the Compass Icon read every frame, so the map
            //     quietly followed Hornet around as she walked.
            //
            // The centre is kept in MAP-LOCAL coordinates, so when the game moves
            // the map to a new zone's anchor we stay looking at the same part of
            // the world rather than being dragged along with the transform.
            // Latched once there is something worth latching: a real zone
            // measurement, or the knowledge that there will never be one
            // because the current zone has no map.
            if (!_worldLatched && (_zoneBoundsOk || !_contentOk))
            {
                _worldHalf = worldFloor * (DsConfig.Int("map_world_zoom", 100) / 100f);
                _worldCentreLocal = WorldCentreLocal();
                _worldLatched = true;
            }

            if (_worldLatched)
            {
                halfHeight = _worldHalf;
                centre = _map.transform.TransformPoint(_worldCentreLocal);
            }
            else
            {
                // Before the first measurement there is nothing worth latching;
                // fall back to the map's own camera rather than re-deriving (and
                // re-logging) the centre every frame.
                halfHeight = worldFloor * (DsConfig.Int("map_world_zoom", 100) / 100f);
                centre = _srcRooms.transform.position;
            }
        }
        else
        {
            // Fitted to the zone rather than copied from the game's camera. The
            // game frames for 16:9 and this panel is 1.33:1, so its orthographic
            // size cropped the wider zones -- see MeasureZone.
            centre = _map.transform.TransformPoint(_zoneCentreLocal);
            halfHeight = areaHalf;
        }

        // A deliberate override, for finding the framing on the device without a
        // seven-minute rebuild. Off unless asked for.
        int forced = DsConfig.Int("map_size", 0);
        if (forced > 0) halfHeight = forced / 100f;

        // Pinch. Applied last so it composes with either framing.
        halfHeight /= _zoom;

        // Pan is carried in map units so that it survives the game rescaling the
        // map under us, which is the same reason the framing is.
        float limit = DsConfig.Int("map_pan_limit", 60);
        _pan = Vector2.ClampMagnitude(_pan, limit);
        centre += new Vector3(_pan.x * scale, _pan.y * scale, 0f);

        float size = halfHeight * scale;
        if (size < 0.01f) size = 0.01f;
        _mapUnitsPerPixel = (2f * halfHeight) / _rtH;

        Pose(_rooms, _srcRooms, centre, size);
        Pose(_decor, _srcDecor, centre, size);

        DescribeAim(centre, size, scale);
    }

    /// <summary>
    /// One line, once, describing where the cameras ended up.
    ///
    /// Everything that can go wrong here is invisible: a camera pointed at empty
    /// space and a camera scaled into a single texel both render exactly the
    /// same flat nothing. A build costs ~7 minutes, so the numbers that
    /// distinguish them are logged rather than guessed at a second time.
    /// </summary>
    void DescribeAim(Vector3 centre, float size, float mapScale)
    {
        if (_describedAim) return;
        _describedAim = true;

        int activeKids = 0;
        var mt = _map.transform;
        for (int i = 0; i < mt.childCount; i++)
            if (mt.GetChild(i).gameObject.activeSelf) activeKids++;

        Vector3 compass = CompassPosition();

        Debug.Log("[DsMap] aim: cam=" + _rooms.transform.position +
                  " size=" + size.ToString("F3") +
                  " rigScale=" + _rig.transform.lossyScale.x.ToString("F4") +
                  " | map pos=" + mt.position + " scale=" + mapScale.ToString("F3") +
                  " activeChildren=" + activeKids + "/" + mt.childCount +
                  " | compass=" + compass +
                  " | src=" + _srcRooms.transform.position +
                  " srcSize=" + _srcRooms.orthographicSize.ToString("F3"));

        Debug.Log("[DsMap] rig: roomsEnabled=" + _rooms.isActiveAndEnabled +
                  " decorEnabled=" + _decor.isActiveAndEnabled +
                  " mask=" + _rooms.cullingMask + " (src=" + _srcRooms.cullingMask + ")" +
                  " clip=" + _rooms.nearClipPlane + ".." + _rooms.farClipPlane +
                  " rt=" + _rtW + "x" + _rtH + " created=" + _rt.IsCreated() +
                  " visible=" + _visible);

        DescribeContent();
    }

    /// <summary>
    /// What is actually there to be seen, and on which layers.
    ///
    /// The camera can be placed perfectly and still render nothing if the
    /// content is on a layer the mask does not include, and the two failures
    /// look identical on the panel. So the sprites are counted per layer and
    /// their world bounds measured: between them, "the frustum misses the
    /// content" and "the mask misses the content" become different log lines
    /// rather than the same black rectangle.
    /// </summary>
    void DescribeContent()
    {
        try
        {
            var renderers = _map.GetComponentsInChildren<SpriteRenderer>(false);
            var perLayer = new System.Collections.Generic.Dictionary<int, int>();
            Bounds b = default(Bounds);
            bool any = false;
            int drawn = 0;

            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || !r.enabled || r.sprite == null) continue;
                drawn++;
                int layer = r.gameObject.layer;
                perLayer[layer] = perLayer.ContainsKey(layer) ? perLayer[layer] + 1 : 1;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }

            var sb = new System.Text.StringBuilder();
            foreach (var kv in perLayer)
                sb.Append(" L").Append(kv.Key).Append('=').Append(kv.Value);

            Debug.Log("[DsMap] content: " + drawn + " sprites of " + renderers.Length +
                      " drawable;" + (sb.Length > 0 ? sb.ToString() : " none") +
                      (any ? " | bounds centre=" + b.center + " size=" + b.size : " | no bounds"));
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DsMap] content probe failed: " + e.Message);
        }
    }

    void Pose(Camera ours, Camera src, Vector3 centre, float size)
    {
        if (ours == null || src == null) return;
        var t = ours.transform;
        t.rotation = src.transform.rotation;
        t.position = new Vector3(centre.x, centre.y, src.transform.position.z);
        ours.orthographicSize = size;
        ours.nearClipPlane = src.nearClipPlane;
        ours.farClipPlane = src.farClipPlane;
        // Copied, not assumed. The mask was hardcoded to 1 << 5 from a prefab
        // dump, which is one more thing to be wrong about than necessary: the
        // camera we are duplicating demonstrably renders this content, so
        // whatever it culls is by definition the right answer.
        ours.cullingMask = src.cullingMask;
    }

    /// <summary>
    /// Measure the current zone, in the map's own coordinates.
    ///
    /// The game frames its quick map for 16:9: orthographic size 8.710664 is a
    /// half-width of ~15.5 map units. This panel is 1.33:1, so the same size
    /// gives ~11.6 -- a quarter less width, which is why copying the game's
    /// camera clipped the wider zones. The fix is not a different constant, it
    /// is to stop using a constant: measure what is actually there and fit it.
    ///
    /// **Rooms only.** Pins, markers, next-area arrows and the compass are not
    /// the map, and letting them into the bounds inflates it -- badly, because
    /// "Compass Icon", "Shade Pos", "Map Markers" and "Flea Tracker Markers"
    /// survive DisableAllAreas and are therefore active during this measurement
    /// even when they belong somewhere else entirely. A corpse marker left in
    /// another zone would stretch the fit across half of Pharloom.
    ///
    /// They are excluded by the game's own separation rather than by a blacklist
    /// of names: rooms sit in the Map Camera's [42,50] depth slice, decorations
    /// ~2.5 units nearer in the Decorator Camera's [30,42]. Anything the rooms
    /// camera would not draw is not part of the map's extent.
    ///
    /// Stored in MAP-LOCAL units, not world ones, so that the game moving or
    /// rescaling the map underneath us cannot invalidate the measurement --
    /// which is the same reason everything else here is relative to that
    /// transform.
    ///
    /// **Measured twice.** A zone's parent object is not the same thing as the
    /// zone: `Abyss_09`, the shaft between the Deep Docks and the Abyss, has its
    /// top cap filed under the Docks and its shaft under the Abyss, so each of
    /// those two zones owns one room some eight units outside the rest of its
    /// map. Fitting to it drags the centre 3.5 units off and zooms out until the
    /// zone proper covers a third of the panel -- the reported symptom, and
    /// across all 30 mapped zones the only two that have it.
    ///
    /// The game does not have the problem because it does not fit: it frames
    /// each zone at an authored anchor and lets the stray room fall off the
    /// edge. So the crop is Team Cherry's own answer, read back off the live
    /// camera -- a room the game's quick map does not show is not part of the
    /// zone's framing. The uncropped extent is kept too, because "where does
    /// this zone's map exist" is a different question with a different answer.
    /// </summary>
    void MeasureZone()
    {
        // Raw first: the extent of everything filed under this zone, which is
        // what "somewhere the map exists" means for the compass check below.
        _zoneBoundsOk = MeasureRooms(false, out _zoneCentreRawLocal, out _zoneExtentsRaw);

        // Assigned even on failure, where MeasureRooms zeroes its outputs: the
        // area view reads the centre without consulting _zoneBoundsOk, and last
        // zone's centre is a worse answer there than the map's origin.
        _zoneCentreLocal = _zoneCentreRawLocal;
        _zoneExtents = _zoneExtentsRaw;
        if (!_zoneBoundsOk || !DsConfig.Bool("map_crop", true)) return;

        // Then the same measurement minus the rooms the game itself crops. A
        // failure here is not fatal: the raw fit is what this code did before
        // the crop existed, and it is wrong by degree rather than in kind.
        Vector3 centre; Vector2 extents;
        if (MeasureRooms(true, out centre, out extents))
        {
            _zoneCentreLocal = centre;
            _zoneExtents = extents;
        }
    }

    /// <summary>
    /// The window the game's own quick map is showing, in map-local units.
    ///
    /// TryOpenQuickMap frames a zone by moving the MAP to that zone's authored
    /// anchor; the camera never moves. So the map-local point under the source
    /// camera is exactly the centre Team Cherry chose for this zone, and it is
    /// read from the live game rather than from a table -- which matters,
    /// because the anchor is conditional (the Deep Docks has three, and picks
    /// between them on progress).
    /// </summary>
    bool TryQuickMapWindow(out Vector3 centreLocal, out Vector2 half)
    {
        centreLocal = Vector3.zero;
        half = Vector2.zero;
        if (_map == null || _srcRooms == null) return false;

        float scale = Mathf.Abs(_map.transform.lossyScale.y);
        if (scale < 1e-4f || float.IsNaN(scale)) return false;

        centreLocal = _map.transform.InverseTransformPoint(_srcRooms.transform.position);

        float h = _srcRooms.orthographicSize / scale;
        if (h < 1e-4f || float.IsNaN(h)) return false;

        // The source camera's own aspect, but never narrower than the 16:9 the
        // game authors its quick maps for. A camera reporting some other shape
        // -- a render texture resized, an aspect read before the target was
        // attached -- would narrow this window, and a narrow window crops rooms
        // that belong. Erring wide only ever crops less.
        float aspect = _srcRooms.aspect;
        if (float.IsNaN(aspect) || aspect < 16f / 9f) aspect = 16f / 9f;

        half = new Vector2(h * aspect, h);
        return true;
    }

    /// <summary>
    /// The same measurement over everything the player has mapped.
    ///
    /// Taken after WorldMap has enabled every unlocked zone, so it is the extent
    /// of the whole known world rather than of one area. Only used to centre the
    /// full map when there is nothing better to centre on.
    ///
    /// Never cropped: the crop asks "does the game's quick map show this room",
    /// and on the full map the honest answer for nearly every room is no.
    /// </summary>
    void MeasureAll()
    {
        Vector2 ignored;
        _allBoundsOk = MeasureRooms(false, out _allCentreLocal, out ignored);
    }

    Vector3 _allCentreLocal;
    bool _allBoundsOk;

    /// <summary>
    /// Bounds of the map rooms currently drawn, in the map's own coordinates.
    ///
    /// The game frames its quick map for 16:9: orthographic size 8.710664 is a
    /// half-width of ~15.5 map units. This panel is 1.33:1, so the same size
    /// gives ~11.6 -- a quarter less width, which is why copying the game's
    /// camera clipped the wider zones. The fix is not a different constant, it
    /// is to stop using a constant: measure what is actually there and fit it.
    ///
    /// **Rooms only.** Pins, markers and the compass are not the map, and
    /// letting them into the bounds inflates it -- badly, because "Compass
    /// Icon", "Shade Pos", "Map Markers" and "Flea Tracker Markers" survive
    /// DisableAllAreas and are therefore active during this measurement even
    /// when they belong somewhere else entirely. A corpse marker left in
    /// another zone would stretch the fit across half of Pharloom.
    ///
    /// They are excluded by the game's own separation rather than by a blacklist
    /// of names: rooms sit in the Map Camera's [42,50] depth slice, decorations
    /// ~2.5 units nearer in the Decorator Camera's [30,42]. Anything the rooms
    /// camera would not draw is not part of the map's extent.
    ///
    /// With ONE exception, added back explicitly at the bottom of this method:
    /// the next-area arrows, which are the one decoration that cannot wander
    /// and are authored to stick out past the rooms.
    ///
    /// Stored in MAP-LOCAL units, not world ones, so that the game moving or
    /// rescaling the map underneath us cannot invalidate the measurement --
    /// which is the same reason everything else here is relative to that
    /// transform.
    ///
    /// `crop` drops the rooms the game's own quick map does not show. See
    /// MeasureZone: it is only ever set for a single zone, and it is how the
    /// Deep Docks stops being framed around a room in the Abyss.
    /// </summary>
    bool MeasureRooms(bool crop, out Vector3 centreLocal, out Vector2 extents)
    {
        centreLocal = Vector3.zero;
        extents = Vector2.zero;
        try
        {
            var renderers = _map.GetComponentsInChildren<SpriteRenderer>(false);
            var cam = _srcRooms.transform;
            float near = _srcRooms.nearClipPlane, far = _srcRooms.farClipPlane;

            float scale = Mathf.Abs(_map.transform.lossyScale.y);
            if (scale < 1e-4f || float.IsNaN(scale)) scale = 1f;

            Vector3 winCentre = Vector3.zero;
            Vector2 winHalf = Vector2.zero;
            bool cropping = crop && TryQuickMapWindow(out winCentre, out winHalf);
            // Enough slack that a room merely clipped by the game's frame stays
            // in. What this rejects is a room the game's frame misses entirely,
            // which in practice means one filed under the wrong zone. The exact
            // value barely matters: measured across all 30 mapped zones,
            // anything from 0.5 to 2.0 rejects the same four rooms, because the
            // strays are ~9.5 units out and the nearest room that belongs is
            // 2.8 units in.
            float slack = DsConfig.Int("map_crop_slack", 100) / 100f;
            int dropped = 0;
            string first = null;

            Bounds b = default(Bounds);
            bool any = false;
            int kept = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || !r.enabled || r.sprite == null) continue;

                // Depth along the camera's own axis, so this stays correct if the
                // rig is ever rotated.
                float depth = cam.InverseTransformPoint(r.bounds.center).z;
                if (depth < near || depth > far) continue;

                if (cropping)
                {
                    // The room's ANCHOR, not its bounds.
                    //
                    // This started as a bounds test with the room's own extents
                    // added to the window -- "keep anything that touches the
                    // frame" -- and it rejected nothing at all, because the one
                    // room it was written for is Abyss_09, the shaft between the
                    // Deep Docks and the Abyss. That is a single sprite some 16
                    // units tall, so the leniency it was granted was eight units
                    // of it, and a room big enough to overlap everything can
                    // never be found to be somewhere else. Sizing a tolerance by
                    // the thing being tested exempts exactly the outliers.
                    //
                    // The anchor has no such problem: it is where the map says
                    // the room is, which is the question being asked.
                    Vector3 c = _map.transform.InverseTransformPoint(r.transform.position);
                    if (!InWindow(r.transform.position, winCentre, winHalf, slack))
                    {
                        dropped++;
                        if (first == null)
                            first = r.name + " at " + c.x.ToString("F1") + "," + c.y.ToString("F1");
                        continue;
                    }
                }

                kept++;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }

            if (!any) return false;

            // Logged only when the count changes, because this runs on every
            // re-assert and the answer is the same every time.
            bool report = dropped > 0 && dropped != _lastDropped;
            if (dropped > 0) _lastDropped = dropped;

            // A crop that takes a third of the zone is not finding a stray, it
            // is missing the zone. The only way that happens is the window being
            // wrong -- a camera moved, an anchor not yet applied -- and in that
            // case the uncropped measurement is the trustworthy one. Refusing is
            // cheap: MeasureZone keeps what it already has.
            if (dropped * 2 > kept)
            {
                if (report)
                    Debug.LogWarning("[DsMap] crop rejected " + dropped + " of " + (dropped + kept) +
                                     " rooms; ignoring it and fitting to all of them");
                return false;
            }

            // The next-area arrows are part of the frame.
            //
            // They are not rooms -- they sit in the decorator's depth slice, so
            // the loop above filters them out with the pins and the compass --
            // but unlike a pin they cannot wander: MapNextAreaDisplay.Refresh
            // reads the GameMapScene it hangs under, so every arrow is a child
            // of a room in this very zone. They are also the one decoration
            // that is authored to stick OUT past the rooms, by around 0.7 to
            // 1.0 units, which is what makes them the thing a tight fit clips
            // first -- and an arrow saying "the map continues this way" is
            // useless with its head cut off.
            //
            // Renderer, not SpriteRenderer. An arrow is TWO objects: "Map_Arrow"
            // carries the sprite and "Area Name" is world-space TextMeshPro,
            // which draws through a MeshRenderer and is invisible to a sprite
            // query. Fitting only the sprites left the label -- the wider half
            // of the pair, and the half that says WHICH way -- hanging over the
            // edge.
            //
            // Cropped on the same rule as the rooms, so an arrow belonging to a
            // room the game does not show cannot smuggle that room's position
            // back into the fit.
            if (crop && DsConfig.Bool("map_fit_arrows", true))
            {
                Vector2 roomsOnly = new Vector2(b.extents.x / scale, b.extents.y / scale);
                int fitted = 0;
                var arrows = _map.GetComponentsInChildren<MapNextAreaDisplay>(false);
                for (int i = 0; i < arrows.Length; i++)
                {
                    if (arrows[i] == null) continue;

                    // Measure the label by its GLYPHS, not by its layout box.
                    //
                    // TryOpenQuickMap switched these arrows on a few statements
                    // ago, and TMP does not build a mesh when a label is
                    // activated -- it does it in its own update, which for this
                    // frame has not happened yet. So the MeshRenderer is sitting
                    // on an empty mesh and the label measures as nothing, which
                    // is why fitting the arrows first moved the frame by exactly
                    // the width of the arrowheads.
                    //
                    // The obvious repair -- fall back to the RectTransform --
                    // was much worse than the disease. Every one of these labels
                    // is authored 8.33 x 4.11 at scale 0.588, i.e. 4.9 x 2.4 map
                    // units centred on the arrow, whatever it says; "BONE EAST"
                    // does not fill a box that size and neither does anything
                    // else. Fitting to it pushed the Deep Docks from a half
                    // width of 5.8 to 8.7 and did the same to every zone with an
                    // arrow in it, which is what made the whole map feel small.
                    //
                    // textBounds is the answer: TMP_Text.GetTextBounds walks the
                    // character info and returns the extent of the VISIBLE
                    // glyphs, in the label's own space, computed during
                    // generation rather than by the renderer. So it is both
                    // tight and available the moment ForceMeshUpdate returns.
                    var labels = arrows[i].GetComponentsInChildren<Tmp>(false);
                    for (int k = 0; k < labels.Length; k++)
                    {
                        var lab = labels[k];
                        if (lab == null) continue;

                        try { lab.ForceMeshUpdate(); } catch { }

                        Bounds tb;
                        try { tb = lab.textBounds; } catch { continue; }
                        // No visible glyphs is not a measurement failure, it is
                        // a label with nothing to show.
                        if (tb.size.x < 1e-3f || tb.size.y < 1e-3f) continue;
                        if (cropping && !InWindow(lab.transform.position, winCentre, winHalf, slack))
                            continue;

                        var lt = lab.transform;
                        _corners[0] = lt.TransformPoint(new Vector3(tb.min.x, tb.min.y, 0f));
                        _corners[1] = lt.TransformPoint(new Vector3(tb.max.x, tb.min.y, 0f));
                        _corners[2] = lt.TransformPoint(new Vector3(tb.min.x, tb.max.y, 0f));
                        _corners[3] = lt.TransformPoint(new Vector3(tb.max.x, tb.max.y, 0f));
                        for (int c = 0; c < 4; c++) b.Encapsulate(_corners[c]);
                        fitted++;
                    }

                    // The arrowhead itself, which is an ordinary sprite. Asked
                    // for as a SpriteRenderer rather than a Renderer so that the
                    // labels' MeshRenderers -- already measured, and measured
                    // better -- cannot come back in through this door.
                    var parts = arrows[i].GetComponentsInChildren<SpriteRenderer>(false);
                    for (int j = 0; j < parts.Length; j++)
                    {
                        var r = parts[j];
                        if (r == null || !r.enabled || r.sprite == null) continue;
                        if (cropping && !InWindow(r.transform.position, winCentre, winHalf, slack))
                            continue;
                        b.Encapsulate(r.bounds);
                        fitted++;
                    }
                }

                // One line per zone, because "the arrows are still off screen"
                // and "the arrows were never measured" look identical on the
                // panel and cost a seven-minute build to tell apart.
                if (!_loggedArrows)
                {
                    _loggedArrows = true;
                    Debug.Log("[DsMap] fitted " + fitted + " arrow part(s) of " + arrows.Length +
                              " arrow(s); extents " + roomsOnly.x.ToString("F1") + "," +
                              roomsOnly.y.ToString("F1") + " -> " +
                              (b.extents.x / scale).ToString("F1") + "," +
                              (b.extents.y / scale).ToString("F1"));
                }
            }

            if (report)
                Debug.Log("[DsMap] cropped " + dropped + " of " + (dropped + kept) +
                          " room(s) the game's own quick map does not show, first " + first +
                          " | window centre=" + winCentre.x.ToString("F1") + "," +
                          winCentre.y.ToString("F1") +
                          " half=" + winHalf.x.ToString("F1") + "," + winHalf.y.ToString("F1") +
                          " -> extents=" + (b.extents.x / scale).ToString("F1") + "," +
                          (b.extents.y / scale).ToString("F1"));

            centreLocal = _map.transform.InverseTransformPoint(b.center);
            extents = new Vector2(b.extents.x / scale, b.extents.y / scale);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DsMap] could not measure the map: " + e.Message);
            return false;
        }
    }

    Vector3 _zoneCentreLocal;
    Vector2 _zoneExtents;
    Vector3 _zoneCentreRawLocal;
    Vector2 _zoneExtentsRaw;
    bool _zoneBoundsOk;
    int _lastDropped;

    /// <summary>
    /// The height the game's own quick map is showing, in map units.
    ///
    /// Not a framing this panel wants -- see AreaHalfHeight -- but the one
    /// framing that is known to show the zone, which makes it the right answer
    /// when there is nothing to measure and the right floor for the full map.
    /// </summary>
    float GameHalfHeight(float scale)
    {
        return _srcRooms.orthographicSize / scale;
    }

    /// <summary>
    /// How much map to show, in map units, so the whole zone fits this panel.
    ///
    /// Floored, because fitting a one-room zone would fill the panel with a
    /// single corridor. The floor used to be the game's own quick-map height,
    /// on the reasoning that "never more zoomed in than L1" costs nothing.
    /// Measuring all 30 mapped zones says otherwise: it was binding on 24 of
    /// them and cost up to 3.5x, because it is the height of a FULL-SCREEN 16:9
    /// frame -- the game shows 21.0 x 11.8 map units there, this panel shows
    /// 15.8 x 11.8 -- and most of Pharloom's zones do not fill even the game's
    /// version of it. Inheriting that height inherits its empty parchment and
    /// loses the width as well. The Cradle covered a quarter of the panel.
    ///
    /// So the floor is now about legibility rather than about the game's camera:
    /// 2.5 map units of half-height is ~10 rooms tall at the measured median
    /// room pitch of 0.5, which is nobody's idea of a single corridor. Across
    /// the 30 zones the median fill goes from 68% to 99%, and the four zones the
    /// floor still binds on -- Moss Cave, Hang, the Cradle, the Front Gate --
    /// are genuinely tiny rather than mis-framed, the worst of them at 79%.
    ///
    /// Note that a zone we could not measure still falls back to the game's
    /// height, not to this floor: the floor is a limit on how far a KNOWN extent
    /// may be zoomed into, and applying it to an unknown one would frame a
    /// fraction of the map and call it the zone.
    /// </summary>
    float AreaHalfHeight(float scale)
    {
        if (!_zoneBoundsOk) return GameHalfHeight(scale);

        float aspect = _rtW / (float)Mathf.Max(_rtH, 1);

        // Breathing room, in PANEL PIXELS rather than as a fraction of the zone.
        //
        // This was a 15% multiplier, which is a margin that grows with the thing
        // it surrounds: Greymoor got 1.1 map units of border and the Cradle got
        // 0.4, for a reason that exists in the arithmetic and nowhere on the
        // panel. What the margin is actually for is keeping the outermost room
        // off the edge of the frame, and "off the edge" is a distance in pixels.
        //
        // So the fit is the extent divided by the fraction of the panel it is
        // allowed to occupy, which is the whole panel less the border on both
        // sides -- worked out per axis, because the panel is not square and a
        // fixed pixel border is a different fraction of each.
        int px = Mathf.Clamp(DsConfig.Int("map_pad_px", 15), 0, 200);
        float usableH = Mathf.Max(_rtH - 2 * px, 1) / (float)Mathf.Max(_rtH, 1);
        float usableW = Mathf.Max(_rtW - 2 * px, 1) / (float)Mathf.Max(_rtW, 1);

        float fit = Mathf.Max(_zoneExtents.y / usableH,
                              (_zoneExtents.x / Mathf.Max(aspect, 0.01f)) / usableW);

        return Mathf.Max(fit, DsConfig.Int("map_min_half", 250) / 100f);
    }
    /// <summary>
    /// Where the full map should centre, in the map's own coordinates.
    ///
    /// Hornet, then the area, then the map. Without the tool that shows her
    /// position the game never places the Compass Icon, so it sits at the map's
    /// origin -- and centring there put the full map in a corner with the
    /// content off to one side. Each fallback is the next most specific answer
    /// to "where am I": the zone we are standing in, and failing that (a zone
    /// with no map, so nothing was measured) the middle of everything we have
    /// mapped.
    /// </summary>
    Vector3 WorldCentreLocal()
    {
        Vector3 local;
        bool haveCompass = TryCompassLocal(out local);
        Vector3 chosen = haveCompass ? local
                       : _zoneBoundsOk ? _zoneCentreLocal
                       : _allBoundsOk ? _allCentreLocal
                       : _map.transform.InverseTransformPoint(_srcRooms.transform.position);

        // One line per full-map open or reset, which is user-paced. Kept because
        // this decision has four inputs and being wrong about it is invisible:
        // the panel simply looks at nothing.
        Debug.Log("[DsMap] centre: compass=" + (haveCompass ? local.ToString() : "none") +
                  " zone=" + (_zoneBoundsOk ? _zoneCentreLocal.ToString() : "none") +
                  " all=" + (_allBoundsOk ? _allCentreLocal.ToString() : "none") +
                  " -> " + chosen);
        return chosen;
    }
    /// <summary>
    /// Hornet's position on the map, if the game is actually tracking it.
    ///
    /// Three tests, because the obvious one is not enough. Without the tool that
    /// shows her position the game never updates the Compass Icon -- but it does
    /// not park it at the origin either, it just leaves it wherever it last was.
    /// Measured on the device: local (-31.77, 22.74) while the zone being drawn
    /// was centred on (-5.17, -10.03) with extents (5.77, 1.51), i.e. a stale
    /// reading some 26 units outside the map, which centred the view on empty
    /// space with the content off the corner of the panel.
    ///
    /// So: it must be active, it must not be at the origin, and -- the test that
    /// actually catches this -- it must be somewhere the map exists.
    /// </summary>
    bool TryCompassLocal(out Vector3 local)
    {
        local = Vector3.zero;
        if (_compass == null) _compass = _map.transform.Find("Compass Icon");
        if (_compass == null || !_compass.gameObject.activeInHierarchy) return false;

        local = _map.transform.InverseTransformPoint(_compass.position);

        // An unplaced compass sits on the map's origin. Real positions do not.
        if (local.sqrMagnitude < 0.0001f) return false;

        // A stale one sits outside the map. Half an extent of slack, so being
        // near the edge of a zone is fine and being in another region is not.
        //
        // Against the UNCROPPED extent deliberately. The framing extent leaves
        // out rooms the game's quick map does not show, but Hornet can stand in
        // one of them -- the Abyss_09 shaft is exactly such a room -- and a
        // compass reading from a room that exists is not a stale one.
        if (_zoneBoundsOk)
        {
            Vector2 slack = _zoneExtentsRaw * 1.5f;
            if (Mathf.Abs(local.x - _zoneCentreRawLocal.x) > slack.x + 1f ||
                Mathf.Abs(local.y - _zoneCentreRawLocal.y) > slack.y + 1f) return false;
        }
        return true;
    }

    Vector3 CompassPosition()
    {
        if (_compass == null) _compass = _map.transform.Find("Compass Icon");
        // Reading position off an inactive transform is fine, and the compass is
        // inactive for as long as the game has no map of its own open.
        if (_compass != null) return _compass.position;
        return _srcRooms != null ? _srcRooms.transform.position : _map.transform.position;
    }

    // ── input ───────────────────────────────────────────────────────────────

    /// <summary>Drag the map. Delta is in panel pixels, y up, as gestures are.</summary>
    public void Pan(Vector2 panelDelta)
    {
        // Dragging the map right should move the map right, so the camera goes
        // left. Panel y is up and world y is up, so neither axis needs flipping.
        _pan -= panelDelta * _mapUnitsPerPixel;
    }

    /// <summary>
    /// Pinch. A factor above 1 spreads the fingers, which means zoom in.
    ///
    /// Zooming in shows less, so the factor divides the half-height. It is
    /// clamped rather than free: the map has a real extent, and letting the
    /// player zoom to a single room's worth of parchment or to a dot in the
    /// middle of nothing are both ways of losing the map with no obvious way
    /// back.
    /// </summary>
    public void Zoom(float factor)
    {
        if (factor <= 0f || float.IsNaN(factor)) return;
        _zoom = Mathf.Clamp(_zoom * factor, MinZoom, MaxZoom);
    }

    public void ResetPan() { _pan = Vector2.zero; }

    public void ResetZoom() { _zoom = 1f; }

    /// <summary>
    /// Back to the framing this mode opens at.
    ///
    /// In the full map that means re-latching onto Hornet where she is now,
    /// which is what makes RESET useful there: it is the way back after panning
    /// off into a corner of Pharloom.
    /// </summary>
    public void ResetView()
    {
        // Logged because this zeroes the player's pan, and every time it has
        // fired when they did not ask for it the symptom has been "dragging
        // stopped working". A long press used to do it by accident.
        if (DsConfig.Bool("map_diag", false))
            Debug.Log("[DsMap] ResetView (pan was " + _pan + ", zoom " + _zoom.ToString("F2") + ")");
        _pan = Vector2.zero;
        _zoom = 1f;
        _worldLatched = false;
    }

    /// <summary>
    /// Switch between the area map and the whole of Pharloom.
    ///
    /// The two modes are backed by different game calls, so a switch has to
    /// re-assert the content immediately rather than wait for the areas to go
    /// dark on their own -- which, in the mode we just left, they would not.
    /// </summary>
    public void SetMode(Frame mode)
    {
        if (mode == Mode) return;
        Mode = mode;
        ResetView();
        _worldLatched = false;    // re-latch the full map on next entry
        _worldAreas = 0;          // and re-learn how much it should be showing
        _nextAssert = 0f;
        _forceAssert = true;
    }

    float _worldHalf;
    Vector3 _worldCentreLocal;
    bool _worldLatched;

    MapZone _lastZone = MapZone.NONE;

    // ── what the screen asks about ──────────────────────────────────────────

    /// <summary>The current zone, or NONE when there is no live map.</summary>
    public MapZone CurrentZone
    {
        get
        {
            try { return _map != null ? _map.GetCurrentMapZone() : MapZone.NONE; }
            catch { return MapZone.NONE; }
        }
    }

    /// <summary>
    /// Does the player own a map of the zone they are standing in?
    ///
    /// HeroController.HasNoMap is public static and answers exactly this without
    /// mutating anything -- unlike TryOpenQuickMap, whose false return is what
    /// V1 had to use, paying a full map teardown for the answer.
    /// </summary>
    public bool HasMapHere
    {
        get
        {
            if (_map == null) return false;
            try { return !HeroController.HasNoMap(_map); }
            catch { }
            try { return _map.HasAnyMapForZone(_map.GetCurrentMapZone()); }
            catch { return false; }
        }
    }
}

/// <summary>
/// Ties the render texture's lifetime to the rig GameObject's.
///
/// The shell rebuilds itself once per run, when the game's fonts finally appear,
/// by destroying every child of the canvas root -- which takes the rig with it.
/// A RenderTexture is not a scene object, so it would survive that as a few
/// megabytes of leaked VRAM. Releasing it here means every teardown path,
/// including ones added later by someone who has never read this file, frees it
/// without having to know it exists.
/// </summary>
public class DsMapRigLifetime : MonoBehaviour
{
    public RenderTexture Texture;

    void OnDestroy()
    {
        if (Texture == null) return;
        try { Texture.Release(); } catch { }
        UnityEngine.Object.Destroy(Texture);
        Texture = null;
    }
}

/// <summary>
/// A hook that runs after every Update in the frame, and before anything is
/// drawn.
///
/// This exists for one reason: the frame in which you sit at a bench. The bench
/// runs a PlayMaker FSM, TryOpenQuickMap is a PlayMaker action, and the arrows
/// therefore come back on during somebody else's Update. Ours had already run,
/// so the arrows were switched off one frame late -- correct, and visibly so,
/// which is the definition of a flicker.
///
/// Script execution order between two Updates is not something to have an
/// opinion about; LateUpdate versus Update is. Unity runs every Update, then
/// every LateUpdate, then renders, so a hide placed here lands after any FSM
/// that could have undone it and still before the camera that would have shown
/// it. It rides on the rig so it dies with the panel.
/// </summary>
public class DsMapLateTick : MonoBehaviour
{
    public Action Tick;

    void LateUpdate()
    {
        if (Tick != null) Tick();
    }
}
#endif
