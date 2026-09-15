// DsJournalScreen — the hunter's journal, as a picture rather than a grid.
//
// It was five columns of large square portraits with a caption strip along the
// bottom: a wall of art with nothing to look at. The creature you are reading
// about deserves the space, and the ones you are not are just a way of choosing.
//
//     +----------------+---------------------------+
//     |  (o) (o) (o)   |                           |
//     |  (o) (o) (o)   |     the selected           |
//     |  (o) (o) (o)   |     creature, large        |
//     |  (o) (o) (o)   |                           |
//     |   scrolls      +---------------------------+
//     |  (o) (o) (o)   |  Mossgrub          47/25  |
//     |                |  description, notes       |
//     +----------------+---------------------------+
//
// The game gives two sprites per record and they are not the same picture:
// IconSprite is the small one, EnemySprite the full creature. The list uses the
// first and the portrait the second, which is what each was drawn for.
//
// The circles are a real circular crop, not a round frame with a square picture
// inside it -- the portraits have opaque backgrounds, so a frame alone would
// still read as a square. A uGUI Mask over our generated disc does the clipping.

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TmpText = TMProOld.TextMeshProUGUI;
using TmpAlign = TMProOld.TextAlignmentOptions;

public class DsJournalScreen : IDsScreen
{
    class Entry
    {
        public string Name, Desc, Notes;
        public Sprite Icon, Portrait;
        public int Kills, Required;
        public bool Unseen;
        /// <summary>Listed by Farsight but never met: a silhouette, not a name.</summary>
        public bool Unknown;
    }

    class Cell
    {
        public RectTransform Root;
        public Image Ring, Art;
    }

    // Left column: the chooser. Three to a row, small, because their job is to
    // be recognised rather than admired.
    const float ListX = 20f;
    const float ListW = 380f;
    const int   Columns = 3;
    const float CellGap = 14f;
    // How far the clip extends past the list on each side, so a bracket on an
    // outer cell is drawn rather than shaved. See the clip in Build.
    const float CursorBleed = 6f;

    // Centre column: the creature. Right column: what is known about it.
    //
    // These were one column, the portrait stacked on top of the prose with a
    // rule between them. Splitting them gives the portrait its full height --
    // it is the thing you are looking at -- and puts the text beside it rather
    // than beneath it, which is the same shape the other tabs now use: chooser,
    // subject, description.
    const float PortraitX = 430f;   // art:   430 .. 860
    const float PortraitW = 430f;
    const float DetailX   = 890f;   // prose: 890 .. 1220
    const float DetailW   = 330f;
    // Sized for a 330 px column; the old 48/36 was chosen for one twice as wide.
    const float DetailTitleSize = 40f;
    const float DetailBodySize = 30f;

    readonly List<Entry> _entries = new List<Entry>();
    readonly List<Cell> _cells = new List<Cell>();
    // One cursor for the chooser, with its brackets pulled in along the
    // diagonal to sit against the circular portraits rather than their boxes.
    readonly DsCursor _cursor = new DsCursor();

    RectTransform _host, _list, _portraitBox, _detail;
    Image _portrait;
    TmpText _name, _desc, _empty;

    Rect _listRect;
    float _cell, _cellH, _listTop, _listH;
    float _portraitW, _portraitH;
    float _scroll, _maxScroll;
    int _selected = -1;
    string _selectedKey, _signature;
    float _nextRefresh;

    public string Id { get { return "journal"; } }
    public string Title { get { return "JOURNAL"; } }
    public bool Available { get { return DsGameData.InGame; } }

    // ── build ───────────────────────────────────────────────────────────────

