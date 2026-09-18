// DsTasksScreen — what Hornet has been asked to do, in the shape the game
// shows it in.
//
// This has been wrong twice, and both mistakes are worth keeping written down.
// The first version was a four-column icon grid like every other screen, which
// is wrong for data read by NAME rather than recognised by picture. The second
// was a single column of name + counter, which was right about the reading and
// wrong about everything else -- because the game does not present a list. It
// lays quests out TWO ACROSS, with the quest's TYPE set small above its name,
// and the type is half the sentence: "Last Dive" is a place, "DESCEND / Last
// Dive" is an instruction.
//
//     +-----------------------------------+---------------------+
//     |        (   )  DESCEND             |  DESCEND            |
//     |        (   )  Last Dive           |  Last Dive          |
//     |  -------------------------------  |                     |
//     |  ( ) SEEK        ( ) SAVE         |  Dive into the void |
//     |      Great Citadel   Threadspun   |  and cut Pharloom   |
//     |  ---<>--- COMPLETED ---<>------   |  free.              |
//     |  ( ) LEARN       ( ) LEARN        |                     |
//     +-----------------------------------+---------------------+
//
// The grouping is the game's, not ours, and each rule below names the method it
// comes from so it can be checked rather than believed:
//
//   * TWO ACROSS is InventoryItemGrid's RowSplit on the Quests section.
//   * A PRIORITISED task is InventoryItemQuestManager.IsInMainQuestSection --
//         MainQuest mainQuest = quest as MainQuest;
//         if (mainQuest == null) return false;
//         return !mainQuest.IsCompleted;
//     a type test, not a name list, which is why "Great Citadel" sorts above
//     "Fleatopia" without us having an opinion about either.
//   * The TYPE line is InventoryItemQuest.SetQuest: typeText.text is
//     quest.QuestType.DisplayName, tinted QuestType.TextColor, and set in caps
//     by the label's own style rather than by rewriting the string.
//   * The two GROUPS are GetGridSections: current quests (rumours appended)
//     and completed quests, each with its own heading Transform.
//   * HIDE/SHOW COMPLETED is the manager's isCompletedQuestsVisible, offered
//     only while completedQuests.Count > 0 -- a control that would do nothing
//     is not shown at all.
//
// Two places where we knowingly differ from the game, both because the panel is
// a touch screen and not a gamepad:
//
//   * The game binds the toggle to MenuActions.Super and draws a Y glyph beside
//     it. Nothing on this panel is reachable by controller, so it takes the
//     header action bar every other screen's controls live in.
//   * The game's divider between the main quest and the rest is an invisible
//     `Spacer`. Set two across, the priorities stop mid-row and the break is
//     genuinely hard to see, so ours is drawn.
//
// What the cell gave up to gain the type line: the per-quest counter. Three
// things carry the "how far along" signal without it -- the game's own
// CanCompleteIcon replaces the icon when a quest is ready to hand in, the name
// goes bold with it, and the description pane still breaks every target down
// individually. A number beside a name in a 384-pixel cell would have cost the
// name the room the design gives it.

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TmpText = TMProOld.TextMeshProUGUI;
using TmpAlign = TMProOld.TextAlignmentOptions;

public class DsTasksScreen : IDsScreen, IDsActionBar
{
    // What the list holds, flattened once so drawing never asks the game
    // anything.
    class Entry
    {
        public string Name, Type, Desc;
        /// <summary>
        /// The quest asset's own name. Identity for selection and for the
        /// caret, and deliberately NOT the display name: display names are
        /// localised and are not guaranteed unique, so a list holding two
        /// quests that read alike would hand the caret to whichever came first.
        /// </summary>
        public string Key;
        /// <summary>
        /// Where the game listed this, kept so sorting into groups cannot also
        /// shuffle the entries inside one.
        /// </summary>
        public int Order;
        public Sprite Icon;
        /// <summary>The quest type's own colour, for the line above the name.</summary>
        public Color TypeColour;
        public bool Main, Completed;
        /// <summary>Per-target progress, empty when the quest counts nothing.</summary>
        public readonly List<Step> Steps = new List<Step>();
        /// <summary>The dots or bar drawn under the name.</summary>
        public Counter Progress;
        /// <summary>True when everything asked for is done but it is not handed in.</summary>
        public bool Ready;
    }

    /// <summary>One of a quest's targets: what to collect, how many, how many so far.</summary>
    struct Step
    {
        public string Name;
        public int Have, Need;
    }

    /// <summary>
    /// The progress indicator under a quest's name, as the game's own list
    /// draws it.
    ///
    /// Which one a quest gets is the QUEST's decision, not a heuristic about
    /// how many things it counts: FullQuestBase.ListCounterType says Dots, Bar
    /// or None outright, and it answers None by itself for a quest flagged
    /// hideCountersWhenCompletable that is ready to hand in. That is why this
    /// stores the game's own enum rather than working anything out.
    /// </summary>
    struct Counter
    {
        public FullQuestBase.ListCounterTypes Kind;
        /// <summary>How many dots to draw, and how many of them are filled.</summary>
        public int Dots, DotsFilled;
        /// <summary>A single bar's progress, 0..1.</summary>
        public float Value;
        /// <summary>
        /// One bar per target, when a quest has more than one. The game splits
        /// the bar rather than averaging, so a quest that is done with one of
        /// two things shows one full segment and one empty -- which is a
        /// different statement from "half done".
        /// </summary>
        public float[] Segments;
        public Color Tint;
    }

    // One drawn cell. Pooled: the set of quests changes rarely, and rebuilding
    // a few dozen GameObjects on a one-second timer would be silly.
    class Cell
    {
        public RectTransform Root;
        /// <summary>
        /// The square the icon is centred IN, so that fitting the art and
        /// placing the slot stay separate jobs. Without it the two fight: the
        /// icon has to be anchored to the middle of something for its trimmed
        /// mesh to be centred, and the thing it must sit at the left of is the
        /// cell.
        /// </summary>
        public RectTransform IconSlot;
        /// <summary>
        /// Where the dots or bar go. Its children are rebuilt rather than
        /// pooled, because the number of dots is a property of the quest and
        /// changes as the quest is worked on.
        /// </summary>
        public RectTransform CounterRoot;
        public Image Icon;
        public TmpText Type, Label;
    }

