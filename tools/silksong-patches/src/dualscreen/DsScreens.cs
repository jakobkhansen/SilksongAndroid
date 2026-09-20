// The second screen's content, one file per screen.
//
// Every screen here is READ-ONLY: it enumerates the game's data through the
// public static managers and draws it. Nothing writes player state, which is
// why none of this can corrupt a save. Writing (equip a crest, socket a tool)
// is deliberately held back until the read-only half is proven.
//
// The enumeration APIs are the reason this is straightforward. Each pane's data
// is reachable from a static method on a manager that is alive from boot, so
// there is no Addressables handling and no waiting for the player to open the
// game's own menu:
//
//     ToolItemManager.GetAllTools()          / GetAllCrests()
//     CollectableItemManager.GetCollectedItems()
//     EnemyJournalManager.GetAllEnemies()    / GetKilledEnemies()
//     QuestManager.GetAllQuests()            / GetActiveQuests()
//
// Individual sprites may still be absent -- the game loads art through
// Addressables -- so every icon is allowed to be null and DsWidgets draws a
// placeholder instead.
//
// Data is re-read on a slow timer rather than every frame. Nothing in an
// inventory changes at 120 Hz, and the grid only re-lays-out when told.

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Shared plumbing: a grid, a refresh timer, and error tolerance.</summary>
public abstract class DsGridScreen : IDsScreen
{
    protected readonly DsIconGrid Grid = new DsIconGrid();
    readonly List<DsItem> _buffer = new List<DsItem>();
    float _nextRefresh;

    public abstract string Id { get; }
    public abstract string Title { get; }
    // Five across gives ~220 px cells on a full-width panel: large enough to
    // read an icon at a glance and to hit with a thumb without aiming.
    protected virtual int Columns => 5;
    protected virtual float RefreshSeconds => 1.0f;
    /// <summary>Left edge of the grid column; negative means full width.</summary>
    protected virtual float GridLeft => -1f;
    protected virtual float GridWidth => -1f;
    /// <summary>Where the detail pane goes; zero width means under the grid.</summary>
    protected virtual Rect DetailRect => default(Rect);
    /// <summary>
    /// Rule the detail pane off along its top edge. False for a pane that is a
    /// full-height column of its own, whose boundary runs down the gutter.
    /// </summary>
    protected virtual bool DetailRule => true;
    /// <summary>
    /// How far left of the grid the section cap reaches, to meet the gutter rule.
    /// Zero for a grid with no rule beside it.
    /// </summary>
    protected virtual float CapReach => 0f;

    /// <summary>The shared grid, so a screen can act on what is selected.</summary>
    protected DsIconGrid Selection => Grid;

    public virtual void Build(RectTransform host)
    {
        Grid.EmptyMessage = EmptyMessage;
        Grid.Build(host, Columns, GridLeft, GridWidth, DetailRect, DetailRule, CapReach);
        Refresh();
    }

    protected virtual string EmptyMessage => "Nothing here yet";

    public virtual void OnShow() { Refresh(); }
    public virtual void OnHide() { }

    /// <summary>Screens have nothing to show until a save is actually loaded.</summary>
    public virtual bool Available => DsGameData.InGame;

    public virtual void Tick(float dt)
    {
        if (Time.unscaledTime >= _nextRefresh)
        {
            _nextRefresh = Time.unscaledTime + RefreshSeconds;
            Refresh();
        }
        Grid.Tick();
    }

    public virtual void OnGesture(DsGesture g) { Grid.OnGesture(g); }

    void Refresh()
    {
        _buffer.Clear();

        // The gate, not a guard. Outside gameplay the managers are half-built
        // and PlayerData is a default instance, so every answer would be wrong
        // -- and one of these accessors can perturb the game's own state when
        // called at the wrong moment. See DsGameData.
        if (!DsGameData.InGame)
        {
            Grid.EmptyMessage = DsGameData.IdleReason;
            Grid.SetItems(_buffer);
            return;
        }

        Grid.EmptyMessage = EmptyMessage;
        try
        {
            var sections = CollectSections();
            if (sections != null) { Grid.SetSections(sections); return; }
            Collect(_buffer);
        }
        catch (Exception e)
        {
            // A manager that is not up yet is normal during a scene load, and
            // is not worth taking the screen down for.
            Debug.LogWarning("[DualScreen] " + Id + ": " + e.Message);
        }
        Grid.SetItems(_buffer);
    }

