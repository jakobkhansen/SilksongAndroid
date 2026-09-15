// DsHornetPanel — the left half of the Inventory tab: what Hornet IS.
//
// The game's Inventory pane is two halves, and only the right one -- the bag of
// collectables -- was being shown here, which made the tab a list of items
// rather than a character sheet. This is the other half: the needle, the mask
// shards, the spool fragments, the silk skills ringed around the silk core, and
// the two currencies.
//
// What is deliberately NOT shown: current health, current silk, and the
// needle's damage number. Those are live combat values that belong on the HUD.
// This panel is for what you check between fights, and the needle's ARTWORK
// already says which upgrade you carry.
//
// Nothing here decides what the art should look like. DsGameArt asks each of
// the game's own widgets to arrange itself and reports the result; this file
// only draws it into a rectangle. So two mask shards look like two mask shards,
// a locked skill stays hidden, and switching saves changes the picture --
// none of which needs a rule of ours.

#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TmpText = TMProOld.TextMeshProUGUI;
using TmpAlign = TMProOld.TextAlignmentOptions;

public class DsHornetPanel
{
    /// <summary>Something tappable, in layout space.</summary>
    struct Hit
    {
        public float X, Y, W, H;
        public string Name, Desc;
    }

    /// <summary>A drawn widget: the images making it up, and its tap target.</summary>
    class Slot
    {
        public RectTransform Root;
        public readonly List<Image> Images = new List<Image>();
        public string Name, Desc;
    }

    readonly List<Hit> _hits = new List<Hit>();
    readonly List<Slot> _slots = new List<Slot>();

    RectTransform _panel;
    Image _rosaryIcon, _shellIcon;
    TmpText _rosaries, _shells;

    Slot _needle, _mask, _spool, _core, _ring;
    readonly List<Slot> _skills = new List<Slot>();

    float _x, _y, _w, _h;
    float _artScale = 1f, _artX;
    float _ringCx, _ringCy, _ringR;
    bool _built;
    string _saveKey;

    // Re-look after a save change, because the first look can be too early.
    //
    // On a NEW save the game's inventory objects exist but have not been set up
    // for it yet, so the needle composed to a bare rectangle -- and SaveKey
    // could not notice, because a new save's values ARE the defaults from the
    // first frame, so nothing ever changed to trigger a second attempt. Going
    // to the menu and back fixed it, which is the tell: the panel was fine, its
    // timing was not.
    //
    // Two triggers, because either alone leaves a hole. The rising edge of the
    // game's own inventory being opened is the exact moment it refreshes this
    // art; the timed retries cover a player who never opens it.
    const int RETRIES = 3;
    const float RETRY_SECONDS = 3f;
    int _retries;
    float _nextRetry;
    bool _wasInventoryOpen;

    /// <summary>Where taps send their name and description.</summary>
    public System.Action<string, string> OnSelect;

    // The ring is tighter than the game's, because the panel is narrower than a
    // 16:9 pane; the core and the mask are larger, because they are the two
    // things worth reading at a glance.
    const float NeedleW = 96f;
    const float MaskSize = 210f;
    const float SpoolSize = 172f;
    // The two sit shoulder to shoulder; the gap is small on purpose, because
    // they read as one row of "what you are carrying towards the next upgrade".
    const float ShardGap = 8f;
    const float CoreSize = 174f;
    const float SkillSize = 96f;
    // The ring sprite is not vertically centred within its own bounds, so
    // drawing it centred on the skills' orbit leaves it sitting high. Nudged
    // down to match what is actually on the panel -- a pixel measurement of the
    // arc disagreed, having caught the wrong edge of the stroke, and the screen
    // is the authority here.
    const float RingArtOffsetY = 20f;
    // How far out the skills sit, as a fraction of the space available. Below
    // 1 they tuck in towards the core rather than hugging the panel edges.
    const float RingTightness = 0.81f;

    // Measured off the game's own inventory rather than derived.
    //
    // Composing a widget from its sprites gives an aspect that depends on
    // Unity reporting sane geometry for objects that have never been drawn,
    // and it kept coming back wrong -- the needle rendered as a stubby arrow
    // and the mask as a squashed blob. These are the proportions the art
    // actually has, taken with a ruler from a screenshot of the real pane
    // (needle 82x724, mask 169x269), and they cannot drift.
    const float NeedleAspect = 0.113f;
    const float MaskAspect = 0.63f;
    const float SpoolAspect = 0.75f;
    const float CoreAspect = 1f;

    // The narrowest this composition can be laid out at before the spool runs
    // off the end of the panel: the needle's lane, then the mask and the spool
    // shoulder to shoulder. Derived from the parts rather than written down as
    // 516, so resizing any of them cannot silently invalidate it.
    const float MinArtW = 12f + NeedleW + 8f + MaskSize + ShardGap + SpoolSize + 10f;
    const float MinArtH = 762f;

