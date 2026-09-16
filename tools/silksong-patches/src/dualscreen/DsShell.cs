// DsShell — the frame around the screens: a tab strip, one visible screen at a
// time, and the routing that gets a gesture to the right place.
//
// The shell owns nothing about what any screen shows. It knows the list, which
// one is visible, and how to switch. That is the whole reason the screens are
// separable, so it is worth keeping the file boring.
//
// Every call into a screen is wrapped. A screen that throws is disabled and the
// shell keeps running: the second screen must never be able to take the game
// down, and a broken Journal must not cost the player their map.

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TmpText = TMProOld.TextMeshProUGUI;
using TmpAlign = TMProOld.TextAlignmentOptions;

public class DsShell
{
    class Entry
    {
        public IDsScreen Screen;
        public InventoryPaneList.PaneTypes Pane;
        public RectTransform Host;
        public RectTransform Tab;
        public Image TabIcon;
        public TmpText TabLabel;
        /// <summary>Where the caret sits for this tab, in the tab bar's space.</summary>
        public Rect CaretRect;
        /// <summary>The icon's outer box in the strip, which CaretRect is centred in.</summary>
        public Rect CaretBox;
        public bool WarnedMissingIcon;
        public bool Built;
        public bool Broken;
    }

    readonly List<Entry> _entries = new List<Entry>();
    readonly RectTransform _root;
    readonly float _w, _h;
    readonly DsLayout _layout;
    readonly DsShellInput _gestures = new DsShellInput();

    RectTransform _tabBar;
    RectTransform _body;
    RectTransform _header;
    DsHudView _hud;
    bool _visible;
    float _nextArtRefresh;
    float _artWaitSince = -1f;
    readonly DsTitleCard _title = new DsTitleCard();
    // One caret for the strip, which travels between tabs rather than being
    // switched on inside whichever tab is chosen.
    readonly DsCursor _tabCursor = new DsCursor();
    // The header's buttons, filled by whichever screen is visible.
    readonly DsActionBar _actions = new DsActionBar();
    readonly List<DsAction> _actionBuffer = new List<DsAction>();
    // The screen's name in the header, in the space the tools used to fill.
    TmpText _headerTitle;
    // Starts idle. The shell is built before anything is known about whether a
    // save is loaded, and defaulting to a screen meant the panel opened on an
    // empty Inventory and only corrected itself once the idle grace expired.
    // The title card is the honest answer to "no idea yet", so it is the one
    // that costs nothing to be wrong about.
    bool _idle = true;
    int _active = -1;

    // ── the slide ───────────────────────────────────────────────────────────
    //
    // Switching tabs moves the content sideways in the direction the tab strip
    // says: pick a tab to the LEFT of the current one and the new screen comes
    // in from the left, pick one to the right and it comes in from the right.
    // Both screens are alive for the length of it, which is what makes it read
    // as one strip of pages moving rather than as a cut between two.
    //
    // The body already has a RectMask2D, so a screen shifted out of the body's
    // rect is clipped for free and nothing is needed to hide it.
    //
    // Duration is a knob rather than a constant, because this is exactly the
    // kind of thing that wants trying at several speeds on the device, and the
    // dev loop for a rebuild is about ten minutes. Zero turns it off.
    //
    // Snappy on purpose. This is a tab switch, not a page turn: the slide is
    // there to say WHICH WAY you moved along the strip, and once that has been
    // said the player is waiting. 150 undersold it -- the movement was over
    // before the eye had followed it, which reads as a flicker rather than as a
    // direction. 300 is long enough to be seen and short enough not to be sat
    // through, and with the ease-out most of the distance is still covered in
    // the first half of it.
    readonly float _slideSeconds =
        Mathf.Clamp(DsConfig.Int("tab_slide_ms", 300), 0, 2000) / 1000f;
    int _slideFrom = -1;
    float _slideT;
    float _slideDir;

    bool Sliding => _slideFrom >= 0;

