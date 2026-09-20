// DsActions — the panel's buttons, in one of two places.
//
// The panel had exactly two of these, drawn by the Map screen into its own
// body: FULL MAP and RESET, in bordered boxes. Both facts were wrong.
//
// Wrong look, because these were bordered boxes in the panel's own dark style,
// and the designs draw them as solid WHITE PLATES with black text. That is a
// deliberate inversion: everything else on this panel is light ink on black, so
// a filled plate is the one thing that reads as "press me" without a border
// around it.
//
// Wrong place, and the right answer turned out to depend on what the action is
// FOR. There are two kinds, and they now go to two different corners:
//
//   Header  A control that belongs to the SCREEN -- FULL MAP switches what the
//           map is showing, MARKERS changes what a tap does. These stay in the
//           top-right, opposite the health, where they have always been.
//
//   Pane    A control that acts on THE THING UNDER THE CURSOR -- USE drinks the
//           selected item, EQUIP fits the selected tool. These belong with the
//           selection, and the selection is described in the description pane,
//           so that is where they go: along the bottom of it, spanning its
//           width. A button that acts on the item you are reading about should
//           be under what you are reading, not diagonally across the panel in a
//           corner that also holds the controls for something else entirely.
//
// A Pane action spans its column rather than being sized to its own text, which
// is what makes a stack of them read as a strip belonging to that column. A
// Header action is still sized to its words, so FULL MAP is wider than RESET
// and neither carries dead space.
//
// Screens opt in by implementing IDsActionBar. The shell asks the visible one
// for its actions every frame, which is what lets USE appear only while the
// cursor is on something that can actually be used: the answer is recomputed
// rather than pushed, so nothing has to remember to withdraw it.

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TmpText = TMProOld.TextMeshProUGUI;
using TmpAlign = TMProOld.TextAlignmentOptions;

/// <summary>Which corner an action belongs in. See the header notes.</summary>
public enum DsActionPlace
{
    /// <summary>Top-right, sized to its own text. For controls that act on the screen.</summary>
    Header,
    /// <summary>
    /// Along the bottom of the screen's description pane, spanning its width.
    /// For controls that act on whatever is selected.
    /// </summary>
    Pane,
}

/// <summary>One button. Label is shown; Invoke runs on a tap.</summary>
public struct DsAction
{
    public string Label;
    public Action Invoke;
    /// <summary>Drawn dim and ignored. For an action that exists but cannot run yet.</summary>
    public bool Disabled;
    public DsActionPlace Place;
    /// <summary>
    /// How solid to draw it, for a button that fades rather than pops. Zero
    /// means OPAQUE, not invisible: the zero value of this struct has to be the
    /// ordinary case, and a button faded to nothing is one the screen simply
    /// stops offering.
    /// </summary>
    public float Alpha;

    public DsAction(string label, Action invoke, bool disabled = false,
                    DsActionPlace place = DsActionPlace.Header, float alpha = 1f)
    {
        Label = label; Invoke = invoke; Disabled = disabled; Place = place; Alpha = alpha;
    }
}

/// <summary>A screen that offers actions.</summary>
public interface IDsActionBar
{
    /// <summary>
    /// Fill <paramref name="into"/> with what can be done right now. Called
    /// every frame while the screen is visible, so it must be cheap and must
    /// not change any game state -- see the warnings in DsScreens about
    /// "read-only" accessors that are nothing of the sort.
    /// </summary>
    void CollectActions(List<DsAction> into);

    /// <summary>
    /// Where this screen's <see cref="DsActionPlace.Pane"/> actions go, in
    /// LAYOUT space -- the buttons are drawn by the shell, which knows nothing
    /// of any screen's columns.
    ///
    /// A zero width means the screen has no such pane, and its pane actions
    /// fall back to the header rather than vanishing: a control that cannot be
    /// placed is still a control the player needs.
    /// </summary>
    Rect ActionPane { get; }
}

public class DsActionBar
{
    class Row
    {
        public RectTransform Root;
        public TmpText Label;
        public Image Plate;
        public Rect Hit;          // layout space, for the shell's hit testing
        public Action Invoke;
        public bool Live;
    }

