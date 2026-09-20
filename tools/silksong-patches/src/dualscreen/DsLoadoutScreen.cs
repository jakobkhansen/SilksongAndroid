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
    const float SlotIcon = 82f;
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
                try { unlocked = tool.IsUnlocked; } catch { }
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

        Sprite art = null;
        try { art = crest.CrestSprite; } catch { }

        // Pixels per world unit, chosen so both the ring and the artwork fit.
        float scale = float.MaxValue;

        if (slots != null && slots.Length > 0)
        {
            float maxAbsX = 0f, maxAbsY = 0f;
            for (int i = 0; i < slots.Length; i++)
            {
                var p = slots[i].Position;
                maxAbsX = Mathf.Max(maxAbsX, Mathf.Abs(p.x));
                maxAbsY = Mathf.Max(maxAbsY, Mathf.Abs(p.y));
            }
            if (maxAbsX > 0.0001f) scale = Mathf.Min(scale, (ringW - SlotIcon) * 0.5f / maxAbsX);
            if (maxAbsY > 0.0001f) scale = Mathf.Min(scale, (ringH - SlotIcon) * 0.5f / maxAbsY);
        }

        Vector2 artWorld = art != null ? (Vector2)art.bounds.size : Vector2.zero;
        if (artWorld.x > 0.0001f) scale = Mathf.Min(scale, ringW / artWorld.x);
        if (artWorld.y > 0.0001f) scale = Mathf.Min(scale, ringH / artWorld.y);
        if (scale == float.MaxValue || scale <= 0f) scale = 1f;

        if (_crestImage != null)
        {
            float aw = artWorld.x > 0.0001f ? artWorld.x * scale : 220f;
            float ah = artWorld.y > 0.0001f ? artWorld.y * scale : 220f;
            DsWidgets.Place(_crestImage.rectTransform, cx - aw * 0.5f, cy - ah * 0.5f, aw, ah);
        }

        if (slots != null)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                var info = slots[i];
                ToolItem tool = null;
                if (saved != null && i < saved.Count && !string.IsNullOrEmpty(saved[i].EquippedTool))
                {
                    try { tool = ToolItemManager.GetToolByName(saved[i].EquippedTool); } catch { }
                }

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
                        locked ? null : tool, info.Type, locked);
            }
        }

        AddExtraSlots();
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
    /// One slot around the crest.
    ///
    /// An empty slot is not an empty circle: the game draws the symbol for what
    /// the slot TAKES, tinted in that type's colour (InventoryToolCrestSlot
    /// returns its slotTypeSprite whenever nothing is equipped). A slot that has
    /// not been unlocked yet is the same symbol in grey at four-fifths scale,
    /// which is again the game's own treatment rather than an invention -- see
    /// its SpriteTint and LOCKED_SLOT_SCALE.
    /// </summary>
    void AddSlot(float x, float y, float size, Color ringColour, ToolItem tool,
                 ToolItemType type, bool locked)
    {
        var holder = DsWidgets.Rect(_crestBox, "slot" + _slotRects.Count);
        DsWidgets.Place(holder, x, y, size, size);

        // Round, because the game's slots are round and a square frame around a
        // round icon reads as a different kind of thing. The ring's colour says
        // what may go in the slot, which is useful even when it is empty.
        var ring = DsWidgets.Circle(holder, "ring", locked ? LockedGrey : ringColour);
        DsWidgets.Stretch(ring.rectTransform);
        var inner = DsWidgets.Circle(ring.rectTransform, "inner", DsTheme.Panel);
        DsWidgets.Stretch(inner.rectTransform, 5f);

        Sprite icon = null;
        try { icon = tool != null ? tool.InventorySpriteBase : null; } catch { }

        float inset = size * 0.17f;
        Color tint = Color.white;
        if (icon == null)
        {
            icon = DsGameArt.CrestSlotSymbol(type);
            tint = locked ? LockedGrey : ringColour;
            // Locked slots are drawn smaller, so the inset grows rather than the
            // rect shrinking -- the ring around it must stay the slot's size.
            if (locked) inset += size * 0.10f;
            else inset += size * 0.04f;
        }

        var img = DsWidgets.Icon(inner.rectTransform, "icon", icon, tint);
        // Inset enough that a square-ish icon stays inside the circle.
        DsWidgets.Stretch(img.rectTransform, inset);

        _slots.Add(img);
        // A locked slot holds nothing and must not answer taps with a tool.
        _slotTools.Add(locked ? null : tool);
        _slotRects.Add(holder);
    }

    static readonly Color LockedGrey = new Color(0.5f, 0.5f, 0.5f, 1f);

    void ClearSlots()
    {
        for (int i = 0; i < _slotRects.Count; i++)
            if (_slotRects[i] != null) UnityEngine.Object.Destroy(_slotRects[i].gameObject);
        _slots.Clear(); _slotTools.Clear(); _slotRects.Clear();
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
            buckets[Groups[i]] = sec;
            sections.Add(sec);
        }

        try
        {
            foreach (var tool in ToolItemManager.GetAllTools())
            {
                if (tool == null) continue;

                bool unlocked = false;
                try { unlocked = tool.IsUnlocked; } catch { }
                if (!unlocked)
                {
                    // A tool that is still hidden would be a spoiler; one that
                    // is merely not yet found is shown dimmed.
                    bool hidden = true;
                    try { hidden = !tool.IsUnlockedNotHidden; } catch { }
                    if (hidden) continue;
                }

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
                    Dim = !unlocked,
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
        if (_choosingCrest)
        {
            if (g.Type != DsGestureType.Tap) return;
            Vector2 point = DsPresentation.ToLayout(g.Position);
            for (int i = 0; i < _crestCells.Count; i++)
            {
                if (!_crestCells[i].Hit.Contains(point)) continue;
                ChooseCrest(_crestCells[i].Id);
                return;
            }
            return;
        }

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
            if (rt == null || tool == null) continue;

            // Slots live inside the crest panel, which is itself placed at
            // (LeftX, 16) within the body, so their layout position is the sum.
            float sx = LeftX + rt.anchoredPosition.x;
            float sy = bodyTop + 16f - rt.anchoredPosition.y;
            float size = rt.sizeDelta.x;
            if (p.x >= sx && p.x <= sx + size && p.y >= sy && p.y <= sy + size)
            {
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
    // A full-body overlay rather than a rearrangement of the tab. Showing and
    // hiding one opaque panel is a single toggle; hiding the tab's own parts
    // would mean reaching into the grid's clip, its detail pane and the two
    // gutter rules and putting them all back afterwards.

    class CrestCell
    {
        public Rect Hit;              // layout space
        public string Id;
        public Image Art;
        public RectTransform Root;
    }

    RectTransform _picker;
    readonly List<CrestCell> _crestCells = new List<CrestCell>();
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
        }
        _picker.SetAsLastSibling();

        for (int i = 0; i < _crestCells.Count; i++)
            if (_crestCells[i].Root != null) UnityEngine.Object.Destroy(_crestCells[i].Root.gameObject);
        _crestCells.Clear();

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

        const int columns = 4;
        const float cellW = 280f, cellH = 205f, gap = 14f;
        float left = (panelW - (columns * cellW + (columns - 1) * gap)) * 0.5f;
        float top = 24f;
        int shown = 0;

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

            int col = shown % columns, row = shown / columns;
            float x = left + col * (cellW + gap);
            float y = top + row * (cellH + gap);
            if (y + cellH > bodyH) break;
            shown++;

            var cell = DsWidgets.Rect(_picker, "crest" + shown);
            DsWidgets.Place(cell, x, y, cellW, cellH);

            Sprite art = null;
            try { art = crest.CrestSprite; } catch { }
            var img = DsWidgets.Icon(cell, "art", art, Color.white);
            DsWidgets.Place(img.rectTransform, (cellW - 140f) * 0.5f, 0f, 140f, 140f);

            bool equipped = crest.name == current;
            string label = "";
            try { label = DsText(crest.DisplayName); } catch { }
            var name = DsWidgets.Label(cell, "name", label, DsTheme.RowSize,
                                       equipped ? DsTheme.Accent : DsTheme.Ink,
                                       TmpAlign.Center);
            if (name != null) DsWidgets.Place(name.rectTransform, 0f, 146f, cellW, 52f);

            _crestCells.Add(new CrestCell
            {
                Id = crest.name,
                Art = img,
                Root = cell,
                // The picker fills the body, so its space is the body's.
                Hit = new Rect(x, DsLayout.Current.Body.y + y, cellW, cellH),
            });
        }
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
    /// Offer the tool under the cursor, but only at a bench.
    ///
    /// That is the game's own rule -- InventoryItemToolManager.CanChangeEquips
    /// is `playerData.atBench` with a cheat override -- and the v3 notes ask for
    /// the button to be absent rather than greyed when it does not apply. This
    /// differs from USE on the Inventory tab deliberately: a consumable you
    /// cannot drink yet is still a consumable, and saying so is useful, while a
    /// tool away from a bench is simply not something the panel can act on.
    /// </summary>
    public void CollectActions(List<DsAction> into)
    {
        if (!AtBench()) return;

        // While choosing, the only thing to offer is a way out. Equipping a
        // tool into a crest you are in the middle of replacing is not a useful
        // thing to be able to do.
        if (_choosingCrest)
        {
            into.Add(new DsAction("BACK", () => ShowCrestPicker(false), false,
                                  DsActionPlace.Pane));
            return;
        }

        into.Add(new DsAction("CREST", () => ShowCrestPicker(true), false,
                              DsActionPlace.Pane));

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
            try { return ToolItemManager.GetToolByName(key); } catch { return null; }
        }
        return _socketTool;
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

            int target = -1;
            if (tool.Type == ToolItemType.Skill)
            {
                // Skills go in the one neutral skill slot, not in any of them.
                for (int i = 0; i < crest.Slots.Length; i++)
                {
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
                    if (crest.Slots[i].Type != tool.Type) continue;
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