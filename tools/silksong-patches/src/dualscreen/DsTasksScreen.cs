// DsTasksScreen — what Hornet has been asked to do, in the order it matters.
//
// The first version was a four-column icon grid like every other screen, which
// is the wrong shape for this data twice over: quests are read by NAME, not
// recognised by icon, and they are not a flat set. The game itself groups them,
// and this follows its grouping rather than inventing one.
//
//     +---------------------------+------------------------------+
//     |  Great Citadel       3/5  |  TITLE                       |
//     |  Grand Gate          1/2  |                              |
//     |  ------------------------ |  description...              |
//     |  Fleatopia           0/3  |                              |
//     |  --- COMPLETED ---------- |  - Silk Spool     2/5        |
//     |  Bone Bottom              |  * Rosaries       12/12      |
//     +---------------------------+------------------------------+
//
// "Prioritised" is not a guess. The game's own inventory has a main-quest
// section, and InventoryItemQuestManager.IsInMainQuestSection is the whole rule:
//
//     MainQuest mainQuest = quest as MainQuest;
//     if (mainQuest == null) return false;
//     return !mainQuest.IsCompleted;
//
// drawn from GetAcceptedQuests() with IsHidden filtered out. So a prioritised
// task is a MainQuest that is not finished -- a type test, not a name list --
// and "Great Citadel" and "Grand Gate" sort to the top because the game says
// they are that kind of thing.
//
// Progress comes from the game too: FullQuestBase.TargetsAndCounters pairs each
// target with how far along it is, and already excludes the targets the game
// flags HideInCount. The row carries one number, the description pane breaks it
// down per target.
//
// Completed tasks sit at the bottom under their own divider. They were briefly
// behind a button; a divider says the same thing without a control, and the
// list is short enough that scrolling past them costs nothing.

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TmpText = TMProOld.TextMeshProUGUI;
using TmpAlign = TMProOld.TextAlignmentOptions;

public class DsTasksScreen : IDsScreen
{
    // What the list holds, flattened once so drawing never asks the game
    // anything.
    class Entry
    {
        public string Name, Desc;
        public Sprite Icon;
        public bool Main, Completed;
        /// <summary>Per-target progress, empty when the quest counts nothing.</summary>
        public readonly List<Step> Steps = new List<Step>();
        /// <summary>The one-line summary shown on the row. Null when there is none.</summary>
        public string Counter;
        public bool CounterDone;
    }

    /// <summary>One of a quest's targets: what to collect, how many, how many so far.</summary>
    struct Step
    {
        public string Name;
        public int Have, Need;
    }

    // One drawn row. Pooled: the set of quests changes rarely, and rebuilding
    // a few dozen GameObjects on a one-second timer would be silly.
    class Row
    {
        public RectTransform Root;
        public Image Fill, Icon;
        public TmpText Label, Counter;
    }

    // A rule, optionally titled, drawn between groups.
    class Divider
    {
        public RectTransform Root;
        public TmpText Label;
        public float Y;
        public float H;
    }

    const float ListX   = 20f;
    const float ListW   = 780f;
    const float DetailX = 840f;
    const float RowH    = 78f;
    const float IconSize = 46f;
    const float CounterW = 100f;
    // A captioned divider has to carry its caption; a bare rule only has to be
    // seen, so it takes barely more room than the line itself.
    const float DividerH = 40f;
    const float PlainDividerH = 18f;
    // Space above the COMPLETED rule, on top of the divider's own height. The
    // finished pile is further from the list than the two live groups are from
    // each other, and the gap is what says so.
    const float CompletedGap = 36f;
    // The name is the thing being scanned for, so it is much larger than the
    // shared row size the other lists use; the counter beside it stays small,
    // which is what makes the pair read as a name with a number rather than two
    // columns. The list is the wider half of the screen for the same reason --
    // a quest name is longer than the space a description actually needs.
    const float NameSize = 52f;
    // The description is prose and is read, not scanned, so it is larger than
    // the shared body size too. Trimmed a little from the 48/36 it used when
    // this pane was 500 px wide, since the list took some of that back.
    const float DetailTitleSize = 42f;
    const float DetailBodySize = 32f;
    const float Pad = DsTheme.Pad;

