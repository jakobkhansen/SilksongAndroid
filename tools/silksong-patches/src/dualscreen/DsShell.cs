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
        public Image TabIcon, CornerTL, CornerBR;
        public TmpText TabLabel;
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
    // Starts idle. The shell is built before anything is known about whether a
    // save is loaded, and defaulting to a screen meant the panel opened on an
    // empty Inventory and only corrected itself once the idle grace expired.
    // The title card is the honest answer to "no idea yet", so it is the one
    // that costs nothing to be wrong about.
    bool _idle = true;
    int _active = -1;

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

        _tabBar = DsWidgets.Rect(_root, "tabs");
        DsWidgets.Place(_tabBar, _layout.Tabs);

        var strip = DsWidgets.Box(_tabBar, "tab-bg", DsTheme.Ground);
        DsWidgets.Stretch(strip.rectTransform);

        DsWidgets.HRule(_tabBar, "rule", DsTheme.Pad, 0f, _w - DsTheme.Pad * 2f);

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

            float size = Mathf.Min(144f, bounds.height - 32f);
            var art = DsWidgets.Rect(e.Tab, "art");
            DsWidgets.Place(art, (bounds.width - size) * 0.5f,
                            (bounds.height - size) * 0.5f, size, size);
            e.TabIcon = DsWidgets.Icon(art, "icon", null, Color.white);
            e.CornerTL = DsWidgets.CursorCorner(art, "c-tl", null,
                new Vector2(0f, 1f), false, 48f, 18f);
            e.CornerBR = DsWidgets.CursorCorner(art, "c-br", null,
                new Vector2(1f, 0f), true, 48f, 18f);

            string title = "?";
            try { title = e.Screen.Title; } catch { }
            e.TabLabel = DsWidgets.Label(e.Tab, "label", title, DsTheme.BodySize,
                                         DsTheme.InkDim, TmpAlign.Center, display: true);
            if (e.TabLabel != null) DsWidgets.Stretch(e.TabLabel.rectTransform);
        }
    }

    void RefreshTabArt()
    {
        if (!DsGameData.InGame || Time.unscaledTime < _nextArtRefresh) return;
        _nextArtRefresh = Time.unscaledTime + 1f;
        if (_artWaitSince < 0f) _artWaitSince = Time.unscaledTime;

        try
        {
            var cursor = DsGameArt.SelectionCursor();
            foreach (var e in _entries)
            {
                var sprite = DsGameArt.TabIcon(e.Pane);
                if (e.TabIcon.sprite != sprite)
                {
                    e.TabIcon.sprite = sprite;
                    if (sprite != null)
                    {
                        float size = Mathf.Min(88f, _layout.Tabs.height - 72f);
                        DsWidgets.FitCentred(e.TabIcon, sprite, size, size);
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
                e.CornerTL.sprite = e.CornerBR.sprite = cursor.Corner;
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

        if (_active >= 0 && _active < _entries.Count)
        {
            var prev = _entries[_active];
            prev.Host.gameObject.SetActive(false);
            Guard(prev, () => prev.Screen.OnHide());
        }

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
        Paint();
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
            bool caret = on && !e.Broken && e.TabIcon.sprite != null && e.CornerTL.sprite != null;
            e.CornerTL.color = e.CornerBR.color = Color.white;
            DsWidgets.SetActive(e.CornerTL, caret);
            DsWidgets.SetActive(e.CornerBR, caret);
        }
    }

    public void Tick(float dt)
    {
        if (_idle) { _title.Tick(); return; }
        RefreshTabArt();
        if (_active < 0 || _active >= _entries.Count) return;
        var e = _entries[_active];
        if (e.Broken) return;
        Guard(e, () => e.Screen.Tick(dt));
    }

    public void OnGesture(DsGesture g)
    {
        // Nothing to press on the title card.
        if (_idle) return;

        int tab;
        var target = _gestures.Route(g, _layout, _entries.Count, out tab);
        if (target == DsGestureTarget.Tab)
        {
            Show(tab);
            return;
        }
        if (target != DsGestureTarget.Body) return;

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
