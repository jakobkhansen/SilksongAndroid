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
    public static void Forget() { _inventory = null; _cursor = null; _nextInventorySearch = 0f; }

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
                int key = (int)slot.Type;
                if (!_slotSymbols.ContainsKey(key) || _slotSymbols[key] == null)
                    _slotSymbols[key] = sprite;
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