    readonly List<Entry> _entries = new List<Entry>();
    readonly List<Row> _rows = new List<Row>();
    readonly List<Divider> _dividers = new List<Divider>();
    // Where each entry sits in list space, so a tap can be turned back into one.
    readonly List<float> _entryY = new List<float>();

    RectTransform _host, _list, _detail;
    TmpText _title, _desc, _empty;

    Rect _listRect;             // panel space, for hit-testing
    float _listTop, _listH;
    float _scroll, _maxScroll;
    int _selected = -1;
    string _signature;
    float _nextRefresh;

    public string Id { get { return "tasks"; } }
    public string Title { get { return "TASKS"; } }
    public bool Available { get { return DsGameData.InGame; } }

    // ── build ───────────────────────────────────────────────────────────────

    public void Build(RectTransform host)
    {
        _host = host;

        var layout = DsLayout.Current;
        float panelW = layout.Width;
        float bodyH = layout.Body.height;

        _listTop = Pad;
        _listH = bodyH - Pad * 2f;
        _listRect = layout.InBody(new Rect(ListX, _listTop, ListW, _listH));

        _list = DsWidgets.Rect(host, "list");
        DsWidgets.Place(_list, ListX, _listTop, ListW, _listH);
        // Rows are moved to scroll, so without clipping one scrolled past the
        // top would draw over the tab strip.
        _list.gameObject.AddComponent<RectMask2D>();

        _empty = DsWidgets.Label(_list, "empty", "No tasks accepted", DsTheme.RowSize,
                                 DsTheme.InkDim, TmpAlign.Center);
        if (_empty != null) DsWidgets.Stretch(_empty.rectTransform);

        // ── right: what the selected task actually says ────────────────────
        //
        // The larger half of the panel. A quest's description plus its targets
        // is the only thing here with real prose in it, and the list beside it
        // is a column of short names that needs far less room than it had.
        float detailW = panelW - DetailX - Pad;

        // Down the gutter between the list and what the selected task says.
        DsWidgets.VRule(host, "split", (ListX + ListW + DetailX) * 0.5f, _listTop, _listH);

        _detail = DsWidgets.Rect(host, "detail");
        DsWidgets.Place(_detail, DetailX, _listTop, detailW, _listH);

        _title = DsWidgets.Label(_detail, "title", "", DetailTitleSize,
                                 DsTheme.Ink, TmpAlign.Left);
        if (_title != null) DsWidgets.Place(_title.rectTransform, 0f, 8f, detailW, 110f);

        _desc = DsWidgets.Label(_detail, "desc", "", DetailBodySize,
                                DsTheme.InkDim, TmpAlign.TopLeft);
        if (_desc != null)
            DsWidgets.Place(_desc.rectTransform, 0f, 126f, detailW, _listH - 138f);

        Refresh(force: true);
    }

    public void OnShow() { Refresh(force: true); }
    public void OnHide() { }

    public void Tick(float dt)
    {
        if (Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + 1f;
        Refresh(force: false);
    }

    // ── data ────────────────────────────────────────────────────────────────

    void Refresh(bool force)
    {
        _entries.Clear();

        if (!DsGameData.InGame)
        {
            Apply("");
            return;
        }

        try { Collect(_entries); }
        catch (Exception e) { Debug.LogWarning("[DsTasks] " + e.Message); }

        // Main quests first, then the rest, then anything finished. Stable
        // within each group, so the list does not shuffle as counters tick.
        _entries.Sort((a, b) => Rank(a).CompareTo(Rank(b)));

        var sig = new System.Text.StringBuilder();
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            sig.Append(e.Main ? 'M' : e.Completed ? 'C' : 'a');
            sig.Append(e.Name);
            // The counters belong in the signature, or a quest that ticks from
            // 2/5 to 3/5 changes nothing the rebuild can see and the row keeps
            // the old number until something else moves.
            sig.Append(e.Counter).Append(';');
        }
        Apply(sig.ToString());
    }

    static int Rank(Entry e) { return e.Completed ? 2 : e.Main ? 0 : 1; }

