// DsLoadoutScreen — the crest and its tools, on one screen.
//
// The game splits these across a pane and a sub-scroller because a 16:9 pane
// shared with the tool grid has no room for both. This panel is 1240x1080 and
// has the room, so the two belong together: what you are wearing, and what you
// could put in it, side by side.
//
//     +----------------------+---------------------------+
//     |  the equipped crest  |  every tool, in three      |
//     |  with its tools in   |  colour groups, one long   |
//     |  their slots         |  scrolling list            |
//     |----------------------|                            |
//     |  selected tool       |                            |
//     |  name + description  |                            |
//     +----------------------+---------------------------+
//
// Only the EQUIPPED crest is drawn. Browsing crests is a thing you do rarely
// and at a bench; knowing what is currently socketed is a thing you want at a
// glance, mid-game, which is exactly what a second screen is for.
//
// The slot ring is the game's own geometry: ToolCrest.Slots[i].Position is
// where that slot sits around the crest, and SaveData.Slots[i].EquippedTool is
// what is in it. Positions arrive in the crest prefab's units, so they are
// normalised into our box rather than assumed to be pixels.

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TmpText = TMProOld.TextMeshProUGUI;
using TmpAlign = TMProOld.TextAlignmentOptions;

public class DsLoadoutScreen : IDsScreen, IDsActionBar
{
    // Layout, in panel pixels with the origin at the top-left of the panel.
    //
    // Three columns: the crest you are wearing, the tools you could socket into
    // it, and what the one under the cursor actually does.
    //
    // The description used to sit UNDER the crest, in the left column, because
    // the tool list ran the full height beside it and the space below the crest
    // would otherwise have been a hole. That put the prose about a tool as far
    // as it could be from the tool itself, and it pinned the crest to the top
    // ~64% of its column whether or not the artwork wanted that shape.
    //
    // As its own column the description sits beside what it describes, and the
    // crest gets its whole column back.
    const float LeftX   = 20f;    // crest: 20 .. 490
    const float LeftW   = 470f;
    const float ListX   = 520f;   // tools: 520 .. 870
    const float ListW   = 350f;
    const float DetailX = 900f;   // prose: 900 .. 1220
    const float DetailW = 320f;
    /// <summary>Where the three columns start, below the body's top padding.</summary>
    const float DetailTop = 16f;
    // The bottom of the description column, kept for CREST and EQUIP/UNEQUIP.
    //
    // Room for TWO rows, because at a bench with a tool selected that is what
    // is offered. Reserved whether or not they are showing: the pair comes and
    // goes with the bench and with the selection, and a description that
    // reflowed each time would be worse than a gap that does not move.
    static readonly float ActionBand = DsActionBar.PaneBand(2);
    const int   ListColumns = 3;
    const float SlotIcon = 96f;
    const float ExtraIcon = 74f;
    // How strongly the crest artwork itself is drawn. A knob, like the cursor's
    // sizes, because it is judged by eye against the tools on top of it.
    static float CrestArtAlpha =>
        Mathf.Clamp01(DsConfig.Int("crest_art_alpha", 75) / 100f);

    RectTransform _crestBox, _host;
    Image _crestImage;
    TmpText _crestName;
    float _crestH;

    readonly DsIconGrid _grid = new DsIconGrid();
    readonly List<Image> _slots = new List<Image>();
    /// <summary>Whether each drawn slot is still locked, and which crest slot it is.</summary>
    readonly List<bool> _slotLocked = new List<bool>();
    readonly List<int> _slotIndex = new List<int>();
    /// <summary>Each drawn slot's type colour, for the cursor that lands on it.</summary>
    readonly List<Color> _slotColour = new List<Color>();
    /// <summary>The locked slot the cursor is on, as an index into the lists above.</summary>
    int _lockedPick = -1;
    readonly List<ToolItem> _slotTools = new List<ToolItem>();
    readonly List<RectTransform> _slotRects = new List<RectTransform>();

    string _crestId;
    int _toolSignature;
    float _nextRefresh;
    // The tool in the socket the player last tapped, which the grid cannot hold
    // for us: handing the cursor to a socket clears the grid's own selection.
    ToolItem _socketTool;

    /// <summary>The shared grid, so the tool list and its detail pane are one thing.</summary>
    DsIconGrid Grid => _grid;

    public string Id => "loadout";
    public string Title => "CREST";
    public bool Available => DsGameData.InGame;

    public void Build(RectTransform host)
    {
        _host = host;
        float bodyH = DsLayout.Current.Body.height;
        float colH = ColumnHeight;

        // The crest has its whole column now that the description has one of
        // its own, so the ring is fitted to the full height rather than to the
        // fraction that was left above the prose.
        _crestH = colH;

        // ── left: the crest ────────────────────────────────────────────────
        // No box. The crest is divided from the tool list by the rule down the
        // gutter, and from its own description by the rule the grid draws.
        _crestBox = DsWidgets.Rect(host, "crest");
        DsWidgets.Place(_crestBox, LeftX, 16f, LeftW, _crestH);

        // The body face: crest names are mixed case ("Hunter Crest").
        _crestName = DsWidgets.Label(_crestBox, "crest-name", "", DsTheme.TitleSize,
                                     DsTheme.Ink, TmpAlign.Center);
        if (_crestName != null) DsWidgets.Place(_crestName.rectTransform, 0f, 14f, LeftW, 52f);

        _crestImage = DsWidgets.Icon(_crestBox, "crest-art", null, Color.white);
        DsWidgets.Place(_crestImage.rectTransform, LeftW * 0.5f - 110f, 90f, 220f, 220f);

        // ── the two gutters ────────────────────────────────────────────────
        // One rule per boundary, down the middle of each, both full height:
        // all three columns now run the depth of the body.
        DsWidgets.VRule(host, "split-tools", (LeftX + LeftW + ListX) * 0.5f, 16f, colH);
        DsWidgets.VRule(host, "split-detail", (ListX + ListW + DetailX) * 0.5f, 16f, colH);

        // ── centre: the tools; right: what the selected one does ───────────
        // The detail pane takes no rule across its top -- the gutter rule
        // beside it is already the boundary. The section caps reach back half a
        // gutter so they sit on the rule between the crest and the tools.
        _grid.Build(host, ListColumns, ListX, ListW,
                    new Rect(DetailX, DetailTop, DetailW, colH - ActionBand),
                    detailRule: false,
                    capReach: ListX - (LeftX + LeftW + ListX) * 0.5f);

        Refresh(force: true);
    }

    public void OnShow() { Refresh(force: true); }
    public void OnHide() { ShowCrestPicker(false); }

    public void Tick(float dt)
    {
        // Standing up from the bench closes it: the picker is a thing you can
        // only act on at one, and leaving it open would offer crests that
        // tapping no longer changes.
        if (_choosingCrest && !AtBench()) ShowCrestPicker(false);
        if (_choosingCrest) TickCrestPicker(dt);

        if (Time.unscaledTime >= _nextRefresh)
        {
            _nextRefresh = Time.unscaledTime + 1f;
            Refresh(force: false);
        }
        _grid.Tick();
    }

    // ── data ────────────────────────────────────────────────────────────────

