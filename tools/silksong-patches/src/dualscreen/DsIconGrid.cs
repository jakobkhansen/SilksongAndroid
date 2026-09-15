// DsIconGrid — a scrolling grid of icons with an optional heading before each
// group, and a detail pane showing whatever is selected.
//
// Icons only: no names in the grid. A name beside every icon costs the width of
// a name for information you already get by tapping, and at arm's length a wall
// of icons is faster to scan than a column of words. It is also how the game's
// own inventory presents the same data.
//
// It exists because Inventory, Tools, Tasks and Journal are the same screen with
// different data, and writing that layout four times would guarantee four
// slightly different versions of it. Screens supply sections; this owns the
// geometry, the selection, the scrolling and the detail pane.
//
// Cells are pooled, and only the visible ones are drawn.

#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TmpText = TMProOld.TextMeshProUGUI;
using TmpAlign = TMProOld.TextAlignmentOptions;

/// <summary>One entry in a grid. Screens produce these; the grid draws them.</summary>
public struct DsItem
{
    public string Name;
    public string Description;
    public Sprite Icon;
    public Color Tint;
    /// <summary>Drawn dim, for items the player has not unlocked.</summary>
    public bool Dim;
    /// <summary>A small count in the corner, e.g. "12". Null for none.</summary>
    public string Badge;
    /// <summary>
    /// Stable identifier, so something outside the grid can select an entry --
    /// tapping a tool socketed in the crest, for instance. Display names would
    /// nearly work and would quietly pick the wrong one for any two items that
    /// share a name.
    /// </summary>
    public string Key;
}

/// <summary>A titled run of items. A grid with one untitled section is a plain grid.</summary>
public class DsSection
{
    public string Title;
    public Color Colour;
    public readonly List<DsItem> Items = new List<DsItem>();

    public DsSection(string title, Color colour) { Title = title; Colour = colour; }
}

public class DsIconGrid
{
    class Cell
    {
        public RectTransform Root;
        public Image Icon;
        public TmpText Badge;
        // The game's own selection cursor: one corner sprite used twice, once
        // rotated 180 degrees, plus a backdrop glow -- borrowed from
        // InventoryCursor rather than drawn, so it is filigree and not right
        // angles. Only two corners are marked; the game's cursor does the same.
        public Image CornerTL, CornerBR, Glow;
    }

    struct Placed
    {
        public int ItemIndex;
        public float X, Y, W, H;   // relative to the grid's top-left
    }

    readonly List<DsSection> _sections = new List<DsSection>();
    readonly List<DsItem> _flat = new List<DsItem>();
    readonly List<Cell> _cells = new List<Cell>();
    readonly List<Placed> _placed = new List<Placed>();
    // Headers scroll with the items. Keeping their unscrolled y here is the
    // whole fix for a bug where the group titles and their rules stayed put
    // while the icons moved underneath them.
    readonly List<RectTransform> _headers = new List<RectTransform>();
    readonly List<float> _headerY = new List<float>();
    const float HeaderH = 54f;

    // Selection brackets. Their centre sits CornerInset inside the cell corner,
    // so the art reaches CornerSize/2 - CornerInset beyond the cell. Flush, so
    // that overhang is zero: anything hanging outside the cell is clipped by
    // the scroll mask on the top row and on both edge columns, which is a
    // bracket that vanishes in exactly the places it is most needed.
    const float CornerSize = 46f;
    const float CornerInset = CornerSize * 0.5f;
    const float CornerOverhang = CornerSize * 0.5f - CornerInset;

    // The count sits in the same corner as the bottom-right bracket, so that
    // one alone is pushed back out far enough to read as a bracket around a
    // number rather than a bracket through one.
    const float BadgeClearance = 10f;

    // ...which puts that bracket outside its cell, and the scroll mask is sized
    // to the columns exactly, so on the last column and the bottom row it was
    // sliced off -- most visibly on the crest screen, whose three columns end
    // flush against the mask. The mask cannot simply be narrowed, because it
    // would only cut in a different place.
    //
    // So the clipping moved out to its own rect, grown by the overhang, and the
    // grid keeps its exact size and position inside it. Nothing that reads
    // _gridW/_gridH -- layout, scrolling, hit-testing -- changes at all; only
    // the rectangle the pixels are allowed to appear in.
    const float CursorBleed = BadgeClearance + 2f;

