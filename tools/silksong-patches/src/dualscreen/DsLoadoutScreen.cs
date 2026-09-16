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

public class DsLoadoutScreen : IDsScreen
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

    /// <summary>The shared grid, so the tool list and its detail pane are one thing.</summary>
    DsIconGrid Grid => _grid;

    public string Id => "loadout";
    public string Title => "CREST";
    public bool Available => DsGameData.InGame;

    public void Build(RectTransform host)
    {
        _host = host;
        float bodyH = DsLayout.Current.Body.height;
        float colH = bodyH - 36f;

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
                    new Rect(DetailX, 16f, DetailW, colH), detailRule: false,
                    capReach: ListX - (LeftX + LeftW + ListX) * 0.5f);

        Refresh(force: true);
    }

    public void OnShow() { Refresh(force: true); }
    public void OnHide() { }

    public void Tick(float dt)
    {
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
                AddSlot(x, y, SlotIcon, DsTheme.ToolTypeColor(info.Type), tool);
            }
        }

        AddExtraSlots(12f, 82f);
    }

    // Tools equipped OUTSIDE the crest's own ring. The game draws these to one
    // side of the crest, and they were simply missing here -- they live in
    // PlayerData.ExtraToolEquips, a separate slot list from the crest's, which
    // is easy to miss because ToolCrest.Slots looks like the whole story.
    //
    // Tucked into the top-left corner: near enough to read as part of the
    // loadout, far enough from the centre not to collide with the artwork.
    void AddExtraSlots(float x, float top)
    {
        List<string> names = null;
        try { names = PlayerData.instance.ExtraToolEquips.GetValidNames(); } catch { }
        if (names == null || names.Count == 0) return;

        float y = top;
        for (int i = 0; i < names.Count; i++)
        {
            ToolCrestsData.SlotData data;
            try { data = PlayerData.instance.ExtraToolEquips.GetData(names[i]); } catch { continue; }
            if (string.IsNullOrEmpty(data.EquippedTool)) continue;

            ToolItem tool = null;
            try { tool = ToolItemManager.GetToolByName(data.EquippedTool); } catch { }
            if (tool == null) continue;

            AddSlot(x, y, ExtraIcon, DsTheme.ToolTypeColor(tool.Type), tool);
            y += ExtraIcon + 14f;
            if (y > _crestH - ExtraIcon) break;
        }
    }

    void AddSlot(float x, float y, float size, Color ringColour, ToolItem tool)
    {
        var holder = DsWidgets.Rect(_crestBox, "slot" + _slotRects.Count);
        DsWidgets.Place(holder, x, y, size, size);

        // Round, because the game's slots are round and a square frame around a
        // round icon reads as a different kind of thing. The ring's colour says
        // what may go in the slot, which is useful even when it is empty.
        var ring = DsWidgets.Circle(holder, "ring", ringColour);
        DsWidgets.Stretch(ring.rectTransform);
        var inner = DsWidgets.Circle(ring.rectTransform, "inner", DsTheme.Panel);
        DsWidgets.Stretch(inner.rectTransform, 5f);

        Sprite icon = null;
        try { icon = tool != null ? tool.InventorySpriteBase : null; } catch { }
        var img = DsWidgets.Icon(inner.rectTransform, "icon", icon, Color.white);
        // Inset enough that a square-ish icon stays inside the circle.
        DsWidgets.Stretch(img.rectTransform, size * 0.17f);

        _slots.Add(img);
        _slotTools.Add(tool);
        _slotRects.Add(holder);
    }

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
}
#endif