    // White plates as the designs draw them: each sized to its OWN text rather
    // than to a shared width, so FULL MAP is wider than MARKERS and neither
    // carries dead space.
    //
    // Generous for their size, and deliberately so. These are 9 mm targets on a
    // panel held in two hands and tapped with a thumb, they sit directly above
    // the map's own top edge, and the cost of a miss is not nothing -- FULL MAP
    // reframes the thing you were reading. The padding buys the plate width and
    // the gap buys the space between two of them; both matter more than the few
    // pixels of header they spend.
    public const float RowH = 54f;
    const float PadX = 30f;        // inside the plate, either side of the text
    // The header's buttons are spread down its whole height rather than being
    // hung from either edge, so the gap above the first, between each pair and
    // below the last are all the same. A floor, for a count that would leave
    // them touching -- there is no such screen today, and a stack that
    // overflowed its band would be a strange way to find out there is.
    const float MinHeaderGap = 10f;
    const float MarginRight = 40f; // from the right edge of the panel
    const float MinW = 130f;

    // A pane strip spans its column instead, so the width is the column's and
    // only the inset is ours. Tighter gaps than the header's: these are one
    // group of related controls rather than a list of unrelated ones.
    const float PaneInsetX = 14f;
    const float PaneInsetY = 12f;
    const float PaneGap = 12f;

    /// <summary>The height of a stack of <paramref name="rows"/> buttons.</summary>
    static float Block(int rows, float gap)
    {
        return rows <= 0 ? 0f : rows * RowH + (rows - 1) * gap;
    }

    /// <summary>
    /// How much of a column a screen must keep clear to seat
    /// <paramref name="rows"/> pane buttons.
    ///
    /// Asked rather than assumed, because the screen that reserves the band and
    /// the bar that fills it are in different files: when these metrics last
    /// lived in both places they disagreed, and the prose ran under the button.
    /// </summary>
    public static float PaneBand(int rows)
    {
        if (rows <= 0) return 0f;
        return PaneInsetY * 2f + Block(rows, PaneGap);
    }

    readonly List<Row> _rows = new List<Row>();
    readonly List<DsAction> _wanted = new List<DsAction>();
    RectTransform _host;
    float _right, _headerH;

    /// <param name="host">
    /// Covers the whole panel, in layout space. The bar draws in two corners
    /// that belong to two different parts of the frame -- the header band and a
    /// screen's own column -- so it cannot live inside either of them.
    /// </param>
    /// <param name="headerH">
    /// The header's height, which its buttons are spread down.
    ///
    /// The whole band, rather than a stack hung from one edge of it. What these
    /// buttons belong to is the SCREEN below, so they were first hung from the
    /// bottom to sit against the rule that divides the two -- but that left the
    /// top of the band empty beside them and read as a block that had slipped.
    /// Spread evenly, the column of buttons is the right-hand side of the
    /// header rather than something parked in a corner of it.
    /// </param>
    public void Build(RectTransform host, float panelWidth, float headerH)
    {
        _host = host;
        _right = panelWidth - MarginRight;
        _headerH = headerH;
    }