    void Refresh(bool force)
    {
        if (!DsGameData.InGame)
        {
            _grid.EmptyMessage = DsGameData.IdleReason;
            _grid.SetItems(null);
            ClearSlots();
            SetCrest(null);
            return;
        }

        ToolCrest crest = null;
        try
        {
            string id = PlayerData.instance.CurrentCrestID;
            if (!string.IsNullOrEmpty(id)) crest = ToolItemManager.GetCrestByName(id);
        }
        catch { }

        // Rebuilding the whole list every second would churn the canvas for no
        // reason, so it only happens when something actually changed. The
        // signature is cheap and catches equip/unequip and pickups.
        int sig = ToolSignature();
        string crestId = crest != null ? crest.name : null;
        if (!force && sig == _toolSignature && crestId == _crestId) return;
        _toolSignature = sig;
        _crestId = crestId;

        SetCrest(crest);
        BuildSlots(crest);
        BuildList();
    }

    int ToolSignature()
    {
        int hash = 17;
        try
        {
            foreach (var tool in ToolItemManager.GetAllTools())
            {
                if (tool == null) continue;
                bool unlocked = false, equipped = false;
                int left = 0;
                // IsUnlockedNotHidden, so that a tool being SUPERSEDED counts
                // as a change: taking the upgrade hides the old one without
                // ever clearing its IsUnlocked flag, and a signature built on
                // IsUnlocked alone would leave the stale pair on screen until
                // something else happened to move.
                try { unlocked = tool.IsUnlockedNotHidden; } catch { }
                try { equipped = tool.IsEquipped; } catch { }
                try { left = tool.SavedData.AmountLeft; } catch { }
                hash = hash * 31 + (unlocked ? 1 : 0) + (equipped ? 2 : 0) + left * 7;
            }
        }
        catch { }
        return hash;
    }

    // ── the crest ───────────────────────────────────────────────────────────

    void SetCrest(ToolCrest crest)
    {
        if (_crestName != null)
            _crestName.text = crest != null ? DsText(crest.DisplayName) : "";
        if (_crestImage != null)
        {
            Sprite art = null;
            try { art = crest != null ? crest.CrestSprite : null; } catch { }
            if (art != null)
            {
                _crestImage.sprite = art;
                _crestImage.useSpriteMesh = true;
                _crestImage.preserveAspect = true;
                // Held back rather than drawn at full strength. The crest is the
                // BACKDROP its slots sit on, and at full white it competed with
                // the tools socketed into it -- which are the things you are
                // looking at and the things the cursor lands on.
                _crestImage.color = new Color(1f, 1f, 1f, CrestArtAlpha);
            }
            else
            {
                _crestImage.sprite = DsTheme.White;
                _crestImage.useSpriteMesh = false;
                _crestImage.color = DsTheme.Locked;
            }
        }
    }

    // Draw the crest with its slots arranged the way the crest itself arranges
    // them, and the extra (non-crest) slots in a column beside it.
    //
    // Two earlier attempts got this wrong, and the second failure is the
    // instructive one:
    //
    //   1. Normalising slot positions to fill the box stretched the ring away
    //      from the artwork.
    //   2. Centring the ring on the BOUNDING BOX of the slot positions shifted
    //      it by a different amount for every crest -- because slot positions
    //      are relative to the crest's own ORIGIN, and a crest whose slots are
    //      not symmetric about that origin has a bounding-box centre somewhere
    //      else entirely. "Every crest is off in its own way" was the symptom
    //      that named the cause.
    //
    // The relationship is exact and needs no fudging: slot positions and the
    // crest sprite share one coordinate space, so one scale (pixels per world
    // unit) maps both. The artwork's pixel size is its own
    // Sprite.bounds.size * scale, not a guess.
    void BuildSlots(ToolCrest crest)
    {
        ClearSlots();
        if (crest == null) return;

        ToolCrest.SlotInfo[] slots = null;
        try { slots = crest.Slots; } catch { }

        List<ToolCrestsData.SlotData> saved = null;
        try { saved = crest.SaveData.Slots; } catch { }

        // The ring is centred in the panel, exactly as the crest is drawn in
        // game. The extra slots tuck into the top-left corner rather than
        // getting a reserved column of their own -- a column pushed the whole
        // crest off-centre, and the corner is empty anyway because the artwork
        // is centred and roughly round.
        float ringX = 20f;
        float ringY = 96f;
        float ringW = LeftW - 40f;
        float ringH = _crestH - ringY - 24f;
        float cx = ringX + ringW * 0.5f;
        float cy = ringY + ringH * 0.5f;

        Vector2 artPx;
        float scale = CrestScale(crest, ringW, ringH, SlotIcon, out artPx);

        if (_crestImage != null)
            DsWidgets.Place(_crestImage.rectTransform, cx - artPx.x * 0.5f, cy - artPx.y * 0.5f,
                            artPx.x, artPx.y);

        if (slots != null)
        {
            // What the game reads to fill a crest's sockets, rather than the
            // same lookup written out again: InventoryToolCrest.GetEquippedForSlots
            // is one call to this and a loop over the answer.
            List<ToolItem> equipped = null;
            try { equipped = ToolItemManager.GetEquippedToolsForCrest(crest.name); } catch { }

            for (int i = 0; i < slots.Length; i++)
            {
                var info = slots[i];
                ToolItem tool = equipped != null && i < equipped.Count ? equipped[i] : null;

                // Positions are relative to the crest's origin, and game space
                // has y up where our layout has y down.
                float x = cx + info.Position.x * scale - SlotIcon * 0.5f;
                float y = cy - info.Position.y * scale - SlotIcon * 0.5f;

                // A slot the player has not opened yet. The game asks the same
                // pair of questions: the crest says the slot CAN be locked, and
                // the save says whether it has been unlocked.
                bool locked = false;
                try
                {
                    if (info.IsLocked)
                        locked = saved == null || i >= saved.Count || !saved[i].IsUnlocked;
                }
                catch { }

                AddSlot(x, y, SlotIcon, DsTheme.ToolTypeColor(info.Type),
                        locked ? null : tool, info.Type, locked, i);
            }
        }

        AddExtraSlots();
    }

    /// <summary>
    /// Pixels per crest-space unit that fits BOTH the slot ring and the
    /// artwork inside w x h, and the artwork's pixel size at that scale.
    ///
    /// Shared by the tab and by the picker's cards so the two cannot drift: a
    /// crest that sits correctly in one and off-centre in the other would look
    /// like the picker showing a different crest.
    /// </summary>
    static float CrestScale(ToolCrest crest, float w, float h, float slotSize, out Vector2 artPx)
    {
        float scale = float.MaxValue;

        ToolCrest.SlotInfo[] slots = null;
        try { slots = crest != null ? crest.Slots : null; } catch { }
        if (slots != null && slots.Length > 0)
        {
            float maxAbsX = 0f, maxAbsY = 0f;
            for (int i = 0; i < slots.Length; i++)
            {
                var p = slots[i].Position;
                maxAbsX = Mathf.Max(maxAbsX, Mathf.Abs(p.x));
                maxAbsY = Mathf.Max(maxAbsY, Mathf.Abs(p.y));
            }
            if (maxAbsX > 0.0001f) scale = Mathf.Min(scale, (w - slotSize) * 0.5f / maxAbsX);
            if (maxAbsY > 0.0001f) scale = Mathf.Min(scale, (h - slotSize) * 0.5f / maxAbsY);
        }

        Sprite art = null;
        try { art = crest != null ? crest.CrestSprite : null; } catch { }
        Vector2 world = art != null ? (Vector2)art.bounds.size : Vector2.zero;
        if (world.x > 0.0001f) scale = Mathf.Min(scale, w / world.x);
        if (world.y > 0.0001f) scale = Mathf.Min(scale, h / world.y);
        if (scale == float.MaxValue || scale <= 0f) scale = 1f;

        artPx = new Vector2(world.x > 0.0001f ? world.x * scale : 220f,
                            world.y > 0.0001f ? world.y * scale : 220f);
        return scale;
    }

