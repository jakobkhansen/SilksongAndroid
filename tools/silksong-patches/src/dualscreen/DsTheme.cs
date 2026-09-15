// DsTheme — the game's own look, borrowed rather than imitated.
//
// Nothing here is authored by us and nothing is committed to the repo. Fonts,
// icons and strings are taken from the running game, which is the only way the
// second screen can look like part of Silksong rather than like a mod of it,
// and the only way it stays correct when the game is patched. The repo
// continues to contain no game content.
//
// The one trap, and it is a silent one: the game does NOT use stock
// TextMeshPro. Team Cherry vendored an older copy into Assembly-CSharp under
// the namespace TMProOld, so the font assets are TMProOld.TMP_FontAsset and a
// stock TMPro.TextMeshProUGUI simply cannot be given one. The two type sets
// have identical names, so the mistake compiles and then produces blank text.
// The alias below is deliberate: everything in this project that touches text
// goes through it.

#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using TMP = TMProOld.TMP_Text;
using TmpFont = TMProOld.TMP_FontAsset;

public static class DsTheme
{
    // ── colour ──────────────────────────────────────────────────────────────
    // A pure-black ground for OLED panels, bone-white text, and the pale gold
    // the game's menus use to mark what is selected.
    public static readonly Color Ground      = Color.black;
    public static readonly Color Panel       = new Color(0.10f, 0.09f, 0.13f, 1f);
    public static readonly Color PanelEdge   = new Color(0.32f, 0.29f, 0.36f, 1f);
    // Sections are divided, not enclosed. A rule is drawn in the gutter BETWEEN
    // two things and belongs to neither, so it is bone-white and full strength:
    // there is only ever one of them per boundary, and it is the only structure
    // on the panel that is not content.
    public static readonly Color Rule        = new Color(0.93f, 0.91f, 0.86f, 1f);
    public static readonly Color Ink         = new Color(0.93f, 0.91f, 0.86f, 1f);
    public static readonly Color InkDim      = new Color(0.62f, 0.60f, 0.58f, 1f);
    public static readonly Color InkFaint    = new Color(0.38f, 0.37f, 0.40f, 1f);
    public static readonly Color Accent      = new Color(0.96f, 0.85f, 0.55f, 1f);
    public static readonly Color Locked      = new Color(0.20f, 0.19f, 0.23f, 1f);

    // Tool types have colours in the game's own HUD; matching them means a
    // red tool reads as a red tool here too.
    //
    // The game's answer is GlobalSettings.UI.GetToolTypeColor, which is where
    // InventoryItemTool.CursorColor gets it from, so that is what we ask --
    // but carefully, and never eagerly.
    //
    // GlobalSettingsBase.Get is NOT a getter. On a miss it cancels the game's
    // own delayed-loader coroutine, DESTROYS that loader's GameObject, starts
    // an Addressables load and blocks on WaitForCompletion. Called at the wrong
    // moment that is a frame hitch at best and a fight with the game's load
    // ordering at worst -- the same shape of trap as CollectableItemManager's
    // cache (see DsScreens). So it is only asked once, only during gameplay,
    // by which time the HUD has long since forced the settings resident and the
    // call is a field read. Anything unexpected keeps the authored values.
    static readonly Color[] _toolColors = new Color[4];
    static bool _toolColorsRead;

    static readonly Color[] _toolColorsFallback =
    {
        new Color(0.85f, 0.35f, 0.32f, 1f),   // Red
        new Color(0.42f, 0.62f, 0.88f, 1f),   // Blue
        new Color(0.92f, 0.80f, 0.38f, 1f),   // Yellow
        new Color(0.75f, 0.72f, 0.80f, 1f),   // Skill
    };

    public static Color ToolTypeColor(ToolItemType type)
    {
        int i = (int)type;
        if (i < 0 || i > 3) i = 3;

        if (!_toolColorsRead && DsGameData.InGame)
        {
            _toolColorsRead = true;
            try
            {
                for (int t = 0; t < 4; t++)
                    _toolColors[t] = GlobalSettings.UI.GetToolTypeColor((ToolItemType)t);
                Debug.Log("[DsTheme] tool colours from the game: " +
                          _toolColors[0] + " " + _toolColors[1] + " " +
                          _toolColors[2] + " " + _toolColors[3]);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[DsTheme] tool colours unavailable, using ours: " + e.Message);
                for (int t = 0; t < 4; t++) _toolColors[t] = default(Color);
            }
        }

        // A zero alpha means we never got a real one.
        return _toolColors[i].a > 0f ? _toolColors[i] : _toolColorsFallback[i];
    }