    // The detail pane's text, sized like the Tasks pane's rather than from the
    // shared theme sizes. Both are the same job: a name and prose about the
    // thing selected beside them, read at arm's length.
    const float DetailTitleSize = 48f;
    const float DetailBodySize = 36f;
    // ...at the width those sizes were chosen for. The pane used to be a band
    // across the whole panel; as a column of its own it is a third of that, and
    // at 48 px an item name like "Pale Oil Lantern" sets a line per word. Below
    // the reference width the face comes down with it, to a floor -- the point
    // is to fit a name on a line or two, not to keep shrinking until prose is
    // unreadable on a 9 cm screen.
    const float DetailRefWidth = 500f;
    const float DetailMinScale = 0.78f;

    RectTransform _grid, _detail;
    TmpText _title, _desc, _empty;

    int _columns;
    float _cell, _gap;
    // The grid's rectangle in LAYOUT space (top-left origin), kept because hit
    // testing is arithmetic in that space rather than a RectTransform query.
    float _gridLeft, _gridTop, _gridW, _gridH;
    float _scroll, _maxScroll;
    int _selected = -1;
    // What is selected, by key rather than index, so a refresh cannot move it.
    string _selectedKey;
    // Set while something outside the grid owns the detail pane.
    bool _external;
    bool _cursorApplied;
    bool _dirty;

    public string EmptyMessage = "Nothing here yet";

    /// <param name="left">Left edge of the grid column, in layout space.</param>
    /// <param name="width">Width of the grid column.</param>
    /// <param name="detail">
    /// Where to put the detail pane, in layout space. Zero width means "directly
    /// under the grid", which is what a full-width screen wants. A screen with
    /// something else beside the grid -- the crest, say -- puts the detail under
    /// THAT instead, so the grid can run the full height of the panel and no
    /// column is left with a hole in it.
    /// </param>
    /// <param name="detailRule">
    /// Whether to rule the detail pane off along its TOP edge.
    ///
    /// True is right when the pane is the bottom of a column: its top edge is
    /// then the only side that divides it from anything. It is wrong when the
    /// pane is a full-height column of its own, which is what the description
    /// section on Inventory and Crest now is -- there the boundary runs down
    /// the gutter beside it and the screen draws that rule itself, so a
    /// horizontal rule here would be a second line across the top of a column
    /// that nothing sits above.
    /// </param>
    public void Build(RectTransform host, int columns,
                      float left = -1f, float width = -1f, Rect detail = default(Rect),
                      bool detailRule = true)
    {
        _columns = Mathf.Max(1, columns);

        var layout = DsLayout.Current;
        float panelW = layout.Width;
        if (left < 0f) left = DsTheme.Pad;
        if (width < 0f) width = panelW - DsTheme.Pad * 2f;
        float h = layout.Body.height;

        bool detailBelow = detail.width <= 0f;
        if (detailBelow)
            detail = new Rect(left, h - DsTheme.FooterHeight, width, DsTheme.FooterHeight - DsTheme.Pad);

        _gap = 10f;
        _cell = (width - _gap * (_columns - 1)) / _columns;

        _gridLeft = left;
        _gridTop = layout.Body.y + DsTheme.Pad;
        _gridW = width;
        _gridH = (detailBelow ? h - DsTheme.FooterHeight : h) - DsTheme.Pad * 2f;

        // Cells are moved to scroll, so without clipping a cell scrolled past
        // the top would draw over the tab strip. The clip is a rect of its own,
        // grown by CursorBleed, so a selection bracket that reaches outside its
        // cell is still drawn -- see the note there.
        var clip = DsWidgets.Rect(host, "grid-clip");
        DsWidgets.Place(clip, left - CursorBleed, DsTheme.Pad - CursorBleed,
                        _gridW + CursorBleed * 2f, _gridH + CursorBleed * 2f);
        clip.gameObject.AddComponent<RectMask2D>();

        _grid = DsWidgets.Rect(clip, "grid");
        DsWidgets.Place(_grid, CursorBleed, CursorBleed, _gridW, _gridH);

        // A rule above the description, not a box around it. This pane is the
        // bottom of a column and its top edge is the only side that actually
        // divides it from anything -- the other three border the panel itself.
        if (detailRule)
            DsWidgets.HRule(host, "detail-rule", detail.x, detail.y - DsTheme.Pad * 0.5f, detail.width);

        _detail = DsWidgets.Rect(host, "detail");
        DsWidgets.Place(_detail, detail.x, detail.y, detail.width, detail.height);

        // No side inset. With the border gone the description aligns to the
        // left edge of the column it describes, which is what makes a ruled
        // layout read as columns rather than as things that happen to be near
        // each other.
        //
        // Sized like the Tasks pane rather than from the shared theme sizes:
        // this is the same job -- a name and prose about whatever is selected
        // beside it -- read at arm's length on a small panel.
        float textScale = Mathf.Clamp(detail.width / DetailRefWidth, DetailMinScale, 1f);
        float titleSize = DetailTitleSize * textScale;
        float titleH = titleSize + 10f;

        // The body face: this holds an item's display NAME, which is mixed case.
        _title = DsWidgets.Label(_detail, "title", "", titleSize, DsTheme.Ink,
                                 TmpAlign.Left);
        if (_title != null) DsWidgets.Place(_title.rectTransform, 0f, 4f, detail.width, titleH);

        // TopLeft, not Left: in TMP "Left" is middle-left, and this rect is now
        // the height of a whole column rather than the 170 px band the pane
        // used to be. Vertically centred prose in a tall rect floats in the
        // middle of the panel with a gap under its own title, which is what it
        // did on Inventory and Crest -- the Journal and Tasks panes were always
        // TopLeft and so never showed it.
        _desc = DsWidgets.Label(_detail, "desc", "", DetailBodySize * textScale, DsTheme.Ink,
                                TmpAlign.TopLeft);
        if (_desc != null)
            DsWidgets.Place(_desc.rectTransform, 0f, titleH + 10f, detail.width,
                            detail.height - titleH - 18f);

        _empty = DsWidgets.Label(_grid, "empty", EmptyMessage, DsTheme.BodySize,
                                 DsTheme.InkFaint, TmpAlign.Center);
        if (_empty != null) DsWidgets.Stretch(_empty.rectTransform);
    }