    public void Build(RectTransform host, float x, float y, float w, float h)
    {
        _x = x; _y = y; _w = w; _h = h;

        // No box. This column is divided from the grid beside it by the rule the
        // Inventory screen draws down the gutter; a frame here would be a second
        // boundary saying the same thing.
        _panel = DsWidgets.Rect(host, "hornet");
        DsWidgets.Place(_panel, x, y, w, h);

        // Fit the existing composition rather than crushing the skill ring into
        // the smaller space left by the shared HUD and bottom tabs.
        //
        // Both axes, not just height. The column used to be 520 px and only the
        // height was ever short, so scaling by height alone was enough. Now that
        // the description has its own column the character column is narrower
        // than the composition's natural width, and laying out at the requested
        // width would push the spool -- which sits shoulder to shoulder with the
        // mask, with only a few pixels to spare at 520 -- off the end of it.
        //
        // So the art is laid out at its natural size and scaled to fit, which is
        // what the height already did. At the old 520x664 this is the same
        // number it always was, so nothing that fits today moves.
        float artWidth = Mathf.Max(w, MinArtW);
        float artHeight = Mathf.Max(h, MinArtH);
        _artScale = Mathf.Min(w / artWidth, h / artHeight);
        _artX = (w - artWidth * _artScale) * 0.5f;
        var art = DsWidgets.Rect(_panel, "art");
        DsWidgets.Place(art, _artX, 0f, artWidth, artHeight);
        art.localScale = new Vector3(_artScale, _artScale, 1f);
        _panel = art;

        // Everything below lays out in ART space, so it must use the art's size
        // rather than the column's. Missing this is how the currency row ended
        // up measured against one width and drawn at another.
        w = artWidth;
        h = artHeight;

        _needle = MakeSlot("needle", 12f, 34f, NeedleW, h - 170f);

        // The needle keeps a narrow lane; everything else shares the rest, so
        // the mask and spool can be as large as the column allows.
        float colX = 12f + NeedleW + 8f;
        float colW = w - colX - 10f;

        // Mask and spool sit side by side across the top of the column. Their
        // widths plus the gap have to stay inside colW, or the spool runs off
        // the panel -- which it did the moment they were scaled up.
        _mask = MakeSlot("mask", colX, 26f, MaskSize, MaskSize);
        _spool = MakeSlot("spool", colX + MaskSize + ShardGap, 44f, SpoolSize, SpoolSize);

        float ringTop = MaskSize + 40f;
        float ringBottom = h - 108f;
        _ringCx = colX + colW * 0.5f - 24f;
        _ringCy = (ringTop + ringBottom) * 0.5f;
        _ringR = (Mathf.Min(colW * 0.5f, (ringBottom - ringTop) * 0.5f) - SkillSize * 0.5f)
                 * RingTightness;

        // The backdrop and the skills must share ONE radius, or they cannot
        // line up: the skills orbit at _ringR, so the ring art is fitted into a
        // box of exactly that diameter and its outer edge lands on the orbit.
        //
        // The art is not vertically centred within its own sprite, though --
        // measured off the panel, the drawn circle sat 10 px below the orbit
        // the icons follow. RingArtOffsetY takes that out. It is a property of
        // the sprite, so it is a constant rather than something to derive.
        float ringBox = _ringR * 2f;
        _ring = MakeSlot("ring", _ringCx - ringBox * 0.5f,
                         _ringCy - ringBox * 0.5f + RingArtOffsetY, ringBox, ringBox);

        _core = MakeSlot("core", _ringCx - CoreSize * 0.5f, _ringCy - CoreSize * 0.5f,
                         CoreSize, CoreSize);

        float curY = h - 88f;
        _rosaryIcon = DsWidgets.Icon(_panel, "rosary-i", null, Color.white);
        DsWidgets.Place(_rosaryIcon.rectTransform, 18f, curY, 58f, 58f);
        _rosaries = DsWidgets.Label(_panel, "rosary-n", "", DsTheme.RowSize, DsTheme.Ink);
        if (_rosaries != null) DsWidgets.Place(_rosaries.rectTransform, 84f, curY + 6f, 170f, 46f);

        _shellIcon = DsWidgets.Icon(_panel, "shell-i", null, Color.white);
        DsWidgets.Place(_shellIcon.rectTransform, w * 0.5f + 18f, curY, 58f, 58f);
        _shells = DsWidgets.Label(_panel, "shell-n", "", DsTheme.RowSize, DsTheme.Ink);
        if (_shells != null) DsWidgets.Place(_shells.rectTransform, w * 0.5f + 84f, curY + 6f, 200f, 46f);
    }