    // ── metrics ─────────────────────────────────────────────────────────────
    // Authored against the panel's 1240x1080. These are the numbers the whole
    // layout is built from, so they live in one place.
    //
    // The panel is ~9 cm across and held at arm's length on a handheld, so
    // everything is larger than a desktop UI would be: a 200 px cell is about
    // 14 mm, which is comfortably above the ~9 mm minimum for a touch target.
    public const float TabBarHeight = DsLayout.TabHeight;
    public const float FooterHeight = 200f;
    public const float Pad          = 20f;
    public const float RuleThickness = 2f;
    public const float TitleSize    = 42f;
    public const float BodySize     = 30f;
    // List rows are read at a glance and at arm's length, so they are larger
    // than body prose rather than smaller, which is the usual instinct.
    public const float RowSize      = 34f;
    public const float SmallSize    = 24f;

    // ── fonts ───────────────────────────────────────────────────────────────
    //
    // The game uses two typefaces, and using the wrong one for body text is
    // very visible: trajan_bold_tmpro is a CAPS display face whose lowercase
    // glyphs are broken (an "l" renders as a stub with a black foot), while
    // perpetua_tmpro is the serif text face with real lowercase. The game's own
    // menus do exactly this -- Trajan for headings, Perpetua for prose -- so
    // matching it is both correct and free.

    static TmpFont _display, _body;
    static bool _searched;
    static float _nextSearch;
    static float _firstSearch;

    // Characters the panel needs and has been seen to lose: a capital M, and
    // the curly apostrophe the game's prose is written with. They are the test
    // because they are what broke -- "Massive Mossgrub" came out as two
    // apostrophes and a box, from a font whose NAME was right.
    const string Required = "MACSIFmacsif\u2019";

    /// <summary>
    /// How good a candidate this is: its atlas size, except that a font which
    /// cannot draw what we need loses to one that can, however large its atlas.
    ///
    /// The vendored TMP is the OLD one -- a single static atlas and a plain
    /// dictionary, with no runtime glyph population -- so a character is either
    /// baked in or it can never be drawn. That makes picking the RIGHT asset
    /// the whole game: more than one loaded asset answers to "perpetua", and
    /// taking the first found meant taking whichever Unity happened to
    /// enumerate first, which could be a subset cut for some other screen.
    /// </summary>
    static int Coverage(TmpFont f)
    {
        int chars = 0;
        try { chars = f.characterDictionary == null ? 0 : f.characterDictionary.Count; } catch { }

        int missing = 0;
        for (int i = 0; i < Required.Length; i++)
        {
            bool has = true;
            try { has = f.HasCharacter(Required[i]); } catch { }
            if (!has) missing++;
        }
        return chars - missing * 100000;
    }

    static bool Complete(TmpFont f)
    {
        if (f == null) return false;
        for (int i = 0; i < Required.Length; i++)
        {
            try { if (!f.HasCharacter(Required[i])) return false; } catch { }
        }
        return true;
    }

    /// <summary>Caps display face, for tabs and titles.</summary>
    public static TmpFont Display { get { Search(); return _display ?? _body; } }

    /// <summary>Text face with real lowercase, for descriptions.</summary>
    public static TmpFont Body { get { Search(); return _body ?? _display; } }

    /// <summary>Anything usable at all yet?</summary>
    public static bool HasFont { get { Search(); return _display != null || _body != null; } }

