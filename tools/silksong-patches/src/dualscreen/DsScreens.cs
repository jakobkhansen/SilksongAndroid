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

    public virtual void Build(RectTransform host)
    {
        Grid.EmptyMessage = EmptyMessage;
        Grid.Build(host, Columns, GridLeft, GridWidth, DetailRect, DetailRule);
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

public class DsInventoryScreen : DsGridScreen
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
    // Three across, not four. In a 400 px column four cells are 92 px, which is
    // under 7 mm on this panel -- smaller than a fingertip and smaller than the
    // art wants. Three gives 127 px and the icons room to be recognised.
    protected override int Columns => 3;
    protected override string EmptyMessage => "Nothing collected yet";
    protected override float GridLeft => GridX;
    protected override float GridWidth => GridW;

    // A column, not the bottom of one: the gutter rule beside it is already the
    // boundary, so it takes no rule across its top.
    protected override bool DetailRule => false;

    protected override Rect DetailRect =>
        new Rect(DetailX, DsTheme.Pad, DetailW,
                 DsLayout.Current.Body.height - DsTheme.Pad * 2f);

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

        // Both halves explain themselves in the same place.
        _hornet.OnSelect = (name, desc) => Grid.ShowDetail(name, desc);
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