    Slot MakeSlot(string name, float x, float y, float w, float h)
    {
        var slot = new Slot { Root = DsWidgets.Rect(_panel, name) };
        DsWidgets.Place(slot.Root, x, y, w, h);
        _slots.Add(slot);
        return slot;
    }

    public void Refresh()
    {
        if (!DsGameData.InGame)
        {
            // Leaving the last save's needle and shards on screen in the menu
            // is worse than showing nothing: it is showing someone else's
            // character sheet.
            if (_built) Clear();
            return;
        }

        // Rebuild when the save changes. Without this the panel keeps the first
        // save it ever saw, which is exactly what happened.
        string key = SaveKey();
        if (key != _saveKey)
        {
            _saveKey = key;
            _retries = 0;
            _nextRetry = Time.unscaledTime + RETRY_SECONDS;
        }
        else if (_built && !InventoryJustOpened() && !RetryDue())
        {
            RefreshCounts();
            return;
        }

        DsGameArt.Forget();
        Draw(_needle, DsGameArt.Needle(), NeedleAspect);
        Draw(_mask, DsGameArt.MaskShards(), MaskAspect);
        Draw(_spool, DsGameArt.SpoolPieces(), SpoolAspect);
        Draw(_ring, DsGameArt.IconRing(), 1f);
        Draw(_core, DsGameArt.SilkCore(), CoreAspect);
        DrawSkills();

        SetIcon(_rosaryIcon, DsGameArt.RosaryIcon());
        SetIcon(_shellIcon, DsGameArt.ShardIcon());

        _built = _needle.Images.Count > 0;
        RebuildHits();
        RefreshCounts();
    }

    /// <summary>
    /// True on the frame the game's own inventory opens.
    ///
    /// That is when it sets its art up for the current save, so it is the one
    /// moment we know a re-read will get the real thing rather than whatever
    /// the objects happened to be holding.
    /// </summary>
    bool InventoryJustOpened()
    {
        bool open = false;
        try { open = PlayerData.instance.isInventoryOpen; } catch { }
        bool rising = open && !_wasInventoryOpen;
        _wasInventoryOpen = open;
        return rising;
    }

    /// <summary>A few more attempts after a save change, then stop.</summary>
    bool RetryDue()
    {
        if (_retries >= RETRIES) return false;
        if (Time.unscaledTime < _nextRetry) return false;
        _retries++;
        _nextRetry = Time.unscaledTime + RETRY_SECONDS;
        return true;
    }

    // Enough of the save's identity to notice a different one, without reading
    // anything expensive: the two counters and the crest change together.
    static string SaveKey()
    {
        try
        {
            var pd = PlayerData.instance;
            return pd.CurrentCrestID + "/" + pd.nailUpgrades + "/" + pd.maxHealthBase +
                   "/" + pd.silkMax + "/" + pd.heartPieces + "/" + pd.silkSpoolParts;
        }
        catch { return ""; }
    }

    void Clear()
    {
        for (int i = 0; i < _slots.Count; i++) ClearSlot(_slots[i]);
        _skills.Clear();
        _hits.Clear();
        SetText(_rosaries, "");
        SetText(_shells, "");
        if (_rosaryIcon != null) _rosaryIcon.enabled = false;
        if (_shellIcon != null) _shellIcon.enabled = false;
        _built = false;
        _saveKey = null;
        _retries = 0;
        _wasInventoryOpen = false;
        DsGameArt.Forget();
    }

    static void ClearSlot(Slot s)
    {
        for (int i = 0; i < s.Images.Count; i++)
            if (s.Images[i] != null) Object.Destroy(s.Images[i].gameObject);
        s.Images.Clear();
        s.Name = null; s.Desc = null;
    }

    // Draw a composed widget into its rectangle, PRESERVING ITS ASPECT. The
    // pieces carry normalised positions, so stretching them into a box of a
    // different shape squashes the whole widget -- which is why the needle and
    // the spool came out short and fat. The art is fitted inside the box
    // instead, centred, at whatever scale makes it fit.
    void Draw(Slot slot, DsGameArt.Widget w, float aspectOverride = 0f)
    {
        ClearSlot(slot);
        if (slot.Root == null || w == null || !w.Ok) return;

        float aspect = aspectOverride > 0f ? aspectOverride : w.Aspect;

        float boxW = slot.Root.sizeDelta.x, boxH = slot.Root.sizeDelta.y;
        float artW = boxW, artH = boxH;
        if (aspect > 0.0001f)
        {
            if (boxW / boxH > aspect) artW = boxH * aspect;   // box is wider than the art
            else artH = boxW / aspect;                        // box is taller
        }
        float offX = (boxW - artW) * 0.5f;
        float offY = (boxH - artH) * 0.5f;

        for (int i = 0; i < w.Pieces.Count; i++)
        {
            var p = w.Pieces[i];
            var img = DsWidgets.Icon(slot.Root, "p" + i, p.Sprite, p.Colour);
            // Tight mesh AND preserved aspect. Each on its own is wrong in a
            // different way: the tight mesh alone stretched the art to fill the
            // rect (the stubby needle), and a plain quad alone sampled the
            // sprite's padding, which in an atlas is the NEIGHBOURING sprite --
            // the stray black block and white slivers around the mask. The
            // grid has used both together all along, which is why its icons
            // were never affected.
            img.useSpriteMesh = true;
            img.preserveAspect = true;
            DsWidgets.Place(img.rectTransform,
                            offX + p.Norm.x * artW, offY + p.Norm.y * artH,
                            p.Norm.width * artW, p.Norm.height * artH);
            if (p.FlipX)
            {
                var s = img.rectTransform.localScale;
                img.rectTransform.localScale = new Vector3(-s.x, s.y, s.z);
            }
            slot.Images.Add(img);
        }
        slot.Name = w.Name;
        slot.Desc = w.Desc;
    }