    void Collect(List<Entry> into)
    {
        // GetAllQuests returns the master list directly and touches no cache;
        // GetAcceptedQuests is version-keyed shared state. Same rule as every
        // other screen here: read the master list, filter it ourselves.
        var quests = QuestManager.GetAllQuests();
        if (quests == null) return;

        foreach (var quest in quests)
        {
            if (quest == null) continue;

            bool accepted = false;
            try { accepted = quest.IsAccepted; } catch { }
            if (!accepted) continue;              // an unaccepted quest is a spoiler

            bool hidden = false;
            try { hidden = quest.IsHidden; } catch { }
            if (hidden) continue;                 // the game hides these from its own list

            var full = quest as FullQuestBase;

            bool completed = false;
            try { if (full != null) completed = full.IsCompleted; } catch { }

            // The game's own rule for its main-quest section, minus the
            // completed test, which is applied above.
            bool main = quest is MainQuest && !completed;

            Sprite icon = null;
            try
            {
                if (full != null && full.QuestType != null)
                {
                    icon = full.QuestType.Icon;
                    // A quest ready to hand in gets the game's own "you can
                    // finish this" icon, which is the most useful thing the
                    // list can tell you at a glance.
                    if (!completed && full.CanComplete && full.QuestType.CanCompleteIcon != null)
                        icon = full.QuestType.CanCompleteIcon;
                }
            }
            catch { }

            string name = "";
            try { name = quest.DisplayName; } catch { }

            string desc = "";
            try { desc = quest.GetDescription(BasicQuestBase.ReadSource.Inventory); } catch { }

            // quest.Location is deliberately not shown. It names where the
            // quest was taken -- the board or the giver -- which is the one
            // thing you already know and never the thing you are looking at
            // this screen to find out.

            var entry = new Entry
            {
                Name = string.IsNullOrEmpty(name) ? quest.name : name,
                Desc = desc,
                Icon = icon,
                Main = main,
                Completed = completed,
            };
            CollectSteps(full, entry);
            into.Add(entry);
        }
    }

    /// <summary>
    /// A quest's targets and how far along each one is.
    ///
    /// FullQuestBase.TargetsAndCounters is the pair sequence the game's own UI
    /// uses, and it already excludes targets flagged HideInCount -- the ones
    /// the game itself does not put in front of the player. Note the naming is
    /// the opposite way round to what it looks like: TargetsAndCountersNotHidden
    /// is the UNFILTERED one, and this is the filtered one.
    ///
    /// Counters is not a plain field read: it resolves alt-tests and reads each
    /// counter's completion amount, and returns the required count outright
    /// once the quest is complete, so a finished quest reads as full rather
    /// than as whatever the counter happens to hold. That is the game's own
    /// answer to "how far along is this", which is the point of asking it
    /// rather than counting something ourselves.
    /// </summary>
    static void CollectSteps(FullQuestBase full, Entry entry)
    {
        if (full == null) return;

        try
        {
            foreach (var pair in full.TargetsAndCounters)
            {
                var target = pair.target;
                int need = target.Count;
                if (need <= 0) continue;              // nothing to count towards

                string label = null;
                try
                {
                    if (target.Counter != null) label = target.Counter.GetUIMsgName();
                    if (string.IsNullOrEmpty(label)) label = target.ItemName;
                }
                catch { }

                entry.Steps.Add(new Step
                {
                    Name = string.IsNullOrEmpty(label) ? "Progress" : label,
                    Have = Mathf.Min(pair.count, need),   // the game can over-count; the goal is the cap
                    Need = need,
                });
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DsTasks] targets: " + e.Message);
        }

        if (entry.Steps.Count == 0) return;

        // One number for the row. A single target shows its own count, which is
        // what nearly every quest has; several show how many are finished,
        // because "7/12" summed across unrelated things would mean nothing.
        if (entry.Steps.Count == 1)
        {
            var s = entry.Steps[0];
            entry.Counter = s.Have + "/" + s.Need;
            entry.CounterDone = s.Have >= s.Need;
        }
        else
        {
            int done = 0;
            for (int i = 0; i < entry.Steps.Count; i++)
                if (entry.Steps[i].Have >= entry.Steps[i].Need) done++;
            entry.Counter = done + "/" + entry.Steps.Count;
            entry.CounterDone = done >= entry.Steps.Count;
        }
    }