    static void Search()
    {
        if (_searched) return;

        // The scan walks every loaded font asset, so a miss must not repeat per
        // label: before the game's UI exists there can be dozens of Label calls
        // in a single build pass.
        if (Time.unscaledTime < _nextSearch) return;
        if (_firstSearch == 0f) _firstSearch = Time.unscaledTime;
        _nextSearch = Time.unscaledTime + 1f;

        try
        {
            var fonts = Resources.FindObjectsOfTypeAll<TmpFont>();
            int bestDisplay = int.MinValue, bestBody = int.MinValue;

            // An exact asset name can be forced from the knob file, so a font
            // that looks right by name but renders wrong can be swapped for
            // another candidate with a restart instead of a ten-minute build.
            // The game ships several assets per face -- two Perpetuas, two
            // Trajans -- and which one is sound is not something the name says.
            string wantBody = DsConfig.Str("font_body", null);
            string wantDisplay = DsConfig.Str("font_display", null);

            // Every candidate is scored rather than the first one taken, so the
            // scan cannot stop early: the better asset may be enumerated last.
            for (int i = 0; i < fonts.Length; i++)
            {
                var f = fonts[i];
                if (f == null || string.IsNullOrEmpty(f.name)) continue;
                string n = f.name.ToLowerInvariant();

                // TMP ships its own default and will happily hand it out;
                // adopting it is how this screen ended up in Arial once.
                if (n.Contains("arial") || n.Contains("liberation")) continue;

                // A forced name wins outright, whatever it scores.
                if (!string.IsNullOrEmpty(wantBody) &&
                    string.Equals(f.name, wantBody, System.StringComparison.OrdinalIgnoreCase))
                {
                    bestBody = int.MaxValue; _body = f; continue;
                }
                if (!string.IsNullOrEmpty(wantDisplay) &&
                    string.Equals(f.name, wantDisplay, System.StringComparison.OrdinalIgnoreCase))
                {
                    bestDisplay = int.MaxValue; _display = f; continue;
                }

                int score = Coverage(f);
                if (n.Contains("trajan"))
                {
                    if (score > bestDisplay) { bestDisplay = score; _display = f; }
                }
                else if (n.Contains("perpetua") || n.Contains("amor"))
                {
                    if (score > bestBody) { bestBody = score; _body = f; }
                }
            }

            if (_display == null && _body == null) return;

            // Keep looking while what we have cannot draw the characters we
            // need. The game loads its UI progressively, and latching onto an
            // incomplete asset found early is exactly the failure this fixes.
            // Give up after half a minute and take the best seen, because a
            // font that renders most things beats no text at all.
            bool good = Complete(_body) && (_display == null || Complete(_display));
            if (!good && Time.unscaledTime - _firstSearch < 30f) return;

            _searched = true;
            Debug.Log("[DsTheme] fonts: display='" + (_display != null ? _display.name : "-") +
                      "' body='" + (_body != null ? _body.name : "-") +
                      "' complete=" + good);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[DsTheme] font lookup failed: " + e.Message);
        }
    }

    /// <summary>Fonts appear once the game has loaded its UI; allow a retry.</summary>
    public static void ForgetFont()
    {
        _display = null; _body = null; _searched = false; _nextSearch = 0f; _firstSearch = 0f;
    }

    // ── sprites ─────────────────────────────────────────────────────────────

    static Sprite _white;

    /// <summary>
    /// A 1x1 opaque sprite, so a plain rect can be drawn without relying on
    /// Unity's built-in UI sprite being present in a stripped player.
    /// </summary>
    public static Sprite White
    {
        get
        {
            if (_white != null) return _white;
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "DsWhite" };
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            _white = Sprite.Create(tex, new UnityEngine.Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            _white.hideFlags = HideFlags.HideAndDontSave;
            return _white;
        }
    }

    // Sprites pulled off the game's prefabs, cached by name so the search
    // happens once. Every lookup may legitimately return null -- the game uses
    // Addressables and an icon may not be resident before its pane has been
    // opened -- so callers must draw a placeholder rather than assume.
    static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

    static Sprite _disc;

    /// <summary>
    /// A filled circle, generated once.
    ///
    /// Slots are round in the game, and a square slot around a round icon
    /// reads as a different thing entirely. Generating it is preferable to
    /// hunting for a circular sprite in the game's atlases: it is eight lines,
    /// it cannot go missing, and it scales to any slot size.
    /// </summary>
    public static Sprite Disc
    {
        get
        {
            if (_disc != null) return _disc;
            const int size = 128;
            const float r = size * 0.5f;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "DsDisc" };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - r, dy = y + 0.5f - r;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    // One pixel of feather, so the edge is not stair-stepped
                    // when the disc is drawn smaller than its texture.
                    float a = Mathf.Clamp01(r - d);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.hideFlags = HideFlags.HideAndDontSave;
            _disc = Sprite.Create(tex, new UnityEngine.Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            _disc.hideFlags = HideFlags.HideAndDontSave;
            return _disc;
        }
    }

    public static Sprite FindSprite(string name)
    {
        Sprite found;
        if (_cache.TryGetValue(name, out found)) return found;
        try
        {
            var all = Resources.FindObjectsOfTypeAll<Sprite>();
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].name == name) { found = all[i]; break; }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[DsTheme] sprite lookup failed for '" + name + "': " + e.Message);
        }
        _cache[name] = found;
        return found;
    }
}
#endif
