// DsGameArt — showing the game's own inventory widgets, by its own rules.
//
// Three attempts at this failed, and the reason is worth recording because it
// is a general lesson about this codebase: the inventory's left column is not
// data that can be read, it is a SCENE that the game arranges. Each widget is a
// set of child GameObjects, and the game switches them on and off:
//
//   * `Needle` has Nail1..Nail5, and InventoryItemNail activates
//     displayStates[nailUpgrades].
//   * `Heart Pieces` has Backboard and Pieces 1..4, and InventoryItemHeartPieces
//     activates every piece up to the count -- CUMULATIVELY, so two shards means
//     two objects visible -- and mirrors the first one when the count is 1.
//   * `Spool Pieces` picks empty/half/full from silkSpoolParts and silkMax.
//   * The skills under `Radial Layout` each carry an InventoryItemConditional
//     with a PlayerDataTest that says whether they are unlocked.
//
// Copying "the sprite" out of any of these therefore cannot work: a mask with
// two shards is not one sprite, it is three stacked and one of them flipped.
// Reading the prefab's CURRENT activation does not work either, because the
// game only arranges it when the pane opens -- so before the player has ever
// opened the inventory, or after switching saves, it shows someone else's
// state or nothing at all.
//
// So this does what the game does: it calls the widget's own UpdateState -- the
// same private method the pane calls on open -- and then MIRRORS whatever
// objects are active, with their relative positions, scales and flips intact.
// The arrangement is the game's; only the drawing is ours. That is why the
// pieces line up, why the counts are right, and why a locked skill stays
// hidden without us reimplementing a single rule.

