// DsTitleCard — the game's own title, for when there is no game to show.
//
// Outside a save the second screen has nothing worth saying. It used to say it
// in words -- "Main menu", "Loading…" -- one small grey line per screen, which
// is accurate and looks like a bug. The game already owns the right image for
// this moment, so this borrows it: the same "Hollow Knight Silksong" logo the
// main menu shows on the primary display.
//
// It is READ, not driven. UIManager.gameTitle is a public SpriteRenderer that
// the game fades in and out with the menu, and taking its sprite means we get
// the LOCALISED one for free -- LogoLanguage swaps between English, Chinese and
// traditional Chinese versions, and reading through it means we swap with it.
// Nothing here touches the renderer, so the menu's own fades are unaffected.
//
// As everywhere else on this screen, no asset ships with us: the sprite is a
// reference to one already loaded from the player's own copy of the game.

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using UnityEngine;
using UnityEngine.UI;

public class DsTitleCard
{
    RectTransform _root;
    Image _logo;
    float _logoW, _logoH;
    float _nextHunt;

    public void Build(Transform parent, int width, int height)
    {
        _root = DsWidgets.Rect(parent, "title-card");
        DsWidgets.Stretch(_root);

        var bg = DsWidgets.Box(_root, "bg", DsTheme.Ground);
        DsWidgets.Stretch(bg.rectTransform);

        // Generous, and centred a little above the middle: the logo has its own
        // margins built in, and sitting it dead centre in a 1.15:1 panel reads
        // as slightly low.
        _logoW = width * 0.82f;
        _logoH = height * 0.52f;
        _logo = DsWidgets.Icon(_root, "logo", null, Color.white);
        _logo.rectTransform.anchorMin = _logo.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        _logo.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        _logo.rectTransform.sizeDelta = new Vector2(_logoW, _logoH);

        SetVisible(false);
    }

    public void SetVisible(bool on)
    {
        if (_root != null && _root.gameObject.activeSelf != on) _root.gameObject.SetActive(on);
    }

    /// <summary>
    /// Keep looking until the game hands over the real title.
    ///
    /// Deliberately does not stop at the first sprite it finds. During the
    /// intro the only logo loaded is Team Cherry's -- which is localised through
    /// the same LogoLanguage component, so a scan finds it and a scan that
    /// stopped there would leave the studio logo on the panel for the rest of
    /// the session. Hunting continues until the sprite comes from UIManager's
    /// own gameTitle, which is the one the menu is actually showing.
    /// </summary>
    public void Tick()
    {
        if (_logo == null || _settled) return;
        if (Time.unscaledTime < _nextHunt) return;
        _nextHunt = Time.unscaledTime + 0.5f;

        bool definitive;
        var sprite = FindTitleSprite(out definitive);
        if (sprite == null) return;

        if (!ReferenceEquals(sprite, _lastSprite))
        {
            _lastSprite = sprite;
            DsWidgets.FitCentred(_logo, sprite, _logoW, _logoH);
            Debug.Log("[DsTitle] logo: '" + sprite.name + "'" + (definitive ? "" : " (provisional)"));
        }
        if (definitive) _settled = true;
    }

    Sprite _lastSprite;
    bool _settled;

    /// <summary>
    /// The title sprite, by the shortest public route first.
    ///
    /// UIManager.gameTitle is the renderer the menu itself fades, and only that
    /// route is treated as definitive. The others exist because the field is
    /// unset until the logo has been shown once -- the game has the same problem
    /// and solves it with GameObject.Find("LogoTitle"), which is where the name
    /// below comes from.
    /// </summary>
    static Sprite FindTitleSprite(out bool definitive)
    {
        definitive = false;
        try
        {
            var ui = UIManager.instance;
            if (ui != null && ui.gameTitle != null)
            {
                if (ui.gameTitle.sprite != null) { definitive = true; return ui.gameTitle.sprite; }
                var chosen = FromLanguage(ui.gameTitle.GetComponent<LogoLanguage>());
                if (chosen != null) return chosen;
            }
        }
        catch { }

        try
        {
            var go = GameObject.Find(TITLE_OBJECT);
            if (go != null)
            {
                var sr = go.GetComponent<SpriteRenderer>();
                if (sr != null && sr.sprite != null) return sr.sprite;
                var chosen = FromLanguage(go.GetComponent<LogoLanguage>());
                if (chosen != null) return chosen;
            }
        }
        catch { }

        // Last resort, and BY NAME. LogoLanguage is not unique to the title --
        // Team Cherry's studio logo is localised through the same component, and
        // during the intro it is the only one loaded, so an unfiltered scan
        // reliably returns the wrong logo.
        try
        {
            var all = Resources.FindObjectsOfTypeAll<LogoLanguage>();
            for (int i = 0; i < all.Length; i++)
            {
                var l = all[i];
                if (l == null || l.gameObject.name != TITLE_OBJECT) continue;
                var chosen = FromLanguage(l);
                if (chosen != null) return chosen;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DsTitle] logo scan failed: " + e.Message);
        }

        return null;
    }

    /// <summary>The game's own name for the title object; see UIManager.</summary>
    const string TITLE_OBJECT = "LogoTitle";

    /// <summary>Pick the localised variant the way LogoLanguage.SetSprite does.</summary>
    static Sprite FromLanguage(LogoLanguage lang)
    {
        if (lang == null) return null;
        try
        {
            string code = TeamCherry.Localization.Language.CurrentLanguage().ToString();
            if (code == "ZH" && lang.chineseSprite != null) return lang.chineseSprite;
            return lang.englishSprite;
        }
        catch { return lang.englishSprite; }
    }
}
#endif