    // Where a cell sits in list space, so a tap can be turned back into one.
    struct Placed
    {
        public float X, Y, W, H;
        /// <summary>
        /// The part of the cell the caret frames, which is not always the cell.
        ///
        /// A prioritised quest owns a full-width row but draws its icon and
        /// words centred in it, and brackets thrown around the whole row would
        /// frame a great deal of nothing. The tap target stays the full row --
        /// there is no reason to make the one thing you are doing harder to hit
        /// than the rest.
        /// </summary>
        public float FocusX, FocusW;
    }

    // A rule, optionally captioned, drawn between groups.
    class Divider
    {
        public RectTransform Root;
        public float Y, H;
    }

    const float ListX   = 20f;
    const float ListW   = 780f;
    const float DetailX = 840f;

    // Two across, as the game's own grid is. Everything below follows from it:
    // a 384-pixel cell is what a quest name has to fit in, which is what sets
    // the icon, the faces and the wrapping.
    const int   Columns  = 2;
    const float CellGap  = 12f;
    const float CellW    = (ListW - CellGap * (Columns - 1)) / Columns;
    const float CellH    = 112f;
    const float CellIcon = 84f;

    // The prioritised quest takes the whole width and a larger icon. It is not
    // one of several things to choose between -- it is the thing you are doing
    // -- and the game gives it its own template for the same reason.
    const float MainH    = 136f;
    const float MainIcon = 104f;

    const float IconGap = 14f;
    const float CellPadL = 4f;
    const float CellPadR = 8f;

    // The type line is set to the game's own proportion: 3.8 against 6, a shade
    // under two-thirds. Smaller than that and the caps stop reading as a word;
    // larger and it competes with the name it is introducing.
    const float TypeSize     = 21f;
    const float NameSize     = 34f;
    const float MainTypeSize = 25f;
    const float MainNameSize = 46f;

    // The progress row under the name. Sized to be read at a glance rather than
    // counted carefully -- the exact numbers are in the description pane, and
    // what the row is for is "nearly done" versus "barely started".
    const float DotSize     = 13f;
    const float MainDotSize = 15f;
    const float DotGap      = 9f;
    const float BarH        = 11f;
    const float MainBarH    = 13f;
    const float BarW        = 210f;
    const float SegmentGap  = 5f;
    /// <summary>The band the counter is drawn in, under the name.</summary>
    const float CounterH     = 22f;
    const float MainCounterH = 26f;
    // A dot per unit is the game's own scheme and is fine for the counts real
    // quests use -- the largest seen is a dozen or so. This is only a stop
    // against a pathological quest turning one cell into hundreds of
    // GameObjects; past it the row switches to a bar, which says the same thing
    // in a fixed amount of space.
    const int MaxDots = 24;
    // Long names shrink rather than truncate. "The Threadspun Town" does not fit
    // a half-width cell at 34, and a name is the one thing on this screen that
    // must arrive whole -- half of it is not a smaller version of the
    // information, it is different information.
    const float NameSizeMin  = 24f;

    const float PlainDividerH = 26f;
    // The rule under the priorities is centred and well short of the column,
    // the way the game's is: a line all the way across reads as the end of the
    // list rather than as a break in it.
    const float PlainRuleW = 440f;
    const float CompletedDividerH = 54f;
    // Space above the COMPLETED rule, on top of the divider's own height. The
    // finished pile is further from the list than the two live groups are from
    // each other, and the gap is what says so.
    const float CompletedGap = 26f;
    const float CompletedSize = 26f;
    // The ornament either side of the caption, drawn at its own aspect.
    const float FluerH = 26f;
    const float FluerGap = 16f;
    // How far the hairline runs outward from each ornament. The game's divider
    // is a centred device rather than a line across the column, and the reach is
    // what keeps it one.
    const float DividerReach = 108f;

    // The description is prose and is read, not scanned, so it is larger than
    // the shared body size.
    const float DetailTypeSize = 26f;
    const float DetailTitleSize = 42f;
    const float DetailBodySize = 32f;
    const float Pad = DsTheme.Pad;
    // Room for the selection brackets to reach outside the cell they frame,
    // without the scroll mask clipping them off.
    const float CursorBleed = 16f;

    readonly List<Entry> _entries = new List<Entry>();
    readonly List<Cell> _cells = new List<Cell>();
    readonly List<Divider> _dividers = new List<Divider>();
    readonly List<Placed> _placed = new List<Placed>();
    // Selection is drawn by the travelling caret every other screen uses, in the
    // scroll mask with the cells so it is clipped exactly as they are.
    readonly DsCursor _cursor = new DsCursor();

    RectTransform _host, _list, _detail;
    TmpText _title, _type, _desc, _empty;

    Rect _listRect;             // panel space, for hit-testing
    float _listTop, _listH;
    float _scroll, _maxScroll;
    int _selected = -1;
    /// <summary>
    /// What is selected, by key rather than by index, because the list is
    /// rebuilt from the game every second and an index into the previous
    /// ordering means nothing in the new one.
    /// </summary>
    string _selectedKey;
    string _signature;
    float _nextRefresh;
    // How many finished quests exist, whether or not they are being shown. The
    // toggle is offered on the strength of this, so hiding them cannot hide the
    // control that brings them back.
    int _completedCount;
    bool _showCompleted = true;

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

        // Cells are moved to scroll, so without clipping one scrolled past the
        // top would draw over the tab strip. The clip is its own rect, grown by
        // the bracket overhang, so a caret that reaches outside its cell is
        // still drawn -- the list itself keeps its exact size inside it, and
        // layout, scrolling and hit-testing are unaffected.
        var clip = DsWidgets.Rect(host, "list-clip");
        DsWidgets.Place(clip, ListX - CursorBleed, _listTop - CursorBleed,
                        ListW + CursorBleed * 2f, _listH + CursorBleed * 2f);
        clip.gameObject.AddComponent<RectMask2D>();

        _list = DsWidgets.Rect(clip, "list");
        DsWidgets.Place(_list, CursorBleed, CursorBleed, ListW, _listH);