    public void Build(RectTransform host)
    {
        _host = host;

        var layout = DsLayout.Current;
        float bodyH = layout.Body.height;

        _listTop = 16f;
        _listH = bodyH - _listTop - 16f;
        _listRect = layout.InBody(new Rect(ListX, _listTop, ListW, _listH));

        _cell = (ListW - CellGap * (Columns - 1)) / Columns;
        // Square. The cell used to carry a kill count under the portrait, and
        // the row height carried it too.
        _cellH = _cell;

        // Clipped by a rect of its own rather than by the list, grown a little
        // on every side. The cursor brackets are drawn at the edge of a cell,
        // and a mask sized exactly to the columns shaves them off the outer
        // ones -- the columns that most need to show which cell is selected.
        // The list keeps its exact size and position inside the clip, so
        // layout, scrolling and hit-testing are all unaffected.
        var clip = DsWidgets.Rect(host, "list-clip");
        DsWidgets.Place(clip, ListX - CursorBleed, _listTop - CursorBleed,
                        ListW + CursorBleed * 2f, _listH + CursorBleed * 2f);
        clip.gameObject.AddComponent<RectMask2D>();

        _list = DsWidgets.Rect(clip, "list");
        DsWidgets.Place(_list, CursorBleed, CursorBleed, ListW, _listH);

        _empty = DsWidgets.Label(_list, "empty", "No creatures recorded", DsTheme.RowSize,
                                 DsTheme.InkDim, TmpAlign.Center);
        if (_empty != null) DsWidgets.Stretch(_empty.rectTransform);

        // ── centre and right ───────────────────────────────────────────────
        float colH = bodyH - _listTop - 16f;

        // One rule per boundary, down the middle of each gutter.
        DsWidgets.VRule(host, "split-art", (ListX + ListW + PortraitX) * 0.5f,
                        _listTop, colH);
        DsWidgets.VRule(host, "split-detail", (PortraitX + PortraitW + DetailX) * 0.5f,
                        _listTop, colH);

        _portraitBox = DsWidgets.Rect(host, "portrait");
        DsWidgets.Place(_portraitBox, PortraitX, _listTop, PortraitW, colH);

        _portrait = DsWidgets.Icon(_portraitBox, "art", null, Color.white);
        _portraitW = PortraitW - 48f;
        _portraitH = colH - 48f;
        DsWidgets.Place(_portrait.rectTransform, 24f, 24f, _portraitW, _portraitH);

        // No rule across the top of the description: it is a column of its own
        // now, and the gutter rule beside it is already the boundary.
        _detail = DsWidgets.Rect(host, "detail");
        DsWidgets.Place(_detail, DetailX, _listTop, DetailW, colH);

        _name = DsWidgets.Label(_detail, "name", "", DetailTitleSize,
                                DsTheme.Ink, TmpAlign.Left);
        if (_name != null) DsWidgets.Place(_name.rectTransform, 0f, 8f, DetailW, 52f);

        _desc = DsWidgets.Label(_detail, "desc", "", DetailBodySize,
                                DsTheme.InkDim, TmpAlign.TopLeft);
        if (_desc != null)
            DsWidgets.Place(_desc.rectTransform, 0f, 68f, DetailW, colH - 76f);

        // Last, so the brackets draw over the portraits. Half of 1 - 1/sqrt2:
        // the diagonal gap of a circle inscribed in its cell, split across the
        // two axes.
        _cursor.CornerInset = _cell * 0.1465f;
        _cursor.Build(host);

        Refresh(force: true);
    }

    public void OnShow() { Refresh(force: true); }
    public void OnHide() { }

    public void Tick(float dt)
    {
        // Before the refresh gate: the cursor animates every frame, and the
        // data is only re-read once a second.
        _cursor.Tick(dt);
        if (Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + 1f;
        Refresh(force: false);
    }

    // ── data ────────────────────────────────────────────────────────────────

    void Refresh(bool force)
    {
        _entries.Clear();

        if (!DsGameData.InGame) { Apply(""); return; }

        try { Collect(_entries); }
        catch (Exception e) { Debug.LogWarning("[DsJournal] " + e.Message); }

        var sig = new System.Text.StringBuilder();
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            sig.Append(e.Name).Append(e.Kills).Append('/').Append(e.Required).Append(';');
        }
        Apply(sig.ToString());
    }