    /// <summary>
    /// Show exactly these actions, reusing the rows already built.
    ///
    /// <paramref name="pane"/> is where Pane-placed actions go, in layout
    /// space. A zero width sends them to the header instead -- see IDsActionBar.
    /// </summary>
    public void Set(List<DsAction> actions, Rect pane)
    {
        if (_host == null) return;

        _wanted.Clear();
        if (actions != null) _wanted.AddRange(actions);

        while (_rows.Count < _wanted.Count) _rows.Add(NewRow(_rows.Count));

        bool hasPane = pane.width > 0f && pane.height > 0f;

        // The pane strip is laid out as a BLOCK, bottom-aligned in its column,
        // so its height has to be known before the first of them is placed.
        // The header's rows are spread down the band instead, which needs only
        // how many there are.
        int paneCount = 0;
        for (int i = 0; i < _wanted.Count; i++)
            if (hasPane && _wanted[i].Place == DsActionPlace.Pane) paneCount++;
        int headerCount = _wanted.Count - paneCount;

        float paneW = Mathf.Max(MinW, pane.width - PaneInsetX * 2f);
        float paneX = pane.x + (pane.width - paneW) * 0.5f;
        float paneTop = pane.yMax - PaneInsetY - Block(paneCount, PaneGap);

        // n buttons make n+1 gaps: above the first, between each pair, below
        // the last.
        float headerGap = headerCount > 0
            ? Mathf.Max(MinHeaderGap, (_headerH - headerCount * RowH) / (headerCount + 1))
            : 0f;

        int paneSeen = 0, headerSeen = 0;

        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            bool on = i < _wanted.Count;
            DsWidgets.SetActive(row.Root, on);
            if (!on) { row.Live = false; row.Invoke = null; continue; }

            var action = _wanted[i];
            row.Invoke = action.Invoke;
            row.Live = !action.Disabled && action.Invoke != null;

            string text = action.Label ?? "";
            if (row.Label != null) row.Label.text = text;

            Rect where;
            if (hasPane && action.Place == DsActionPlace.Pane)
            {
                where = new Rect(paneX, paneTop + paneSeen * (RowH + PaneGap), paneW, RowH);
                paneSeen++;
            }
            else
            {
                // Sized to the STRING, measured the way the currency counters
                // learned to: preferredWidth reports the width of the rect it
                // already has, which is the number we are trying to work out.
                float textW = 0f;
                if (row.Label != null && text.Length > 0)
                {
                    try { textW = row.Label.GetPreferredValues(text).x; } catch { }
                }
                float w = Mathf.Max(MinW, textW + PadX * 2f);
                where = new Rect(_right - w, headerGap * (headerSeen + 1) + RowH * headerSeen,
                                 w, RowH);
                headerSeen++;
            }

            DsWidgets.Place(row.Root, where);
            // The host covers the panel, so what was placed and what is
            // hit-tested are the same rectangle in the same space.
            row.Hit = where;

            // A white plate with black text. Dimmed rather than recoloured when
            // it cannot run, so a disabled action still reads as the same
            // button rather than as a different kind of thing.
            //
            // Alpha multiplies both, for a button that fades in and out rather
            // than popping -- see the note on DsAction.Alpha for why zero here
            // means opaque.
            float alpha = action.Alpha <= 0f ? 1f : Mathf.Min(1f, action.Alpha);
            if (row.Plate != null)
                row.Plate.color = new Color(1f, 1f, 1f, (row.Live ? 1f : 0.35f) * alpha);
            if (row.Label != null)
                row.Label.color = new Color(0f, 0f, 0f, (row.Live ? 1f : 0.55f) * alpha);
        }
    }

    Row NewRow(int index)
    {
        var root = DsWidgets.Rect(_host, "action" + index);

        var plate = DsWidgets.Icon(root, "plate", DsTheme.Rounded, Color.white);
        // Sliced, so the corner radius stays put whatever width the plate ends
        // up at -- a button sized to "FULL MAP" and one sized to "RESET" have
        // to show the same curve.
        plate.type = Image.Type.Sliced;
        plate.useSpriteMesh = false;
        plate.preserveAspect = false;
        DsWidgets.Stretch(plate.rectTransform);

        var label = DsWidgets.Label(root, "t", "", DsTheme.RowSize, Color.black,
                                    TmpAlign.Center, display: true);
        if (label != null) DsWidgets.Stretch(label.rectTransform, 4f);

        return new Row { Root = root, Plate = plate, Label = label };
    }

    /// <summary>Run whatever sits under <paramref name="layoutPoint"/>. True if one did.</summary>
    public bool OnTap(Vector2 layoutPoint)
    {
        for (int i = 0; i < _rows.Count && i < _wanted.Count; i++)
        {
            var row = _rows[i];
            if (!row.Live || !row.Hit.Contains(layoutPoint)) continue;
            var invoke = row.Invoke;
            if (invoke == null) return false;
            try { invoke(); }
            catch (Exception e) { Debug.LogWarning("[DsActions] '" + row.Label.text + "' failed: " + e.Message); }
            return true;
        }
        return false;
    }

    public void Clear() { Set(null, default(Rect)); }
}
#endif