    /// <summary>
    /// A crest and the tools socketed into it, drawn into <paramref name="box"/>
    /// with no bookkeeping -- nothing here can be selected, so none of it is
    /// remembered. For the picker's cards; the tab's own crest is BuildSlots,
    /// which needs every socket back to hit-test it.
    /// </summary>
    static void PaintCrest(RectTransform parent, ToolCrest crest, Rect box, float slotSize)
    {
        if (parent == null || crest == null) return;

        Vector2 artPx;
        float scale = CrestScale(crest, box.width, box.height, slotSize, out artPx);
        float cx = box.x + box.width * 0.5f;
        float cy = box.y + box.height * 0.5f;

        Sprite art = null;
        try { art = crest.CrestSprite; } catch { }
        if (art != null)
        {
            var img = DsWidgets.Icon(parent, "art", art, Color.white);
            DsWidgets.Place(img.rectTransform, cx - artPx.x * 0.5f, cy - artPx.y * 0.5f,
                            artPx.x, artPx.y);
            // The backdrop treatment the equipped crest gets, and for the same
            // reason: what is IN the sockets is the thing being read here.
            img.color = new Color(1f, 1f, 1f, CrestArtAlpha);
        }

        ToolCrest.SlotInfo[] slots = null;
        try { slots = crest.Slots; } catch { }
        if (slots == null) return;

        List<ToolCrestsData.SlotData> saved = null;
        try { saved = crest.SaveData.Slots; } catch { }

        List<ToolItem> equipped = null;
        try { equipped = ToolItemManager.GetEquippedToolsForCrest(crest.name); } catch { }

        for (int i = 0; i < slots.Length; i++)
        {
            var info = slots[i];

            bool locked = false;
            try
            {
                if (info.IsLocked)
                    locked = saved == null || i >= saved.Count || !saved[i].IsUnlocked;
            }
            catch { }

            ToolItem tool = null;
            if (!locked && equipped != null && i < equipped.Count) tool = equipped[i];

            var holder = DsWidgets.Rect(parent, "slot" + i);
            DsWidgets.Place(holder,
                            cx + info.Position.x * scale - slotSize * 0.5f,
                            cy - info.Position.y * scale - slotSize * 0.5f,
                            slotSize, slotSize);
            DrawSocket(holder, slotSize, DsTheme.ToolTypeColor(info.Type), tool, info.Type, locked);
        }
    }

    /// <summary>
    /// The tools equipped OUTSIDE the crest's own ring, in a row under it.
    ///
    /// They used to tuck into the top-left corner, which put them where the eye
    /// starts and made them read as part of the crest's own ring. The game keeps
    /// them apart from it -- a separate framed pair beside the crest -- and
    /// below is where this panel has the room.
    /// </summary>
    void AddExtraSlots()
    {
        List<string> names = null;
        try { names = PlayerData.instance.ExtraToolEquips.GetValidNames(); } catch { }
        if (names == null || names.Count == 0) return;

        var tools = new List<ToolItem>();
        for (int i = 0; i < names.Count; i++)
        {
            ToolCrestsData.SlotData data;
            try { data = PlayerData.instance.ExtraToolEquips.GetData(names[i]); } catch { continue; }
            if (string.IsNullOrEmpty(data.EquippedTool)) continue;

            ToolItem tool = null;
            try { tool = ToolItemManager.GetToolByName(data.EquippedTool); } catch { }
            if (tool != null) tools.Add(tool);
        }
        if (tools.Count == 0) return;

        // Centred as a row beneath the crest, clear of its lowest slot.
        const float gap = 18f;
        float width = tools.Count * ExtraIcon + (tools.Count - 1) * gap;
        float x = (LeftW - width) * 0.5f;
        float y = _crestH - ExtraIcon - 8f;

        for (int i = 0; i < tools.Count; i++)
        {
            AddSlot(x, y, ExtraIcon, DsTheme.ToolTypeColor(tools[i].Type), tools[i],
                    tools[i].Type, false);
            x += ExtraIcon + gap;
        }
    }
    // Tucked into the top-left corner: near enough to read as part of the
    // loadout, far enough from the centre not to collide with the artwork.
    void AddSlot(float x, float y, float size, Color ringColour, ToolItem tool)
    {
        AddSlot(x, y, size, ringColour, tool, ToolItemType.Red, false);
    }

    /// <summary>
    /// One slot around the crest, remembered so the cursor can land on it.
    /// </summary>
    void AddSlot(float x, float y, float size, Color ringColour, ToolItem tool,
                 ToolItemType type, bool locked, int index = -1)
    {
        var holder = DsWidgets.Rect(_crestBox, "slot" + _slotRects.Count);
        DsWidgets.Place(holder, x, y, size, size);

        _slots.Add(DrawSocket(holder, size, ringColour, tool, type, locked));
        _slotTools.Add(locked ? null : tool);
        _slotRects.Add(holder);
        _slotLocked.Add(locked);
        _slotIndex.Add(index);
        _slotColour.Add(locked ? LockedGrey : ringColour);
    }

    /// <summary>
    /// One socket's art, and nothing else -- no bookkeeping, so the picker's
    /// cards can use it too. Returns the Image carrying the socket's meaning.
    ///
    /// An empty slot is not an empty circle: the game draws the symbol for what
    /// the slot TAKES, tinted in that type's colour (InventoryToolCrestSlot
    /// returns its slotTypeSprite whenever nothing is equipped). A slot that has
    /// not been unlocked yet is the same symbol in grey at four-fifths scale,
    /// which is again the game's own treatment rather than an invention -- see
    /// its SpriteTint and LOCKED_SLOT_SCALE.
    /// </summary>
    static Image DrawSocket(RectTransform holder, float size, Color ringColour,
                            ToolItem tool, ToolItemType type, bool locked)
    {
        // A locked socket is a thick ring with a slit cut through it at top and
        // bottom and a dot at its centre, and NOTHING else -- no coloured ring,
        // no type symbol -- which reads as a fitting with no socket in it
        // rather than as a socket that happens to be empty.
        //
        // The game's own sprite; see DsGameArt.LockedSocketSymbol for how it was
        // found, since no field holds it. Until the atlas answers, the socket
        // draws nothing and the lookup retries -- the same as every other piece
        // of borrowed art here.
        if (locked)
        {
            var glyph = DsGameArt.LockedSocketSymbol();
            var mark = DsWidgets.Icon(holder, "locked", glyph, LockedGrey);
            mark.preserveAspect = true;
            if (glyph != null)
                DsWidgets.FitInk(mark, glyph, size * LockedSymbol, size * LockedSymbol);
            mark.color = LockedGrey;

            return mark;
        }

        Sprite held = null;
        try { held = tool != null ? tool.InventorySpriteBase : null; } catch { }

        // An EMPTY socket is its symbol and nothing else. The ring exists to
        // hold something and to say what may go in it; with nothing in it the
        // symbol already says the second part, in the type's own colour, and a
        // circle drawn around it reads as a socket that is somehow filled with
        // emptiness. Larger, too, because it is now the whole of what is there
        // rather than a mark inside a frame.
        if (held == null)
        {
            var symbol = DsGameArt.CrestSlotSymbol(type);
            var mark = DsWidgets.Icon(holder, "symbol", symbol, ringColour);
            mark.preserveAspect = true;
            if (symbol != null)
                DsWidgets.FitInk(mark, symbol, size * EmptySymbol, size * EmptySymbol);
            else
                DsWidgets.Stretch(mark.rectTransform, size * (1f - EmptySymbol) * 0.5f);
            mark.color = ringColour;

            return mark;
        }

        // Round, because the game's slots are round and a square frame around a
        // round icon reads as a different kind of thing. The ring's colour says
        // what is in the slot.
        var ring = DsWidgets.Circle(holder, "ring", ringColour);
        DsWidgets.Stretch(ring.rectTransform);
        var inner = DsWidgets.Circle(ring.rectTransform, "inner", DsTheme.Panel);
        DsWidgets.Stretch(inner.rectTransform, 5f);

        var img = DsWidgets.Icon(inner.rectTransform, "icon", held, Color.white);
        // Sized by the tool art's INK rather than by its rect, for the same
        // reason the symbols are: these sprites carry their own padding, and
        // insetting the rect left the drawing adrift in the middle of the ring.
        DsWidgets.FitInk(img, held, size * FilledInk, size * FilledInk);
        img.color = Color.white;

        return img;
    }