    /// <summary>Fill the list with what this screen shows.</summary>
    protected abstract void Collect(List<DsItem> into);

    /// <summary>
    /// Grouped contents, for a screen whose items divide into named runs.
    /// Null means "use Collect", which is what most screens do.
    /// </summary>
    protected virtual List<DsSection> CollectSections() { return null; }

    /// <summary>LocalisedString resolves through the game's own localisation.</summary>
    protected static string Text(TeamCherry.Localization.LocalisedString s)
    {
        try { return s.ToString(); } catch { return ""; }
    }
}

// Tools and Crests used to be two screens here. They are now one purpose-built
// screen, DsLoadoutScreen: the equipped crest with its tools in their slots on
// the left, every tool in three colour groups on the right. A grid of crest
// icons told you nothing you wanted mid-game.
// ── Inventory (collectables) ────────────────────────────────────────────────
//
// This screen is the reason DsGameData exists, and the reason "read-only" is
// not the same as "safe".
//
// The obvious call is CollectableItemManager.GetCollectedItems(). It is public,
// static, and returns exactly what we want -- and calling it from here
// CORRUPTED THE GAME'S OWN INVENTORY. Two side effects, neither visible from
// the signature:
//
//   * It writes a STATIC cache (_collectedItemCache) that the game's own
//     inventory pane reads back. We were filling the game's cache.
//   * It calls IsInHiddenMode(), which reads
//     HeroController.instance.Config.ForceBareInventory and INCREMENTS the
//     shared Version whenever the answer changes. Our poll runs on a timer,
//     including while the hero does not exist, so the answer flipped and the
//     game's cache was invalidated -- and repopulated from our context, with
//     the bare-inventory list. Observed: the panel showed two items and the
//     game's real inventory was wrong.
//
// So this reads the save data directly instead. PlayerData.Collectables is the
// authoritative store, GetItemByName is a pure lookup into the master list, and
// neither touches the manager's cache or its version.

public class DsInventoryScreen : DsGridScreen, IDsActionBar
{
    // Three columns: what Hornet IS, what she is CARRYING, and what the thing
    // under the cursor is.
    //
    // The description used to be a band along the bottom spanning both columns.
    // It read as a caption on the whole screen rather than on the selection,
    // and it cost the grid the bottom 190 px of a panel that has no vertical
    // room to spare -- so the items were laid out wide and short, which is the
    // wrong shape for a list that grows.
    //
    // Giving it a column of its own turns that around: the grid is now narrow
    // and FULL HEIGHT, which is the shape a collection actually has, and it
    // scrolls when there are more items than fit rather than being sized to the
    // worst case. The rules run down the two gutters, so each column is bounded
    // by the thing beside it instead of by a line under everything.
    const float LeftX   = 20f;    // Hornet: 20 .. 440
    const float LeftW   = 420f;
    const float GridX   = 470f;   // items:  470 .. 870
    const float GridW   = 400f;
    const float DetailX = 900f;   // prose:  900 .. 1220
    const float DetailW = 320f;

    readonly DsHornetPanel _hornet = new DsHornetPanel();

    public override string Id => "inventory";
    public override string Title => "INVENTORY";
    // Three across, agreed with the designer. In a 400 px column that is a
    // 127 px cell against four's 92, which is both what the art wants and a
    // comfortable thumb target -- four came to about 7 mm on this panel.
    //
    // Three also means the collection no longer fits: seven relics and the
    // consumables below them run past the bottom of the column. That is the
    // intended shape rather than a problem to design around -- the grid is a
    // scrolling one (DsIconGrid owns the same drag-to-scroll the Journal and
    // Tasks lists use), and it was only ever incidental that the old wider
    // layout happened to fit everything at once.
    protected override int Columns => 3;
    protected override string EmptyMessage => "Nothing collected yet";
    protected override float GridLeft => GridX;
    protected override float GridWidth => GridW;

    // A column, not the bottom of one: the gutter rule beside it is already the
    // boundary, so it takes no rule across its top.
    protected override bool DetailRule => false;

