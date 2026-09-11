// DsWidgets — the small set of things every screen is built from.
//
// Deliberately thin. This is not a UI framework: it is the four or five shapes
// the second screen actually needs (a rect, a label, an icon, a bordered
// panel), with the layer and the font already correct so no screen has to
// remember either. Everything returns the RectTransform, because that is what
// layout code wants next.
//
// Nothing here creates a raycast target. The second screen has no EventSystem
// and no GraphicRaycaster -- we own every rect on it and hit-test our own
// rectangles -- so leaving raycastTarget on would only cost layout work.

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;
using TmpText = TMProOld.TextMeshProUGUI;
using TmpAlign = TMProOld.TextAlignmentOptions;

public static class DsWidgets
{
    public static RectTransform Rect(Transform parent, string name)
    {
        var go = new GameObject(name) { layer = DsPresentation.LAYER };
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    /// <summary>A solid block of colour.</summary>
    public static Image Box(Transform parent, string name, Color color)
    {
        var rt = Rect(parent, name);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = DsTheme.White;
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    /// <summary>A solid circle. Slots are round in the game.</summary>
    public static Image Circle(Transform parent, string name, Color color)
    {
        var rt = Rect(parent, name);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = DsTheme.Disc;
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    /// <summary>
    /// A filled rect with a one-pixel-ish border, drawn as five boxes.
    ///
    /// This is for CONTROLS, not for sections. Sections on this panel are
    /// divided by a rule (see HRule/VRule) rather than boxed, which means a box
    /// is no longer decoration -- it is the panel's only remaining signal that
    /// something can be tapped, and it should be spent on nothing else.
    /// </summary>
    public static RectTransform Panel(Transform parent, string name, Color fill, Color edge, float thickness = 2f)
    {
        var root = Rect(parent, name);
        var bg = Box(root, "fill", fill);
        Stretch(bg.rectTransform);

        Edge(root, "e-top",    new Vector2(0f, 1f), new Vector2(1f, 1f), thickness, true,  edge);
        Edge(root, "e-bottom", new Vector2(0f, 0f), new Vector2(1f, 0f), thickness, true,  edge);
        Edge(root, "e-left",   new Vector2(0f, 0f), new Vector2(0f, 1f), thickness, false, edge);
        Edge(root, "e-right",  new Vector2(1f, 0f), new Vector2(1f, 1f), thickness, false, edge);
        return root;
    }

    static void Edge(Transform parent, string name, Vector2 aMin, Vector2 aMax,
                     float thickness, bool horizontal, Color color)
    {
        var rt = Box(parent, name, color).rectTransform;
        rt.anchorMin = aMin;
        rt.anchorMax = aMax;
        rt.sizeDelta = horizontal ? new Vector2(0f, thickness) : new Vector2(thickness, 0f);
        rt.anchoredPosition = Vector2.zero;
    }

    // ── rules ───────────────────────────────────────────────────────────────
    //
    // The panel divides its sections with a line between them rather than a box
    // around each. The difference is not only taste: a box has four sides and
    // three of them border nothing, so a screen with a character panel, a grid
    // and a description pane was drawing twelve edges to express two
    // boundaries. It read as a form rather than as a page, and on a 9 cm screen
    // the frames were competing with the art inside them.
    //
    // Both take the rule's CENTRE LINE, because that is what a gutter has: the
    // caller knows the two columns are 540 and 560, and 550 is the answer. It
    // also means the thickness can change in one place without moving anything.

    /// <summary>A rule across a boundary. <paramref name="y"/> is its centre line.</summary>
    public static RectTransform HRule(Transform parent, string name, float x, float y, float length)
    {
        var rt = Box(parent, name, DsTheme.Rule).rectTransform;
        Place(rt, x, y - DsTheme.RuleThickness * 0.5f, length, DsTheme.RuleThickness);
        return rt;
    }

    /// <summary>A rule down a gutter. <paramref name="x"/> is its centre line.</summary>
    public static RectTransform VRule(Transform parent, string name, float x, float y, float length)
    {
        var rt = Box(parent, name, DsTheme.Rule).rectTransform;
        Place(rt, x - DsTheme.RuleThickness * 0.5f, y, DsTheme.RuleThickness, length);
        return rt;
    }

    /// <summary>
    /// Text in one of the game's fonts. Returns null if none has been found
    /// yet, which is a legitimate state early in startup.
    ///
    /// <paramref name="display"/> selects the caps face used for tabs and
    /// titles. THE RULE: use it only for strings you have written yourself and
    /// know to be uppercase. Trajan's lowercase glyphs are broken -- an "l"
    /// renders as a stub with a black foot -- so any label carrying text FROM
    /// THE GAME (an item name, a crest name, a zone name) must leave this false
    /// and take the body face, which is what the game uses for the same strings
    /// in its own UI.
    ///
    /// This is worth being blunt about because it does not look like a bug when
    /// it happens: a zone header reading "She??wood" reads as a broken font, or
    /// a broken atlas, or a missing glyph range, and the actual cause is one
    /// boolean at the call site.
    /// </summary>
    public static TmpText Label(Transform parent, string name, string text, float size,
                                Color color, TmpAlign align = TmpAlign.Left, bool display = false)
    {
        var rt = Rect(parent, name);
        var t = rt.gameObject.AddComponent<TmpText>();
        var font = display ? DsTheme.Display : DsTheme.Body;
        if (font != null) t.font = font;
        t.text = text;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.raycastTarget = false;
        t.enableWordWrapping = true;
        t.OverflowMode = TMProOld.TextOverflowModes.Truncate;
        return t;
    }

    /// <summary>
    /// An icon. A null sprite is normal -- the game loads art through
    /// Addressables and it may not be resident yet -- so it draws a faint
    /// placeholder block instead of nothing, which makes a missing icon
    /// visible rather than mysterious.
    ///
    /// useSpriteMesh matters here. The game's icons are packed in atlases, and
    /// a UI Image drawn as a plain quad samples the sprite's whole rectangle --
    /// including the padding, which in an atlas is the NEIGHBOURING sprite.
    /// That showed up as fragments of other icons in the corners of every cell.
    /// Drawing the sprite's own tight mesh samples only the sprite.
    /// </summary>
    public static Image Icon(Transform parent, string name, Sprite sprite, Color tint)
    {
        var rt = Rect(parent, name);
        var img = rt.gameObject.AddComponent<Image>();
        img.type = Image.Type.Simple;
        img.useSpriteMesh = true;
        img.preserveAspect = true;
        img.sprite = sprite;
        // Nothing to show means show nothing. This used to fall back to a grey
        // block, which reads as a deliberate "empty" only in a grid of slots --
        // everywhere else it is indistinguishable from a bug, and it hid two:
        // an empty crest socket looked filled with something unnameable, and a
        // currency icon whose art failed to convert looked identical to one
        // that had simply not loaded yet. Callers that do want a placeholder
        // set one explicitly.
        img.color = sprite != null ? tint : Color.clear;
        img.raycastTarget = false;
        return img;
    }

    /// <summary>
    /// Set an icon's sprite and centre what you actually SEE, fitted in maxW x maxH.
    ///
    /// Icons are drawn with useSpriteMesh, which samples only the sprite's own
    /// tight geometry and so cannot pick up a neighbouring atlas entry. The cost
    /// is that a trimmed mesh is not centred within its sprite's rect: a symbol
    /// with uneven transparent margins sits visibly to one side of a rect that
    /// is itself perfectly centred.
    ///
    /// Sprite.bounds is that offset, in local units relative to the pivot, so
    /// the fix is to shift the rect back by it rather than to change how the
    /// sprite is drawn. Sizing the rect to the sprite's own aspect first makes
    /// preserveAspect a no-op, which keeps the arithmetic honest -- otherwise
    /// the drawn size is not the rect size and the offset would be scaled wrong.
    /// </summary>
    public static void FitCentred(Image img, Sprite sprite, float maxW, float maxH)
    {
        if (img == null || sprite == null) return;

        img.sprite = sprite;
        img.color = Color.white;

        var full = sprite.rect;
        float aspect = full.width / Mathf.Max(full.height, 1f);
        float w = maxW, h = maxH;
        if (w / Mathf.Max(h, 1f) > aspect) w = h * aspect; else h = w / Mathf.Max(aspect, 0.0001f);

        var rt = img.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(w, h);

        // The mesh's centre, as a fraction of the full sprite rect.
        float ppu = sprite.pixelsPerUnit;
        if (ppu <= 0f) ppu = 100f;
        Vector2 unitsFull = new Vector2(full.width / ppu, full.height / ppu);
        Vector3 c = sprite.bounds.center;
        Vector2 frac = new Vector2(
            unitsFull.x > 0f ? c.x / unitsFull.x : 0f,
            unitsFull.y > 0f ? c.y / unitsFull.y : 0f);

        rt.anchoredPosition = new Vector2(-frac.x * w, -frac.y * h);
    }

    public static void Stretch(RectTransform rt, float pad = 0f)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(pad, pad);
        rt.offsetMax = new Vector2(-pad, -pad);
    }

    public static Image CursorCorner(RectTransform parent, string name, Sprite sprite,
                                     Vector2 anchor, bool rotate, float size, float inset)
    {
        var img = Icon(parent, name, sprite, Color.white);
        var rt = img.rectTransform;
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(size, size);
        rt.anchoredPosition = new Vector2(anchor.x < 0.5f ? inset : -inset,
                                          anchor.y < 0.5f ? inset : -inset);
        if (rotate) rt.localRotation = Quaternion.Euler(0f, 0f, 180f);
        img.gameObject.SetActive(false);
        return img;
    }

    public static void Place(RectTransform rt, UnityEngine.Rect bounds)
    {
        Place(rt, bounds.x, bounds.y, bounds.width, bounds.height);
    }

    /// <summary>Place a rect by top-left corner, in panel pixels with y down.</summary>
    public static void Place(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
    }

    public static void SetActive(Component c, bool on)
    {
        if (c != null && c.gameObject.activeSelf != on) c.gameObject.SetActive(on);
    }
}
#endif