    /// <summary>Replace the contents with a single untitled run.</summary>
    public void SetItems(IEnumerable<DsItem> items)
    {
        var one = new DsSection(null, Color.white);
        if (items != null) one.Items.AddRange(items);
        var list = new List<DsSection>(1) { one };
        SetSections(list);
    }

    /// <summary>Replace the contents with titled groups.</summary>
    public void SetSections(List<DsSection> sections)
    {
        _sections.Clear();
        if (sections != null) _sections.AddRange(sections);

        _flat.Clear();
        for (int s = 0; s < _sections.Count; s++)
            for (int i = 0; i < _sections[s].Items.Count; i++)
                _flat.Add(_sections[s].Items[i]);

        // Selection follows the ITEM, not its index. Refreshing on a timer and
        // resetting to the first entry each time is why tapping the needle
        // showed its description for a moment and then snapped back to the
        // first item in the bag.
        _selected = -1;
        if (!string.IsNullOrEmpty(_selectedKey))
        {
            for (int i = 0; i < _flat.Count; i++)
                if (_flat[i].Key == _selectedKey) { _selected = i; break; }
        }
        // Only pick a default when nothing has ever been chosen and nothing
        // outside the grid is being shown.
        if (_selected < 0 && string.IsNullOrEmpty(_selectedKey) && !_external && _flat.Count > 0)
        {
            _selected = 0;
            _selectedKey = _flat[0].Key;
        }
        _dirty = true;
    }

    public int Count => _flat.Count;