#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public static class DsGameArt
{
    /// <summary>One sprite in a composed widget, placed relative to the whole.</summary>
    public struct Piece
    {
        public Sprite Sprite;
        /// <summary>Position and size within the widget's bounds, 0..1.</summary>
        public Rect Norm;
        public bool FlipX;
        public Color Colour;
    }

    /// <summary>A widget: its art, and what the game says it is.</summary>
    public class Widget
    {
        public readonly List<Piece> Pieces = new List<Piece>();
        public string Name, Desc;
        /// <summary>Aspect of the composed art (width / height), for fitting.</summary>
        public float Aspect = 1f;
        /// <summary>Where the game places this, relative to its layout's centre.</summary>
        public Vector2 Dir;
        public bool Ok { get { return Pieces.Count > 0; } }
    }

    const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    static Transform _inventory;
    static float _nextInventorySearch;

    static Transform Inventory
    {
        get
        {
            if (_inventory != null) return _inventory;
            if (Time.unscaledTime < _nextInventorySearch) return null;
            _nextInventorySearch = Time.unscaledTime + 1f;
            try
            {
                var lists = Resources.FindObjectsOfTypeAll<InventoryPaneList>();
                for (int i = 0; i < lists.Length; i++)
                    if (lists[i] != null && lists[i].gameObject.scene.IsValid())
                    { _inventory = lists[i].transform; break; }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[DsGameArt] inventory lookup failed: " + e.Message);
            }
            return _inventory;
        }
    }

    /// <summary>Drop cached references — the scene changed, or the save did.</summary>
    public static void Forget()
    {
        _inventory = null; _cursor = null; _nextInventorySearch = 0f;
        _questFluer = null; _questSectionRule = null; _nextFluerSearch = 0f;
        _questCounter = null; _nextCounterSearch = 0f;
        _toolDividers.Clear(); _nextDividerSearch = 0f;
        _unlockItem = null; _nextUnlockSearch = 0f;
        _lockedSocket = null; _nextLockedSearch = 0f;
        _journalFrames = default(JournalFrames);
        _journalRule = default(JournalRule);
        _journalLocked = null; _nextJournalSearch = 0f;
        if (_silhouette != null) { UnityEngine.Object.Destroy(_silhouette); _silhouette = null; }
    }

    public static Sprite TabIcon(InventoryPaneList.PaneTypes type)
    {
        var root = Inventory;
        if (root == null) return null;
        var list = root.GetComponent<InventoryPaneList>();
        var pane = list != null ? list.GetPane(type) : null;
        return pane != null ? pane.ListIcon : null;
    }

    // ── the widgets ─────────────────────────────────────────────────────────

    public static Widget Needle() { return Build<InventoryItemNail>(); }
    public static Widget MaskShards() { return Build<InventoryItemHeartPieces>(); }
    public static Widget SpoolPieces() { return Build<InventoryItemSpoolPieces>(); }
    public static Widget SilkCore() { return Build<InventoryItemSpool>(); }

    /// <summary>
    /// The silk skills, in the ring order the game uses, and only the ones the
    /// player has -- decided by each skill's own PlayerDataTest, which is the
    /// game's rule rather than a guess at it.
    /// </summary>
    // The ring's order, clockwise from the top, by the game's own object names.
    // RadialLayoutUI positions these at runtime and has not necessarily run
    // when we look -- before the pane is first opened every child still sits at
    // the origin -- so the order is stated here rather than derived from
    // transforms that may all be zero.
    static readonly string[] RingOrder =
    {
        "Needolin",       // Needolin
        "Sprint",         // Swift Step
        "Harpoon Dash",   // Clawline
        "Eva Heal",       // Sylphsong
        "Super Jump",     // Silk Soar
        "Wall Jump",      // Cling Grip
    };

    public static List<Widget> Skills()
    {
        var found = new List<Widget>();
        var radial = FindByName("Radial Layout");
        if (radial == null) return found;

        var byName = new Dictionary<string, Widget>();
        var extras = new List<Widget>();

        for (int i = 0; i < radial.childCount; i++)
        {
            var child = radial.GetChild(i);
            var cond = child.GetComponent<InventoryItemConditional>();
            if (cond == null) continue;

            // Locked skills are a spoiler. The test is public and is exactly
            // what the game consults.
            if (!IsUnlocked(cond)) continue;

            var w = Compose(child);
            if (!w.Ok) continue;

            w.Name = child.name;
            try
            {
                // InventoryItemConditional returns empty strings while its own
                // object is inactive, so fall back to the object's name.
                string n = cond.DisplayName;
                if (!string.IsNullOrEmpty(n)) w.Name = n;
                w.Desc = cond.Description;
            }
            catch { }

            if (!byName.ContainsKey(child.name)) byName[child.name] = w;
            else extras.Add(w);
        }

        for (int i = 0; i < RingOrder.Length; i++)
        {
            Widget w;
            if (byName.TryGetValue(RingOrder[i], out w)) { found.Add(w); byName.Remove(RingOrder[i]); }
        }
        // Anything the game gains later still shows, just after the known ones.
        foreach (var kv in byName) found.Add(kv.Value);
        found.AddRange(extras);
        return found;
    }

    /// <summary>The ring the skills sit on.</summary>
    public static Widget IconRing()
    {
        var t = FindByName("Icon Ring");
        return t != null ? Compose(t) : new Widget();
    }

    // ── the selection cursor ────────────────────────────────────────────────

    /// <summary>
    /// The game's own selection cursor: two corner brackets and a glow.
    ///
    /// InventoryCursor names four corner transforms, but only two carry art --
    /// the others resolved to nothing and rendered as blank squares. The
    /// bottom-right is the top-left sprite ROTATED, which is why it is stored
    /// once and turned by the caller rather than fetched twice.
    /// </summary>
    public class Cursor
    {
        public Sprite Corner, Glow;
        /// <summary>
        /// InventoryCursor's defaultGlowColor -- the tint it restores whenever
        /// the thing selected does not ask for one of its own. Taken from the
        /// live renderer rather than guessed, so the light behind an ordinary
        /// item is the colour the game lights it.
        /// </summary>
        public Color GlowColor = new Color(1f, 0.94f, 0.72f, 0.30f);
        public bool Ok { get { return Corner != null; } }
    }

    static Cursor _cursor;

    public static Cursor SelectionCursor()
    {
        if (_cursor != null && _cursor.Ok) return _cursor;
        _cursor = new Cursor();

        try
        {
            var all = Resources.FindObjectsOfTypeAll<InventoryCursor>();
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i];
                if (c == null || !c.gameObject.scene.IsValid()) continue;

                // Whichever corner actually has a sprite; they are the same
                // piece of art at different rotations.
                _cursor.Corner = FieldSprite(c, "topLeft")
                              ?? FieldSprite(c, "bottomRight")
                              ?? FieldSprite(c, "topRight")
                              ?? FieldSprite(c, "bottomLeft");

                var glow = typeof(InventoryCursor).GetField("backGlow", Priv);
                var sr = glow != null ? glow.GetValue(c) as SpriteRenderer : null;
                if (sr != null)
                {
                    _cursor.Glow = sr.sprite;
                    _cursor.GlowColor = sr.color;
                }

                if (_cursor.Ok) break;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[DualScreen] cursor art unavailable: " + e.Message);
        }
        return _cursor;
    }

    // ── crest slot symbols ──────────────────────────────────────────────────

    /// <summary>
    /// The symbol the game draws in an EMPTY crest slot, one per tool type.
    ///
    /// InventoryToolCrestSlot.Sprite returns its serialized slotTypeSprite
    /// whenever nothing is equipped, tinted with the slot's own type colour, so
    /// each type has its own shape. A locked slot is the SAME sprite in grey at
    /// four-fifths scale -- see SpriteTint there -- rather than a shape of its
    /// own, which is why there is nothing separate to look up for it.
    /// </summary>
    static readonly Dictionary<int, Sprite> _slotSymbols = new Dictionary<int, Sprite>();
    static bool _slotSymbolsSearched;

    public static Sprite CrestSlotSymbol(ToolItemType type)
    {
        Sprite found;
        if (_slotSymbols.TryGetValue((int)type, out found) && found != null) return found;
        if (_slotSymbolsSearched && _slotSymbols.Count > 0) return null;

        try
        {
            var field = typeof(InventoryToolCrestSlot).GetField("slotTypeSprite", Priv);
            if (field == null) { _slotSymbolsSearched = true; return null; }

            var slots = Resources.FindObjectsOfTypeAll<InventoryToolCrestSlot>();
            for (int i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                if (slot == null) continue;
                var sprite = field.GetValue(slot) as Sprite;
                if (sprite == null) continue;

                int key = SlotTypeOf(slot);
                if (key < 0) continue;
                if (!_slotSymbols.ContainsKey(key) || _slotSymbols[key] == null)
                {
                    _slotSymbols[key] = sprite;
                    Debug.Log("[DsGameArt] crest slot symbol " + (ToolItemType)key + ": " +
                              sprite.name + " from '" + slot.name + "' (slotInfo says " +
                              SafeType(slot) + ")");
                }
            }
            if (_slotSymbols.Count > 0) _slotSymbolsSearched = true;
        }
        catch (System.Exception e)
        {
            _slotSymbolsSearched = true;
            Debug.LogWarning("[DualScreen] crest slot symbols unavailable: " + e.Message);
        }

        _slotSymbols.TryGetValue((int)type, out found);
        return found;
    }

    /// <summary>
    /// Which type a slot object's symbol belongs to, by its NAME.
    ///
    /// Not by slot.Type, which is the obvious answer and is wrong here.
    /// That reads `slotInfo`, a serialized field the pane fills in when it
    /// binds a slot to the crest being shown -- so every slot object that has
    /// not been bound, and FindObjectsOfTypeAll returns plenty of those,
    /// reports the enum's default of Red. The first such slot claimed Red with
    /// whatever glyph it happened to carry, and three of the crest's sockets
    /// drew the blue "()" because a Defend Slot got there first.
    ///
    /// The names are stable and say what the prefabs are: the locked socket
    /// this was first noticed on is 'Defend Slot(Clone)'. Anything unrecognised
    /// is skipped rather than guessed at -- a missing symbol is a slot drawn
    /// bare, which is honest; a wrong one is a lie about what fits there.
    /// </summary>
    static int SlotTypeOf(InventoryToolCrestSlot slot)
    {
        string n = slot.name;
        if (string.IsNullOrEmpty(n)) return -1;
        n = n.ToLowerInvariant();
        if (n.Contains("attack")) return (int)ToolItemType.Red;
        if (n.Contains("defend") || n.Contains("defence") || n.Contains("defense"))
            return (int)ToolItemType.Blue;
        if (n.Contains("explore")) return (int)ToolItemType.Yellow;
        if (n.Contains("skill") || n.Contains("neutral")) return (int)ToolItemType.Skill;
        return -1;
    }

    static string SafeType(InventoryToolCrestSlot slot)
    {
        try { return slot.Type.ToString(); } catch { return "?"; }
    }

    // ── the quest list's divider ────────────────────────────────────────────

    static Sprite _questFluer;
    static Sprite _questSectionRule;
    static float _nextFluerSearch;

    /// <summary>
    /// The ornament the game sets either side of COMPLETED in its own quest
    /// list, taken off the live pane.
    ///
    /// It has to be read from the scene rather than shipped with us, and the
    /// reason is worth recording: in a decompile of the game this sprite
    /// reference is the placeholder GUID `0000000deadbeef15deadf00d0000000` --
    /// the same unresolvable id that stands in for the quest icons and the
    /// button glyphs -- so there is no PNG on disk to lift. The object graph
    /// still tells us exactly where the art lives, which is enough:
    /// InventoryItemQuestManager.completedHeading is a Transform holding a
    /// `Title Text` with `Fluer Left` and `Fluer Right` under it, and those
    /// two SpriteRenderers are the ornament. Left and right are the same art
    /// mirrored, so one sprite is all there is to find.
    ///
    /// Null is normal and not an error: the pane is built when the game's own
    /// inventory first opens, and before that there is nothing to read. The
    /// caller falls back to a plain rule.
    /// </summary>
    public static Sprite QuestDivider()
    {
        if (_questFluer != null) return _questFluer;
        if (!SearchHeadings()) return null;
        return _questFluer;
    }

    /// <summary>
    /// The rule the game puts between the prioritised quest and the rest.
    ///
    /// This is NOT the fluer above. InventoryItemQuestManager.currentHeading
    /// holds a single child called `Spacer` carrying one wide, thin sprite --
    /// 7.53 x 0.34 world units, a 22:1 line with an ornate knot worked into its
    /// middle. One piece of art, drawn whole.
    ///
    /// Worth a warning for anyone who goes looking: in the decompiled prefab
    /// that renderer reads `m_Enabled: 0`, which says the object draws nothing.
    /// On the running game it plainly does. The serialised state is the
    /// template's, not the live one, so the prefab can say a thing is invisible
    /// while the pane in front of you is drawing it -- which is exactly how
    /// this rule came to be reimplemented twice from a screenshot instead of
    /// simply being read.
    /// </summary>
    public static Sprite QuestSectionRule()
    {
        if (_questSectionRule != null) return _questSectionRule;
        if (!SearchHeadings()) return null;
        return _questSectionRule;
    }

    /// <summary>
    /// Look for both headings' art in one pass. False when the search is
    /// rate-limited, so the callers can return their cached answer.
    /// </summary>
    static bool SearchHeadings()
    {
        if (_questFluer != null && _questSectionRule != null) return true;
        // FindObjectsOfTypeAll walks every loaded object, so it is rate-limited
        // the way the inventory lookup is rather than run per rebuild.
        if (Time.unscaledTime < _nextFluerSearch) return false;
        _nextFluerSearch = Time.unscaledTime + 2f;

        if (_questFluer == null) _questFluer = HeadingSprite("completedHeading");
        if (_questSectionRule == null) _questSectionRule = HeadingSprite("currentHeading");
        return true;
    }

    /// <summary>The first sprite under one of the quest manager's headings.</summary>
    static Sprite HeadingSprite(string field)
    {
        try
        {
            var f = typeof(InventoryItemQuestManager).GetField(field, Priv);
            if (f == null) return null;

            var managers = Resources.FindObjectsOfTypeAll<InventoryItemQuestManager>();
            for (int i = 0; i < managers.Length; i++)
            {
                if (managers[i] == null) continue;
                var heading = f.GetValue(managers[i]) as Transform;
                if (heading == null) continue;

                // Include inactive, and note that a DISABLED renderer is still
                // returned here -- which matters, because the section rule's is
                // disabled in the serialised prefab. We only want the sprite
                // reference, not the renderer's opinion about drawing it.
                var renderers = heading.GetComponentsInChildren<SpriteRenderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                    if (renderers[r] != null && renderers[r].sprite != null)
                        return renderers[r].sprite;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[DualScreen] quest " + field + " art unavailable: " + e.Message);
        }
        return null;
    }

    // ── the quest list's progress counters ──────────────────────────────────

    /// <summary>
    /// The art and the two state colours the game draws a quest's progress
    /// with. Any field may be null; callers fall back to plain shapes.
    /// </summary>
    public class QuestCounter
    {
        /// <summary>The dot, in its done and not-yet-done states.</summary>
        public Sprite DotFilled, DotEmpty;
        /// <summary>
        /// Each state's own colour, which the game MULTIPLIES by the quest's
        /// ProgressBarTint rather than replacing -- see IconCounterItem.TintColor,
        /// `spriteRenderer.color = baseColor.MultiplyElements(tintColor)`. So an
        /// empty dot is dark because its state colour is dark, not because the
        /// quest asked for a dark tint.
        /// </summary>
        public Color FilledColour = Color.white, EmptyColour = Color.white;
        /// <summary>
        /// The material an uncollected symbol is drawn through: `Quest
        /// Silhouette`, whose shader replaces the sprite's colour outright
        /// rather than tinting it. Read from the SYMBOL counter, not the dots.
        /// </summary>
        public Material EmptyMaterial;
        /// <summary>The filled part of a progress bar.</summary>
        public Sprite Bar;

        /// <summary>
        /// The description pane's SYMBOL states, which are a different counter
        /// from the list's dots and are set up differently: the dots carry no
        /// material at all, while the symbols carry `Quest Silhouette` on their
        /// inactive state. Reading the dots' states and hoping is how the
        /// symbols came out as dimmed art instead of flat shapes.
        /// </summary>
        public Color SymbolFilled = Color.white, SymbolEmpty = new Color(0.22f, 0.22f, 0.22f, 1f);
        public Material SymbolEmptyMaterial;

        public bool Ok { get { return DotFilled != null || DotEmpty != null || Bar != null; } }
        /// <summary>Nothing left to look for.</summary>
        public bool Complete
        {
            get
            {
                return DotFilled != null && DotEmpty != null && Bar != null &&
                       SymbolEmptyMaterial != null;
            }
        }
    }

    static QuestCounter _questCounter;
    static float _nextCounterSearch;

    /// <summary>
    /// The dot and bar art the game's own quest list draws progress with.
    ///
    /// Same problem as the divider, and the same answer: these sprites are
    /// unresolvable in a decompile, but the object graph that reaches them is
    /// not. InventoryItemQuest holds an `iconCounter` and a `progressBar`, and
    /// the dot's two states live on the IconCounter's `templateItem` -- the
    /// prefab it clones one of per dot.
    ///
    /// The search keeps going until all three pieces are found rather than
    /// stopping at the first quest item, because a given template need not have
    /// every one of them assigned: the main-quest template and the ordinary one
    /// are different prefabs and only one may carry a bar.
    /// </summary>
    public static QuestCounter QuestCounterArt()
    {
        if (_questCounter != null && _questCounter.Complete) return _questCounter;
        if (Time.unscaledTime < _nextCounterSearch) return _questCounter;
        _nextCounterSearch = Time.unscaledTime + 2f;

        var found = _questCounter ?? new QuestCounter();
        try
        {
            var items = Resources.FindObjectsOfTypeAll<InventoryItemQuest>();
            for (int i = 0; i < items.Length && !found.Complete; i++)
            {
                var item = items[i];
                if (item == null) continue;

                var counter = PrivateField(item, typeof(InventoryItemQuest), "iconCounter") as IconCounter;
                if (counter != null)
                {
                    var template = PrivateField(counter, typeof(IconCounter), "templateItem") as IconCounterItem;
                    if (template != null)
                    {
                        ReadDotState(template, "activeState",
                                     ref found.DotFilled, ref found.FilledColour, ref _ignored);
                        ReadDotState(template, "inactiveState",
                                     ref found.DotEmpty, ref found.EmptyColour, ref found.EmptyMaterial);
                    }
                }

                if (found.Bar == null)
                {
                    var slider = PrivateField(item, typeof(InventoryItemQuest), "progressBar") as ImageSlider;
                    if (slider != null)
                    {
                        var img = PrivateField(slider, typeof(ImageSlider), "image") as UnityEngine.UI.Image;
                        if (img != null && img.sprite != null) found.Bar = img.sprite;
                    }
                }
            }

            // The symbols are a DIFFERENT counter: QuestItemDescription's
            // rangeDisplay, whose template carries the silhouette material the
            // list's dots do not have.
            if (found.SymbolEmptyMaterial == null)
            {
                var panes = Resources.FindObjectsOfTypeAll<QuestItemDescription>();
                for (int i = 0; i < panes.Length && found.SymbolEmptyMaterial == null; i++)
                {
                    if (panes[i] == null) continue;
                    var range = PrivateField(panes[i], typeof(QuestItemDescription), "rangeDisplay") as IconCounter;
                    if (range == null) continue;
                    var template = PrivateField(range, typeof(IconCounter), "templateItem") as IconCounterItem;
                    if (template == null) continue;

                    Sprite unused = null;
                    Material none = null;
                    ReadDotState(template, "activeState", ref unused, ref found.SymbolFilled, ref none);
                    ReadDotState(template, "inactiveState", ref unused, ref found.SymbolEmpty,
                                 ref found.SymbolEmptyMaterial);
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[DualScreen] quest counter art unavailable: " + e.Message);
        }

        _questCounter = found;
        return found;
    }

    /// <summary>
    /// One of IconCounterItem's two DisplayStates.
    ///
    /// DisplayState is a PRIVATE nested struct, so it cannot be named here and
    /// is read field by field off the boxed value. Its own fields are public
    /// within it, which is why this asks for public members of a private type.
    /// </summary>
    static void ReadDotState(IconCounterItem item, string field,
                             ref Sprite sprite, ref Color colour, ref Material material)
    {
        try
        {
            var f = typeof(IconCounterItem).GetField(field, Priv);
            if (f == null) return;
            object state = f.GetValue(item);
            if (state == null) return;

            var type = state.GetType();
            var spriteField = type.GetField("Sprite");
            var colourField = type.GetField("Color");
            var materialField = type.GetField("Material");

            if (spriteField != null)
            {
                var s = spriteField.GetValue(state) as Sprite;
                if (s != null) sprite = s;
            }
            if (colourField != null && colourField.FieldType == typeof(Color))
                colour = (Color)colourField.GetValue(state);
            if (materialField != null)
            {
                var m = materialField.GetValue(state) as Material;
                if (m != null) material = m;
            }
        }
        catch { }
    }

    /// <summary>Somewhere for a state's material to go when we do not want it.</summary>
    static Material _ignored;

    static Material _silhouette;

    /// <summary>
    /// The material the game draws an UNCOLLECTED symbol through.
    ///
    /// This is not a dim tint, and the difference is the whole look: the
    /// material is `Quest Silhouette`, whose shader is Sprites_Default-ColorFlash
    /// with `_FlashAmount` at 1 -- the sprite's colour is REPLACED by
    /// `_FlashColor`, leaving only its alpha. A tint multiplies instead, so a
    /// gold item tinted grey stays a dark gold item with all its shading; run
    /// through this it becomes a flat featureless shape, which is what says
    /// "not yet" at a glance.
    ///
    /// IconCounterItem carries `setFlashColour`, and pushes the state's own
    /// colour into `_FlashColor` per item. Every uncollected symbol shares one
    /// colour, so one shared instance does for all of them rather than a
    /// material per icon.
    /// </summary>
    public static Material QuestSilhouette()
    {
        if (_silhouette != null) return _silhouette;

        var art = QuestCounterArt();
        if (art == null || art.SymbolEmptyMaterial == null) return null;

        try
        {
            var m = new Material(art.SymbolEmptyMaterial) { hideFlags = HideFlags.HideAndDontSave };
            if (m.HasProperty("_FlashAmount")) m.SetFloat("_FlashAmount", 1f);
            if (m.HasProperty("_FlashColor")) m.SetColor("_FlashColor", art.SymbolEmpty);
            _silhouette = m;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[DualScreen] quest silhouette unavailable: " + e.Message);
        }
        return _silhouette;
    }

    static object PrivateField(object owner, System.Type type, string name)
    {
        try
        {
            var f = type.GetField(name, Priv);
            return f != null ? f.GetValue(owner) : null;
        }
        catch { return null; }
    }

    // ── the tool list's section dividers ────────────────────────────────────

    /// <summary>A divider as the game draws it: its art, and the tint it uses.</summary>
    public struct Divider
    {
        public Sprite Sprite;
        public Color Colour;
        public bool Ok { get { return Sprite != null; } }
    }

    static readonly Dictionary<int, Divider> _toolDividers = new Dictionary<int, Divider>();
    static float _nextDividerSearch;

    /// <summary>
    /// The rule the game's own tool list puts between two groups.
    ///
    /// It is not a line with a label: it is one piece of art per tool type --
    /// a hairline with that type's glyph worked into the middle of it -- which
    /// is why this screen's groups no longer carry the words ATTACK and
    /// SURVIVAL. The glyph says it, in the colour the game uses for that type,
    /// and it says it in the game's own hand.
    ///
    /// InventoryItemToolManager.listSectionHeaders is the array, indexed by
    /// ToolItemType's RAW value rather than by display order -- the game orders
    /// the sections with an [EnumOrder] attribute and indexes the headers with
    /// the enum itself. Getting that wrong is subtle: every divider appears,
    /// each just carries the wrong group's glyph.
    ///
    /// Read off the live pane, like everything else here: the sprite is the
    /// placeholder GUID in a decompile. The renderer's own tint comes with it,
    /// so a white glyph the game colours and a glyph that is already coloured
    /// both come out right.
    /// </summary>
    public static Divider ToolSectionDivider(ToolItemType type)
    {
        Divider found;
        if (_toolDividers.TryGetValue((int)type, out found) && found.Ok) return found;
        if (Time.unscaledTime < _nextDividerSearch) return found;
        _nextDividerSearch = Time.unscaledTime + 2f;

        try
        {
            var field = typeof(InventoryItemToolManager)
                .GetField("listSectionHeaders", Priv);
            if (field == null) return found;

            var managers = Resources.FindObjectsOfTypeAll<InventoryItemToolManager>();
            for (int m = 0; m < managers.Length; m++)
            {
                if (managers[m] == null) continue;
                var headers = field.GetValue(managers[m]) as System.Array;
                if (headers == null) continue;

                for (int i = 0; i < headers.Length; i++)
                {
                    var holder = headers.GetValue(i) as Component;
                    if (holder == null) continue;

                    // Include disabled renderers. The headers are switched off
                    // in the serialised prefab and the pane turns them on as it
                    // builds, so a search that respected `enabled` would find
                    // nothing at all before the player first opens the tools.
                    var sr = holder.GetComponent<SpriteRenderer>()
                          ?? holder.GetComponentInChildren<SpriteRenderer>(true);
                    if (sr == null || sr.sprite == null) continue;

                    _toolDividers[i] = new Divider { Sprite = sr.sprite, Colour = sr.color };
                    Debug.Log("[DsGameArt] tool divider " + i + ": " + sr.sprite.name +
                              " " + sr.sprite.rect.width + "x" + sr.sprite.rect.height +
                              " colour=" + sr.color + " enabled=" + sr.enabled);
                }
                if (_toolDividers.Count > 0) break;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[DualScreen] tool dividers unavailable: " + e.Message);
        }

        _toolDividers.TryGetValue((int)type, out found);
        return found;
    }


    static Sprite _lockedSocket;
    static float _nextLockedSearch;

    /// <summary>
    /// The mark the game shows where a socket has not been opened yet: a thick
    /// ring with a narrow slit cut through it at top and bottom, and a filled
    /// dot at its centre.
    ///
    /// The game's own sprite, by name, like every other piece of art here.
    ///
    /// Finding the name was the whole difficulty. It is NOT reachable through a
    /// field -- PlaySlotStateAnims plays `_lockedAnim` on the slot's animator
    /// and the clip swaps the renderer's sprite -- and it is not called what
    /// the rest of the family is called: every other slot glyph is
    /// `UI_tool_slot_*`, so a dump filtered on `UI_` missed it entirely and
    /// several plausible neighbours were tried and rejected on screen.
    /// `UI_tool_slot_locked_fill` is the bare ring with no dot;
    /// `UI_tool_slot_lock_glows_inner*` have the right shape but are the soft
    /// glow layers. It was pinned down in the end by dumping the whole Inventory
    /// atlas page (`sprite_dump`, see DsProbe), spotting the sharp glyph on it,
    /// and matching a sprite whose rect covered that point.
    ///
    /// `locked_sprite=<name>` overrides it, which is how a candidate can be
    /// tried for the cost of an app restart rather than a ten-minute rebuild.
    /// </summary>
    public static Sprite LockedSocketSymbol()
    {
        if (_lockedSocket != null) return _lockedSocket;
        if (Time.unscaledTime < _nextLockedSearch) return null;
        _nextLockedSearch = Time.unscaledTime + 2f;

        string want = DsConfig.Str("locked_sprite", LockedSocketSprite);
        if (string.IsNullOrEmpty(want)) return null;

        try
        {
            var all = Resources.FindObjectsOfTypeAll<Sprite>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null || all[i].name != want) continue;
                _lockedSocket = all[i];
                Debug.Log("[DsGameArt] locked socket glyph: '" + want + "' " +
                          all[i].rect.width + "x" + all[i].rect.height);
                break;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[DualScreen] locked socket glyph unavailable: " + e.Message);
        }
        return _lockedSocket;
    }

    /// <summary>The sprite the locked mark is taken from, unless overridden.</summary>
    const string LockedSocketSprite = "Tool_slot_lock_ring";


    ///
    /// Asked of the game rather than looked up by name: it is a serialised
    /// field on the tool pane (`slotUnlockItem`), so whatever the game spends
    /// is what we spend, and a save with none of them answers zero through the
    /// same CollectedAmount the game's own CanUnlockSlot reads.
    /// </summary>
    public static CollectableItem SlotUnlockItem()
    {
        if (_unlockItem != null) return _unlockItem;
        if (Time.unscaledTime < _nextUnlockSearch) return null;
        _nextUnlockSearch = Time.unscaledTime + 2f;

        try
        {
            var managers = Resources.FindObjectsOfTypeAll<InventoryItemToolManager>();
            for (int i = 0; i < managers.Length && _unlockItem == null; i++)
            {
                if (managers[i] == null) continue;
                _unlockItem = managers[i].SlotUnlockItem;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[DualScreen] slot unlock item unavailable: " + e.Message);
        }
        return _unlockItem;
    }

    static CollectableItem _unlockItem;
    static float _nextUnlockSearch;

    // ── the journal ─────────────────────────────────────────────────────────

    /// <summary>The two rings the journal draws around a creature's portrait.</summary>
    public struct JournalFrames
    {
        /// <summary>Seen, but the notes are not finished.</summary>
        public Sprite Standard;
        /// <summary>Killed enough of them for the hunter's own commentary.</summary>
        public Sprite Complete;
        public bool Ok { get { return Standard != null || Complete != null; } }
    }

    /// <summary>The mask the journal sets between a description and its notes.</summary>
    public struct JournalRule
    {
        public Sprite Symbol;
        public bool Ok { get { return Symbol != null; } }
    }

    static JournalFrames _journalFrames;
    static JournalRule _journalRule;
    static string _journalLocked;
    static float _nextJournalSearch;

    /// <summary>
    /// The journal's two rings.
    ///
    /// Which one a creature gets is JournalEntryItem.Setup's own rule --
    /// `record.KillCount >= record.KillsRequired` -- and the two are different
    /// pieces of art rather than one tinted twice: the complete ring carries
    /// flourishes the standard one does not, which is the whole of how the
    /// grid tells you at a glance which notes are finished.
    ///
    /// They are GameObjects on the item (`standardFrame` and `completeFrame`),
    /// switched on and off, so both are read whatever the one live item
    /// happens to be showing.
    /// </summary>
    public static JournalFrames Frames()
    {
        if (_journalFrames.Ok) return _journalFrames;
        SearchJournal();
        return _journalFrames;
    }

    /// <summary>The hunter's mask, set between a description and her notes.</summary>
    public static JournalRule Rule()
    {
        if (_journalRule.Ok) return _journalRule;
        SearchJournal();
        return _journalRule;
    }

    /// <summary>
    /// The game's own "defeat N more" line, with its {0} still in it.
    ///
    /// Taken from JournalItemManager.notesLockedText rather than written here,
    /// so it arrives in the player's language and in the game's words --
    /// `string.Format(notesLockedText, killsRequired - killCount)` is exactly
    /// what the game does with it.
    /// </summary>
    public static string JournalNotesLocked()
    {
        if (!string.IsNullOrEmpty(_journalLocked)) return _journalLocked;
        SearchJournal();
        return _journalLocked;
    }

    static void SearchJournal()
    {
        if (Time.unscaledTime < _nextJournalSearch) return;
        _nextJournalSearch = Time.unscaledTime + 2f;

        try
        {
            if (!_journalFrames.Ok)
            {
                var entries = Resources.FindObjectsOfTypeAll<JournalEntryItem>();
                for (int i = 0; i < entries.Length && !_journalFrames.Ok; i++)
                {
                    if (entries[i] == null) continue;
                    _journalFrames.Standard = FrameSprite(entries[i], "standardFrame");
                    _journalFrames.Complete = FrameSprite(entries[i], "completeFrame");
                }
            }

            var managers = Resources.FindObjectsOfTypeAll<JournalItemManager>();
            for (int i = 0; i < managers.Length; i++)
            {
                var mgr = managers[i];
                if (mgr == null) continue;

                if (string.IsNullOrEmpty(_journalLocked))
                {
                    var f = typeof(JournalItemManager).GetField("notesLockedText", Priv);
                    if (f != null)
                    {
                        object ls = f.GetValue(mgr);
                        if (ls != null)
                        {
                            string s = ls.ToString();
                            // An unresolved key comes back as !!Sheet/KEY!! --
                            // see DsTasksScreen. Not worth showing.
                            if (!string.IsNullOrWhiteSpace(s) &&
                                !(s.StartsWith("!!") && s.EndsWith("!!")))
                                _journalLocked = s;
                        }
                    }
                }

                // The mask is static scenery on the pane rather than a field on
                // anything, so it is found by name under the manager's root.
                var root = mgr.transform;
                while (root.parent != null) root = root.parent;

                if (!_journalRule.Ok) _journalRule.Symbol = NamedSprite(root, "hunter_symbol");

                if (_journalRule.Ok && !string.IsNullOrEmpty(_journalLocked)) break;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[DualScreen] journal art unavailable: " + e.Message);
        }
    }

    static Sprite FrameSprite(JournalEntryItem item, string field)
    {
        try
        {
            var f = typeof(JournalEntryItem).GetField(field, Priv);
            var go = f != null ? f.GetValue(item) as GameObject : null;
            if (go == null) return null;
            var sr = go.GetComponent<SpriteRenderer>()
                  ?? go.GetComponentInChildren<SpriteRenderer>(true);
            return sr != null ? sr.sprite : null;
        }
        catch { return null; }
    }

    /// <summary>The sprite on the first descendant with this exact name.</summary>
    static Sprite NamedSprite(Transform root, string name)
    {
        try
        {
            var all = root.GetComponentsInChildren<SpriteRenderer>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].sprite != null && all[i].name == name)
                    return all[i].sprite;
        }
        catch { }
        return null;
    }

    // ── map markers ─────────────────────────────────────────────────────────

    static readonly Dictionary<int, Sprite> _markerIcons = new Dictionary<int, Sprite>();
    static bool _markerIconsSearched;

    /// <summary>
    /// The pin art for a marker type, taken from the game's own templates.
    ///
    /// GameMap keeps mapMarkerTemplates[] indexed by MapMarkerMenu.MarkerTypes
    /// -- the very prefabs it clones onto the map -- so the icon in our strip is
    /// the thing the player will see land on it, rather than a second opinion
    /// about what a pin looks like.
    /// </summary>
    public static Sprite MarkerIcon(int type)
    {
        Sprite found;
        if (_markerIcons.TryGetValue(type, out found) && found != null) return found;
        if (_markerIconsSearched && _markerIcons.Count > 0) return null;

        try
        {
            var maps = Resources.FindObjectsOfTypeAll<GameMap>();
            var field = typeof(GameMap).GetField("mapMarkerTemplates", Priv);
            if (field == null) { _markerIconsSearched = true; return null; }

            for (int m = 0; m < maps.Length; m++)
            {
                var templates = field.GetValue(maps[m]) as GameObject[];
                if (templates == null) continue;
                for (int i = 0; i < templates.Length; i++)
                {
                    if (templates[i] == null) continue;
                    var sr = templates[i].GetComponentInChildren<SpriteRenderer>(true);
                    if (sr != null && sr.sprite != null && !_markerIcons.ContainsKey(i))
                        _markerIcons[i] = sr.sprite;
                }
                if (_markerIcons.Count > 0) break;
            }
            if (_markerIcons.Count > 0) _markerIconsSearched = true;
        }
        catch (System.Exception e)
        {
            _markerIconsSearched = true;
            Debug.LogWarning("[DualScreen] marker art unavailable: " + e.Message);
        }

        _markerIcons.TryGetValue(type, out found);
        return found;
    }

    static Sprite FieldSprite(Component owner, string field)
    {
        try
        {
            var f = owner.GetType().GetField(field, Priv);
            var t = f != null ? f.GetValue(owner) as Transform : null;
            if (t == null) return null;
            var sr = t.GetComponent<SpriteRenderer>() ?? t.GetComponentInChildren<SpriteRenderer>(true);
            return sr != null ? sr.sprite : null;
        }
        catch { return null; }
    }

    static bool IsUnlocked(InventoryItemConditional cond)
    {
        try
        {
            if (cond.Test == null) return true;
            return cond.Test.IsFulfilled;
        }
        catch { return false; }
    }

    // ── the mechanism ───────────────────────────────────────────────────────

    static Widget Build<T>() where T : Component
    {
        var w = new Widget();
        var root = Inventory;
        if (root == null) return w;

        var item = root.GetComponentInChildren<T>(true);
        if (item == null) return w;

        // Ask the widget to arrange itself for the CURRENT save, exactly as the
        // pane does when it opens. Without this the art is whatever the last
        // save left behind, or nothing at all on a fresh boot.
        Refresh(item);

        var composed = Compose(item.transform);
        w.Pieces.AddRange(composed.Pieces);

        var sel = item as InventoryItemSelectable;
        if (sel != null)
        {
            try { w.Name = sel.DisplayName; w.Desc = sel.Description; } catch { }
        }
        return w;
    }

    // The widgets do not agree on what their refresh method is called:
    // InventoryItemNail and InventoryItemHeartPieces have UpdateState, while
    // InventoryItemSpool overrides UpdateDisplay -- and it is UpdateDisplay
    // that runs `hearts[i].SetActive(silkRegenMax > i)`, which is the whole
    // question of how many silk hearts are lit. Calling only UpdateState left
    // every heart node switched on, so a save with one heart showed three.
    static readonly string[] RefreshMethods = { "UpdateState", "UpdateDisplay" };

    static void Refresh(Component c)
    {
        for (int i = 0; i < RefreshMethods.Length; i++)
        {
            try
            {
                // Walk the hierarchy: a private method declared on a base class
                // is not returned by GetMethod on the derived type.
                for (var t = c.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                {
                    var m = t.GetMethod(RefreshMethods[i], Priv);
                    if (m == null || m.GetParameters().Length != 0) continue;
                    m.Invoke(c, null);
                    break;
                }
            }
            catch { }
        }

        // Several pieces are gated by a PlayerDataTestResponse, which only
        // evaluates when its object is enabled -- so before the pane has been
        // opened they are all switched on. Asking each one to evaluate does
        // what enabling it would have done, and nothing else.
        try
        {
            var gates = c.GetComponentsInChildren<PlayerDataTestResponse>(true);
            var eval = typeof(PlayerDataTestResponse).GetMethod("Evaluate", Priv);
            if (eval != null)
                for (int i = 0; i < gates.Length; i++)
                    if (gates[i] != null) eval.Invoke(gates[i], null);
        }
        catch { }
    }

    /// <summary>
    /// Mirror a subtree's active sprites, keeping their relative arrangement.
    ///
    /// This is what makes two mask shards look like two mask shards: the game
    /// stacks several objects, flips one, and the result only reads correctly
    /// if the positions and flips come along.
    /// </summary>
    static Widget Compose(Transform root)
    {
        var w = new Widget();
        if (root == null) return w;

        var renderers = root.GetComponentsInChildren<SpriteRenderer>(true);
        var live = new List<SpriteRenderer>();
        for (int i = 0; i < renderers.Length; i++)
        {
            var sr = renderers[i];
            if (sr == null || sr.sprite == null) continue;
            if (!sr.gameObject.activeInHierarchy && !ActiveUnder(sr.transform, root)) continue;
            // "New item" orbs are a HUD affordance, not part of the artwork.
            if (sr.name.IndexOf("Orb", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
            // Several pieces are gated by a PlayerDataTestResponse that only
            // runs when the object is enabled -- so before the pane has been
            // opened they are all switched on, which is why every silk dot
            // showed regardless of how many hearts the save had. Their test is
            // consulted directly instead: reading it changes nothing, where
            // invoking their response would fire UnityEvents at the game.
            if (!TestPasses(sr.transform, root)) continue;
            live.Add(sr);
        }
        if (live.Count == 0) return w;

        // Bounds computed from the SPRITE and the transform, not from
        // Renderer.bounds.
        //
        // Renderer.bounds is only meaningful for a renderer that has been
        // drawn, and this whole subtree is inactive while the pane is closed --
        // so it returned stale or degenerate boxes. That is what squashed the
        // mask into a wide blob, threw one skill outside the ring, and made the
        // needle vanish entirely: with a zero-height box, every normalised
        // position is nonsense. The sprite's own bounds and the transform are
        // valid whether or not anything has ever been rendered.
        var boxes = new List<Rect>(live.Count);
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        for (int i = 0; i < live.Count; i++)
        {
            var sr = live[i];
            var t = sr.transform;
            var scale = t.lossyScale;
            var size = sr.sprite.bounds.size;
            float halfW = Mathf.Abs(size.x * scale.x) * 0.5f;
            float halfH = Mathf.Abs(size.y * scale.y) * 0.5f;

            var centre = t.TransformPoint(sr.sprite.bounds.center);
            var r = new Rect(centre.x - halfW, centre.y - halfH, halfW * 2f, halfH * 2f);
            boxes.Add(r);

            if (r.xMin < minX) minX = r.xMin;
            if (r.yMin < minY) minY = r.yMin;
            if (r.xMax > maxX) maxX = r.xMax;
            if (r.yMax > maxY) maxY = r.yMax;
        }

        float spanX = maxX - minX, spanY = maxY - minY;
        if (spanX <= 0.0001f || spanY <= 0.0001f) return w;
        w.Aspect = spanX / spanY;

        for (int i = 0; i < live.Count; i++)
        {
            var sr = live[i];
            var r = boxes[i];
            w.Pieces.Add(new Piece
            {
                Sprite = sr.sprite,
                // y is flipped: world space counts up, layout counts down.
                Norm = new Rect((r.xMin - minX) / spanX,
                                (maxY - r.yMax) / spanY,
                                r.width / spanX,
                                r.height / spanY),
                FlipX = sr.flipX || sr.transform.lossyScale.x < 0f,
                Colour = new Color(sr.color.r, sr.color.g, sr.color.b, 1f),
            });
        }
        return w;
    }

    // activeInHierarchy is false for the whole inventory while the pane is
    // closed, so activity is judged only as far up as the widget's own root.
    static bool ActiveUnder(Transform t, Transform root)
    {
        while (t != null && t != root)
        {
            if (!t.gameObject.activeSelf) return false;
            t = t.parent;
        }
        return root == null || root.gameObject.activeSelf;
    }

    // Read-only evaluation of the PlayerDataTestResponse gates on a piece and
    // its ancestors. The response's own Evaluate() would fire UnityEvents into
    // the game; the test behind it is just a question about the save.
    static bool TestPasses(Transform t, Transform root)
    {
        while (t != null)
        {
            var resp = t.GetComponent<PlayerDataTestResponse>();
            if (resp != null)
            {
                try
                {
                    var f = typeof(PlayerDataTestResponse).GetField("test", Priv);
                    var test = f != null ? f.GetValue(resp) as PlayerDataTest : null;
                    if (test != null && test.IsDefined && !test.IsFulfilled) return false;
                }
                catch { }
            }
            if (t == root) break;
            t = t.parent;
        }
        return true;
    }

    static Transform FindByName(string name)
    {
        var root = Inventory;
        if (root == null) return null;
        var all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
            if (all[i].name == name) return all[i];
        return null;
    }

    // ── currencies, which are 2D Toolkit rather than Unity sprites ──────────

    static Sprite _rosaryIcon, _shardIcon;

    public static Sprite RosaryIcon()
    {
        if (_rosaryIcon == null) _rosaryIcon = CurrencyIcon("Geo");
        return _rosaryIcon;
    }

    public static Sprite ShardIcon()
    {
        if (_shardIcon == null) _shardIcon = CurrencyIcon("Shards");
        return _shardIcon;
    }

    static Sprite CurrencyIcon(string counterName)
    {
        try
        {
            var counter = FindByName(counterName);
            if (counter == null) return null;
            return Tk2dToSprite(counter.GetComponentInChildren<tk2dSprite>(true));
        }
        catch { return null; }
    }

    /// <summary>
    /// Build a Unity Sprite from a 2D Toolkit sprite.
    ///
    /// tk2d predates Unity's sprite system: it draws a quad with UVs into an
    /// atlas material, so there is no Sprite anywhere to borrow and these icons
    /// came up blank however they were asked for. The atlas texture and the
    /// definition's UVs are enough to make one.
    /// </summary>
    static Sprite Tk2dToSprite(tk2dSprite tk)
    {
        if (tk == null) return null;
        var def = tk.CurrentSprite;
        if (def == null || def.material == null) return null;

        var tex = def.material.mainTexture as Texture2D;
        if (tex == null || def.uvs == null || def.uvs.Length < 4) return null;

        float minU = 1f, maxU = 0f, minV = 1f, maxV = 0f;
        for (int i = 0; i < def.uvs.Length; i++)
        {
            var uv = def.uvs[i];
            if (uv.x < minU) minU = uv.x; if (uv.x > maxU) maxU = uv.x;
            if (uv.y < minV) minV = uv.y; if (uv.y > maxV) maxV = uv.y;
        }

        var rect = new Rect(minU * tex.width, minV * tex.height,
                            (maxU - minU) * tex.width, (maxV - minV) * tex.height);
        if (rect.width < 1f || rect.height < 1f) return null;

        var sprite = Sprite.Create(tex, rect, new Vector2(0.5f, 0.5f), 100f);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }
}
#endif