    /// <summary>
    /// How large each kind of art is drawn, as a multiple of the socket.
    ///
    /// These are ABOVE one, and that is not a mistake. The slot glyphs are
    /// 181x181 sprites whose drawing occupies roughly a third of that, with the
    /// rest transparent -- and the padding is baked into the TEXTURE, so it is
    /// invisible to Sprite.bounds and no fitting rule can measure it away.
    /// Asking for four-fifths of a socket therefore drew about a quarter of
    /// one, and raising the fraction toward 1 could never have been enough.
    /// The sprite's rect is simply drawn larger than the socket so that the ink
    /// inside it comes out the right size.
    /// A filled socket is the exception and is a fraction rather than a
    /// multiple: a tool's own inventory art is drawn close to its edges, so it
    /// needs no allowance for padding. It sits just inside the ring's inner
    /// edge -- close enough to fill it, short of touching it.
    /// The locked mark is a multiple for the same reason the symbols are: its
    /// sprite is 151x151 with a drawing about forty pixels across sitting in
    /// the middle of it.
    /// </summary>
    const float FilledInk    = 0.94f;
    const float EmptySymbol  = 1.85f;
    const float LockedSymbol = 1.24f;

    /// <summary>The colour the cursor takes on a given drawn slot.</summary>
    Color SlotColour(int drawn)
    {
        return drawn >= 0 && drawn < _slotColour.Count ? _slotColour[drawn] : DsTheme.Ink;
    }

    static readonly Color LockedGrey = new Color(0.5f, 0.5f, 0.5f, 1f);

    void ClearSlots()
    {
        for (int i = 0; i < _slotRects.Count; i++)
            if (_slotRects[i] != null) UnityEngine.Object.Destroy(_slotRects[i].gameObject);
        _slots.Clear(); _slotTools.Clear(); _slotRects.Clear();
        _slotLocked.Clear(); _slotIndex.Clear(); _slotColour.Clear();
        _lockedPick = -1;
    }

    // ── the tool list ───────────────────────────────────────────────────────
    //
    // Four groups, not three. The weaver skills are ToolItemType.Skill -- the
    // white-ringed slot at the centre of the crest -- and leaving them out
    // meant the one slot you cannot fill from the list was also the only one
    // whose contents were listed nowhere. They come first, as they do in game.

    static readonly ToolItemType[] Groups =
    {
        ToolItemType.Skill, ToolItemType.Red, ToolItemType.Blue, ToolItemType.Yellow
    };

    /// <summary>
    /// The words the groups used to carry, kept for the moment before the
    /// game's own dividers are readable -- the pane they live on is built when
    /// the player first opens the game's inventory, and until then there is
    /// nothing to read. See DsGameArt.ToolSectionDivider.
    /// </summary>
    static string GroupTitle(ToolItemType t)
    {
        switch (t)
        {
            case ToolItemType.Skill:  return "SKILLS";
            case ToolItemType.Red:    return "ATTACK";
            case ToolItemType.Blue:   return "SURVIVAL";
            default:                  return "EXPLORATION";
        }
    }

    void BuildList()
    {
        var buckets = new Dictionary<ToolItemType, DsSection>();
        var sections = new List<DsSection>();
        for (int i = 0; i < Groups.Length; i++)
        {
            var sec = new DsSection(GroupTitle(Groups[i]), DsTheme.ToolTypeColor(Groups[i]));
            // The game's own divider when it can be had, which drops the word:
            // the glyph on it is what the game shows and it is already in the
            // right colour. The title stays as the fallback.
            var art = DsGameArt.ToolSectionDivider(Groups[i]);
            if (art.Ok)
            {
                sec.Icon = art.Sprite;
                // The renderer's RGB, never its alpha. These headers hang off a
                // NestedFadeGroup, which is how the game fades the whole pane
                // in -- so while its inventory is closed the alpha is zero, and
                // taking the colour whole gave four invisible dividers and no
                // titles either, since the art was found and simply not seen.
                var c = art.Colour;
                sec.IconColour = new Color(c.r, c.g, c.b, 1f);
                // A colour that carries no light at all is not a tint the game
                // meant; fall back to the type's own.
                if (c.r + c.g + c.b < 0.05f)
                    sec.IconColour = DsTheme.ToolTypeColor(Groups[i]);
            }
            buckets[Groups[i]] = sec;
            sections.Add(sec);
        }

        try
        {
            foreach (var tool in ToolItemManager.GetAllTools())
            {
                if (tool == null) continue;

                // The game's own test, and deliberately NOT simply IsUnlocked:
                // ToolItemManager.GetUnlockedTools filters on
                // IsUnlockedNotHidden, which is `IsUnlocked && !IsHidden`.
                //
                // The second half is what keeps SUPERSEDED tools out. A tool
                // replaced by a better one -- the Needle Phial, the Claw
                // Mirror -- stays unlocked forever: ToolItem.Unlock calls
                // `getReplaces.Lock()` on the one it supersedes, and Lock only
                // sets IsHidden and strips the tool from every crest. It never
                // touches IsUnlocked. So a list built on IsUnlocked shows both
                // halves of every upgrade, one of which the player can no
                // longer equip or use.
                bool unlocked = false;
                try { unlocked = tool.IsUnlockedNotHidden; } catch { }
                if (!unlocked) continue;

                DsSection bucket;
                if (!buckets.TryGetValue(tool.Type, out bucket)) continue;

                Sprite icon = null;
                try { icon = tool.InventorySpriteBase; } catch { }

                string badge = null;
                try { int left = tool.SavedData.AmountLeft; if (left > 0) badge = left.ToString(); } catch { }

                bool equipped = false;
                try { equipped = tool.IsEquipped; } catch { }

                bucket.Items.Add(new DsItem
                {
                    Key = tool.name,
                    Name = DsText(tool.DisplayName),
                    Description = DsText(tool.Description),
                    Icon = icon,
                    Tint = Color.white,
                    Badge = badge,
                    // The light behind a selected tool takes the colour of its
                    // type, the way the game's does -- see
                    // InventoryItemTool.CursorColor.
                    Glow = DsTheme.ToolTypeColor(tool.Type),
                });
            }
        }
        catch { }

        Grid.EmptyMessage = "No tools yet";
        Grid.SetSections(sections);
    }
    // ── input ───────────────────────────────────────────────────────────────

