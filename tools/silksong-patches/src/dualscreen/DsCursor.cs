// DsCursor — the game's own selection cursor, moved rather than teleported.
//
// Ported from InventoryCursor. Worth reading that class before changing this
// one, because the behaviour it describes is not the obvious one:
//
//   * The cursor is ONE object that moves, not a pair of brackets switched on
//     inside whichever cell is selected. That is the whole difference between
//     a cursor that travels to the thing you picked and a highlight that blinks
//     from one place to another.
//
//   * It is made of corner brackets plus a soft light BEHIND the item -- the
//     `back`/`backGlow` pair in the original. The light takes the size of the
//     thing selected and is tinted per item: the game gives a tool the colour
//     of its type, so a red tool lights red.
//
//   * The move is a plain linear lerp over unscaled time, 0.15 s by default
//     (InventoryCursor.moveTime). No easing. It is short enough that easing
//     would not be seen, and the game does not do it.
//
// The original lerps each corner separately from wherever it happened to be,
// which for two corners moving together is the same thing as lerping the
// RECTANGLE they bound -- so that is what this does, and it keeps the maths in
// one place.
//
// Everything is in "place space": x right, y DOWN from the parent's top-left,
// the space DsWidgets.Place uses. Screens already think in it.
//
// The one structural awkwardness: the light belongs behind the content and the
// brackets in front of it, and a single object cannot be both. So a cursor owns
// two roots and puts one at each end of the parent's child order. Build it
// AFTER the content it decorates, or the brackets will be under it.

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;

public class DsCursor
{
    // InventoryCursor.moveTime. A knob, because the right answer is a matter of
    // feel and a rebuild costs about ten minutes; 0 snaps.
    static float MoveTime => Mathf.Clamp(DsConfig.Int("cursor_move_ms", 150), 0, 2000) / 1000f;

    // The brackets are drawn at this size and sit this far inside the corner of
    // the thing selected. Both are knobs: how big a caret should be is a matter
    // of feel against art it has to frame, and a rebuild costs ten minutes.
    static float ConfiguredCornerPx => Mathf.Clamp(DsConfig.Int("cursor_corner_px", 64), 8f, 240f);

    // ZERO would be what the game does: InventoryCursor puts each corner at
    // boxOffset +/- boxScale/2, i.e. exactly ON the corner of the thing
    // selected. Our cells carry more air around their art than the game's do,
    // so a small inset brings the brackets in against the icon rather than
    // leaving them floating in the gap between cells.
    //
    // Insetting by half the bracket's own size -- which looks like the obvious
    // reading of "inside the corner" -- walks them far too far in: on a 127 px
    // cell it put the bottom-right bracket in the middle of the item's art.
    static float ConfiguredInsetPx => DsConfig.Int("cursor_inset_px", 12);

    float _cornerPx;

    /// <summary>
    /// How far inside the target's corner each bracket's centre sits.
    ///
    /// Adjustable because not every target is a square of art. The Journal's
    /// portraits are CIRCLES, and a circle inscribed in a square leaves its
    /// corners empty -- the nearest ink is about 0.29r along the diagonal, so
    /// brackets placed at the bounding box float off the picture.
    /// </summary>
    public float CornerInset { get; set; }

    // The light is a little larger than the item, the way the game's is: it
    // reads as something lit from behind rather than as a panel behind it.
    const float GlowPad = 12f;

    RectTransform _glowRoot, _cornerRoot;
    Image _glow, _tl, _br;

    Rect _from, _to, _now;
    Color _fromColor, _toColor, _nowColor;
    float _t = 1f;
    bool _shown;
    bool _hasArt;
    // Which thing the cursor is on. The distinction that matters is between
    // "the selection changed" and "the thing selected MOVED" -- a grid scrolls
    // under its own cursor, and the second must not restart the first's
    // animation or the cursor never arrives anywhere.
    string _key;

    /// <summary>The tint the game lights an ordinary item with.</summary>
    public Color DefaultGlow { get; private set; }

    public bool Visible => _shown;

    /// <param name="glowIndex">
    /// Where the light goes in the parent's child order. Zero -- behind
    /// everything -- is right for a screen body, whose children are all content.
    /// It is WRONG for the tab strip, whose first child is an opaque black
    /// backdrop: put the light behind that and the light is simply not there.
    /// </param>
    public void Build(RectTransform parent, int glowIndex = 0)
    {
        DefaultGlow = new Color(1f, 0.94f, 0.72f, 0.30f);
        _cornerPx = ConfiguredCornerPx;
        if (CornerInset <= 0f) CornerInset = ConfiguredInsetPx;
        // Behind the content...
        _glowRoot = DsWidgets.Rect(parent, "cursor-glow");
        DsWidgets.Stretch(_glowRoot);
        _glowRoot.SetSiblingIndex(Mathf.Clamp(glowIndex, 0, parent.childCount - 1));
        _glow = DsWidgets.Icon(_glowRoot, "g", null, Color.clear);

        // ...and in front of it.
        _cornerRoot = DsWidgets.Rect(parent, "cursor");
        DsWidgets.Stretch(_cornerRoot);
        _cornerRoot.SetAsLastSibling();
        _tl = DsWidgets.Icon(_cornerRoot, "c-tl", null, Color.clear);
        _br = DsWidgets.Icon(_cornerRoot, "c-br", null, Color.clear);
        if (_br != null) _br.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 180f);