    /// <summary>Select an entry by its Key, and scroll it into view.</summary>
    public bool SelectByKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        for (int i = 0; i < _flat.Count; i++)
        {
            if (_flat[i].Key != key) continue;
            _external = false;
            _selected = i;
            _selectedKey = key;

            // Bring it into view, since the caller may have selected something
            // scrolled far out of sight.
            for (int p = 0; p < _placed.Count; p++)
            {
                if (_placed[p].ItemIndex != i) continue;
                float top = _placed[p].Y, bottom = top + _placed[p].H;
                if (top < _scroll) _scroll = top;
                else if (bottom > _scroll + _gridH) _scroll = bottom - _gridH;
                _scroll = Mathf.Clamp(_scroll, 0f, _maxScroll);
                break;
            }

            Paint();
            PaintDetail();
            return true;
        }
        return false;
    }

    public void Tick()
    {
        if (!_dirty) return;
        _dirty = false;
        Layout();
        PaintDetail();
    }

    void Layout()
    {
        DsWidgets.SetActive(_empty, _flat.Count == 0);

        for (int i = 0; i < _headers.Count; i++)
            if (_headers[i] != null) Object.Destroy(_headers[i].gameObject);
        _headers.Clear();
        _headerY.Clear();
        _placed.Clear();

        const float sectionGap = 16f;

        float y = CornerOverhang;
        int flatIndex = 0;

        for (int s = 0; s < _sections.Count; s++)
        {
            var sec = _sections[s];
            if (sec.Items.Count == 0) continue;

            if (!string.IsNullOrEmpty(sec.Title))
            {
                if (y > CornerOverhang) y += sectionGap;
                var head = DsWidgets.Rect(_grid, "head" + s);
                DsWidgets.Place(head, 0f, y, _gridW, HeaderH);

                // A blank title means "rule only" -- a divider is enough to say
                // two groups are different without naming them.
                if (sec.Title.Trim().Length > 0)
                {
                    var label = DsWidgets.Label(head, "t", sec.Title, DsTheme.BodySize,
                                                sec.Colour, TmpAlign.Left, display: true);
                    if (label != null) DsWidgets.Place(label.rectTransform, 4f, 0f, 340f, 38f);
                }

                var rule = DsWidgets.Box(head, "rule", sec.Colour).rectTransform;
                DsWidgets.Place(rule, 4f, HeaderH - 12f, _gridW - 8f, 2f);

                _headers.Add(head);
                _headerY.Add(y);
                y += HeaderH;
            }

            for (int i = 0; i < sec.Items.Count; i++, flatIndex++)
            {
                int col = i % _columns;
                float x = col * (_cell + _gap);
                _placed.Add(new Placed { ItemIndex = flatIndex, X = x, Y = y, W = _cell, H = _cell });
                if (col == _columns - 1 || i == sec.Items.Count - 1) y += _cell + _gap;
            }
        }

        _maxScroll = Mathf.Max(0f, y - _gridH);
        _scroll = Mathf.Clamp(_scroll, 0f, _maxScroll);

        EnsureCells(_placed.Count);
        Paint();
    }

    void Paint()
    {
        // Headers scroll with the icons they title. They are separate objects
        // from the cells, so they need their own pass -- forgetting it left the
        // group titles pinned in place while the grid moved beneath them.
        for (int i = 0; i < _headers.Count; i++)
        {
            var h = _headers[i];
            if (h == null) continue;
            float hy = _headerY[i] - _scroll;
            bool vis = hy + HeaderH > 0f && hy < _gridH;
            if (h.gameObject.activeSelf != vis) h.gameObject.SetActive(vis);
            if (vis) DsWidgets.Place(h, 0f, hy, _gridW, HeaderH);
        }

        for (int i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            if (i >= _placed.Count) { cell.Root.gameObject.SetActive(false); continue; }

            var p = _placed[i];
            float y = p.Y - _scroll;
            bool visible = y + p.H > -p.H && y < _gridH + p.H;
            cell.Root.gameObject.SetActive(visible);
            if (!visible) continue;

            DsWidgets.Place(cell.Root, p.X, y, p.W, p.H);

            var item = _flat[p.ItemIndex];
            bool selected = p.ItemIndex == _selected;

            // Selection is the game's own cursor: two corners and a glow.
            DsWidgets.SetActive(cell.CornerTL, selected);
            DsWidgets.SetActive(cell.CornerBR, selected);
            if (cell.Glow != null)
                cell.Glow.color = selected ? new Color(1f, 0.94f, 0.72f, 0.30f) : Color.clear;

            if (item.Icon != null)
            {
                cell.Icon.sprite = item.Icon;
                cell.Icon.useSpriteMesh = true;      // atlas-tight, see DsWidgets.Icon
                cell.Icon.preserveAspect = true;
                cell.Icon.color = item.Dim ? new Color(1f, 1f, 1f, 0.28f) : item.Tint;
            }
            else
            {
                cell.Icon.sprite = DsTheme.White;
                cell.Icon.useSpriteMesh = false;
                cell.Icon.color = item.Dim ? DsTheme.Locked
                                           : new Color(item.Tint.r, item.Tint.g, item.Tint.b, 0.45f);
            }

            if (cell.Badge != null)
            {
                bool show = !string.IsNullOrEmpty(item.Badge);
                DsWidgets.SetActive(cell.Badge, show);
                if (show) cell.Badge.text = item.Badge;
            }
        }
    }

    void EnsureCells(int needed)
    {
        var cursor = DsGameArt.SelectionCursor();

        // Cells built before the game's inventory existed cached a null cursor.
        // Once the art appears, give it to them.
        if (cursor.Ok && !_cursorApplied && _cells.Count > 0)
        {
            _cursorApplied = true;
            for (int i = 0; i < _cells.Count; i++)
            {
                SetSprite(_cells[i].Glow, cursor.Glow);
                SetCorner(_cells[i].CornerTL, cursor.Corner);
                SetCorner(_cells[i].CornerBR, cursor.Corner);
            }
        }
        if (cursor.Ok) _cursorApplied = true;

        while (_cells.Count < needed && _cells.Count < 512)
        {
            var root = DsWidgets.Rect(_grid, "cell" + _cells.Count);

            // No cell background. An item is its icon; a grid of tinted squares
            // reads as a spreadsheet, and the game draws its inventory as bare
            // art on the panel.
            var glow = DsWidgets.Icon(root, "glow", cursor.Glow, Color.clear);
            DsWidgets.Stretch(glow.rectTransform, -10f);

            var icon = DsWidgets.Icon(root, "icon", null, Color.white);
            DsWidgets.Stretch(icon.rectTransform, 10f);

            var badge = DsWidgets.Label(root, "badge", "", DsTheme.SmallSize,
                                        DsTheme.Accent, TmpAlign.BottomRight);
            if (badge != null) DsWidgets.Stretch(badge.rectTransform, 4f);

            // Corners last, so they sit above the icon. The bottom-right is the
            // same sprite turned 180 degrees, which is how the game does it.
            var tl = Corner(root, "c-tl", cursor.Corner, new Vector2(0f, 1f), false, CornerInset);
            var br = Corner(root, "c-br", cursor.Corner, new Vector2(1f, 0f), true,
                            CornerInset - BadgeClearance);

            _cells.Add(new Cell
            {
                Root = root, Icon = icon, Badge = badge, Glow = glow,
                CornerTL = tl, CornerBR = br,
            });
        }
    }

    // One corner of the game's cursor, anchored to the matching corner of the
    // cell, as the game's is around an item.
    static Image Corner(RectTransform parent, string name, Sprite sprite, Vector2 anchor,
                        bool rotate, float inset)
    {
        return DsWidgets.CursorCorner(parent, name, sprite, anchor, rotate, CornerSize, inset);
    }

    static void SetSprite(Image img, Sprite s)
    {
        if (img == null || s == null) return;
        img.sprite = s;
        img.preserveAspect = true;
    }

    // A corner starts transparent so a missing bracket is absent rather than a
    // grey block; once the real art arrives it has to be made opaque again.
    static void SetCorner(Image img, Sprite s)
    {
        if (img == null || s == null) return;
        SetSprite(img, s);
        img.color = Color.white;
    }

    void PaintDetail()
    {
        // Something outside the grid may own the pane -- the needle, or a skill
        // in the ring. It keeps it until a grid item is tapped.
        if (_external) return;

        bool has = _selected >= 0 && _selected < _flat.Count;
        if (_title != null) _title.text = has ? (_flat[_selected].Name ?? "") : "";
        if (_desc != null) _desc.text = has ? (_flat[_selected].Description ?? "") : "";
    }

    /// <summary>
    /// Show something the grid does not own, for a screen that has a second
    /// half -- the needle and the skills on the Inventory tab, for instance.
    /// One description pane for the whole screen is less to look at than two.
    /// </summary>
    public void ShowDetail(string name, string description)
    {
        _external = true;
        _selected = -1;
        _selectedKey = null;
        if (_title != null) _title.text = name ?? "";
        if (_desc != null) _desc.text = description ?? "";
        Paint();
    }

    public void OnGesture(DsGesture g)
    {
        Vector2 p = DsPresentation.ToLayout(g.Position);

        switch (g.Type)
        {
            case DsGestureType.Drag:
                // Panel y is up, so dragging the finger up scrolls further down
                // the list. Only when the finger is over this grid.
                if (new Rect(_gridLeft, _gridTop, _gridW, _gridH).Contains(p))
                {
                    _scroll = Mathf.Clamp(_scroll + g.Delta.y, 0f, _maxScroll);
                    Paint();
                }
                break;

            case DsGestureType.Tap:
                int hit = HitTest(p);
                if (hit >= 0)
                {
                    _external = false;      // the grid takes the pane back
                    _selected = hit;
                    _selectedKey = _flat[hit].Key;
                    Paint();
                    PaintDetail();
                }
                break;
        }
    }

    // Panel touch -> item index, in LAYOUT space (origin top-left, y down, one
    // unit per panel pixel) because that is the space everything was placed in.
    //
    // Deliberately NOT RectTransformUtility: the canvas is ScreenSpaceCamera on
    // a display Unity reports as 0x0, so its screen-point conversion silently
    // maps a corner tap to the middle of the grid.
    int HitTest(Vector2 layoutPoint)
    {
        if (!new Rect(_gridLeft, _gridTop, _gridW, _gridH).Contains(layoutPoint)) return -1;
        float x = layoutPoint.x - _gridLeft;
        float y = layoutPoint.y - _gridTop + _scroll;
        if (x < 0f || x > _gridW) return -1;

        for (int i = 0; i < _placed.Count; i++)
        {
            var p = _placed[i];
            if (x >= p.X && x <= p.X + p.W && y >= p.Y && y <= p.Y + p.H) return p.ItemIndex;
        }
        return -1;
    }
}
#endif