    void DrawSkills()
    {
        for (int i = 0; i < _skills.Count; i++)
        {
            ClearSlot(_skills[i]);
            if (_skills[i].Root != null) Object.Destroy(_skills[i].Root.gameObject);
            _slots.Remove(_skills[i]);
        }
        _skills.Clear();

        var skills = DsGameArt.Skills();
        for (int i = 0; i < skills.Count; i++)
        {
            // Evenly spaced, clockwise from the top, in the order DsGameArt
            // returns them: Needolin, Swift Step, Clawline, Sylphsong,
            // Silk Soar, Cling Grip.
            float a = -Mathf.PI * 0.5f + (Mathf.PI * 2f) * i / Mathf.Max(1, skills.Count);
            float sx = _ringCx + Mathf.Cos(a) * _ringR - SkillSize * 0.5f;
            float sy = _ringCy + Mathf.Sin(a) * _ringR - SkillSize * 0.5f;

            var slot = MakeSlot("skill" + i, sx, sy, SkillSize, SkillSize);
            Draw(slot, skills[i]);
            _skills.Add(slot);
        }
    }

    void RebuildHits()
    {
        _hits.Clear();
        for (int i = 0; i < _slots.Count; i++)
        {
            var s = _slots[i];
            if (s.Root == null || s.Images.Count == 0) continue;
            if (string.IsNullOrEmpty(s.Name) && string.IsNullOrEmpty(s.Desc)) continue;

            _hits.Add(new Hit
            {
                X = _x + _artX + s.Root.anchoredPosition.x * _artScale,
                Y = DsLayout.Current.Body.y + _y - s.Root.anchoredPosition.y * _artScale,
                W = s.Root.sizeDelta.x * _artScale,
                H = s.Root.sizeDelta.y * _artScale,
                Name = s.Name,
                Desc = s.Desc,
            });
        }
    }

    void RefreshCounts()
    {
        try
        {
            var pd = PlayerData.instance;
            SetText(_rosaries, pd.geo.ToString());

            string shells = pd.ShellShards.ToString();
            int cap = CurrencyCap(CurrencyType.Shard);
            if (cap > 0) shells += " / " + cap;
            SetText(_shells, shells);
        }
        catch { }

        if (_rosaryIcon != null) _rosaryIcon.enabled = _rosaryIcon.sprite != null;
        if (_shellIcon != null) _shellIcon.enabled = _shellIcon.sprite != null;
    }

    /// <summary>True if the tap was ours.</summary>
    public bool OnTap(Vector2 layoutPoint)
    {
        // Smallest target first, so a skill on top of the ring wins over the
        // needle's tall strip behind it.
        int best = -1;
        float bestArea = float.MaxValue;
        for (int i = 0; i < _hits.Count; i++)
        {
            var h = _hits[i];
            if (layoutPoint.x < h.X || layoutPoint.x > h.X + h.W) continue;
            if (layoutPoint.y < h.Y || layoutPoint.y > h.Y + h.H) continue;
            float area = h.W * h.H;
            if (area < bestArea) { bestArea = area; best = i; }
        }
        if (best < 0) return false;

        if (OnSelect != null) OnSelect(_hits[best].Name, _hits[best].Desc);
        return true;
    }

    static void SetIcon(Image img, Sprite s)
    {
        if (img == null || s == null) return;
        img.sprite = s;
        img.useSpriteMesh = true;
        img.preserveAspect = true;
        img.color = Color.white;
        img.enabled = true;
    }

    // GlobalSettings is Addressables-backed, so this may legitimately be
    // unavailable; a missing cap costs a "/ 400" and nothing else.
    static int CurrencyCap(CurrencyType type)
    {
        try { return GlobalSettings.Gameplay.GetCurrencyCap(type); }
        catch { return 0; }
    }

    static void SetText(TmpText t, string v) { if (t != null) t.text = v; }
}
#endif