    // ── layout ──────────────────────────────────────────────────────────────

    void Apply(string signature)
    {
        if (signature == _signature) { Paint(); return; }
        _signature = signature;

        // Selection is held by name, so it survives a rebuild caused by a
        // counter changing somewhere else in the list.
        string keep = (_selected >= 0 && _selected < _rows.Count && _selected < _entries.Count)
                    ? _entries[_selected].Name : null;

        Rebuild();

        _selected = -1;
        if (keep != null)
        {
            for (int i = 0; i < _entries.Count; i++)
                if (_entries[i].Name == keep) { _selected = i; break; }
        }
        if (_selected < 0 && _entries.Count > 0) _selected = 0;

        Paint();
        PaintDetail();
    }

    void Rebuild()
    {
        for (int i = 0; i < _dividers.Count; i++)
            if (_dividers[i].Root != null) UnityEngine.Object.Destroy(_dividers[i].Root.gameObject);
        _dividers.Clear();
        _entryY.Clear();

        DsWidgets.SetActive(_empty, _entries.Count == 0);

        while (_rows.Count < _entries.Count) _rows.Add(MakeRow(_rows.Count));
        for (int i = 0; i < _rows.Count; i++)
            DsWidgets.SetActive(_rows[i].Root, i < _entries.Count);

        float y = 0f;
        bool wroteMainRule = false, wroteCompletedRule = false;

        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];

            // A bare rule under the priorities, and a captioned one before the
            // finished pile. The first is tight to the rows it separates: main
            // and ordinary quests are one list in priority order, so the line
            // marks where the priorities stop rather than splitting the list in
            // two. The second is given room, because the finished pile really
            // is a different thing.
            if (!e.Main && !e.Completed && !wroteMainRule && i > 0)
            {
                wroteMainRule = true;
                y += MakeDivider(null, y);
            }
            if (e.Completed && !wroteCompletedRule)
            {
                wroteCompletedRule = true;
                if (i > 0) y += CompletedGap;
                y += MakeDivider("COMPLETED", y);
            }

            var row = _rows[i];
            DsWidgets.Place(row.Root, 0f, y, ListW, RowH);
            _entryY.Add(y);

            row.Icon.sprite = e.Icon;
            row.Icon.color = e.Icon != null ? (e.Completed ? DsTheme.InkFaint : Color.white) : Color.clear;

            // Everything asked for is done but the quest has not been handed in.
            // Weight rather than colour: gold is what the game uses for a thing
            // you can act on, and spending it on a row in a list means the eye
            // is drawn to it before the tab it belongs to. Bold says the same
            // "this one is ready" without competing with the game's own palette.
            bool ready = e.CounterDone && !e.Completed;

            if (row.Label != null)
            {
                row.Label.text = e.Name;
                row.Label.color = e.Completed ? DsTheme.InkFaint
                                : e.Main ? DsTheme.Ink : DsTheme.InkDim;
                row.Label.fontStyle = ready ? TMProOld.FontStyles.Bold
                                            : TMProOld.FontStyles.Normal;
            }

            if (row.Counter != null)
            {
                // A finished quest's counter is noise -- it is in the completed
                // group and greyed out already, and every one of them reads n/n.
                row.Counter.text = e.Completed ? "" : (e.Counter ?? "");
                row.Counter.color = DsTheme.InkFaint;
                row.Counter.fontStyle = ready ? TMProOld.FontStyles.Bold
                                              : TMProOld.FontStyles.Normal;
            }