    public void OnGesture(DsGesture g)
    {
        // The picker covers the body, so nothing beneath it should see a tap.
        if (_choosingCrest) { PickerGesture(g); return; }

        // The grid handles everything on its own side, including scrolling and
        // selection, and ignores anything outside its own column.
        _grid.OnGesture(g);

        // Tapping a socketed tool selects it too, which is the fastest way to
        // check what you are actually carrying without hunting the list.
        if (g.Type != DsGestureType.Tap) return;

        Vector2 p = DsPresentation.ToLayout(g.Position);
        if (p.x >= ListX) return;

        float bodyTop = DsLayout.Current.Body.y;
        for (int i = 0; i < _slotRects.Count; i++)
        {
            var rt = _slotRects[i];
            var tool = _slotTools[i];
            // Every socket answers a tap, whatever is or is not in it. A LOCKED
            // one is selected in order to act on the socket itself; an EMPTY
            // one has nothing to act on and nothing to say, and is still worth
            // landing on -- it is how you see which sockets this crest has and
            // which colour each one takes, and a socket you cannot put the
            // cursor on reads as a picture rather than as part of the screen.
            bool locked = i < _slotLocked.Count && _slotLocked[i];
            if (rt == null) continue;

            // Slots live inside the crest panel, which is itself placed at
            // (LeftX, 16) within the body, so their layout position is the sum.
            float sx = LeftX + rt.anchoredPosition.x;
            float sy = bodyTop + 16f - rt.anchoredPosition.y;
            float size = rt.sizeDelta.x;
            if (p.x >= sx && p.x <= sx + size && p.y >= sy && p.y <= sy + size)
            {
                var where = new Rect(sx, sy - bodyTop, size, size);

                if (locked)
                {
                    _lockedPick = i;
                    _socketTool = null;
                    // Nothing to say about it. The game says nothing either --
                    // a locked socket has no description, and the UNLOCK button
                    // appearing beside it is what tells you what it is and
                    // whether you can afford it. Prose invented to fill the
                    // pane would be this panel talking about itself.
                    _grid.ShowDetail(string.Empty, string.Empty);
                    _grid.SetExternalTarget(where, LockedGrey, "locked:" + i);
                    return;
                }

                _lockedPick = -1;

                if (tool == null)
                {
                    // An empty socket. The cursor goes to it and the
                    // description pane empties: there is no tool to describe,
                    // and leaving the last one's prose up would attach it to a
                    // socket that does not hold it.
                    _socketTool = null;
                    _grid.ShowDetail(string.Empty, string.Empty);
                    _grid.SetExternalTarget(where, SlotColour(i), "empty:" + i);
                    return;
                }

                // Select it in the list, so the description pane fills and the
                // grid knows what is chosen...
                _grid.SelectByKey(tool.name);
                // ...and remember it here, because handing the cursor to the
                // socket below clears the grid's key, and the header's
                // EQUIP/UNEQUIP needs to know what is under the cursor.
                _socketTool = tool;

                // ...but keep the cursor HERE, on the socket that was actually
                // tapped, rather than letting it jump across to the same tool's
                // cell in the list. The socket is what the eye is on.
                _grid.SetExternalTarget(
                    new Rect(sx, sy - bodyTop, size, size),
                    DsTheme.ToolTypeColor(tool.Type), "socket:" + tool.name);
                return;
            }
        }
    }

    static string DsText(TeamCherry.Localization.LocalisedString s)
    {
        try { return s.ToString(); } catch { return ""; }
    }
    // ── crest picker ────────────────────────────────────────────────────────
    //
    // A carousel, because that is what the game has. InventoryToolCrestList
    // lays every unlocked crest out in one row, keeps the chosen one centred by
    // sliding the row under it (ScrollToCrestRoutine lerps the parent's x over
    // 0.3 s), and shows an arrow on each side that has somewhere to go --
    // `scrollLeftArrowGroup.FadeTo(currentCrestIndex > 0 ? 1 : 0)`. What was
    // here before, a four-across grid of every crest at once, held the same
    // crests and said nothing about which was chosen.
    //
    // Each card carries the crest's own sockets with the tools actually in
    // them, which is the other half of what the game shows while switching:
    // every InventoryToolCrest in its list calls GetEquippedForSlots, so you
    // can see what a crest is carrying BEFORE you put it on.
    //
    // A full-body overlay rather than a rearrangement of the tab. Showing and
    // hiding one opaque panel is a single toggle; hiding the tab's own parts
    // would mean reaching into the grid's clip, its detail pane and the two
    // gutter rules and putting them all back afterwards.

    class CrestCard
    {
        public string Id;
        public RectTransform Root;
        /// <summary>How strongly it is drawn; see DeselectedFade.</summary>
        public CanvasGroup Fade;
    }

    // The card, and how far apart two of them sit. The stride is WIDER than the
    // card on purpose: it leaves each neighbour showing a sliver at the edge of
    // the panel, which is what says the row continues, and it leaves a gap
    // between that sliver and the centre card for the arrows to sit in.
    const float CardW = 460f;
    const float CardStride = 700f;
    const float CardNameH = 58f;
    const float CardDescH = 132f;
    const float ArrowSize = 84f;
    /// <summary>The crest's own sockets are drawn smaller here than on the tab.</summary>
    const float CardSlotIcon = 84f;

    /// <summary>
    /// How long the row takes to carry one crest off and the next one on.
    ///
    /// The game's scrollTime is 0.3 s and the move is a plain linear lerp over
    /// unscaled time, with no easing -- the same as InventoryCursor, and the
    /// same as DsCursor for the same reason. A knob because it is a matter of
    /// feel; 0 snaps.
    /// </summary>
    static float ScrollSeconds
    {
        get { return Mathf.Clamp(DsConfig.Int("crest_scroll_ms", 280), 0, 2000) / 1000f; }
    }

    /// <summary>
    /// How a crest that is not the chosen one is drawn. The game dims it to
    /// InventoryToolCrest.DeselectedColor -- a flat half grey -- and shrinks it
    /// to its deselectedScale; on a black panel, fading is the same gesture and
    /// needs no second colour.
    /// </summary>
    const float DeselectedFade = 0.4f;
    const float DeselectedScale = 0.86f;

    RectTransform _picker, _strip;
    readonly List<CrestCard> _crestCards = new List<CrestCard>();
    Image _arrowLeft, _arrowRight;
    int _crestPick = -1;
    float _stripFrom, _stripTo, _stripNow, _stripT = 1f;
    bool _choosingCrest;

    void ShowCrestPicker(bool on)
    {
        _choosingCrest = on;
        if (on) BuildCrestPicker();
        if (_picker != null) DsWidgets.SetActive(_picker, on);
    }