    public DsShell(RectTransform root)
    {
        _root = root;
        _layout = DsLayout.Current;
        _w = _layout.Width; _h = _layout.Height;
        Build();
    }

    public bool LayoutChanged
    {
        get
        {
            Vector2 size = DsPresentation.LayoutSize;
            return size.x > 0f && size.y > 0f && !_layout.MatchesSize(size);
        }
    }

    void Build()
    {
        var bg = DsWidgets.Box(_root, "shell-bg", DsTheme.Ground);
        DsWidgets.Stretch(bg.rectTransform);

        // Frame elements draw after the clipped content.
        _body = DsWidgets.Rect(_root, "body");
        DsWidgets.Place(_body, _layout.Body);
        _body.gameObject.AddComponent<RectMask2D>();

        _header = DsWidgets.Box(_root, "hud-header", DsTheme.Ground).rectTransform;
        DsWidgets.Place(_header, _layout.Hud);
        _hud = _header.gameObject.AddComponent<DsHudView>();
        _hud.Build(_header, _layout.Hud.width, _layout.Hud.height);
        DsWidgets.HRule(_header, "rule", DsTheme.Pad, _layout.Hud.height,
                        _w - DsTheme.Pad * 2f);

        // Built into the header, after the HUD, so its labels draw over the
        // ground rather than under the health's render texture.
        _actions.Build(_header, _w);

        // The screen's name, beside the silk bar. Placed per frame rather than
        // here: the space it sits in depends on the player's maximum silk and
        // on the HUD's framing, neither of which is known at build time.
        //
        // The DISPLAY face, which is caps-only and correct for the tab names we
        // write ourselves -- but the Map overrides this with a zone name from
        // the game, which is mixed case, so the face is chosen per frame too.
        _headerTitle = DsWidgets.Label(_header, "screen-title", "", TitleSize,
                                       DsTheme.Ink, TmpAlign.Bottom, display: true);
        DsWidgets.SetActive(_headerTitle, false);

        _tabBar = DsWidgets.Rect(_root, "tabs");
        DsWidgets.Place(_tabBar, _layout.Tabs);

        var strip = DsWidgets.Box(_tabBar, "tab-bg", DsTheme.Ground);
        DsWidgets.Stretch(strip.rectTransform);

        // No rule above the tabs. The icons are already separated from the
        // content by the band of black they sit in, and a second line there
        // boxed the body in: with a rule under the HUD as well, the content
        // read as a panel with a frame rather than as the page it is. The
        // rule under the HUD stays, because that one divides two DIFFERENT
        // things -- what you are, and what you are looking at.

        // Built last so it covers everything, because that is exactly its job:
        // outside a save there is no screen worth showing and no tab worth
        // offering, so the whole frame goes away rather than sitting there
        // greyed out.
        _title.Build(_root, Mathf.RoundToInt(_w), Mathf.RoundToInt(_h));

        // Match the initial _idle state, rather than waiting for the first
        // SetIdle to disagree with it.
        _tabBar.gameObject.SetActive(false);
        _body.gameObject.SetActive(false);
        _header.gameObject.SetActive(false);
        _title.SetVisible(true);
    }

    /// <summary>
    /// Outside a save, show the game's title instead of the tabs.
    ///
    /// The shell owns this rather than each screen, because "there is no save
    /// loaded" is a fact about the whole panel, not about the Journal. It also
    /// means a screen can no longer disagree with its neighbours about how to
    /// say so, which is what five separate grey "Main menu" labels amounted to.
    /// </summary>
    public void SetIdle(bool idle)
    {
        if (idle == _idle) return;
        _idle = idle;
        _gestures.Reset();

        // A slide caught by the title card would resume against hosts that are
        // no longer on screen, and leave one shifted when it came back.
        EndSlide();
        if (idle) _actions.Clear();

        _tabBar.gameObject.SetActive(!idle);
        _body.gameObject.SetActive(!idle);
        _header.gameObject.SetActive(!idle);
        _hud.SetVisible(!idle && _visible);
        _title.SetVisible(idle);

        // Hide the active screen properly on the way out, so it stops ticking
        // and stops driving anything of the game's -- the map screen in
        // particular holds the game's map open while it is visible.
        if (_active >= 0 && _active < _entries.Count)
        {
            var e = _entries[_active];
            if (!e.Broken) Guard(e, () => { if (idle) e.Screen.OnHide(); else e.Screen.OnShow(); });
        }
    }