    // Half the gutter, which is how far the section cap has to reach left to sit
    // on the rule running down it. Derived, so moving a column cannot leave the
    // cap hanging in mid-air.
    protected override float CapReach => GridX - (LeftX + LeftW + GridX) * 0.5f;

    // The bottom of the description column, kept for the USE button.
    //
    // Reserved whether or not there is anything to put in it, which is
    // deliberate: USE comes and goes with the selection, and a description that
    // reflowed every time the cursor moved between a rosary and a relic would
    // be doing something far more distracting than leaving a gap. A short
    // description -- which is nearly all of them -- leaves the space empty
    // anyway, and that is where the button appears.
    static readonly float ActionBand = DsActionBar.PaneBand(1);

    static float ColumnHeight => DsLayout.Current.Body.height - DsTheme.Pad * 2f;

    protected override Rect DetailRect =>
        new Rect(DetailX, DsTheme.Pad, DetailW, ColumnHeight - ActionBand);

    /// <summary>
    /// USE sits under the prose about the thing it would consume. See
    /// DsActions: this is a control that acts on the SELECTION, so it belongs
    /// with the selection rather than in the corner with the screen's own
    /// controls.
    /// </summary>
    public Rect ActionPane =>
        DsLayout.Current.InBody(
            new Rect(DetailX, DsTheme.Pad + ColumnHeight - ActionBand, DetailW, ActionBand));

    public override void Build(RectTransform host)
    {
        float colH = DsLayout.Current.Body.height - DsTheme.Pad * 2f;

        // Full height now. The character column no longer stops short to leave
        // room for a description band underneath it.
        _hornet.Build(host, LeftX, DsTheme.Pad, LeftW, colH);

        // One rule per boundary, down the middle of each gutter. Both run the
        // full height of the body, because all three columns now do.
        DsWidgets.VRule(host, "split-items", (LeftX + LeftW + GridX) * 0.5f,
                        DsTheme.Pad, colH);
        DsWidgets.VRule(host, "split-detail", (GridX + GridW + DetailX) * 0.5f,
                        DsTheme.Pad, colH);

        // Both halves explain themselves in the same place, and the one cursor
        // crosses between them: tapping the needle walks it out of the grid and
        // over to the character column.
        _hornet.OnSelect = (name, desc, layoutRect) =>
        {
            Grid.ShowDetail(name, desc);
            float bodyY = DsLayout.Current.Body.y;
            // The widget's exact box. No growing: the cursor insets by a
            // fraction of what it is framing, so it is already tight on a small
            // counter and on the mask alike.
            Grid.SetExternalTarget(
                new Rect(layoutRect.x, layoutRect.y - bodyY, layoutRect.width, layoutRect.height),
                DsGameArt.SelectionCursor().GlowColor, name);
        };
        base.Build(host);
    }

    public override void Tick(float dt)
    {
        base.Tick(dt);
        _hornet.Refresh();
    }

    public override void OnGesture(DsGesture g)
    {
        if (g.Type == DsGestureType.Tap &&
            _hornet.OnTap(DsPresentation.ToLayout(g.Position))) return;
        base.OnGesture(g);
    }

    protected override void Collect(List<DsItem> into)
    {
        // Superseded by CollectSections; the base class calls this only for
        // screens that do not group.
    }