    void BuildCrestPicker()
    {
        float bodyH = DsLayout.Current.Body.height;
        float panelW = DsLayout.Current.Width;

        if (_picker == null)
        {
            _picker = DsWidgets.Box(_host, "crest-picker", DsTheme.Ground).rectTransform;
            DsWidgets.Place(_picker, 0f, 0f, panelW, bodyH);

            // The row is clipped to the panel, so a card on its way out is cut
            // at the edge rather than drawn across the tab bar beside it.
            var clip = DsWidgets.Rect(_picker, "clip");
            DsWidgets.Place(clip, 0f, 0f, panelW, bodyH);
            clip.gameObject.AddComponent<RectMask2D>();

            _strip = DsWidgets.Rect(clip, "strip");

            // After the clip, so they draw over the cards they point at.
            _arrowLeft = MakeArrow("arrow-left", false, panelW, bodyH);
            _arrowRight = MakeArrow("arrow-right", true, panelW, bodyH);
        }
        _picker.SetAsLastSibling();

        for (int i = 0; i < _crestCards.Count; i++)
            if (_crestCards[i].Root != null) UnityEngine.Object.Destroy(_crestCards[i].Root.gameObject);
        _crestCards.Clear();
        _crestPick = -1;

        List<ToolCrest> crests = null;
        try { crests = ToolItemManager.GetAllCrests(); } catch { }
        if (crests == null) return;

        string current = null;
        try { current = PlayerData.instance.CurrentCrestID; } catch { }

        // One line naming every crest and why it is in or out. The filter is the
        // game's own (InventoryToolCrest shows itself on CrestData.IsVisible),
        // so when this list disagrees with the game's the answer is always in
        // these four flags rather than in our code.
        var report = new System.Text.StringBuilder("[DsCrest]");
        foreach (var c in crests)
        {
            if (c == null) continue;
            try
            {
                report.Append(' ').Append(c.name)
                      .Append(c.IsVisible ? "=show" : "=hide")
                      .Append("(unlocked=").Append(c.IsUnlocked)
                      .Append(" hidden=").Append(c.IsHidden)
                      .Append(" upgraded=").Append(c.IsUpgradedVersionUnlocked).Append(')');
            }
            catch { }
        }
        Debug.Log(report.ToString());

        foreach (var crest in crests)
        {
            if (crest == null) continue;
            // The game's own test, and it is not simply "unlocked". IsVisible
            // also hides a crest whose UPGRADED version has been unlocked,
            // which is why both Hunter's Crests were listed: the base one stays
            // unlocked forever once its evolved form exists, and only the
            // evolved one should be offered. It also hides a crest marked
            // hidden unless it happens to be the one equipped.
            bool ok = false;
            try { ok = crest.IsVisible; } catch { }
            if (!ok) continue;

            if (crest.name == current) _crestPick = _crestCards.Count;
            _crestCards.Add(BuildCrestCard(crest, _crestCards.Count, bodyH));
        }

        if (_crestPick < 0) _crestPick = 0;

        // Opening it puts the crest you are wearing under the arrows without
        // travelling to it: there is nowhere it could sensibly have come from.
        _stripNow = _stripFrom = _stripTo = StripXFor(_crestPick);
        _stripT = 1f;
        ApplyStrip();
    }

    CrestCard BuildCrestCard(ToolCrest crest, int index, float bodyH)
    {
        float cardH = bodyH - 24f;

        var root = DsWidgets.Rect(_strip, "crest" + index);
        // By its CENTRE, so that shrinking an unchosen card leaves it where it
        // was rather than dragging it toward its own top-left corner.
        PlaceCentred(root, index * CardStride, 12f, CardW, cardH);
        var fade = root.gameObject.AddComponent<CanvasGroup>();

        // The body face: crest names are mixed case ("Hunter Crest").
        string label = "";
        try { label = DsText(crest.DisplayName); } catch { }
        var name = DsWidgets.Label(root, "name", label, DsTheme.TitleSize,
                                   DsTheme.Ink, TmpAlign.Center);
        if (name != null) DsWidgets.Place(name.rectTransform, 0f, 0f, CardW, CardNameH);

        string prose = "";
        try { prose = DsText(crest.Description); } catch { }
        var desc = DsWidgets.Label(root, "desc", prose, DsTheme.RowSize,
                                   DsTheme.InkDim, TmpAlign.Top);
        if (desc != null)
            DsWidgets.Place(desc.rectTransform, 16f, cardH - CardDescH, CardW - 32f, CardDescH);

        float ringTop = CardNameH + 12f;
        PaintCrest(root, crest, new Rect(0f, ringTop, CardW, cardH - ringTop - CardDescH - 12f),
                   CardSlotIcon);

        return new CrestCard { Id = crest.name, Root = root, Fade = fade };
    }