    void Collect(List<Entry> into)
    {
        // The game's own choice of list, from JournalItemManager.GetItems():
        // without Farsight the journal shows only what you have actually
        // killed, and with it the full required set including creatures you
        // have never met. Using GetAllEnemies() and filtering by IsVisible --
        // which is what this screen did first -- produces a different list from
        // the one the game shows, and therefore different totals.
        bool farsight = false;
        try { farsight = PlayerData.instance.ConstructedFarsight; } catch { }

        List<EnemyJournalRecord> records = null;
        try
        {
            records = farsight ? EnemyJournalManager.GetRequiredEnemies()
                               : EnemyJournalManager.GetKilledEnemies();
        }
        catch { }
        if (records == null) return;

        foreach (var rec in records)
        {
            if (rec == null) continue;

            bool visible = false;
            try { visible = rec.IsVisible; } catch { }

            int kills = 0, required = 0;
            try { kills = rec.KillCount; } catch { }
            try { required = rec.KillsRequired; } catch { }

            // An unmet creature is a silhouette in the game's own journal, not
            // an absence -- that is the point of Farsight. Keep the row, drop
            // the name and the art.
            if (!visible)
            {
                into.Add(new Entry
                {
                    Name = "???",
                    Desc = "",
                    Notes = "",
                    Kills = 0,
                    Required = required,
                    Unseen = true,
                    Unknown = true,
                });
                continue;
            }

            Sprite icon = null, portrait = null;
            try { icon = rec.IconSprite; } catch { }
            try { portrait = rec.EnemySprite; } catch { }

            into.Add(new Entry
            {
                Name = Text(rec.DisplayName),
                Desc = Text(rec.Description),
                // Notes are the hunter's own commentary, and the game keeps
                // them locked until the kill requirement is met.
                Notes = kills >= required ? Text(rec.Notes) : "",
                Icon = icon ?? portrait,
                Portrait = portrait ?? icon,
                Kills = kills,
                Required = required,
                Unseen = kills <= 0,
            });
        }
    }

    static string Text(TeamCherry.Localization.LocalisedString s)
    {
        try { return s.ToString(); } catch { return ""; }
    }

    // ── layout ──────────────────────────────────────────────────────────────

    void Apply(string signature)
    {
        if (signature == _signature) { Paint(); return; }
        _signature = signature;

        Rebuild();

        // Selection survives a rebuild, so a kill count ticking over does not
        // throw you back to the top of the list.
        _selected = -1;
        if (_selectedKey != null)
            for (int i = 0; i < _entries.Count; i++)
                if (_entries[i].Name == _selectedKey) { _selected = i; break; }
        if (_selected < 0 && _entries.Count > 0) { _selected = 0; _selectedKey = _entries[0].Name; }

        Paint();
        PaintDetail();
    }

    void Rebuild()
    {
        DsWidgets.SetActive(_empty, _entries.Count == 0);

        while (_cells.Count < _entries.Count) _cells.Add(MakeCell(_cells.Count));
        for (int i = 0; i < _cells.Count; i++)
            DsWidgets.SetActive(_cells[i].Root, i < _entries.Count);

        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            var c = _cells[i];

            if (c.Art != null)
            {
                c.Art.sprite = e.Icon;
                // Unkilled creatures are drawn down rather than hidden, and an
                // unmet one is a black silhouette -- the game's own treatment,
                // and the reason Farsight is worth building.
                c.Art.color = e.Icon == null ? Color.clear
                            : e.Unknown ? new Color(0f, 0f, 0f, 0.55f)
                            : e.Unseen ? new Color(1f, 1f, 1f, 0.35f)
                            : Color.white;
            }
        }