            y += RowH;
        }

        _maxScroll = Mathf.Max(0f, y - _listH);
        _scroll = Mathf.Clamp(_scroll, 0f, _maxScroll);
    }

    Row MakeRow(int index)
    {
        var root = DsWidgets.Rect(_list, "row" + index);
        var fill = DsWidgets.Box(root, "fill", Color.clear);
        DsWidgets.Stretch(fill.rectTransform);

        var icon = DsWidgets.Icon(root, "icon", null, Color.white);
        DsWidgets.Place(icon.rectTransform, 8f, (RowH - IconSize) * 0.5f, IconSize, IconSize);

        var label = DsWidgets.Label(root, "name", "", NameSize, DsTheme.Ink, TmpAlign.Left);
        if (label != null)
            DsWidgets.Place(label.rectTransform, 8f + IconSize + 14f, 0f,
                            ListW - IconSize - CounterW - 46f, RowH);

        // Right-aligned, so a column of counters lines up down the list however
        // long the names beside them are.
        var counter = DsWidgets.Label(root, "count", "", DsTheme.RowSize,
                                      DsTheme.InkDim, TmpAlign.Right);
        if (counter != null)
            DsWidgets.Place(counter.rectTransform, ListW - CounterW - 12f, 0f, CounterW, RowH);

        return new Row { Root = root, Fill = fill, Icon = icon, Label = label, Counter = counter };
    }

    /// <summary>A rule, captioned or bare. Returns its height.</summary>
    float MakeDivider(string title, float y)
    {
        bool captioned = !string.IsNullOrEmpty(title);
        float h = captioned ? DividerH : PlainDividerH;

        var root = DsWidgets.Rect(_list, "div" + _dividers.Count);
        DsWidgets.Place(root, 0f, y, ListW, h);

        TmpText label = null;
        if (captioned)
        {
            label = DsWidgets.Label(root, "t", title, DsTheme.SmallSize,
                                    DsTheme.InkDim, TmpAlign.Left, display: true);
            if (label != null) DsWidgets.Place(label.rectTransform, 8f, 0f, 340f, 26f);
        }

        DsWidgets.HRule(root, "rule", 8f, h - 9f, ListW - 16f);

        _dividers.Add(new Divider { Root = root, Label = label, Y = y, H = h });
        return h;
    }

    void Paint()
    {
        for (int i = 0; i < _entryY.Count && i < _rows.Count; i++)
            DsWidgets.Place(_rows[i].Root, 0f, _entryY[i] - _scroll, ListW, RowH);

        // Dividers scroll with the rows. They are part of the list, not a frame
        // around it, and leaving them pinned made the "COMPLETED" rule float
        // over unrelated entries.
        for (int i = 0; i < _dividers.Count; i++)
        {
            var d = _dividers[i];
            if (d.Root != null) DsWidgets.Place(d.Root, 0f, d.Y - _scroll, ListW, d.H);
        }

        for (int i = 0; i < _rows.Count && i < _entries.Count; i++)
            _rows[i].Fill.color = i == _selected ? DsTheme.Panel : Color.clear;
    }

    void PaintDetail()
    {
        bool ok = _selected >= 0 && _selected < _entries.Count;
        if (_title != null) _title.text = ok ? _entries[_selected].Name : "";
        if (_desc == null) return;
        if (!ok) { _desc.text = ""; return; }

        var e = _entries[_selected];
        var sb = new System.Text.StringBuilder(e.Desc ?? "");

        // The targets, spelled out under the description. This is the part you
        // open a quest to see -- the description says what was asked for, the
        // counters say how much of it is done -- and it is why the row's single
        // number is only ever a summary.
        if (e.Steps.Count > 0 && !e.Completed)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            for (int i = 0; i < e.Steps.Count; i++)
            {
                var s = e.Steps[i];
                if (i > 0) sb.Append('\n');
                sb.Append(s.Have >= s.Need ? "* " : "- ")
                  .Append(s.Name).Append("   ")
                  .Append(s.Have).Append('/').Append(s.Need);
            }
        }

        _desc.text = sb.ToString();
    }

    // ── input ───────────────────────────────────────────────────────────────

    public void OnGesture(DsGesture g)
    {
        Vector2 p = DsPresentation.ToLayout(g.Position);

        switch (g.Type)
        {
            case DsGestureType.Tap:
                int hit = HitTest(p);
                if (hit >= 0) { _selected = hit; Paint(); PaintDetail(); }
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
        float y = layoutPoint.y - _listRect.y + _scroll;
        for (int i = 0; i < _entryY.Count; i++)
            if (y >= _entryY[i] && y <= _entryY[i] + RowH) return i;
        return -1;
    }
}
#endif