    Image MakeArrow(string name, bool right, float panelW, float bodyH)
    {
        var img = DsWidgets.Icon(_picker, name, DsTheme.Chevron, DsTheme.Ink);
        // A generated sprite has no atlas neighbours to avoid and no trim to
        // honour, so the plain quad is both correct and cheaper.
        img.useSpriteMesh = false;
        PlaceCentred(img.rectTransform, ArrowCentreX(right, panelW) - ArrowSize * 0.5f,
                     (bodyH - ArrowSize) * 0.5f, ArrowSize, ArrowSize);
        if (right) img.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 180f);
        return img;
    }

    /// <summary>
    /// Where an arrow sits: midway between the neighbour's visible sliver and
    /// the centre card, so it crowds neither.
    /// </summary>
    static float ArrowCentreX(bool right, float panelW)
    {
        float cardLeft = (panelW - CardW) * 0.5f;
        float peek = Mathf.Max(0f, panelW - (cardLeft + CardStride));
        float cx = Mathf.Max(ArrowSize, (peek + cardLeft) * 0.5f);
        return right ? panelW - cx : cx;
    }

    /// <summary>Where the row has to sit for card <paramref name="i"/> to be centred.</summary>
    static float StripXFor(int i)
    {
        return (DsLayout.Current.Width - CardW) * 0.5f - i * CardStride;
    }

    /// <summary>
    /// Place a rect by its centre rather than its top-left corner, so a scale
    /// applied to it grows and shrinks in place. Children inside are unaffected
    /// -- they anchor to the rect, not to its pivot.
    /// </summary>
    static void PlaceCentred(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x + w * 0.5f, -(y + h * 0.5f));
        rt.sizeDelta = new Vector2(w, h);
    }

    /// <summary>
    /// Move one crest along and slide the row after it.
    ///
    /// The game's own bounds: SwitchSelectedCrest steps the index and does
    /// nothing at all when that would leave the list, so the ends are stops
    /// rather than a wrap.
    /// </summary>
    void StepCrest(int direction)
    {
        if (direction == 0 || _crestCards.Count == 0) return;
        int next = _crestPick + (direction > 0 ? 1 : -1);
        if (next < 0 || next >= _crestCards.Count) return;

        _crestPick = next;
        _stripFrom = _stripNow;
        _stripTo = StripXFor(next);
        _stripT = ScrollSeconds <= 0f ? 1f : 0f;
        ApplyStrip();
    }

    /// <summary>Carry the row toward the chosen crest, linearly, as the game does.</summary>
    void TickCrestPicker(float dt)
    {
        if (_strip == null || _stripT >= 1f) return;
        float time = ScrollSeconds;
        _stripT = time <= 0f ? 1f : Mathf.Min(1f, _stripT + dt / time);
        _stripNow = Mathf.Lerp(_stripFrom, _stripTo, _stripT);
        ApplyStrip();
    }

    /// <summary>Put the row where it has got to, and dress the cards to match.</summary>
    void ApplyStrip()
    {
        if (_strip == null) return;

        DsWidgets.Place(_strip, _stripNow, 0f,
                        DsLayout.Current.Width, DsLayout.Current.Body.height);

        for (int i = 0; i < _crestCards.Count; i++)
        {
            var card = _crestCards[i];
            bool chosen = i == _crestPick;
            if (card.Fade != null) card.Fade.alpha = chosen ? 1f : DeselectedFade;
            if (card.Root != null)
                card.Root.localScale = chosen
                    ? Vector3.one
                    : new Vector3(DeselectedScale, DeselectedScale, 1f);
        }

        DsWidgets.SetActive(_arrowLeft, _crestPick > 0);
        DsWidgets.SetActive(_arrowRight, _crestPick >= 0 && _crestPick < _crestCards.Count - 1);
    }

    /// <summary>The crest under the arrows, which is the one EQUIP acts on.</summary>
    string PickedCrestId()
    {
        return _crestPick >= 0 && _crestPick < _crestCards.Count ? _crestCards[_crestPick].Id : null;
    }

    /// <summary>
    /// Everything the picker answers. Arrows first, because they are drawn over
    /// the cards; then the cards themselves, where the centre one is the choice
    /// and either neighbour is a step toward it.
    /// </summary>
    void PickerGesture(DsGesture g)
    {
        // A flick carries the row the way the thumb went, which is the gesture
        // the arrows stand in for. Horizontal only: a vertical flick on a row
        // that cannot move vertically is not aimed at it.
        if (g.Type == DsGestureType.Fling)
        {
            if (Mathf.Abs(g.Delta.x) > Mathf.Abs(g.Delta.y))
                StepCrest(g.Delta.x > 0f ? -1 : 1);
            return;
        }
        if (g.Type != DsGestureType.Tap) return;

        Vector2 p = DsPresentation.ToLayout(g.Position);
        float panelW = DsLayout.Current.Width;
        var body = DsLayout.Current.Body;

        if (_crestPick > 0 && ArrowHit(false, panelW, body, p)) { StepCrest(-1); return; }
        if (_crestPick < _crestCards.Count - 1 && ArrowHit(true, panelW, body, p))
        { StepCrest(1); return; }

        if (p.y < body.y || p.y > body.y + body.height) return;

        for (int i = 0; i < _crestCards.Count; i++)
        {
            float left = _stripNow + i * CardStride;
            if (p.x < left || p.x > left + CardW) continue;
            if (i == _crestPick) ChooseCrest(_crestCards[i].Id);
            else StepCrest(i > _crestPick ? 1 : -1);
            return;
        }
    }

    /// <summary>
    /// An arrow's touch target, which is twice its art on each axis: the
    /// chevron is a thin shape and a target drawn to it would be a thin target.
    /// </summary>
    static bool ArrowHit(bool right, float panelW, Rect body, Vector2 p)
    {
        float cx = ArrowCentreX(right, panelW);
        float cy = body.y + body.height * 0.5f;
        return p.x >= cx - ArrowSize && p.x <= cx + ArrowSize &&
               p.y >= cy - ArrowSize && p.y <= cy + ArrowSize;
    }

    void ChooseCrest(string crestId)
    {
        try
        {
            ToolItemManager.SetEquippedCrest(crestId);
            try { ToolItemManager.SendEquippedChangedEvent(); } catch { }
            // The ring, its slots and the tool list all describe the crest that
            // was equipped a moment ago.
            Refresh(force: true);
            _socketTool = null;
        }
        catch (Exception e) { Debug.LogWarning("[DualScreen] crest change failed: " + e.Message); }
        ShowCrestPicker(false);
    }

    // ── EQUIP / UNEQUIP ─────────────────────────────────────────────────────

    /// <summary>
    /// Offer what can be done to the thing under the cursor.
    ///
    /// That is the game's own rule -- InventoryItemToolManager.CanChangeEquips
    /// is `playerData.atBench` with a cheat override -- and the v3 notes ask for
    /// the button to be absent rather than greyed when it does not apply. This
    /// differs from USE on the Inventory tab deliberately: a consumable you
    /// cannot drink yet is still a consumable, and saying so is useful, while a
    /// tool away from a bench is simply not something the panel can act on.
    ///
    /// UNLOCK is the one exception, because it is one in the game too:
    /// InventoryToolCrestSlot.Submit answers a LOCKED socket and returns before
    /// it ever reaches CanChangeEquips, so opening a socket with a Memory
    /// Locket is something the player can do anywhere. Only what goes IN the
    /// socket afterwards needs a bench.
    /// </summary>
    public void CollectActions(List<DsAction> into)
    {
        // While choosing, the only things to offer are a way out and a way to
        // take what is under the arrows. Equipping a TOOL into a crest you are
        // in the middle of replacing is not a useful thing to be able to do.
        if (_choosingCrest)
        {
            into.Add(new DsAction("BACK", () => ShowCrestPicker(false), false,
                                  DsActionPlace.Pane));

            // Second, so it lands at the BOTTOM of the strip and nearest the
            // thumb -- the same rule EQUIP follows on the tab itself. Tapping
            // the centre card does the same thing; this is what says so.
            string picked = PickedCrestId();
            if (!string.IsNullOrEmpty(picked))
                into.Add(new DsAction("EQUIP", () => ChooseCrest(picked), false,
                                      DsActionPlace.Pane));
            return;
        }

        bool atBench = AtBench();

        if (atBench)
            into.Add(new DsAction("CREST", () => ShowCrestPicker(true), false,
                                  DsActionPlace.Pane));

        // A locked socket replaces EQUIP with the only thing that can be done
        // to it. Shown greyed rather than withdrawn when there is nothing to
        // spend, which is the game's own treatment -- InventoryToolCrestSlot
        // draws the prompt either way and reads manager.CanUnlockSlot to decide
        // whether it responds. It is also the more useful of the two: "this
        // needs a Memory Locket" is worth knowing, and a button that simply
        // vanished would leave the socket looking like a dead end.
        int locked = LockedPick();
        if (locked >= 0)
        {
            bool can = CanUnlockSocket();
            into.Add(new DsAction("UNLOCK",
                                  can ? (Action)(() => UnlockSocket(locked)) : null,
                                  !can, DsActionPlace.Pane));
            return;
        }

        if (!atBench) return;

        var tool = SelectedTool();
        if (tool == null) return;

        bool equipped = false;
        try { equipped = ToolItemManager.IsToolEquipped(tool.name); } catch { }

        // Second, so it lands at the BOTTOM of the strip: it is the one that
        // acts on what the cursor is actually on, and the nearest the thumb.
        into.Add(equipped
            ? new DsAction("UNEQUIP", () => Unequip(tool), false, DsActionPlace.Pane)
            : new DsAction("EQUIP", () => Equip(tool), false, DsActionPlace.Pane));
    }

    /// <summary>
    /// The height of the three columns. One definition, because the detail
    /// column's own rect and the strip pinned to its bottom are worked out in
    /// different places and must agree to the pixel.
    /// </summary>
    static float ColumnHeight => DsLayout.Current.Body.height - 36f;

    /// <summary>
    /// The buttons sit under the prose about the tool they act on. See
    /// DsActions for why these are Pane actions and the map's are not.
    /// </summary>
    public Rect ActionPane
    {
        get
        {
            return DsLayout.Current.InBody(
                new Rect(DetailX, DetailTop + ColumnHeight - ActionBand, DetailW, ActionBand));
        }
    }

    /// <summary>
    /// At a bench, which is the game's own condition for changing equips
    /// (InventoryItemToolManager.CanChangeEquips reads playerData.atBench).
    ///
    /// Deliberately only that. The game also lets a cheat flag override it, and
    /// honouring that here would make the panel disagree with the bench rule
    /// the rest of the game is played by.
    /// </summary>
    static bool AtBench()
    {
        if (!DsGameData.InGame) return false;
        try { return PlayerData.instance.atBench; } catch { return false; }
    }

    ToolItem SelectedTool()
    {
        if (!DsGameData.InGame) return null;

        // The grid's own selection wins when it has one. Tapping a socket hands
        // the cursor to the socket, which clears the grid's key -- so without
        // remembering the socket here, selecting an equipped tool in the crest
        // offered nothing at all and the player had to hunt the same tool down
        // in the list to unequip it.
        string key = _grid.SelectedKey;
        if (!string.IsNullOrEmpty(key))
        {
            _socketTool = null;
            // Choosing anything in the list is also choosing to stop looking at
            // a locked socket, and UNLOCK must go with it.
            _lockedPick = -1;
            try { return ToolItemManager.GetToolByName(key); } catch { return null; }
        }
        return _socketTool;
    }

    // ── unlocking a socket ──────────────────────────────────────────────────

    /// <summary>
    /// Whether a socket can be opened right now, which is the game's own test:
    /// InventoryItemToolManager.CanUnlockSlot is `slotUnlockItem.CollectedAmount
    /// > 0` and nothing else.
    /// </summary>
    bool CanUnlockSocket()
    {
        var item = DsGameArt.SlotUnlockItem();
        if (item == null) return false;
        try { return item.CollectedAmount > 0; } catch { return false; }
    }

    /// <summary>The locked socket under the cursor, or -1.</summary>
    int LockedPick()
    {
        if (_lockedPick < 0 || _lockedPick >= _slotLocked.Count) return -1;
        if (!_slotLocked[_lockedPick]) return -1;
        // Superseded by a choice in the list.
        if (!string.IsNullOrEmpty(_grid.SelectedKey)) return -1;
        return _lockedPick;
    }

    /// <summary>
    /// Open the socket, which is the whole of what the game's own unlock
    /// coroutine does to the save:
    ///
    ///     ToolCrestsData.SlotData saveData = SaveData;
    ///     saveData.IsUnlocked = true;
    ///     SaveData = saveData;
    ///     manager.SlotUnlockItem.Take(1, showCounter: false);
    ///
    /// Everything else in that routine is the hold-to-open animation -- the
    /// shake, the particles, the rumble -- and belongs to a pane we are not
    /// drawing.
    ///
    /// The write-back is spelled out because SlotData is a STRUCT: reading it,
    /// setting the flag and dropping it would change a copy and save nothing.
    /// The list-growing is the game's too, from InventoryToolCrestSlot's own
    /// setter, and it matters for a crest whose slots have never been written.
    /// </summary>
    void UnlockSocket(int drawnIndex)
    {
        if (drawnIndex < 0 || drawnIndex >= _slotIndex.Count) return;
        int slot = _slotIndex[drawnIndex];
        if (slot < 0) return;

        var item = DsGameArt.SlotUnlockItem();
        if (item == null) return;

        try
        {
            if (item.CollectedAmount <= 0) return;

            var crest = ToolItemManager.GetCrestByName(_crestId);
            if (crest == null) return;

            var pd = PlayerData.instance;
            var data = pd.ToolEquips.GetData(crest.name);
            var list = data.Slots;
            if (list == null)
            {
                list = data.Slots = new List<ToolCrestsData.SlotData>();
                pd.ToolEquips.SetData(crest.name, data);
            }
            while (list.Count < slot + 1) list.Add(default(ToolCrestsData.SlotData));

            var sd = list[slot];
            if (sd.IsUnlocked) return;       // already open; do not spend a second
            sd.IsUnlocked = true;
            list[slot] = sd;

            item.Take(1, showCounter: false);
            Debug.Log("[DualScreen] unlocked socket " + slot + " on " + crest.name);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DualScreen] unlock failed: " + e.Message);
            return;
        }

        _lockedPick = -1;
        Refresh(force: true);
    }

    void Unequip(ToolItem tool)
    {
        try
        {
            ToolItemManager.UnequipTool(tool);
            Refresh(force: true);
        }
        catch (Exception e) { Debug.LogWarning("[DualScreen] unequip failed: " + e.Message); }
    }

    /// <summary>
    /// Put the tool in a slot of its own type, preferring an empty one.
    ///
    /// This is ToolItemManager.AutoEquip's slot arithmetic, done here rather
    /// than by calling it, because AutoEquip also does two things that belong to
    /// the game's own inventory and not to us: it sets UnlockedTool, which is
    /// the "newly unlocked" presentation state, and it calls
    /// InventoryPaneList.SetNextOpen("Tools"), which decides which pane the
    /// player's NEXT press of the inventory button lands on. Equipping from the
    /// second screen should not reach into either.
    ///
    /// What is left -- SetEquippedTools and the changed event -- is the part
    /// that actually equips, and both are public and free of that baggage.
    /// </summary>
    void Equip(ToolItem tool)
    {
        try
        {
            string crestId = PlayerData.instance.CurrentCrestID;
            var crest = ToolItemManager.GetCrestByName(crestId);
            if (crest == null || crest.Slots == null) return;

            var equipped = ToolItemManager.GetEquippedToolsForCrest(crestId);
            var slots = new List<string>(crest.Slots.Length);
            for (int i = 0; i < crest.Slots.Length; i++)
            {
                var held = (equipped != null && i < equipped.Count) ? equipped[i] : null;
                slots.Add(held != null ? held.name : string.Empty);
            }

            // Which sockets are actually open. A locked one is not a candidate
            // for anything: without this the panel would happily equip into a
            // socket the player has not bought, which is both wrong and a
            // quiet way to make the UNLOCK button pointless.
            List<ToolCrestsData.SlotData> saved = null;
            try { saved = crest.SaveData.Slots; } catch { }
            var open = new bool[crest.Slots.Length];
            for (int i = 0; i < crest.Slots.Length; i++)
            {
                bool locked = false;
                try
                {
                    if (crest.Slots[i].IsLocked)
                        locked = saved == null || i >= saved.Count || !saved[i].IsUnlocked;
                }
                catch { }
                open[i] = !locked;
            }

            int target = -1;
            if (tool.Type == ToolItemType.Skill)
            {
                // Skills go in the one neutral skill slot, not in any of them.
                for (int i = 0; i < crest.Slots.Length; i++)
                {
                    if (!open[i]) continue;
                    if (crest.Slots[i].Type == ToolItemType.Skill &&
                        crest.Slots[i].AttackBinding == AttackToolBinding.Neutral)
                    { target = i; break; }
                }
            }
            else
            {
                // An empty slot of the right colour if there is one; otherwise
                // the last of that colour, which is then replaced.
                int lastOfType = -1, firstFree = -1;
                for (int i = 0; i < crest.Slots.Length; i++)
                {
                    if (!open[i] || crest.Slots[i].Type != tool.Type) continue;
                    lastOfType = i;
                    if (string.IsNullOrEmpty(slots[i])) firstFree = i;
                }
                target = firstFree >= 0 ? firstFree : lastOfType;
            }
            if (target < 0) return;      // this crest has nowhere to put it

            slots[target] = tool.name;
            ToolItemManager.SetEquippedTools(crestId, slots);
            ToolItemManager.SendEquippedChangedEvent();
            Refresh(force: true);
        }
        catch (Exception e) { Debug.LogWarning("[DualScreen] equip failed: " + e.Message); }
    }
}
#endif