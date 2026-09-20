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
        /// <summary>
        /// Killed enough of them for the hunter's commentary, which is the
        /// game's own test -- JournalEntryItem.Setup picks its ring on
        /// `record.KillCount >= record.KillsRequired` and nothing else.
        /// </summary>
        public bool Complete;
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
    // Roomier than it was. The portraits now sit inside the game's own ring
    // rather than filling a bare disc, so they read as framed pictures with air
    // around them instead of as a wall of circles that happen to touch.
    const float CellGap = 26f;
    /// <summary>
    /// How far the portrait sits inside its ring, as a fraction of the cell.
    ///
    /// A fraction rather than a pixel count because the ring is the game's art
    /// and carries its own transparent margin: drawn across a 109-pixel cell
    /// its circle is only about 84 across, and its INNER edge about 71. A flat
    /// nine pixels left the portrait a little wider than that inner edge, so
    /// creatures with wide heads spilled over the frame that was supposed to
    /// contain them.
    /// </summary>
    const float ArtInsetFraction = 0.16f;
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
    /// <summary>The rule between the description and the hunter's notes.</summary>
    RectTransform _ruleRoot;
    Image _portrait, _glow;
    TmpText _name, _desc, _notes, _empty;

    Rect _listRect;
    float _cell, _cellH, _listTop, _listH;
    float _portraitW, _portraitH;
    float _detailW;
    string _ruleSig;
    /// <summary>Frames left to re-lay the description. See Tick.</summary>
    int _settle;
    /// <summary>Where the description's prose starts, under the creature's name.</summary>
    const float DetailTextTop = 68f;
    /// <summary>The rule's own band, and the air either side of it.</summary>
    const float RuleH = 52f;
    const float RuleGap = 18f;
    /// <summary>The fallback hairline's width, for before the mask is readable.</summary>
    const float RuleReach = 150f;
    const float RuleSymbolH = 44f;
    /// <summary>The least room the notes are given under the rule.</summary>
    const float MinNotesH = 90f;
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

        // The soft light the creature stands on, behind it and centred in the
        // column. The game puts one there and without it the portrait floats in
        // a black field -- it is what makes the centre column read as a lit
        // plate rather than as a hole with a picture in it.
        //
        // Built before the portrait so it draws behind, and sized generously:
        // it is a wide radial falloff, not a disc with an edge.
        _glow = DsWidgets.Icon(_portraitBox, "glow", null, Color.white);
        _glow.preserveAspect = true;
        float glowSize = Mathf.Min(PortraitW, colH) * 1.25f;
        DsWidgets.Place(_glow.rectTransform, (PortraitW - glowSize) * 0.5f,
                        (colH - glowSize) * 0.5f, glowSize, glowSize);

        _portrait = DsWidgets.Icon(_portraitBox, "art", null, Color.white);
        _portraitW = PortraitW - 48f;
        _portraitH = colH - 48f;
        DsWidgets.Place(_portrait.rectTransform, 24f, 24f, _portraitW, _portraitH);

        // No rule across the top of the description: it is a column of its own
        // now, and the gutter rule beside it is already the boundary.
        _detail = DsWidgets.Rect(host, "detail");
        DsWidgets.Place(_detail, DetailX, _listTop, DetailW, colH);
        _detailW = DetailW;

        _name = DsWidgets.Label(_detail, "name", "", DetailTitleSize,
                                DsTheme.Ink, TmpAlign.Left);
        if (_name != null) DsWidgets.Place(_name.rectTransform, 0f, 8f, DetailW, 52f);

        _desc = DsWidgets.Label(_detail, "desc", "", DetailBodySize,
                                DsTheme.InkDim, TmpAlign.TopLeft);
        if (_desc != null)
            DsWidgets.Place(_desc.rectTransform, 0f, DetailTextTop, DetailW, colH - 76f);

        // The rule between what is known and what the hunter has to say about
        // it, built once and moved to follow the prose. Two lines either side
        // of her mask, which is how the game assembles it -- `divider`,
        // `divider (1)` and `hunter_symbol` are three separate renderers.
        _ruleRoot = DsWidgets.Rect(_detail, "rule");
        DsWidgets.SetActive(_ruleRoot, false);

        _notes = DsWidgets.Label(_detail, "notes", "", DetailBodySize,
                                 DsTheme.InkDim, TmpAlign.TopLeft);

        // Inside the scrolling list, with the portraits, so the mask clips the
        // cursor exactly as it clips them. Half of 1 - 1/sqrt2: the diagonal gap
        // of a circle inscribed in its cell, split across the two axes.
        _cursor.CornerInset = _cell * 0.1465f;
        _cursor.Build(_list);

        Refresh(force: true);
    }

    public void OnShow() { Refresh(force: true); }
    public void OnHide() { }

    public void Tick(float dt)
    {
        // Before the refresh gate: the cursor animates every frame, and the
        // data is only re-read once a second.
        _cursor.Tick(dt);

        // One more pass at the description's layout, the frame after it was
        // written. ForceMeshUpdate covers the ordinary case, but a label whose
        // FONT has not arrived yet cannot be measured at all -- and the pane is
        // built before the fonts are found often enough to matter. A second
        // pass costs one layout and removes the whole class of "it was wrong
        // until I clicked something else".
        if (_settle > 0) { _settle--; PaintDetail(); }

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
        // Whether the game's ring art has arrived yet is part of the shape of
        // the list: it is readable only once the game's own journal has been
        // opened, and a grid drawn before then has to be rebuilt to pick it up.
        sig.Append(DsGameArt.Frames().Ok ? "F;" : "f;");
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
                Complete = kills >= required,
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
        _settle = 1;
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

            if (c.Ring != null)
            {
                // Two rings, and which one is the game's own rule: a creature
                // whose notes are finished gets the ring with the flourishes on
                // it. An unmet one gets the plain ring, dimmed -- the game
                // hides its frames entirely there and shows an `emptyIcon`
                // instead, which is a third piece of art this panel has no
                // room to introduce for a row that says "???" anyway.
                var frames = DsGameArt.Frames();
                var art = e.Complete && !e.Unknown
                        ? (frames.Complete ?? frames.Standard)
                        : (frames.Standard ?? frames.Complete);

                c.Ring.sprite = art;
                c.Ring.color = art == null ? Color.clear
                             : e.Unknown ? new Color(1f, 1f, 1f, 0.30f)
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

        // The ring is the game's own art when it can be had, drawn at the full
        // cell. It replaced a flat disc behind the portrait, which was doing
        // the job of a frame without looking like one.
        var ring = DsWidgets.Icon(root, "ring", null, Color.white);
        ring.preserveAspect = true;
        DsWidgets.Place(ring.rectTransform, 0f, 0f, _cell, _cell);

        // A Mask over the generated disc, so the square portrait is clipped to
        // a circle. showMaskGraphic is off because the ring in front of it
        // draws the edge; this disc exists only to define the shape.
        //
        // Inset by ArtInset rather than a few pixels: the ring's own line sits
        // a little inside the sprite's bounds, and a portrait filling the whole
        // circle would run under it. The picture is the thing being framed, so
        // it gives way rather than the frame.
        var maskRt = DsWidgets.Rect(root, "mask");
        float inset = _cell * ArtInsetFraction;
        DsWidgets.Place(maskRt, inset, inset, _cell - inset * 2f, _cell - inset * 2f);
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

        // The ring draws OVER the portrait, so it is moved after the mask: the
        // frame's inner edge should overlap the picture, not be hidden by it.
        ring.rectTransform.SetAsLastSibling();

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

        // Cells arrive as later siblings than the cursor; without this the
        // portraits draw over the brackets.
        _cursor.BringToFront();
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

        // No clamping and no hiding: the cursor lives in the same scrolling list
        // as the portraits, so it travels with the one it is on and the list's
        // mask clips it exactly as it clips the picture. See the note in
        // DsIconGrid -- clamping parked the brackets at the top of the column
        // around nothing, and hiding left a selected entry unmarked.
        _cursor.MoveTo(new Rect(x, y, _cell, _cell), null, _entries[_selected].Name);
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

        if (_glow != null)
        {
            // The same light the cursor puts behind an item it is on, rather
            // than a disc: the game lights this portrait the way it lights
            // anything it wants you to look at, and a hard-edged circle behind
            // a creature reads as a plate it is standing on instead.
            var cursor = DsGameArt.SelectionCursor();
            var glow = cursor != null ? cursor.Glow : null;
            // Never lights an unmet one, whose whole point is that it is a
            // shape in the dark.
            bool lit = ok && glow != null && _entries[_selected].Portrait != null
                       && !_entries[_selected].Unknown;
            _glow.sprite = glow;
            _glow.color = lit ? cursor.GlowColor : Color.clear;
        }

        if (_name != null) _name.text = ok ? _entries[_selected].Name : "";

        if (_desc == null) return;
        if (!ok) { _desc.text = ""; ShowRule(false, 0f); return; }

        var entry = _entries[_selected];
        _desc.text = entry.Desc ?? "";

        // Below the description, and only for a creature actually met: the
        // game's own rule with the hunter's mask on it, and under that either
        // her notes or how many more of them to defeat before she writes any.
        //
        // The two are the same slot because the game treats them as one --
        // JournalItemManager writes both into `notesText`, dimming it while it
        // holds the count -- and because a pane that showed a heading with
        // nothing under it would be worse than one that showed neither.
        string below = entry.Unknown ? ""
                     : entry.Complete ? (entry.Notes ?? "")
                     : LockedNote(entry);

        float descH = 0f;
        if (!string.IsNullOrEmpty(below) && !string.IsNullOrEmpty(_desc.text))
        {
            descH = MeasuredHeight(_desc, _detailW);
        }

        if (string.IsNullOrEmpty(below))
        {
            ShowRule(false, 0f);
            if (_notes != null) _notes.text = "";
            return;
        }

        float ruleY = DetailTextTop + descH + RuleGap;
        // Never so far down that the rule and what follows it leave the column.
        // A description long enough to do that is rare, and losing the hunter's
        // notes entirely would be a poor way to handle it.
        float room = _listH - RuleH - RuleGap * 2f - MinNotesH;
        if (ruleY > room) ruleY = Mathf.Max(DetailTextTop, room);
        ShowRule(true, ruleY);

        if (_notes != null)
        {
            _notes.text = below;
            // The count is the game's disabled ink, as its own pane dims the
            // same line while it says this; the notes themselves are not dim.
            _notes.color = entry.Complete ? DsTheme.InkDim : DsTheme.InkFaint;
            DsWidgets.Place(_notes.rectTransform, 0f, ruleY + RuleH + RuleGap,
                            _detailW, Mathf.Max(60f, _listH - ruleY - RuleH - RuleGap * 2f));
        }
    }

    /// <summary>
    /// The game's own "defeat N more" line, with the number filled in.
    ///
    /// `string.Format(notesLockedText, killsRequired - killCount)` is exactly
    /// what JournalItemManager does, so the wording and the language are the
    /// game's. A fallback in English only for a save whose journal pane has
    /// never been built and has no string to lend.
    /// </summary>
    static string LockedNote(Entry e)
    {
        int left = Mathf.Max(1, e.Required - e.Kills);
        string format = DsGameArt.JournalNotesLocked();
        if (!string.IsNullOrEmpty(format))
        {
            try { return string.Format(format, left); } catch { }
        }
        return "Defeat " + left + " more to complete the Hunter's Notes.";
    }

    /// <summary>
    /// How tall a label's text actually is at a given width.
    ///
    /// ForceMeshUpdate first, and that is the whole point of this existing.
    /// TMP lays a label out on ITS next update, not when the text is assigned,
    /// so measuring straight after setting it returns whatever the label held
    /// before -- which on the first open of the pane is nothing at all. The
    /// symptom was precise and confusing: the hunter's mask drawn a line or two
    /// INTO the description instead of below it, correcting itself the moment
    /// the selection changed and the measurement ran against a laid-out label.
    /// </summary>
    static float MeasuredHeight(TmpText label, float width)
    {
        if (label == null) return 0f;
        try
        {
            label.ForceMeshUpdate();
            float h = label.GetPreferredValues(label.text, width, 0f).y;
            if (h > 0f) return h;
        }
        catch { }
        // Better to push the rule too far down than to draw it through the
        // prose, so an unmeasurable label is treated as a full column.
        try { return label.preferredHeight; } catch { return 0f; }
    }

    /// <summary>Show or hide the rule under the description, at <paramref name="y"/>.</summary>
    void ShowRule(bool on, float y)
    {
        if (_ruleRoot == null) return;
        DsWidgets.SetActive(_ruleRoot, on);
        if (!on) return;

        DsWidgets.Place(_ruleRoot, 0f, y, _detailW, RuleH);
        BuildRule();
    }

    /// <summary>
    /// The rule itself: the hunter's mask, centred, and nothing else.
    ///
    /// The game sets two tapered lines either side of it, and they were drawn
    /// here too at first. In a 330-pixel column they read as three small marks
    /// in a row rather than as one device, and the mask -- which is the part
    /// that says whose notes these are -- was the smallest of them. Alone and
    /// larger it does the whole job: what follows is a different voice, and a
    /// line across the column was never what said so.
    ///
    /// Rebuilt only when the art it is made of changes, which in practice means
    /// once -- the mask is readable only after the game's own journal has been
    /// opened.
    /// </summary>
    void BuildRule()
    {
        var art = DsGameArt.Rule();
        string sig = art.Symbol != null ? art.Symbol.name : "-";
        if (sig == _ruleSig) return;
        _ruleSig = sig;

        for (int i = _ruleRoot.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(_ruleRoot.GetChild(i).gameObject);

        if (art.Symbol == null)
        {
            // Nothing to draw it with yet; a plain hairline says "and now
            // something else" well enough until the mask arrives.
            DsWidgets.HRule(_ruleRoot, "rule", (_detailW - RuleReach) * 0.5f,
                            RuleH * 0.5f, RuleReach);
            return;
        }

        var r = art.Symbol.rect;
        float w = Mathf.Clamp(RuleSymbolH * (r.width / Mathf.Max(r.height, 1f)), 12f, 200f);
        var sym = DsWidgets.Icon(_ruleRoot, "mask", art.Symbol, DsTheme.Rule);
        sym.preserveAspect = true;
        DsWidgets.Place(sym.rectTransform, (_detailW - w) * 0.5f,
                        (RuleH - RuleSymbolH) * 0.5f, w, RuleSymbolH);
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
                    _settle = 1;
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