        SetActive(false);
        Adopt();
    }

    /// <summary>
    /// Take the art once the game has it. Called from Tick: the cursor can be
    /// built before the inventory exists, and a bracket that never arrives
    /// should leave nothing behind rather than a grey square.
    ///
    /// Guarded by _hasArt because DsGameArt rescans every loaded object when it
    /// has not found the cursor yet, which is not something to do per frame.
    /// </summary>
    public void Adopt()
    {
        if (_hasArt) return;
        var art = DsGameArt.SelectionCursor();
        if (!art.Ok) return;
        _hasArt = true;

        DefaultGlow = art.GlowColor;
        // Corners take the sprite's TIGHT mesh. The brackets are filigree with
        // transparent padding around them in the atlas, and drawn as a plain
        // quad that padding is part of the image -- which pushes the visible ink
        // off the corner it is supposed to sit on. This is why the caret looked
        // offset up and to the left.
        SetSprite(_tl, art.Corner, mesh: true, aspect: true);
        SetSprite(_br, art.Corner, mesh: true, aspect: true);
        // The light is the opposite case: STRETCHED to the size of the thing
        // selected, the way the game scales its `back` to the item's box.
        // A tight mesh or a preserved aspect would letterbox it and leave a wide
        // item lit down the middle only.
        SetSprite(_glow, art.Glow, mesh: false, aspect: false);
    }

    static void SetSprite(Image img, Sprite s, bool mesh, bool aspect)
    {
        if (img == null || s == null) return;
        img.sprite = s;
        img.useSpriteMesh = mesh;
        img.preserveAspect = aspect;
    }

    /// <summary>
    /// Put the cursor on <paramref name="target"/>, in the parent's place space.
    ///
    /// <paramref name="key"/> identifies WHAT is selected. A different key is a
    /// new selection and the cursor travels to it; the same key with a moved
    /// rect is the same thing in a new place -- a scrolled grid -- and the
    /// cursor simply follows, which is what InventoryCursor's LateUpdate does.
    ///
    /// The first show snaps rather than flying in from wherever the cursor last
    /// was, which is InventoryCursor's skipNextLerp: a cursor appearing on a
    /// screen has no previous position worth animating from.
    /// </summary>
    public void MoveTo(Rect target, Color? glow, string key)
    {
        Color want = glow ?? DefaultGlow;
        bool newTarget = key != _key;
        bool snap = !_shown || MoveTime <= 0f;

        if (newTarget || snap)
        {
            _from = snap ? target : _now;
            _fromColor = snap ? want : _nowColor;
            _t = snap ? 1f : 0f;
        }
        _key = key;
        _to = target;
        _toColor = want;

        if (!_shown) { _shown = true; SetActive(true); }
        Apply(_t);
    }

    public void Hide()
    {
        if (!_shown) return;
        _shown = false;
        _t = 1f;
        _key = null;
        SetActive(false);
    }

    public void Tick(float dt)
    {
        Adopt();
        if (!_shown) return;
        if (_t < 1f)
        {
            float time = MoveTime;
            _t = time <= 0f ? 1f : Mathf.Min(1f, _t + dt / time);
        }
        Apply(_t);
    }

    void Apply(float t)
    {
        _now = new Rect(
            Mathf.Lerp(_from.x, _to.x, t),
            Mathf.Lerp(_from.y, _to.y, t),
            Mathf.Lerp(_from.width, _to.width, t),
            Mathf.Lerp(_from.height, _to.height, t));
        _nowColor = Color.Lerp(_fromColor, _toColor, t);

        if (_glow != null)
        {
            DsWidgets.Place(_glow.rectTransform,
                            _now.x - GlowPad, _now.y - GlowPad,
                            _now.width + GlowPad * 2f, _now.height + GlowPad * 2f);
            _glow.color = _glow.sprite != null ? _nowColor : Color.clear;
        }

        PlaceCorner(_tl, _now.x + CornerInset, _now.y + CornerInset);
        PlaceCorner(_br, _now.xMax - CornerInset, _now.yMax - CornerInset);
    }

    // Corners are placed by their CENTRE, so the art sits astride the item's
    // corner rather than inside it.
    //
    // Deliberately NOT DsWidgets.Place. That anchors by the TOP-LEFT corner,
    // and subtracting half the size to centre it is only equivalent for a plain
    // quad -- these are drawn with useSpriteMesh, whose mesh is laid out from
    // the SPRITE's pivot mapped into the rect, so the rect's own pivot moves the
    // ink. With a top-left pivot both brackets were thrown off by most of their
    // own size, which is why the pair looked centred on the item's corner
    // instead of around the item. A centre pivot is what the per-cell brackets
    // used before this, and it is what the game's own cursor does.
    void PlaceCorner(Image img, float cx, float cy)
    {
        if (img == null) return;
        var rt = img.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(_cornerPx, _cornerPx);
        rt.anchoredPosition = new Vector2(cx, -cy);
        img.color = img.sprite != null ? Color.white : Color.clear;
    }

    void SetActive(bool on)
    {
        if (_glowRoot != null && _glowRoot.gameObject.activeSelf != on)
            _glowRoot.gameObject.SetActive(on);
        if (_cornerRoot != null && _cornerRoot.gameObject.activeSelf != on)
            _cornerRoot.gameObject.SetActive(on);
    }
}
#endif
