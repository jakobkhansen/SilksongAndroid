// DsMapScreen — the map tab.
//
// Two states, and the second one matters as much as the first:
//
//   * The player owns a map of the zone they are in: draw it, centred on them,
//     from DsMapView's render texture.
//   * They do not: draw the game's own No-Map symbol, and do not touch the
//     game's map state at all.
//
// The symbol is READ, not cloned. V1 built copies of the No_Map_symbol renderers
// on its private layer and forced their alpha back to 1 every frame, because the
// quick-map FSM's FadeGroup fades the originals and it needed a renderer it
// owned. We want the Sprite, not the renderer: take it off the game's
// SpriteRenderer once and draw it as an ordinary uGUI Image. After the read,
// nothing of the game's is involved, so there is nothing left to fight.
//
// The sprite cannot be named ahead of time -- it is a Sprite reference on one of
// the game's prefabs, not a named constant -- so it is found by object name,
// with a scan as a fallback, and a null degrades to a line of text rather than
// to a blank panel.
//
// FULL MAP is not a button yet. The framing it needs already exists
// (DsMapView.Frame.World) and is reachable with `map_frame=world` in the
// DsConfig file, so the maths can be checked on the device before a control is
// drawn for it. That ordering is deliberate: the button is five lines once the
// framing is known to be right, and misleading once it is known to be wrong.

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using GlobalEnums;
using TmpText = TMProOld.TextMeshProUGUI;
using TmpAlign = TMProOld.TextAlignmentOptions;

public class DsMapScreen : IDsScreen, IDsActionBar
{
    enum State { Idle, NoMap, Map }

    const float HeaderH = 72f;
    const float SymbolSize = 260f;
    // How long the last rendered frame is held when the game drops out of
    // gameplay. Scene transitions do that for a few frames, and flashing the
    // idle panel in the middle of walking through a door is the flicker.
    const float HoldSeconds = 2.5f;

    DsMapView _view;
    RectTransform _host;
    RectTransform _mapPanel, _noMapBox;
    RawImage _raw;
    Image _symbol;
    TmpText _header, _noMapText;

    Rect _mapRect;              // in panel layout space, for hit-testing
    State _state = State.Idle;
    bool _stateApplied;
    bool _buttonsShown;
    float _holdUntil;
    MapZone _zone = MapZone.NONE;
    float _nextSymbolHunt;
    float _nextHeader;

    public string Id { get { return "map"; } }
    public string Title { get { return "MAP"; } }
    public bool Available { get { return DsGameData.InGame; } }

    // ── build ───────────────────────────────────────────────────────────────

    public void Build(RectTransform host)
    {
        _host = host;

        var layout = DsLayout.Current;
        float panelW = layout.Width;
        float bodyH = layout.Body.height;

        float x = DsTheme.Pad;
        float y = HeaderH;
        float w = panelW - DsTheme.Pad * 2f;
        float h = bodyH - HeaderH - DsTheme.Pad;

        // Kept in panel space too, because gestures arrive in panel pixels and
        // converting the rect once is cheaper and clearer than converting every
        // drag.
        _mapRect = layout.InBody(new Rect(x, y, w, h));

        // FULL MAP and RESET live in the HEADER now, not over the map -- see
        // DsActions. Both were controls drawn on a pannable surface, which is a
        // control you hit while dragging, and they took the width the zone name
        // wanted. The header gives the name the whole line.
        _header = DsWidgets.Label(host, "zone", "", DsTheme.TitleSize,
                                  DsTheme.Ink, TmpAlign.Left);
        if (_header != null)
            DsWidgets.Place(_header.rectTransform, x, 10f, w, HeaderH - 18f);

        // No border. The map is a picture with its own edges; a frame around it
        // read as a second, competing one.
        _mapPanel = DsWidgets.Box(host, "map", DsTheme.Ground).rectTransform;
        DsWidgets.Place(_mapPanel, x, y, w, h);

        var rawRt = DsWidgets.Rect(_mapPanel, "render");
        DsWidgets.Stretch(rawRt);
        _raw = rawRt.gameObject.AddComponent<RawImage>();
        _raw.raycastTarget = false;
        _raw.color = Color.white;

        _view = new DsMapView(_mapPanel);
        _view.Mode = DsConfig.Str("map_frame", "area") == "world"
                   ? DsMapView.Frame.World : DsMapView.Frame.Area;
        _view.Build(Mathf.RoundToInt(w), Mathf.RoundToInt(h));
        _raw.texture = _view.Texture;

        // Anything that is not a map is the same picture: the game's own No-Map
        // symbol. "Loading…" and "Main menu" were accurate and useless -- the
        // panel is a map, and the honest thing for it to say when it has no map
        // is the symbol the game already uses for exactly that.
        _noMapBox = DsWidgets.Rect(_mapPanel, "no-map");
        DsWidgets.Stretch(_noMapBox);

        _symbol = DsWidgets.Icon(_noMapBox, "symbol", null, Color.white);

        // No caption. The game's own No-Map symbol is the caption -- a player
        // who recognises it does not need telling, and one who does not will not
        // be helped by a line of text under it either. It only comes back if the
        // sprite cannot be found at all, below.
        _noMapText = DsWidgets.Label(_noMapBox, "no-map-text", "", DsTheme.RowSize,
                                     DsTheme.InkDim, TmpAlign.Center);
        if (_noMapText != null)
            DsWidgets.Place(_noMapText.rectTransform, 0f, h * 0.5f + SymbolSize * 0.55f, w, 60f);

        Apply(State.Idle, force: true);
    }