    public void SetVisible(bool visible)
    {
        _visible = visible;
        _hud.SetVisible(visible && !_idle);
        if (!visible) _gestures.Reset();
    }

    public void Dispose()
    {
        _hud.Stop();
        _gestures.Reset();
    }

    public void Register(IDsScreen screen, InventoryPaneList.PaneTypes pane)
    {
        var host = DsWidgets.Rect(_body, "screen-" + screen.Id);
        DsWidgets.Stretch(host);
        host.gameObject.SetActive(false);

        _entries.Add(new Entry { Screen = screen, Pane = pane, Host = host });
    }

    /// <summary>Lay the tabs out once every screen has registered.</summary>
    public void Finish(string preferredId)
    {
        LayoutTabs();

        int start = _entries.FindIndex(e => e.Screen.Id == preferredId);
        if (start < 0) start = 0;
        Show(start);
    }

    void LayoutTabs()
    {
        int n = _entries.Count;
        if (n == 0) return;
        for (int i = 0; i < n; i++)
        {
            var e = _entries[i];
            Rect bounds = _layout.TabRect(i, n);
            e.Tab = DsWidgets.Rect(_tabBar, "tab-" + e.Screen.Id);
            DsWidgets.Place(e.Tab, bounds.x, 0f, bounds.width, bounds.height);

            float size = Mathf.Min(144f, bounds.height - 16f);
            var art = DsWidgets.Rect(e.Tab, "art");
            DsWidgets.Place(art, (bounds.width - size) * 0.5f,
                            (bounds.height - size) * 0.5f, size, size);
            e.TabIcon = DsWidgets.Icon(art, "icon", null, Color.white);

            // Where the caret sits for this tab, in the tab bar's space. Starts
            // as the icon's whole box and is narrowed to the art itself once
            // the sprite arrives -- see RefreshTabArt.
            float icon = Mathf.Min(88f, bounds.height - 40f);
            e.CaretBox = new Rect(bounds.x + (bounds.width - icon) * 0.5f,
                                  (bounds.height - icon) * 0.5f, icon, icon);
            e.CaretRect = e.CaretBox;

            string title = "?";
            try { title = e.Screen.Title; } catch { }
            e.TabLabel = DsWidgets.Label(e.Tab, "label", title, DsTheme.BodySize,
                                         DsTheme.InkDim, TmpAlign.Center, display: true);
            if (e.TabLabel != null) DsWidgets.Stretch(e.TabLabel.rectTransform);
        }

        // Built after the tabs so its brackets draw over them, and its light
        // goes at index 1 -- after the strip's opaque black backdrop, which
        // would otherwise cover it completely.
        //
        // No box adjustment of its own: it frames the icon's fitted art, and
        // DsCursor's shared constant brings the brackets in from there.
        _tabCursor.Build(_tabBar, glowIndex: 1);
    }