    // The game splits collectables into relics and consumables, with a divider
    // before the consumables and none before the first group
    // (InventoryItemCollectableManager.GetGridSections). Grouping on the same
    // CollectableItem.IsConsumable() means the split matches the game's rather
    // than being a second opinion about what counts as a consumable.
    protected override List<DsSection> CollectSections()
    {
        var relics = new DsSection(null, DsTheme.InkDim);
        // The divider alone says "these are different"; the game does not
        // label its consumable group either.
        var consumables = new DsSection(" ", DsTheme.InkDim);

        // The master list is the ground truth, and reading it is pure:
        // GetAllCollectables() returns masterList directly. The game's own pane
        // goes through GetCollectedItems(), which additionally fills a shared
        // cache, can bump a shared version, and calls ReportPreviouslyCollected()
        // on everything it returns -- i.e. it MARKS ITEMS AS SEEN. A second
        // screen must not do that just by being open.
        var mgr = CollectableItemManager.Instance;
        if (mgr == null) return new List<DsSection> { relics, consumables };

        var all = mgr.GetAllCollectables();
        if (all == null) return new List<DsSection> { relics, consumables };

        foreach (var item in all)
        {
            if (item == null) continue;

            bool visible = false;
            try { visible = item.IsVisible; } catch { }
            if (!visible) continue;

            int amount = 0;
            try { amount = item.CollectedAmount; } catch { }

            Sprite icon = null;
            string title = item.name, desc = "";
            try { icon = item.GetIcon(CollectableItem.ReadSource.Inventory); } catch { }
            try { title = item.GetDisplayName(CollectableItem.ReadSource.Inventory); } catch { }
            try { desc = item.GetDescription(CollectableItem.ReadSource.Inventory); } catch { }

            bool consumable = false;
            try { consumable = item.IsConsumable(); } catch { }

            (consumable ? consumables : relics).Items.Add(new DsItem
            {
                Key = item.name,
                Name = title,
                Description = desc,
                Icon = icon,
                Tint = Color.white,
                Dim = false,
                Badge = amount > 1 ? amount.ToString() : null,
            });
        }

        return new List<DsSection> { relics, consumables };
    }

    // ── USE ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Offer USE only while the selection is something that can be drunk right
    /// now, which is the game's own test: InventoryItemCollectable shows its
    /// consume prompt under exactly `IsConsumable() && CanConsumeRightNow()`.
    /// The second half is the one that matters -- a Rosary Cluster is always
    /// consumable and is not usable at full health, and CanConsumeRightNow is
    /// what knows the difference.
    /// </summary>
    public void CollectActions(List<DsAction> into)
    {
        var item = SelectedCollectable();
        if (item == null) return;

        bool consumable = false, now = false;
        try { consumable = item.IsConsumable(); } catch { }
        if (!consumable) return;
        try { now = item.CanConsumeRightNow(); } catch { }

        // Shown disabled rather than withdrawn when it cannot be used. The item
        // IS a thing you drink; that it would do nothing at this moment is
        // worth saying, and it is what the game says too -- it draws the same
        // prompt greyed (forceDisabled) instead of removing it.
        into.Add(new DsAction("USE", now ? (Action)(() => Consume(item)) : null, !now,
                              DsActionPlace.Pane));
    }

    CollectableItem SelectedCollectable()
    {
        string key = Selection.SelectedKey;
        if (string.IsNullOrEmpty(key) || !DsGameData.InGame) return null;

        var mgr = CollectableItemManager.Instance;
        if (mgr == null) return null;
        try
        {
            // The master list, as CollectSections uses: pure, and it does not
            // mark anything as seen just by being read.
            var all = mgr.GetAllCollectables();
            if (all == null) return null;
            foreach (var item in all)
                if (item != null && item.name == key) return item;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Drink it, which is the whole of what InventoryItemCollectable's consume
    /// coroutine actually DOES to the save: the response, then the item.
    ///
    /// Everything else in that routine is presentation -- the hold, the shake,
    /// the sounds, the fade -- and belongs to a pane we are not drawing. The
    /// two calls below are taken from the end of ConsumeRoutine, in its order:
    /// the response first, because TakeItemOnConsume decides separately whether
    /// the item is spent at all, and some are not.
    /// </summary>
    static void Consume(CollectableItem item)
    {
        try
        {
            item.ConsumeItemResponse();
            bool take = true;
            try { take = item.TakeItemOnConsume; } catch { }
            if (take) item.Take(1, showCounter: false);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DualScreen] use failed: " + e.Message);
        }
    }
}
// ── Journal ─────────────────────────────────────────────────────────────────
//
// Moved to DsJournalScreen.cs. A grid of large portraits was a wall of art with
// nothing to read; the creature you have selected wants the space, and the rest
// are only a way of choosing it.

// ── Tasks (quests) ──────────────────────────────────────────────────────────
//
// Moved to DsTasksScreen.cs. A four-column icon grid was the wrong shape for
// quests twice over: they are read by name rather than recognised by icon, and
// the game groups them into main quests, other accepted ones and finished ones.
#endif