    public void OnShow()
    {
        _view.ResetPan();
        _nextHeader = 0f;
    }

    public void OnHide()
    {
        // Stop rendering the moment the tab goes away. The content is left
        // enabled deliberately: "zones active, display off" is the state the
        // game itself sits in between maps, so there is nothing to restore and
        // nothing that can be caught half-restored.
        if (_view != null) _view.SetVisible(false);
    }

    // ── per frame ───────────────────────────────────────────────────────────

    public void Tick(float dt)
    {
        State want;

        if (!DsGameData.InGame || !_view.Poll())
        {
            want = State.Idle;
        }
        else
        {
            // Drive answers this rather than a pre-check, because only
            // TryOpenQuickMap knows whether the zone has a map, and only it can
            // take the previous zone's map down when it does not.
            want = _view.Drive() ? State.Map : State.NoMap;
            if (want == State.Map) _holdUntil = Time.unscaledTime + HoldSeconds;
        }

        // Hold the last frame through a scene transition.
        //
        // Walking through a door takes the game out of a gameplay scene for a
        // few frames -- no hero, so DsGameData.InGame goes false -- and the map
        // would drop to the idle panel and back. That is the flicker seen when
        // travelling between zones. Freezing the cameras leaves the render
        // texture holding its last good frame, so the panel simply stops
        // updating for a moment instead of blinking.
        //
        // Idle only. A no-map zone is a real answer, arrived at while the game
        // is perfectly alive, and holding the previous zone's map over it would
        // be the very bug this is next to.
        if (want == State.Idle && _state == State.Map && Time.unscaledTime < _holdUntil)
        {
            _view.SetVisible(false);
            return;
        }

        Apply(want, force: false);
        RefreshText();
    }

    void Apply(State s, bool force)
    {
        // Visibility is driven every tick rather than only on a change. OnHide
        // switches the cameras off without changing the state, so a state-gated
        // SetVisible would leave them off for good once the player tabbed away
        // from the map and back -- an empty panel with nothing in the log.
        if (_view != null) _view.SetVisible(s == State.Map);

        if (!force && s == _state && _stateApplied) return;
        _state = s;
        _stateApplied = true;

        DsWidgets.SetActive(_raw, s == State.Map);
        // One picture for every not-a-map state, idle included.
        DsWidgets.SetActive(_noMapBox, s != State.Map);

        // The controls survive the No-Map state, because the full map is exactly
        // what you want when you are standing somewhere you have no map of --
        // and the game's own menu does not hide its map pane there either. They
        // go only when there is no map anywhere to open, or no game at all.
        _buttonsShown = s == State.Map || (s == State.NoMap && _view != null && _view.HasAnyMap);

        // The texture handle can change if the rig is ever rebuilt; re-reading
        // it here costs nothing and removes a way for the panel to go black.
        if (s == State.Map && _raw != null && _view != null && _raw.texture != _view.Texture)
            _raw.texture = _view.Texture;
    }

    void RefreshText()
    {
        if (Time.unscaledTime < _nextHeader) return;
        _nextHeader = Time.unscaledTime + 0.5f;

        if (_state == State.Idle)
        {
            // No zone to name, and the symbol says the rest.
            if (_header != null) _header.text = "";
            HuntSymbol();
            return;
        }

        if (_state == State.NoMap)
        {
            // Nothing to title. The name comes from TryOpenQuickMap, which
            // refuses for a zone with no map, so anything shown here would be
            // the PREVIOUS area's name -- which is worse than a blank, because
            // it is confidently wrong about where you are.
            if (_header != null) _header.text = "";
            _zone = MapZone.NONE;
            HuntSymbol();
            return;
        }

        // The full map is not an area, so it has no area name. Showing the zone
        // Hornet happens to be standing in, over a view of the whole of
        // Pharloom, labels the wrong thing.
        if (_view.Mode == DsMapView.Frame.World)
        {
            if (_header != null) _header.text = "";
            _zone = MapZone.NONE;      // force a refresh on the way back
            return;
        }

        var zone = _view.CurrentZone;
        if (zone != _zone || _header == null || _header.text.Length == 0)
        {
            _zone = zone;
            // TryOpenQuickMap hands back the name the game itself would print,
            // including the per-zone NameOverride that lives on a private
            // ZoneInfo and is unreachable from here. Fall back to the
            // localisation sheet until the map has been opened once.
            string named = _view.ZoneName;
            if (string.IsNullOrEmpty(named)) named = ZoneName(zone);
            if (_header != null) _header.text = named;
        }

        if (_state == State.NoMap) HuntSymbol();
    }