        _empty = DsWidgets.Label(_list, "empty", "No tasks accepted", DsTheme.RowSize,
                                 DsTheme.InkDim, TmpAlign.Center);
        if (_empty != null) DsWidgets.Stretch(_empty.rectTransform);

        // ── right: what the selected task actually says ────────────────────
        float detailW = panelW - DetailX - Pad;

        // Down the gutter between the list and what the selected task says.
        DsWidgets.VRule(host, "split", (ListX + ListW + DetailX) * 0.5f, _listTop, _listH);

        _detail = DsWidgets.Rect(host, "detail");
        DsWidgets.Place(_detail, DetailX, _listTop, detailW, _listH);

        // The description repeats the cell's own two lines, type above name, so
        // that reading across from a selection lands on the same words in the
        // same order rather than on a bare title.
        _type = DsWidgets.Label(_detail, "type", "", DetailTypeSize,
                                DsTheme.InkDim, TmpAlign.Left);
        if (_type != null)
        {
            _type.fontStyle = TMProOld.FontStyles.UpperCase;
            _type.enableWordWrapping = false;
            DsWidgets.Place(_type.rectTransform, 0f, 8f, detailW, 32f);
        }

        _title = DsWidgets.Label(_detail, "title", "", DetailTitleSize,
                                 DsTheme.Ink, TmpAlign.Left);
        if (_title != null) DsWidgets.Place(_title.rectTransform, 0f, 42f, detailW, 100f);

        _desc = DsWidgets.Label(_detail, "desc", "", DetailBodySize,
                                DsTheme.InkDim, TmpAlign.TopLeft);
        if (_desc != null)
            DsWidgets.Place(_desc.rectTransform, 0f, 150f, detailW, _listH - 162f);

        // In the scroll mask, with the cells. A half-scrolled cell gets a
        // half-drawn caret, and one scrolled away takes its caret with it.
        _cursor.Build(_list);
        // The cells are wide rectangles rather than squares of art, so the
        // brackets are set by a fraction of the SHORTER side: a pixel inset that
        // sits tight on a 112-high cell is lost on a 136-high one.
        _cursor.CornerInsetFraction = 0.12f;