    void RefreshTabArt()
    {
        if (!DsGameData.InGame || Time.unscaledTime < _nextArtRefresh) return;
        _nextArtRefresh = Time.unscaledTime + 1f;
        if (_artWaitSince < 0f) _artWaitSince = Time.unscaledTime;

        try
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                var sprite = DsGameArt.TabIcon(e.Pane);
                if (e.TabIcon.sprite != sprite)
                {
                    e.TabIcon.sprite = sprite;
                    if (sprite != null)
                    {
                        float size = Mathf.Min(88f, _layout.Tabs.height - 40f);
                        DsWidgets.FitCentred(e.TabIcon, sprite, size, size);
                        // FitCentred sizes the rect to the sprite's own aspect
                        // and shifts it so the trimmed mesh is centred, so the
                        // rect it leaves IS the visible art -- which is the box
                        // the caret should be on, rather than the square the
                        // icon was fitted into. Same reasoning as IconRect.
                        var rt = e.TabIcon.rectTransform;
                        Vector2 half = rt.sizeDelta * 0.5f;
                        Vector2 mid = new Vector2(
                            e.CaretBox.x + e.CaretBox.width * 0.5f + rt.anchoredPosition.x,
                            e.CaretBox.y + e.CaretBox.height * 0.5f - rt.anchoredPosition.y);
                        e.CaretRect = new Rect(mid.x - half.x, mid.y - half.y,
                                               rt.sizeDelta.x, rt.sizeDelta.y);
                        if (i == _active) Paint();
                        Debug.Log("[DsTabs] " + e.Screen.Id + " icon='" + sprite.name + "'");
                    }
                }
                DsWidgets.SetActive(e.TabIcon, sprite != null);
                DsWidgets.SetActive(e.TabLabel, sprite == null);
                if (sprite == null && !e.WarnedMissingIcon && Time.unscaledTime - _artWaitSince > 10f)
                {
                    e.WarnedMissingIcon = true;
                    Debug.LogWarning("[DsTabs] " + e.Screen.Id + " icon unavailable; using its label");
                }
            }
            Paint();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DsTabs] art refresh failed: " + e.Message);
        }
    }

    public void Show(int index)
    {
        if (index < 0 || index >= _entries.Count) return;
        if (index == _active) return;

        // A switch made DURING a slide settles the one in flight first, so two
        // slides can never be moving the same hosts in opposite directions.
        // Tapping along the tab strip quickly is a normal thing to do.
        EndSlide();

        int from = _active;
        _active = index;
        var e = _entries[index];

        // Built on first show, not at startup: an unused screen costs nothing,
        // and one that throws while building disables only itself.
        if (!e.Built && !e.Broken)
        {
            e.Built = true;
            Guard(e, () => e.Screen.Build(e.Host));
        }

        e.Host.gameObject.SetActive(true);
        Guard(e, () => e.Screen.OnShow());
        // The outgoing screen's actions are not the incoming one's.
        _actions.Clear();

        bool canSlide = _slideSeconds > 0f && from >= 0 && from < _entries.Count
                        && !e.Broken && !_entries[from].Broken;
        if (canSlide)
        {
            // The outgoing screen keeps its host active and is hidden at the
            // END of the slide, not now -- it has to stay on screen to be the
            // thing sliding off.
            _slideFrom = from;
            _slideT = 0f;
            _slideDir = index > from ? 1f : -1f;
            Shift(e.Host, _slideDir * _w);
            Shift(_entries[from].Host, 0f);
        }
        else if (from >= 0 && from < _entries.Count)
        {
            var prev = _entries[from];
            Shift(prev.Host, 0f);
            prev.Host.gameObject.SetActive(false);
            Guard(prev, () => prev.Screen.OnHide());
        }

        Paint();
    }

    /// <summary>
    /// Move a screen's host sideways within the body.
    ///
    /// Both offsets, not anchoredPosition: the hosts are stretched to the body,
    /// and for a stretched rect the offsets ARE its edges. Shifting both by the
    /// same amount translates it and leaves its size alone.
    /// </summary>
    static void Shift(RectTransform host, float dx)
    {
        if (host == null) return;
        host.offsetMin = new Vector2(dx, 0f);
        host.offsetMax = new Vector2(dx, 0f);
    }

    void TickSlide(float dt)
    {
        if (!Sliding) return;

        _slideT += dt / Mathf.Max(0.001f, _slideSeconds);
        if (_slideT >= 1f) { EndSlide(); return; }

        // Ease out. A linear slide reads as mechanical at this distance and
        // this duration; arriving slowly is what makes it feel like paper.
        float k = 1f - Mathf.Pow(1f - _slideT, 3f);

        if (_active >= 0 && _active < _entries.Count)
            Shift(_entries[_active].Host, (1f - k) * _slideDir * _w);
        if (_slideFrom >= 0 && _slideFrom < _entries.Count)
            Shift(_entries[_slideFrom].Host, -k * _slideDir * _w);
    }

    /// <summary>Put both screens where the slide was going to leave them.</summary>
    void EndSlide()
    {
        if (!Sliding) return;

        int from = _slideFrom;
        _slideFrom = -1;

        if (from >= 0 && from < _entries.Count)
        {
            var prev = _entries[from];
            Shift(prev.Host, 0f);
            prev.Host.gameObject.SetActive(false);
            Guard(prev, () => prev.Screen.OnHide());
        }
        if (_active >= 0 && _active < _entries.Count)
            Shift(_entries[_active].Host, 0f);
    }

    public void Next(int direction)
    {
        if (_entries.Count == 0) return;
        int i = _active;
        for (int step = 0; step < _entries.Count; step++)
        {
            i = (i + direction + _entries.Count) % _entries.Count;
            var e = _entries[i];
            if (e.Broken) continue;
            bool ok = true;
            try { ok = e.Screen.Available; } catch { ok = false; }
            if (ok) { Show(i); return; }
        }
    }

    /// <summary>The id of the visible screen, for persisting across runs.</summary>
    public string ActiveId => (_active >= 0 && _active < _entries.Count) ? _entries[_active].Screen.Id : null;

    void Paint()
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            bool on = i == _active;
            if (e.TabIcon != null)
                e.TabIcon.color = e.Broken ? DsTheme.InkFaint : on ? Color.white : DsTheme.InkDim;
            if (e.TabLabel != null) e.TabLabel.color = e.Broken ? DsTheme.InkFaint
                                                    : on ? DsTheme.Ink : DsTheme.InkDim;
        }

        // The caret goes to the chosen tab, or nowhere if there is not yet an
        // icon under it to bracket.
        if (_active >= 0 && _active < _entries.Count)
        {
            var e = _entries[_active];
            bool show = !e.Broken && e.TabIcon != null && e.TabIcon.sprite != null;
            if (show) _tabCursor.MoveTo(e.CaretRect, null, e.Screen.Id);
            else _tabCursor.Hide();
        }
        else _tabCursor.Hide();
    }

    public void Tick(float dt)
    {
        if (_idle) { _title.Tick(); return; }
        RefreshTabArt();
        _tabCursor.Tick(dt);
        TickSlide(dt);

        // The screen sliding OUT is still on screen, so it still gets ticked.
        //
        // Not ticking it is nearly invisible for a screen made of static rects,
        // and very visible for the Map: its panel is a RawImage over a render
        // texture that only holds a picture for as long as the view is being
        // driven, so a map that stopped ticking went stale part-way across
        // instead of sliding off. Anything that draws live has the same
        // problem, so the fix belongs here rather than in the map.
        if (Sliding && _slideFrom >= 0 && _slideFrom < _entries.Count)
        {
            var going = _entries[_slideFrom];
            if (!going.Broken) Guard(going, () => going.Screen.Tick(dt));
        }

        if (_active < 0 || _active >= _entries.Count) return;
        var e = _entries[_active];
        if (e.Broken) return;
        Guard(e, () => e.Screen.Tick(dt));
        RefreshActions(e);
        RefreshTitle(e);
    }

    // Sized against the designs, where the title is the largest thing on the
    // header after the health itself. A knob, because it is judged by eye and
    // DsConfig costs a restart rather than a rebuild.
    static float TitleSize => Mathf.Clamp(DsConfig.Int("header_title_px", 52), 12, 120);

    /// <summary>Air left between the title and the rule under the header.</summary>
    static float TitleBottomGap => Mathf.Clamp(DsConfig.Int("header_title_gap_px", 6), 0, 80);

    /// <summary>
    /// Put the screen's name beside the silk bar, in the room the tools left.
    ///
    /// Re-placed every frame because that room is not fixed: it ends where the
    /// health ends, and the health grows and shrinks with the player's masks.
    /// </summary>
    void RefreshTitle(Entry e)
    {
        if (_headerTitle == null) return;

        string text = null;
        var custom = e.Screen as IDsHeaderTitle;
        if (custom != null) { try { text = custom.HeaderTitle; } catch { } }
        if (string.IsNullOrEmpty(text)) { try { text = e.Screen.Title; } catch { } }

        Rect space = _hud != null ? _hud.TitleSpace : new Rect(0f, 0f, 0f, 0f);
        bool show = !string.IsNullOrEmpty(text) && space.height > 10f && !e.Broken;
        DsWidgets.SetActive(_headerTitle, show);
        if (!show) return;

        // Upper case, always. A tab name is already written that way; a zone
        // name arrives from the game in mixed case ("Choral Chambers") and is
        // raised to match, which also keeps it on the display face -- Trajan
        // has no real lowercase, see DsWidgets.Label.
        text = text.ToUpperInvariant();
        var font = DsTheme.Display;
        if (font != null && _headerTitle.font != font)
        {
            _headerTitle.font = font;
            try { if (font.material != null) _headerTitle.fontSharedMaterial = font.material; } catch { }
        }

        if (_headerTitle.text != text) _headerTitle.text = text;
        // Centred on the PANEL rather than in the gap it sits in, which is what
        // the designs do: the gap is wherever the health happens to end, and a
        // title centred in it would wander as the player gains and loses masks.
        // Only its row comes from the gap.
        //
        // Sat on the BOTTOM of that row, a little clear of the rule beneath it.
        // Centred vertically it floated in the middle of a band taller than the
        // words, which read as a gap between the title and the divider rather
        // than as a title above one.
        DsWidgets.Place(_headerTitle.rectTransform, 0f, space.y, _w,
                        Mathf.Max(10f, space.height - TitleBottomGap));
    }

    /// <summary>
    /// Ask the visible screen what can be done right now.
    ///
    /// Every frame, and pulled rather than pushed, which is what lets an action
    /// appear and withdraw on its own: USE is offered only while the cursor is
    /// on something consumable, and nothing has to remember to take it away
    /// when the selection moves or the last of an item is drunk.
    /// </summary>
    void RefreshActions(Entry e)
    {
        var source = e.Screen as IDsActionBar;
        if (source == null) { _actions.Clear(); return; }

        _actionBuffer.Clear();
        Guard(e, () => source.CollectActions(_actionBuffer));
        _actions.Set(_actionBuffer);
    }

    public void OnGesture(DsGesture g)
    {
        // Nothing to press on the title card.
        if (_idle) return;

        // The header's buttons first, and before the shell's own routing: they
        // sit outside the body, which is the only region that routing knows
        // about, and a tap on one is not a tap on the screen beneath.
        if (g.Type == DsGestureType.Tap &&
            _actions.OnTap(_layout.ToLayout(g.Position))) return;

        int tab;
        var target = _gestures.Route(g, _layout, _entries.Count, out tab);
        if (target == DsGestureTarget.Tab)
        {
            Show(tab);
            return;
        }
        if (target != DsGestureTarget.Body) return;

        // Not while the content is moving. Every screen hit-tests against the
        // rectangles it was LAID OUT at, so a tap part-way through a slide
        // lands on whatever is nominally at that point rather than on what the
        // player can see there. Tabs still work, so a slide can be cut short
        // by picking another one.
        if (Sliding) return;

        if (_active < 0 || _active >= _entries.Count) return;
        var e = _entries[_active];
        if (e.Broken) return;
        Guard(e, () => e.Screen.OnGesture(g));
    }

    // One place where a screen's exception is turned into that screen being
    // switched off, so the failure is contained and visible rather than fatal.
    void Guard(Entry e, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            e.Broken = true;
            if (e.Host != null) e.Host.gameObject.SetActive(false);
            Debug.LogError("[DualScreen] screen '" + e.Screen.Id + "' disabled after error: " + ex);
            Paint();
        }
    }
}
#endif