    /// <summary>
    /// The game's own name for the zone, from its own localisation.
    ///
    /// GameMap.TryOpenQuickMap resolves it as Language.Get(zone, "Map Zones")
    /// with the &lt;br&gt; stripped, so this matches what the player would see on
    /// the quick map -- minus the per-zone NameOverride, which lives on a private
    /// ZoneInfo and is not reachable from here.
    /// </summary>
    static string ZoneName(MapZone zone)
    {
        if (zone == MapZone.NONE) return "";
        try
        {
            string s = TeamCherry.Localization.Language.Get(zone.ToString(), "Map Zones");
            if (!string.IsNullOrEmpty(s)) return s.Replace("<br>", " ").Trim();
        }
        catch { }
        // A readable fallback beats the raw enum: PATH_OF_BONE -> Path Of Bone.
        return Pretty(zone.ToString());
    }

    static string Pretty(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        bool startOfWord = true;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '_') { sb.Append(' '); startOfWord = true; continue; }
            sb.Append(startOfWord ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            startOfWord = false;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Borrow the No-Map sprite from the game, retrying until the HUD exists.
    ///
    /// Two places have one under the same object name -- the quick map's, and the
    /// inventory Map pane's -- so the direct path is tried first and a scan
    /// catches whichever is loaded.
    /// </summary>
    void HuntSymbol()
    {
        if (_symbol == null || _symbol.sprite != null) return;
        if (Time.unscaledTime < _nextSymbolHunt) return;
        _nextSymbolHunt = Time.unscaledTime + 1f;

        var sprite = FindNoMapSprite();
        if (sprite == null)
        {
            // Only now is a caption worth having: with no symbol, an empty panel
            // is indistinguishable from a bug.
            if (_noMapText != null && _noMapText.text.Length == 0)
                _noMapText.text = "No map of this area";
            return;
        }

        DsWidgets.FitCentred(_symbol, sprite, SymbolSize, SymbolSize);
        if (_noMapText != null) _noMapText.text = "";
        Debug.Log("[DsMap] no-map symbol: '" + sprite.name + "'");
    }

    static Sprite FindNoMapSprite()
    {
        try
        {
            var gc = GameCameras.instance;
            if (gc != null && gc.hudCamera != null)
            {
                var hud = gc.hudCamera.GetComponent<HUDCamera>();
                if (hud != null && hud.GameplayChild != null)
                {
                    var t = hud.GameplayChild.transform.Find("Quick Map/No Map/No_Map_symbol");
                    if (t != null)
                    {
                        var sr = t.GetComponent<SpriteRenderer>();
                        if (sr != null && sr.sprite != null) return sr.sprite;
                    }
                }
            }
        }
        catch { }

        try
        {
            var all = Resources.FindObjectsOfTypeAll<SpriteRenderer>();
            for (int i = 0; i < all.Length; i++)
            {
                var sr = all[i];
                if (sr == null || sr.sprite == null) continue;
                if (sr.gameObject.name == "No_Map_symbol") return sr.sprite;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DsMap] no-map sprite scan failed: " + e.Message);
        }
        return null;
    }

    // ── input ───────────────────────────────────────────────────────────────

    public void OnGesture(DsGesture g)
    {
        if (_view == null) return;

        Vector2 p = DsPresentation.ToLayout(g.Position);

        if (_state != State.Map) return;

        switch (g.Type)
        {
            case DsGestureType.Drag:
                if (_mapRect.Contains(p)) _view.Pan(g.Delta);
                break;

            case DsGestureType.Pinch:
                if (_mapRect.Contains(p)) _view.Zoom(g.Scale);
                break;
        }
    }

    void ToggleMode()
    {
        var next = _view.Mode == DsMapView.Frame.Area
                 ? DsMapView.Frame.World
                 : DsMapView.Frame.Area;
        _view.SetMode(next);
    }

    // ── header actions ──────────────────────────────────────────────────────

    public void CollectActions(List<DsAction> into)
    {
        // Offered wherever they would do something, which is the same condition
        // the buttons were drawn under before: a map to reframe, or at least a
        // map somewhere to go back to.
        if (_view == null || !_buttonsShown) return;

        into.Add(new DsAction(
            _view.Mode == DsMapView.Frame.World ? "AREA MAP" : "FULL MAP",
            ToggleMode));
        into.Add(new DsAction("RESET", () => _view.ResetView()));
    }
}
#endif