        Refresh(force: true);
    }

    public void OnShow() { Refresh(force: true); }
    public void OnHide() { }

    public void Tick(float dt)
    {
        _cursor.Tick(dt);
        if (Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + 1f;
        Refresh(force: false);
    }

    // ── actions ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The completed-quest toggle, in the header with every other screen's
    /// controls.
    ///
    /// Offered only when something is actually finished, which is the game's
    /// own condition (completedQuests.Count > 0) rather than a nicety: a button
    /// that hides an empty group is a control the player has to try in order to
    /// discover it does nothing.
    /// </summary>
    public void CollectActions(List<DsAction> into)
    {
        if (_completedCount <= 0) return;
        into.Add(new DsAction(_showCompleted ? "HIDE COMPLETED" : "SHOW COMPLETED",
                              ToggleCompleted));
    }

    void ToggleCompleted()
    {
        _showCompleted = !_showCompleted;
        // Hiding a group above the scroll position would otherwise leave the
        // list parked past its own end.
        _scroll = 0f;
        Refresh(force: true);
    }

    // ── data ────────────────────────────────────────────────────────────────

    void Refresh(bool force)
    {
        _entries.Clear();
        _completedCount = 0;

        if (!DsGameData.InGame)
        {
            Apply("", force);
            return;
        }

        try { Collect(_entries); }
        catch (Exception e) { Debug.LogWarning("[DsTasks] " + e.Message); }

        // Main quests first, then the rest, then anything finished -- and
        // within a group, the order the game listed them in.
        //
        // The ordinal tiebreak is not decoration. List.Sort is an introsort and
        // is NOT stable, so comparing on rank alone leaves entries of equal rank
        // free to swap: accepting one quest could visibly reshuffle unrelated
        // ones, which on a list read by name looks like the save has changed
        // under the player.
        _entries.Sort((a, b) =>
        {
            int byRank = Rank(a).CompareTo(Rank(b));
            return byRank != 0 ? byRank : a.Order.CompareTo(b.Order);
        });

        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].Completed) _completedCount++;

        if (!_showCompleted)
            _entries.RemoveAll(e => e.Completed);

        var sig = new System.Text.StringBuilder();
        // The toggle is part of the shape of the list, so it belongs in the
        // signature: without it, hiding the finished pile changes nothing the
        // rebuild can see and the cells stay exactly where they were.
        sig.Append(_showCompleted ? "S;" : "H;");

        // So does whether the game has handed us the divider ornament yet. It
        // arrives only once the game's own inventory has been opened, which can
        // happen while this screen is sitting in front of the player, and a
        // divider drawn before then has to be rebuilt to pick the art up.
        // Asking costs nothing here -- DsGameArt rate-limits the search itself
        // -- and is skipped entirely when no divider is being drawn.
        if (_showCompleted && _completedCount > 0)
            sig.Append(DsGameArt.QuestDivider() != null ? "F;" : "f;");

        // And the same for the dot and bar art, for the same reason: a cell
        // drawn with the fallback shapes has to be rebuilt once the game's own
        // are available.
        var counterArt = DsGameArt.QuestCounterArt();
        sig.Append(counterArt != null && counterArt.Ok ? "C;" : "c;");

        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            sig.Append(e.Main ? 'M' : e.Completed ? 'C' : 'a');
            sig.Append(e.Type).Append('/').Append(e.Name);
            // "Ready to hand in" changes the icon and the weight of the name, so
            // it has to be visible to the rebuild too.
            sig.Append(e.Ready ? '!' : '.');
            // So does the progress, now that it is DRAWN in the cell rather
            // than only written in the description pane: a quest ticking from
            // 2/5 to 3/5 has to fill another dot, and without this the cell
            // keeps the old row until something else in the list moves.
            sig.Append((int)e.Progress.Kind).Append(':')
               .Append(e.Progress.Dots).Append('/').Append(e.Progress.DotsFilled).Append(':')
               .Append(Mathf.RoundToInt(e.Progress.Value * 1000f));
            if (e.Progress.Segments != null)
                for (int s = 0; s < e.Progress.Segments.Length; s++)
                    sig.Append(',').Append(Mathf.RoundToInt(e.Progress.Segments[s] * 1000f));
            sig.Append(';');
        }
        Apply(sig.ToString(), force);
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
            string type = "";
            Color typeColour = DsTheme.InkDim;
            bool canComplete = false;
            try
            {
                if (full != null && full.QuestType != null)
                {
                    var questType = full.QuestType;
                    icon = questType.Icon;
                    type = questType.DisplayName;

                    // Not every type carries a colour, and an unset one is
                    // transparent black -- which would draw the header as a hole
                    // rather than as text.
                    var tint = questType.TextColor;
                    if (tint.a > 0.1f && (tint.r + tint.g + tint.b) > 0.1f) typeColour = tint;

                    canComplete = !completed && full.CanComplete;
                    // A quest ready to hand in gets the game's own "you can
                    // finish this" icon, which is the most useful thing the
                    // list can tell you at a glance.
                    if (canComplete && questType.CanCompleteIcon != null)
                        icon = questType.CanCompleteIcon;
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
                Key = quest.name,
                Order = into.Count,
                Type = type ?? "",
                Desc = desc,
                Icon = icon,
                TypeColour = typeColour,
                Main = main,
                Completed = completed,
                Ready = canComplete,
            };
            CollectSteps(full, entry);
            CollectCounter(full, entry);
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

                string label = TargetLabel(target);

                entry.Steps.Add(new Step
                {
                    Name = label,                         // null when it has no usable name
                    Have = Mathf.Min(pair.count, need),   // the game can over-count; the goal is the cap
                    Need = need,
                });
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DsTasks] targets: " + e.Message);
        }
    }

    /// <summary>
    /// Work out the progress indicator, following InventoryItemQuest.SetQuest
    /// step for step.
    ///
    /// The game's arithmetic, which this reproduces:
    ///
    ///     num  += Mathf.Clamp(count, 0, target.Count)   // done, per target
    ///     num2 += target.Count                          // asked for
    ///     num3++                                        // how many targets
    ///
    /// then Dots draws `HideMax ? num : num2` of them with `num` filled, and
    /// Bar draws one bar at num/num2 -- or, with more than one target, a
    /// SEGMENT PER TARGET at that target's own fraction.
    ///
    /// Two deliberate differences, both narrow:
    ///
    ///   * A completed quest gets nothing, because SetQuest guards this whole
    ///     block with `!isInCompletedSection`.
    ///   * Targets asking for nothing are skipped, where the game counts them.
    ///     One would contribute a division by zero to the game's own segmented
    ///     bar, so this is a guard rather than a change of rule.
    /// </summary>
    static void CollectCounter(FullQuestBase full, Entry entry)
    {
        entry.Progress.Kind = FullQuestBase.ListCounterTypes.None;
        if (full == null || entry.Completed || entry.Steps.Count == 0) return;

        try
        {
            // Not a plain field read: ListCounterType answers None by itself
            // for a quest that is ready to hand in and flagged
            // hideCountersWhenCompletable.
            var kind = full.ListCounterType;
            if (kind == FullQuestBase.ListCounterTypes.None) return;

            int have = 0, need = 0;
            for (int i = 0; i < entry.Steps.Count; i++)
            {
                have += entry.Steps[i].Have;      // already clamped to Need
                need += entry.Steps[i].Need;
            }
            if (need <= 0) return;

            entry.Progress.Kind = kind;
            entry.Progress.Tint = full.ProgressBarTint;
            // Set for BOTH kinds, not just Bar. A dot row past the sane
            // maximum falls back to drawing a bar, and that fallback has
            // nothing to draw unless the fraction was worked out here.
            entry.Progress.Value = (float)have / need;

            if (kind == FullQuestBase.ListCounterTypes.Dots)
            {
                // HideMax makes the row say "how many you have" rather than
                // "how many there are", for a quest that does not want to give
                // its total away.
                entry.Progress.Dots = full.HideMax ? have : need;
                entry.Progress.DotsFilled = have;
            }
            else if (entry.Steps.Count > 1)
            {
                var segments = new float[entry.Steps.Count];
                for (int i = 0; i < entry.Steps.Count; i++)
                {
                    var s = entry.Steps[i];
                    segments[i] = s.Need > 0 ? (float)s.Have / s.Need : 0f;
                }
                entry.Progress.Segments = segments;
            }
        }
        catch (Exception e)
        {
            entry.Progress.Kind = FullQuestBase.ListCounterTypes.None;
            Debug.LogWarning("[DsTasks] counter: " + e.Message);
        }
    }

    /// <summary>
    /// What to call one of a quest's targets, or null when it has no name
    /// worth showing.
    ///
    /// The order is the game's, from FullQuestBase.MaybeAppendItemList: the
    /// ITEM's name when it has one, and only otherwise the counter's popup
    /// name. This used to ask the counter first, which is how a target with a
    /// perfectly good item name ended up labelled by its counter instead.
    /// </summary>
    static string TargetLabel(FullQuestBase.QuestTarget target)
    {
        string label = null;
        try
        {
            // IsEmpty BEFORE converting, which is the whole trick -- see
            // Localised below for what an unguarded conversion produces.
            var itemName = target.ItemName;
            if (!itemName.IsEmpty) label = itemName;
            else if (target.Counter != null) label = target.Counter.GetUIMsgName();
        }
        catch { }
        return Localised(label);
    }

    /// <summary>
    /// A localised string, or null if what came back is not real text.
    ///
    /// An empty LocalisedString does NOT convert to an empty string. Team
    /// Cherry's lookup answers an unresolvable entry with the literal
    /// "!!" + Sheet + "/" + Key + "!!", so a target with no name set arrives as
    /// the four characters "!!/!!" -- which is not null, not empty, not
    /// whitespace, and sails through every test of that kind.
    ///
    /// It showed up as "- !!/!!   71/100" beside a quest's progress, and it is
    /// worth guarding rather than special-casing because the same marker can
    /// come back from a key that simply has no translation in the current
    /// language.
    /// </summary>
    static string Localised(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.Length >= 4 && s.StartsWith("!!") && s.EndsWith("!!")) return null;
        return s;
    }

    // ── layout ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Take a freshly collected list, rebuilding the layout only if its SHAPE
    /// changed.
    ///
    /// The signature covers what the cells are and where they go. It cannot
    /// cover everything, and deliberately does not: a quest ticking from 2/5 to
    /// 3/5 changes no cell -- the counters live in the description pane now --
    /// so the pane is repainted on every refresh whether or not anything was
    /// rebuilt. Leaving that inside the rebuild is why the detail pane used to
    /// sit on a stale count until something else in the list happened to move.
    /// </summary>
    void Apply(string signature, bool force)
    {
        if (!force && signature == _signature)
        {
            // Same shape, possibly new numbers.
            Resolve();
            Paint();
            PaintDetail();
            return;
        }
        _signature = signature;

        Rebuild();
        Resolve();
        Paint();
        PaintDetail();
    }

    /// <summary>
    /// Point _selected at whatever _selectedKey names, now that the list has
    /// been rebuilt.
    ///
    /// The key is held on the side rather than read back out of the list,
    /// because Refresh clears and refills _entries before this runs: an index
    /// kept from last time addresses the NEW ordering, so reading the "previous"
    /// selection from it returns whichever quest has since taken that slot.
    /// Accepting a main quest, which pushes everything down one, was enough to
    /// move the caret to the neighbour.
    /// </summary>
    void Resolve()
    {
        _selected = -1;
        if (!string.IsNullOrEmpty(_selectedKey))
        {
            for (int i = 0; i < _entries.Count; i++)
                if (_entries[i].Key == _selectedKey) { _selected = i; break; }
        }
        if (_selected < 0 && _entries.Count > 0) _selected = 0;
        _selectedKey = _selected >= 0 && _selected < _entries.Count
                     ? _entries[_selected].Key : null;
    }

    void Rebuild()
    {
        for (int i = 0; i < _dividers.Count; i++)
            if (_dividers[i].Root != null) UnityEngine.Object.Destroy(_dividers[i].Root.gameObject);
        _dividers.Clear();
        _placed.Clear();

        DsWidgets.SetActive(_empty, _entries.Count == 0);

        while (_cells.Count < _entries.Count) _cells.Add(MakeCell(_cells.Count));
        for (int i = 0; i < _cells.Count; i++)
            DsWidgets.SetActive(_cells[i].Root, i < _entries.Count);

        float y = 0f;
        int column = 0;
        bool sawMain = false, wroteMainRule = false, wroteCompletedRule = false;

        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.Main) sawMain = true;

            // A bare rule under the priorities, and a captioned one before the
            // finished pile. The first is tight to the cells it separates:
            // prioritised and ordinary quests are one list in priority order, so
            // the line marks where the priorities stop rather than splitting the
            // list in two. The second is given room, because the finished pile
            // really is a different thing.
            //
            // Both close the open row first. A group that began in the right-
            // hand column of the previous group's last row would read as part of
            // it, whatever is drawn in between.
            //
            // sawMain, not i > 0: the rule separates priorities from the rest,
            // so with no prioritised quest there is nothing for it to separate.
            // Testing the index instead drew it between the first and second
            // ordinary quest of a save that had never had a main quest at all.
            if (!e.Main && !e.Completed && !wroteMainRule)
            {
                wroteMainRule = true;
                if (sawMain)
                {
                    y += CloseRow(ref column);
                    y += MakePlainDivider(y);
                }
            }
            if (e.Completed && !wroteCompletedRule)
            {
                wroteCompletedRule = true;
                y += CloseRow(ref column);
                if (i > 0) y += CompletedGap;
                y += MakeCompletedDivider(y);
            }

            // The prioritised quest is its own row at its own size.
            if (e.Main && column != 0) { y += CellH; column = 0; }

            float w = e.Main ? ListW : CellW;
            float h = e.Main ? MainH : CellH;
            float x = e.Main ? 0f : column * (CellW + CellGap);

            var cell = _cells[i];
            DsWidgets.Place(cell.Root, x, y, w, h);

            float blockX, blockW;
            PaintCell(cell, e, w, h, out blockX, out blockW);
            _placed.Add(new Placed
            {
                X = x, Y = y, W = w, H = h,
                FocusX = x + blockX, FocusW = blockW,
            });

            if (e.Main) { y += h; column = 0; }
            else
            {
                column++;
                if (column >= Columns) { column = 0; y += h; }
            }
        }

        // A row left half-full still occupies its height.
        y += CloseRow(ref column);

        _maxScroll = Mathf.Max(0f, y - _listH);
        _scroll = Mathf.Clamp(_scroll, 0f, _maxScroll);

        // Cells and dividers are created after the cursor was built, so they
        // arrive as later siblings and would draw over the brackets.
        _cursor.BringToFront();
    }

    /// <summary>Finish an open row. Returns the height it still owes.</summary>
    static float CloseRow(ref int column)
    {
        if (column == 0) return 0f;
        column = 0;
        return CellH;
    }

    /// <summary>
    /// Put an entry's art and words into a cell, at the cell's size.
    ///
    /// Reports back the horizontal extent of what it actually drew, relative to
    /// the cell, so the caret can frame the content rather than the container.
    /// </summary>
    void PaintCell(Cell cell, Entry e, float w, float h, out float blockX, out float blockW)
    {
        bool main = e.Main;
        float iconSize = main ? MainIcon : CellIcon;
        float typeH = main ? 30f : 26f;
        float nameH = main ? 54f : 44f;

        // The words are given their faces BEFORE anything is measured or
        // placed. GetPreferredValues reports the width a string wants at the
        // label's CURRENT size, so measuring first and sizing afterwards
        // measures the previous entry's face.
        if (cell.Type != null)
        {
            cell.Type.text = e.Type ?? "";
            cell.Type.fontSize = main ? MainTypeSize : TypeSize;
            // Completed quests grey out wholesale: the type colour is the game's
            // signal for a live quest, and keeping it on a finished one makes
            // the pile at the bottom louder than the list above it.
            cell.Type.color = e.Completed ? DsTheme.InkFaint : e.TypeColour;
        }

        if (cell.Label != null)
        {
            cell.Label.text = e.Name;
            cell.Label.color = e.Completed ? DsTheme.InkFaint
                             : e.Main ? DsTheme.Ink : DsTheme.InkDim;
            // Weight rather than colour for "ready to hand in": gold is what the
            // game uses for a thing you can act on, and spending it on a cell in
            // a list draws the eye to it before the tab it belongs to.
            cell.Label.fontStyle = e.Ready ? TMProOld.FontStyles.Bold
                                           : TMProOld.FontStyles.Normal;
            cell.Label.fontSizeMax = main ? MainNameSize : NameSize;
            cell.Label.fontSize = cell.Label.fontSizeMax;
        }

        float iconX = CellPadL;
        float textW = Mathf.Max(40f, w - CellPadL - iconSize - IconGap - CellPadR);

        // The prioritised quest is CENTRED in its row, which is the game's own
        // itemListLayout alignment (UpperCenter) and what the designs show: a
        // single wide cell with its icon and words left hard against the column
        // edge reads as a list of one, not as the thing being worked on.
        //
        // Centring needs the block's true width, so the name is measured. A
        // failed measurement falls back to the full width, which is the
        // left-aligned layout -- worse looking, never broken.
        if (main)
        {
            float want = 0f;
            if (cell.Label != null)
                try { want = cell.Label.GetPreferredValues(cell.Label.text).x; } catch { }
            if (cell.Type != null)
                try { want = Mathf.Max(want, cell.Type.GetPreferredValues(cell.Type.text).x); } catch { }

            if (want > 1f)
            {
                textW = Mathf.Min(want + 4f, textW);
                iconX = Mathf.Max(CellPadL, (w - (iconSize + IconGap + textW)) * 0.5f);
            }
        }

        float textX = iconX + iconSize + IconGap;

        if (cell.IconSlot != null)
        {
            DsWidgets.Place(cell.IconSlot, iconX, (h - iconSize) * 0.5f, iconSize, iconSize);

            if (cell.Icon != null)
            {
                if (e.Icon != null)
                {
                    // Fitted and centred within the slot rather than simply
                    // placed: the quest type icons are atlas sprites drawn from
                    // their trimmed mesh, and a symbol with uneven transparent
                    // margins sits visibly to one side of a rect that is itself
                    // perfectly centred.
                    DsWidgets.FitCentred(cell.Icon, e.Icon, iconSize, iconSize);
                    cell.Icon.color = e.Completed ? DsTheme.InkFaint : Color.white;
                }
                else
                {
                    cell.Icon.sprite = null;
                    cell.Icon.color = Color.clear;
                }
            }
        }

        // Type above name, above the progress row, as the game's template
        // stacks them. The three heights are centred together in the cell so
        // that a cell reads as one block beside its icon rather than as labels
        // that happen to be near it.
        //
        // A quest with no counter reserves no room for one. Keeping the band
        // unconditionally would push the type and name up by half its height on
        // every quest that has nothing to show, which is most of them.
        float counterH = HasCounter(e) ? (main ? MainCounterH : CounterH) : 0f;
        float top = (h - typeH - nameH - counterH) * 0.5f;

        if (cell.Type != null)
            DsWidgets.Place(cell.Type.rectTransform, textX, top, textW, typeH);
        if (cell.Label != null)
            DsWidgets.Place(cell.Label.rectTransform, textX, top + typeH, textW, nameH);

        PaintCounter(cell, e, textX, top + typeH + nameH, textW, counterH, main);

        blockX = iconX;
        blockW = Mathf.Min(iconSize + IconGap + textW, w - iconX);
    }

    /// <summary>Whether an entry has anything to draw under its name.</summary>
    static bool HasCounter(Entry e)
    {
        var p = e.Progress;
        if (p.Kind == FullQuestBase.ListCounterTypes.Dots) return p.Dots > 0;
        return p.Kind == FullQuestBase.ListCounterTypes.Bar;
    }

    /// <summary>
    /// The dots or the bar, in the band under the name.
    ///
    /// Children are destroyed and rebuilt rather than pooled. The count is a
    /// property of the quest -- five bugs is five dots -- so a pooled row would
    /// have to grow, shrink and re-sprite anyway, and this only runs when the
    /// list's signature changes rather than per frame.
    /// </summary>
    void PaintCounter(Cell cell, Entry e, float x, float y, float w, float h, bool main)
    {
        var root = cell.CounterRoot;
        if (root == null) return;

        for (int i = root.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(root.GetChild(i).gameObject);

        if (h <= 0f || !HasCounter(e)) { DsWidgets.SetActive(root, false); return; }
        DsWidgets.SetActive(root, true);
        DsWidgets.Place(root, x, y, w, h);

        var art = DsGameArt.QuestCounterArt();
        var p = e.Progress;

        // The quest's tint MULTIPLIES each state's own colour rather than
        // replacing it, which is what IconCounterItem.TintColor does. It is
        // also why an unset tint must read as white rather than as black: a
        // quest that never set one would otherwise put out an invisible row.
        Color tint = p.Tint;
        if (tint.a <= 0.01f) tint = Color.white;

        if (p.Kind == FullQuestBase.ListCounterTypes.Dots && p.Dots <= MaxDots)
            PaintDots(root, p, art, tint, w, h, main);
        else
            PaintBar(root, p, art, tint, w, h, main);
    }

    void PaintDots(RectTransform root, Counter p, DsGameArt.QuestCounter art,
                   Color tint, float w, float h, bool main)
    {
        int count = Mathf.Min(p.Dots, MaxDots);
        if (count <= 0) return;

        float size = main ? MainDotSize : DotSize;
        float gap = DotGap;

        // Shrink to fit rather than overflow, which is IconCounter's own rule:
        // it scales its spacing by maxSize/naturalSize and clamps at 1, so a
        // long row tightens instead of running out of the cell.
        float natural = count * size + (count - 1) * gap;
        if (natural > w && natural > 0f)
        {
            float scale = w / natural;
            size *= scale;
            gap *= scale;
        }

        Sprite filled = art != null ? art.DotFilled : null;
        Sprite empty = art != null ? art.DotEmpty : null;
        // A plain disc is a fair stand-in: the game's dot IS a small circle, and
        // at this size the difference is a few pixels of edge treatment.
        Sprite fallback = DsTheme.Disc;

        Color filledColour = (art != null ? art.FilledColour : Color.white) * tint;
        // The empty state is dark in the game's own art. Without it every dot
        // would read as filled and the row would say the quest is done.
        Color emptyColour = art != null && art.DotEmpty != null
                          ? art.EmptyColour * tint
                          : DsTheme.InkFaint;

        float top = (h - size) * 0.5f;
        for (int i = 0; i < count; i++)
        {
            bool on = i < p.DotsFilled;
            var sprite = (on ? filled : empty) ?? fallback;
            var img = DsWidgets.Icon(root, "dot" + i, sprite, Color.white);
            img.preserveAspect = true;
            img.color = on ? filledColour : emptyColour;
            DsWidgets.Place(img.rectTransform, i * (size + gap), top, size, size);
        }
    }

    void PaintBar(RectTransform root, Counter p, DsGameArt.QuestCounter art,
                  Color tint, float w, float h, bool main)
    {
        float barH = main ? MainBarH : BarH;
        float barW = Mathf.Min(BarW, w);
        float top = (h - barH) * 0.5f;

        Sprite fill = art != null && art.Bar != null ? art.Bar : DsTheme.White;

        // One bar per target when there is more than one. The game splits
        // rather than averages, and the distinction matters: one of two things
        // finished is a full segment and an empty one, not a half-full bar.
        int segments = p.Segments != null ? p.Segments.Length : 1;
        float segW = (barW - SegmentGap * (segments - 1)) / segments;
        if (segW < 6f) { segments = 1; segW = barW; }

        for (int i = 0; i < segments; i++)
        {
            float value = segments == 1 ? p.Value : p.Segments[i];
            value = Mathf.Clamp01(value);
            float sx = i * (segW + SegmentGap);

            // The track first, so an empty bar is still a bar. Without it a
            // quest at zero shows nothing at all and reads as a quest with no
            // counter rather than as one not started.
            var track = DsWidgets.Box(root, "track" + i, DsTheme.Locked);
            DsWidgets.Place(track.rectTransform, sx, top, segW, barH);

            if (value <= 0f) continue;

            // ImageSlider floors a non-zero value at minDisplayValue so that
            // "started" is visible at all; a couple of pixels does the same job
            // here without needing the game's number.
            float fw = Mathf.Max(segW * value, 3f);
            var img = DsWidgets.Icon(root, "fill" + i, fill, Color.white);
            img.preserveAspect = false;
            img.useSpriteMesh = false;
            img.color = tint;
            DsWidgets.Place(img.rectTransform, sx, top, fw, barH);
        }
    }

    Cell MakeCell(int index)
    {
        var root = DsWidgets.Rect(_list, "cell" + index);

        var slot = DsWidgets.Rect(root, "icon-slot");
        var icon = DsWidgets.Icon(slot, "icon", null, Color.white);

        var type = DsWidgets.Label(root, "type", "", TypeSize, DsTheme.InkDim, TmpAlign.Left);
        if (type != null)
        {
            // Caps by the label's style, the way the game's template does it,
            // rather than by rewriting the string: the type name is localised,
            // and upper-casing text in code is a per-language decision we are in
            // no position to make.
            type.fontStyle = TMProOld.FontStyles.UpperCase;
            type.enableWordWrapping = false;
        }

        var label = DsWidgets.Label(root, "name", "", NameSize, DsTheme.Ink, TmpAlign.Left);
        if (label != null)
        {
            // One line that shrinks, not two lines that wrap. A quest name is a
            // title, and a wrapped title in a half-width cell breaks the two-line
            // type/name rhythm the whole grid is built on.
            label.enableWordWrapping = false;
            label.enableAutoSizing = true;
            label.fontSizeMin = NameSizeMin;
            label.fontSizeMax = NameSize;
        }

        return new Cell
        {
            Root = root, IconSlot = slot, Icon = icon,
            Type = type, Label = label,
            CounterRoot = DsWidgets.Rect(root, "counter"),
        };
    }

    /// <summary>
    /// The rule under the priorities. Returns its height.
    ///
    /// A single tapered hairline, centred and well short of the column. That is
    /// the shape the game's own `currentHeading` has: one `Spacer` sprite of
    /// 7.53 x 0.34 world units -- a 22:1 line -- with no ornament on it at all.
    ///
    /// It was briefly drawn as a pair of mirrored fluers meeting in the middle,
    /// on the strength of a glance at a screenshot. That is the COMPLETED
    /// caption's decoration, and putting two of them nose to nose here made a
    /// heavy knot the game does not have. The ornament belongs to the captioned
    /// rule; this one is a line.
    /// </summary>
    float MakePlainDivider(float y)
    {
        var root = DsWidgets.Rect(_list, "div" + _dividers.Count);
        DsWidgets.Place(root, 0f, y, ListW, PlainDividerH);

        // DsRuleArt's hairline fades to nothing at both ends, so a centred span
        // of it reads as the game's tapered line rather than as a bar with cut
        // ends -- see the note there on why it is stretched whole.
        DsWidgets.HRule(root, "rule", (ListW - PlainRuleW) * 0.5f,
                        PlainDividerH * 0.5f, PlainRuleW);

        _dividers.Add(new Divider { Root = root, Y = y, H = PlainDividerH });
        return PlainDividerH;
    }

    /// <summary>
    /// The drawn width of the ornament at a given height, with sane bounds.
    ///
    /// This is art read off a live scene object, so a degenerate sprite rect
    /// must not be able to draw a divider the width of the panel.
    /// </summary>
    static float OrnamentWidth(Sprite art, float height)
    {
        var r = art.rect;
        return Mathf.Clamp(height * (r.width / Mathf.Max(r.height, 1f)), 16f, 160f);
    }

    /// <summary>
    /// The captioned rule before the finished pile, built the way the game
    /// builds its own: the word centred, an ornament mirrored either side of it,
    /// and a hairline running outward from each ornament.
    ///
    /// It is a device in the middle of the column rather than a line across it.
    /// A full-width rule here would read as the bottom of the list, which is
    /// precisely the wrong thing to say about a group that has more below it.
    /// </summary>
    float MakeCompletedDivider(float y)
    {
        const float h = CompletedDividerH;
        var root = DsWidgets.Rect(_list, "div" + _dividers.Count);
        DsWidgets.Place(root, 0f, y, ListW, h);

        // "COMPLETED" is ours and is known to be uppercase, so it may take the
        // caps display face -- see the warning on DsWidgets.Label. The quest
        // names beside it may not.
        var label = DsWidgets.Label(root, "t", "COMPLETED", CompletedSize,
                                    DsTheme.InkDim, TmpAlign.Center, display: true);

        float textW = 220f;
        if (label != null)
        {
            // Measured, not assumed: the ornaments sit against the ends of the
            // word, so a guessed width puts them through it or adrift of it.
            // GetPreferredValues reports the width the string wants rather than
            // the width of the rect it currently has.
            try { textW = Mathf.Clamp(label.GetPreferredValues("COMPLETED").x, 120f, 360f); }
            catch { }
            DsWidgets.Place(label.rectTransform, (ListW - textW) * 0.5f, (h - 34f) * 0.5f,
                            textW, 34f);
        }

        float mid = h * 0.5f;
        float leftEdge = (ListW - textW) * 0.5f - FluerGap;
        float rightEdge = (ListW + textW) * 0.5f + FluerGap;

        var art = DsGameArt.QuestDivider();
        float fluerW = 0f;
        if (art != null)
        {
            fluerW = OrnamentWidth(art, FluerH);
            Ornament(root, "fluer-l", art, leftEdge - fluerW, mid - FluerH * 0.5f, fluerW, false);
            Ornament(root, "fluer-r", art, rightEdge, mid - FluerH * 0.5f, fluerW, true);
        }

        // The hairlines outward. They stop short of the column's edges on
        // purpose, and shorten rather than overlap if the caption is wide.
        float leftRuleEnd = leftEdge - fluerW;
        float rightRuleStart = rightEdge + fluerW;
        float reach = Mathf.Min(DividerReach, Mathf.Max(0f, leftRuleEnd));
        if (reach > 8f)
        {
            DsWidgets.HRule(root, "rule-l", leftRuleEnd - reach, mid, reach);
            DsWidgets.HRule(root, "rule-r", rightRuleStart, mid, reach);
        }

        _dividers.Add(new Divider { Root = root, Y = y, H = h });
        return h;
    }

    /// <summary>
    /// One end of the caption's ornament. The game holds a `Fluer Left` and a
    /// `Fluer Right` and they are the same art, so the right-hand one is simply
    /// the left one mirrored -- a negative x scale rather than a second sprite.
    ///
    /// <paramref name="x"/> is the LEFT edge of the drawn art either way. That
    /// needs saying because the mirror does not preserve it for free: rects here
    /// are placed from their top-left corner, which is also the pivot a negative
    /// scale flips about, so a mirrored rect anchored at x covers x-w to x. It
    /// is anchored a width further along to put it back.
    /// </summary>
    static void Ornament(RectTransform parent, string name, Sprite art,
                         float x, float y, float w, bool mirror)
    {
        var img = DsWidgets.Icon(parent, name, art, DsTheme.Rule);
        var rt = img.rectTransform;
        DsWidgets.Place(rt, mirror ? x + w : x, y, w, FluerH);
        if (mirror) rt.localScale = new Vector3(-1f, 1f, 1f);
    }

    void Paint()
    {
        for (int i = 0; i < _placed.Count && i < _cells.Count; i++)
        {
            var p = _placed[i];
            DsWidgets.Place(_cells[i].Root, p.X, p.Y - _scroll, p.W, p.H);
        }

        // Dividers scroll with the cells. They are part of the list, not a frame
        // around it, and leaving them pinned made the "COMPLETED" rule float
        // over unrelated entries.
        for (int i = 0; i < _dividers.Count; i++)
        {
            var d = _dividers[i];
            if (d.Root != null) DsWidgets.Place(d.Root, 0f, d.Y - _scroll, ListW, d.H);
        }

        if (_selected >= 0 && _selected < _placed.Count)
        {
            var p = _placed[_selected];
            // The key is the entry, not the index: a scrolled grid moves the
            // cell under its own caret, and that must not restart the travel
            // animation or the caret never arrives anywhere.
            _cursor.MoveTo(new Rect(p.FocusX, p.Y - _scroll, p.FocusW, p.H), null,
                           _selected < _entries.Count ? _entries[_selected].Key : null);
        }
        else _cursor.Hide();
    }

    void PaintDetail()
    {
        bool ok = _selected >= 0 && _selected < _entries.Count;
        var e = ok ? _entries[_selected] : null;

        if (_type != null)
        {
            _type.text = ok ? (e.Type ?? "") : "";
            _type.color = ok && !e.Completed ? e.TypeColour : DsTheme.InkFaint;
        }
        if (_title != null) _title.text = ok ? e.Name : "";
        if (_desc == null) return;
        if (!ok) { _desc.text = ""; return; }

        var sb = new System.Text.StringBuilder(e.Desc ?? "");

        // The targets, spelled out under the description. This is the part you
        // open a quest to see -- the description says what was asked for, the
        // counters say how much of it is done -- and it is the whole reason the
        // cell can afford to carry no number at all.
        if (e.Steps.Count > 0 && !e.Completed)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            for (int i = 0; i < e.Steps.Count; i++)
            {
                var s = e.Steps[i];
                if (i > 0) sb.Append('\n');

                // A target with no name of its own is just a number. The bullet
                // is a list marker for a list of things, and "- 71/100" reads as
                // a thing whose name failed to load rather than as a count.
                if (!string.IsNullOrEmpty(s.Name))
                    sb.Append(s.Have >= s.Need ? "* " : "- ")
                      .Append(s.Name).Append("   ");

                sb.Append(s.Have).Append('/').Append(s.Need);
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
                if (hit >= 0)
                {
                    _selected = hit;
                    _selectedKey = _entries[hit].Key;
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
        for (int i = 0; i < _placed.Count; i++)
        {
            var p = _placed[i];
            if (x >= p.X && x <= p.X + p.W && y >= p.Y && y <= p.Y + p.H) return i;
        }
        return -1;
    }
}
#endif