        int rows = (_entries.Count + Columns - 1) / Columns;
        _maxScroll = Mathf.Max(0f, rows * (_cellH + CellGap) - _listH);
        _scroll = Mathf.Clamp(_scroll, 0f, _maxScroll);
    }

    Cell MakeCell(int index)
    {
        var root = DsWidgets.Rect(_list, "cell" + index);

        var ring = DsWidgets.Circle(root, "ring", DsTheme.PanelEdge);
        DsWidgets.Place(ring.rectTransform, 0f, 0f, _cell, _cell);

        // A Mask over the generated disc, so the square portrait is clipped to
        // a circle. showMaskGraphic is off because the ring behind it already
        // draws the edge; this disc exists only to define the shape.
        var maskRt = DsWidgets.Rect(ring.rectTransform, "mask");
        DsWidgets.Stretch(maskRt, 4f);
        var maskImg = maskRt.gameObject.AddComponent<Image>();
        maskImg.sprite = DsTheme.Disc;
        maskImg.raycastTarget = false;
        var mask = maskRt.gameObject.AddComponent<Mask>();
        mask.showMaskGraphic = false;

        var art = DsWidgets.Icon(maskRt, "art", null, Color.white);
        // Deliberately NOT preserving aspect: the portrait should fill the
        // circle, and a letterboxed picture inside a round hole looks like a
        // mistake. The sources are close to square, so the crop is slight.
        art.preserveAspect = false;
        DsWidgets.Stretch(art.rectTransform);

        // The game's own selection cursor -- two filigree corners, the
        // bottom-right one the same sprite turned 180 degrees -- rather than a
        // coloured ring, so selection looks the same here as it does on every
        // other screen and in the game's own inventory.
        //
        // Anchored to the RING rather than to the cell. That mattered when the
        // cell was taller than the circle by a caption; it is kept because the
        // ring is what the bracket is framing either way.
        return new Cell { Root = root, Ring = ring, Art = art };
    }

    void Paint()
    {
        for (int i = 0; i < _entries.Count && i < _cells.Count; i++)
        {
            int col = i % Columns, row = i / Columns;
            float x = col * (_cell + CellGap);
            float y = row * (_cellH + CellGap) - _scroll;
            DsWidgets.Place(_cells[i].Root, x, y, _cell, _cellH);
        }

        PaintCursor();
    }

    // Selection is the game's cursor, not a colour: the ring stays the frame it
    // always was. One cursor travels between the portraits rather than a pair of
    // brackets being switched on inside the chosen one.
    //
    // It lives in the host rather than in the scrolling list, so the list's mask
    // no longer clips it; a portrait scrolled out of the viewport therefore has
    // to have its cursor hidden explicitly.
    void PaintCursor()
    {
        if (_selected < 0 || _selected >= _entries.Count) { _cursor.Hide(); return; }

        int col = _selected % Columns, row = _selected / Columns;
        float x = col * (_cell + CellGap);
        float y = row * (_cellH + CellGap) - _scroll;
        if (y < 0f || y + _cell > _listH) { _cursor.Hide(); return; }

        _cursor.MoveTo(new Rect(ListX + x, _listTop + y, _cell, _cell),
                       null, _entries[_selected].Name);
    }

    void PaintDetail()
    {
        bool ok = _selected >= 0 && _selected < _entries.Count;

        if (_portrait != null)
        {
            if (ok && _entries[_selected].Portrait != null)
            {
                // FitCentred, not a plain assignment: an Image draws a trimmed
                // atlas sprite's own mesh, which is not centred inside its rect,
                // so portraits with uneven transparent margins sat visibly off
                // to one side. Sprite.bounds.center is that offset.
                DsWidgets.FitCentred(_portrait, _entries[_selected].Portrait,
                                     _portraitW, _portraitH);
            }
            else
            {
                _portrait.sprite = null;
                _portrait.color = Color.clear;
            }
        }

        if (_name != null) _name.text = ok ? _entries[_selected].Name : "";

        if (_desc != null)
        {
            if (!ok) { _desc.text = ""; return; }
            var e = _entries[_selected];
            string text = e.Desc ?? "";
            if (!string.IsNullOrEmpty(e.Notes))
                text = string.IsNullOrEmpty(text) ? e.Notes : text + "\n\n" + e.Notes;
            _desc.text = text;
        }
    }

    // ── input ───────────────────────────────────────────────────────────────

    public void OnGesture(DsGesture g)
    {
        Vector2 p = DsPresentation.ToLayout(g.Position);

        switch (g.Type)
        {
            case DsGestureType.Tap:
                int hit = HitTest(p);
                if (hit >= 0)
                {
                    _selected = hit;
                    _selectedKey = _entries[hit].Name;
                    Paint();
                    PaintDetail();
                }
                break;

            case DsGestureType.Drag:
                // Panel y is up, so dragging the finger up scrolls further down
                // the list. Only when the finger is over the list.
                if (_listRect.Contains(p))
                {
                    _scroll = Mathf.Clamp(_scroll + g.Delta.y, 0f, _maxScroll);
                    Paint();
                }
                break;
        }
    }

    // Panel touch -> entry index, in LAYOUT space, because that is the space
    // everything was placed in. Deliberately not RectTransformUtility: the
    // canvas is ScreenSpaceCamera on a display Unity reports as 0x0, so its
    // screen-point conversion silently maps a corner tap into the middle.
    int HitTest(Vector2 layoutPoint)
    {
        if (!_listRect.Contains(layoutPoint)) return -1;
        float x = layoutPoint.x - _listRect.x;
        float y = layoutPoint.y - _listRect.y + _scroll;

        int col = (int)(x / (_cell + CellGap));
        int row = (int)(y / (_cellH + CellGap));
        if (col < 0 || col >= Columns || row < 0) return -1;

        // Reject the gaps, so a tap between two circles selects neither rather
        // than whichever one owns the pixel by rounding.
        if (x - col * (_cell + CellGap) > _cell) return -1;
        if (y - row * (_cellH + CellGap) > _cellH) return -1;

        int index = row * Columns + col;
        return index < _entries.Count ? index : -1;
    }
}
#endif
