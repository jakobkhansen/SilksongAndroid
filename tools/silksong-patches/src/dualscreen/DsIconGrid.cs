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
    /// What colour to light this item when it is selected. Null takes the
    /// game's default glow. Tools set it to their type's colour, which is how
    /// a red tool lights red -- see InventoryItemTool.CursorColor.
    /// </summary>
    public Color? Glow;
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
    // Headers are no longer all one height. The section cap is 43 px of art, and
    // a group that also carries a caps title needs room for both; one that does
    // not -- the Inventory's consumables, which the game leaves unnamed too --
    // needs only the cap. Sizing every header for the worst case put a band of
    // dead space above half the groups on the panel with the least to spare.
    readonly List<float> _headerH = new List<float>();
    const float HeaderTitleH = 44f;
    const float HeaderRuleH = 52f;

    // Selection is drawn by ONE cursor that travels, not by brackets switched
    // on inside the selected cell -- see DsCursor. There are two of them, in
    // two different parents, for one reason:
    //
    //   _cursor      lives INSIDE the grid's scroll mask, with the cells. It
    //                therefore scrolls with the item it is on and is clipped
    //                exactly as the item is, so a half-scrolled item gets a
    //                half-drawn cursor and an item scrolled away takes its
    //                cursor with it. That is what the game does, and it is what
    //                neither hiding it nor clamping it managed: hiding left a
    //                selected row with nothing on it, and clamping parked the
    //                brackets at the top of the column around no item at all.
    //
    //   _freeCursor  lives in the screen host, OUTSIDE the mask, for targets
    //                that are not grid cells: the needle, the mask and the
    //                counters in the character column, a tool socketed in the
    //                crest. Those must not be clipped to the grid's column.
    //
    // Only one is ever shown, and the one taking over is seeded with the other's
    // position so the handover reads as a single cursor crossing a boundary.
    readonly DsCursor _cursor = new DsCursor();
    readonly DsCursor _freeCursor = new DsCursor();
    RectTransform _host;
    // Somewhere other than a cell owns the cursor -- the needle, say. Kept in
    // host space, already converted by whoever set it.
    bool _hasExternalTarget;
    Rect _externalTarget;
    Color _externalGlow;
    string _externalKey;

    // The count sits in the same corner as the bottom-right bracket, so that
    // one alone is pushed back out far enough to read as a bracket around a
    // number rather than a bracket through one.
    const float BadgeClearance = 10f;
    // How far the count's figure sits INSIDE the art's bottom-right corner, as
    // a fraction of the cell.
    //
    // Measured from the art rather than from the cell, which is the thing that
    // was not obvious: the icon is inset by _iconPad, so a box ending flush
    // with the CELL leaves the figure hanging off the sprite's corner rather
    // than sitting on it. The overlap has to clear that padding before it
    // starts biting into the art at all.
    //
    // A knob, because it is judged by eye and DsConfig only costs an app
    // restart rather than a rebuild.
    static float BadgeOverlapFrac =>
        Mathf.Clamp(DsConfig.Int("badge_overlap_pct", 14), 0, 40) / 100f;

    /// <summary>
    /// Backed off from the art's corner on both axes, after the overlap has
    /// been applied. Judged on the panel: the figure sat correctly on the art
    /// but a little too far into it, and the same amount suits both axes even
    /// though what they are measured from differs.
    /// </summary>
    static float BadgeBackOff => DsConfig.Int("badge_backoff_px", 10);

    /// <summary>Extra back-off across only, on top of the shared amount.</summary>
    static float BadgeBackOffX => DsConfig.Int("badge_backoff_x_px", 5);

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
    // How far the art is inset inside its cell. Proportional, not the flat 10 px
    // it used to be: against the old 157 px cell that was 13% of it, but the
    // same 10 px in a 92 px cell is 22%, so narrowing the column shrank the ICONS
    // half again as much as it shrank the cells and the grid read as small with
    // too much air around it. A fraction keeps the art the same share of its cell
    // at any column width.
    float _iconPad;
    // How far left of the column the section cap reaches, to meet the gutter rule.
    float _capReach;
    // The grid's rectangle in LAYOUT space (top-left origin), kept because hit
    // testing is arithmetic in that space rather than a RectTransform query.
    float _gridLeft, _gridTop, _gridW, _gridH;
    float _scroll, _maxScroll;
    int _selected = -1;
    // What is selected, by key rather than index, so a refresh cannot move it.
    string _selectedKey;
    // Set while something outside the grid owns the detail pane.
    bool _external;
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
    /// <param name="capReach">
    /// How far LEFT of the column the section cap reaches, so its tick lands on
    /// the gutter rule instead of floating in the column beside it.
    ///
    /// The cap is the junction between a group boundary and the column boundary,
    /// and it only reads as one if the two actually meet; drawn flush with the
    /// icons it looked like a stray mark. The grid cannot work this out for
    /// itself -- where the gutter rule runs is the screen's business, not the
    /// grid's -- so the screen passes the distance. Zero means "draw it inside
    /// the column", which is right for a grid with no rule beside it.
    /// </param>
    public void Build(RectTransform host, int columns,
                      float left = -1f, float width = -1f, Rect detail = default(Rect),
                      bool detailRule = true, float capReach = 0f)
    {
        _columns = Mathf.Max(1, columns);
        _capReach = Mathf.Max(0f, capReach);

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
        _iconPad = Mathf.Max(3f, _cell * 0.06f);

        _gridLeft = left;
        _gridTop = layout.Body.y + DsTheme.Pad;
        _gridW = width;
        _gridH = (detailBelow ? h - DsTheme.FooterHeight : h) - DsTheme.Pad * 2f;

        // Cells are moved to scroll, so without clipping a cell scrolled past
        // the top would draw over the tab strip. The clip is a rect of its own,
        // grown by CursorBleed, so a selection bracket that reaches outside its
        // cell is still drawn -- see the note there.
        //
        // The left side is grown by whichever is larger, the bracket's overhang
        // or the section cap's reach. Sizing it to the bracket alone is what cut
        // the cap off short of the gutter rule it is supposed to touch.
        float leftBleed = Mathf.Max(CursorBleed, _capReach);
        var clip = DsWidgets.Rect(host, "grid-clip");
        DsWidgets.Place(clip, left - leftBleed, DsTheme.Pad - CursorBleed,
                        _gridW + leftBleed + CursorBleed, _gridH + CursorBleed * 2f);
        clip.gameObject.AddComponent<RectMask2D>();

        _grid = DsWidgets.Rect(clip, "grid");
        DsWidgets.Place(_grid, leftBleed, CursorBleed, _gridW, _gridH);

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

        // Two cursors, two parents -- see the note on the fields.
        //
        // The grid's goes in with the cells so the scroll mask clips it; the
        // free one goes in the host, last, so its brackets draw over everything
        // the screen put down.
        //
        // Both frame the target's box exactly, as InventoryCursor does -- it
        // puts its corners on boxOffset +/- boxScale/2 and nowhere else. The
        // game gets away with that because the box is a BoxCollider2D authored
        // per item, tight around the art; ours is derived instead (see
        // IconRect, and DsHornetPanel.Slot.Art), and DsCursor's shared constant
        // takes up the slack a derived box leaves.
        _host = host;
        _cursor.Build(_grid);
        _freeCursor.Build(host);
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
            _hasExternalTarget = false;
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
        float dt = Time.unscaledDeltaTime;
        _cursor.Tick(dt);
        _freeCursor.Tick(dt);
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
        _headerH.Clear();
        _placed.Clear();

        const float sectionGap = 16f;

        float y = 0f;
        int flatIndex = 0;

        for (int s = 0; s < _sections.Count; s++)
        {
            var sec = _sections[s];
            if (sec.Items.Count == 0) continue;

            if (!string.IsNullOrEmpty(sec.Title))
            {
                if (y > 0f) y += sectionGap;

                // A blank title means "cap only" -- a divider is enough to say
                // two groups are different without naming them.
                bool titled = sec.Title.Trim().Length > 0;
                float headH = (titled ? HeaderTitleH : 0f) + HeaderRuleH;

                var head = DsWidgets.Rect(_grid, "head" + s);
                DsWidgets.Place(head, 0f, y, _gridW, headH);

                if (titled)
                {
                    var label = DsWidgets.Label(head, "t", sec.Title, DsTheme.BodySize,
                                                sec.Colour, TmpAlign.Left, display: true);
                    if (label != null) DsWidgets.Place(label.rectTransform, 4f, 0f, 340f, 38f);
                }

                // Starts on the gutter rule, not inside the column, so the cap's
                // tick meets the line it belongs to. The label stays inside.
                DsWidgets.SectionRule(head, "rule", -_capReach,
                                      (titled ? HeaderTitleH : 0f) + HeaderRuleH * 0.5f,
                                      _gridW + _capReach, sec.Colour);

                _headers.Add(head);
                _headerY.Add(y);
                _headerH.Add(headH);
                y += headH;
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
        // Headers are rebuilt here, after the cursor, so re-assert its order.
        _cursor.BringToFront();
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
            float hh = _headerH[i];
            bool vis = hy + hh > 0f && hy < _gridH;
            if (h.gameObject.activeSelf != vis) h.gameObject.SetActive(vis);
            if (vis) DsWidgets.Place(h, 0f, hy, _gridW, hh);
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

        PaintCursor();
    }

    /// <summary>
    /// Put the cursor on whatever is selected, in the SCREEN's space.
    ///
    /// Run from Paint rather than only on selection, because the target moves
    /// without the selection changing: the grid scrolls under it. The cursor is
    /// outside the grid's scroll mask, so a cell that has scrolled out of the
    /// viewport would otherwise leave its cursor sitting over the next column.
    /// </summary>
    void PaintCursor()
    {
        if (_hasExternalTarget)
        {
            // Handing over from the grid: start the free cursor where the grid's
            // one is, converted out of grid space, so it travels rather than
            // reappearing somewhere else.
            if (_cursor.Visible)
            {
                var g = _cursor.Current;
                _freeCursor.Seed(new Rect(_gridLeft + g.x, DsTheme.Pad + g.y, g.width, g.height),
                                 _cursor.CurrentGlow);
                _cursor.Hide();
            }
            _freeCursor.MoveTo(_externalTarget, _externalGlow, "ext:" + _externalKey);
            return;
        }

        if (_selected < 0 || _selected >= _flat.Count)
        {
            _cursor.Hide(); _freeCursor.Hide(); return;
        }

        for (int i = 0; i < _placed.Count; i++)
        {
            var p = _placed[i];
            if (p.ItemIndex != _selected) continue;

            // Grid space: the same coordinates the cells are placed in, so the
            // cursor scrolls and clips with them and needs no clamping.
            Rect box = IconRect(p, _flat[_selected].Icon);

            if (_freeCursor.Visible)
            {
                var f = _freeCursor.Current;
                _cursor.Seed(new Rect(f.x - _gridLeft, f.y - DsTheme.Pad, f.width, f.height),
                             _freeCursor.CurrentGlow);
                _freeCursor.Hide();
            }
            _cursor.MoveTo(box, _flat[_selected].Glow, _selectedKey);
            return;
        }
        _cursor.Hide();
    }

    /// <summary>
    /// The box the cursor should frame for a cell: the icon's VISIBLE INK.
    ///
    /// This is the one piece of InventoryCursor we cannot copy directly. The
    /// game reads a BoxCollider2D off the thing selected and puts the brackets
    /// on its corners exactly -- boxOffset +/- boxScale/2, with no inset at all.
    /// Those colliders are authored per item, by hand, tight around the art.
    ///
    /// We draw our own icons and have no such boxes, so the equivalent has to be
    /// derived, and the naive derivation is what made the caret look loose. An
    /// icon's rect is NOT its art:
    ///
    ///   * the rect is square while the sprite usually is not, so preserveAspect
    ///     leaves empty bands down two sides -- worst on the Crest tab, whose
    ///     tools are mostly taller than they are wide;
    ///   * and the sprite's own rect includes the transparent padding it was
    ///     packed with, while useSpriteMesh draws only the TRIMMED mesh inside
    ///     it. That padding is invisible and was still being framed.
    ///
    /// Sprite.bounds is the trimmed mesh, so the ratio of it to the full rect
    /// gives the ink's size and offset within the drawn icon -- the same
    /// reasoning DsWidgets.FitCentred uses to centre a trimmed sprite. Framing
    /// that is as close to the game's hand-made boxes as we can get without
    /// authoring one per item.
    /// </summary>
    Rect IconRect(Placed p, Sprite icon)
    {
        float x = p.X + _iconPad;
        float y = p.Y - _scroll + _iconPad;
        float w = p.W - _iconPad * 2f;
        float h = p.H - _iconPad * 2f;
        if (icon == null) return new Rect(x, y, w, h);

        var full = icon.rect;
        if (full.width <= 0f || full.height <= 0f) return new Rect(x, y, w, h);

        // preserveAspect fits the sprite's FULL rect into the icon's box.
        float aspect = full.width / full.height;
        float dw = w, dh = h;
        if (w / Mathf.Max(h, 0.0001f) > aspect) dw = h * aspect;
        else dh = w / Mathf.Max(aspect, 0.0001f);
        float cx = x + w * 0.5f;
        float cy = y + h * 0.5f;

        // ...and within that, the ink is the trimmed mesh.
        float ppu = icon.pixelsPerUnit;
        if (ppu <= 0f) ppu = 100f;
        Vector2 unitsFull = new Vector2(full.width / ppu, full.height / ppu);
        if (unitsFull.x <= 0f || unitsFull.y <= 0f)
            return new Rect(cx - dw * 0.5f, cy - dh * 0.5f, dw, dh);

        Vector3 size = icon.bounds.size;
        Vector3 mid = icon.bounds.center;
        float iw = dw * Mathf.Clamp01(size.x / unitsFull.x);
        float ih = dh * Mathf.Clamp01(size.y / unitsFull.y);
        // Sprite bounds are y-up; layout space is y-down.
        cx += (mid.x / unitsFull.x) * dw;
        cy -= (mid.y / unitsFull.y) * dh;

        if (iw <= 1f || ih <= 1f) return new Rect(cx - dw * 0.5f, cy - dh * 0.5f, dw, dh);
        return new Rect(cx - iw * 0.5f, cy - ih * 0.5f, iw, ih);
    }

    /// <summary>
    /// Hand the cursor to something that is not a grid cell -- the needle in
    /// the character column, say. Pass a rect in the screen host's space.
    /// </summary>
    public void SetExternalTarget(Rect hostRect, Color glow, string key)
    {
        _hasExternalTarget = true;
        _externalTarget = hostRect;
        _externalGlow = glow;
        _externalKey = key;
        PaintCursor();
    }

    public void ClearExternalTarget()
    {
        if (!_hasExternalTarget) return;
        _hasExternalTarget = false;
        PaintCursor();
    }

    /// <summary>
    /// Kept for callers that used to grow an exact widget box before handing it
    /// over. They no longer need to: the cursor insets by a fraction of the
    /// target now, so an exact box is already framed tightly.
    /// </summary>
    public float CursorInset => 0f;

    void EnsureCells(int needed)
    {
        while (_cells.Count < needed && _cells.Count < 512)
        {
            var root = DsWidgets.Rect(_grid, "cell" + _cells.Count);

            // No cell background, and no cursor art either. An item is its icon;
            // a grid of tinted squares reads as a spreadsheet, and the game
            // draws its inventory as bare art on the panel. The selection is
            // drawn once, by the cursor that travels -- see DsCursor.
            var icon = DsWidgets.Icon(root, "icon", null, Color.white);
            DsWidgets.Stretch(icon.rectTransform, _iconPad);

            // The count sits ON the art's bottom-right corner, overlapping it,
            // which is where the game puts its own amountText. Created after
            // the icon, so it draws over it.
            // White, not the panel's gold accent: in the game's inventory the
            // count is plain white ink on the art, and gold is reserved here
            // for things you can act on.
            //
            // Sized against the cell rather than from the shared small size, so
            // it stays readable at arm's length instead of shrinking away in a
            // corner, and large enough to read as a quantity on the art rather
            // than as a footnote to it.
            float badgeSize = Mathf.Max(30f, _cell * 0.32f);
            var badge = DsWidgets.Label(root, "badge", "", badgeSize,
                                        Color.white, TmpAlign.BottomRight);
            if (badge != null)
            {
                float bw = _cell * 0.66f;
                float bh = badgeSize * 1.25f;
                // The two axes are not the same job. Across, the figure bites
                // into the art so it reads as part of the item rather than as a
                // label beside it. Down, it wants to sit ON the art's bottom
                // edge -- the counts in the game's own inventory hang off the
                // foot of the sprite, and pulling them up by the same amount
                // they are pulled in left them floating in the middle of it.
                float insetX = _iconPad + _cell * BadgeOverlapFrac - BadgeBackOff - BadgeBackOffX;
                float insetY = _iconPad - BadgeBackOff;
                DsWidgets.Place(badge.rectTransform,
                                _cell - bw - insetX, _cell - bh - insetY, bw, bh);
            }

            _cells.Add(new Cell { Root = root, Icon = icon, Badge = badge });
        }

        // Cells and headers are added to the same parent as the grid's cursor
        // and therefore arrive as later siblings; without this the icons draw
        // over the brackets.
        _cursor.BringToFront();
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
                    _hasExternalTarget = false;   // ...and the cursor with it
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
