// DsActions — the buttons in the top-right of the header.
//
// The panel had exactly two of these, drawn by the Map screen into its own
// body: FULL MAP and RESET, in bordered boxes. Both facts were wrong.
//
// Wrong place, because a control that belongs to the SCREEN was sitting inside
// the screen's content, where it competed with the map for the one part of the
// panel the player is actually looking at -- and where, on a pannable surface,
// it is a thing you hit by accident while dragging. The designs put actions in
// the header, opposite the health, on every tab that has one.
//
// Wrong look, because these were bordered boxes in the panel's own dark style,
// and the designs draw them as solid WHITE PLATES with black text, sized to
// their own words and stacked in the top-right corner. That is a deliberate
// inversion: everything else on this panel is light ink on black, so a filled
// plate is the one thing that reads as "press me" without a border around it.
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

/// <summary>One button in the header. Label is shown; Invoke runs on a tap.</summary>
public struct DsAction
{
    public string Label;
    public Action Invoke;
    /// <summary>Drawn dim and ignored. For an action that exists but cannot run yet.</summary>
    public bool Disabled;

    public DsAction(string label, Action invoke, bool disabled = false)
    {
        Label = label; Invoke = invoke; Disabled = disabled;
    }
}

/// <summary>A screen that offers actions in the header.</summary>
public interface IDsActionBar
{
    /// <summary>
    /// Fill <paramref name="into"/> with what can be done right now. Called
    /// every frame while the screen is visible, so it must be cheap and must
    /// not change any game state -- see the warnings in DsScreens about
    /// "read-only" accessors that are nothing of the sort.
    /// </summary>
    void CollectActions(List<DsAction> into);
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

    // White plates in the top-right, as the designs draw them: each sized to
    // its OWN text rather than to a shared width, so FULL MAP is wider than
    // RESET and neither carries dead space.
    const float RowH = 48f;
    const float RowGap = 18f;
    const float PadX = 22f;        // inside the plate, either side of the text
    const float MarginTop = 40f;   // from the top of the header
    const float MarginRight = 40f; // from the right edge of the panel
    const float MinW = 110f;

    readonly List<Row> _rows = new List<Row>();
    readonly List<DsAction> _wanted = new List<DsAction>();
    RectTransform _host;
    float _right, _top;

    public void Build(RectTransform header, float panelWidth)
    {
        _host = header;
        _right = panelWidth - MarginRight;
        _top = MarginTop;
    }

    /// <summary>Show exactly these actions, reusing the rows already built.</summary>
    public void Set(List<DsAction> actions)
    {
        if (_host == null) return;

        _wanted.Clear();
        if (actions != null) _wanted.AddRange(actions);

        while (_rows.Count < _wanted.Count) _rows.Add(NewRow(_rows.Count));

        float y = _top;
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

            // Sized to the STRING, measured the way the currency counters
            // learned to: preferredWidth reports the width of the rect it
            // already has, which is the number we are trying to work out.
            float textW = 0f;
            if (row.Label != null && text.Length > 0)
            {
                try { textW = row.Label.GetPreferredValues(text).x; } catch { }
            }
            float w = Mathf.Max(MinW, textW + PadX * 2f);

            DsWidgets.Place(row.Root, _right - w, y, w, RowH);
            // The header sits at the top of the panel, so layout space and the
            // header's own space share an origin: no conversion needed.
            row.Hit = new Rect(_right - w, y, w, RowH);

            // A white plate with black text. Dimmed rather than recoloured when
            // it cannot run, so a disabled action still reads as the same
            // button rather than as a different kind of thing.
            if (row.Plate != null)
                row.Plate.color = row.Live ? Color.white : new Color(1f, 1f, 1f, 0.35f);
            if (row.Label != null)
                row.Label.color = row.Live ? Color.black : new Color(0f, 0f, 0f, 0.55f);

            y += RowH + RowGap;
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

    public void Clear() { Set(null); }
}
#endif
