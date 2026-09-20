// DsProbe — dump the game's inventory hierarchy, once, on request.
//
// This exists because guessing at the game's structure from its class
// definitions has repeatedly cost a seven-minute build to disprove. The left
// half of the inventory pane turned out not to be sprites on components at all:
// it is GameObjects that the game activates and deactivates (see
// InventoryItemHeartPieces.DisplayState), so "find the component and read its
// Sprite field" was never going to work, and the panel came up blank three
// times before that was clear.
//
// A dump of what is actually there -- names, components, and which
// SpriteRenderer holds which sprite -- turns the next attempt into a lookup
// instead of another guess.
//
//     adb shell 'echo "probe=1" > \
//         /sdcard/Android/data/com.jakobkhansen.silksong/files/dualscreen_v2'
//     adb shell am force-stop com.jakobkhansen.silksong      # then relaunch
//     adb logcat -d | grep DsProbe
//
// To turn it off again, delete the file. It SHIPS in every build, gated at
// runtime rather than compiled out, because the whole point is that the next
// person to wonder where a sprite lives pays a restart instead of a build.
// (DsConfig reads the file once per process, so a change needs a restart --
// not a rebuild.)
//
// It runs once, the first time the inventory is available, and is off unless
// asked for.

#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

public static class DsProbe
{
    static bool _done;
    static bool _fontsDone;
    static int _fontRuns;
    static float _nextFontDump;

    /// <summary>
    /// Dump every loaded font asset, and which of a probe string's characters
    /// each one is missing.
    ///
    /// This exists because "the capital M renders as two apostrophes and the
    /// curly quote renders as a box" has several possible causes that look
    /// identical on screen -- we picked a subset atlas, the game's atlas is
    /// dynamic and full, or the string does not contain the characters it
    /// appears to -- and they are told apart by the font's own tables rather
    /// than by looking harder at the panel.
    ///
    ///     adb shell 'echo "font_probe=1" > \
    ///         /sdcard/Android/data/com.jakobkhansen.silksong/files/dualscreen_v2'
    /// </summary>
    public static void MaybeDumpFonts()
    {
        int mode = DsConfig.Int("font_probe", 0);
        if (mode <= 0) return;

        // font_probe=1 runs once at startup, which is enough to describe the
        // assets. font_probe=2 repeats, because the strings that break are not
        // on screen at startup -- the journal name that started this was empty
        // when the one-shot dump ran, so it reported every label healthy.
        if (mode == 1 && _fontsDone) return;
        if (mode >= 2)
        {
            if (_fontRuns >= 12) return;
            if (Time.unscaledTime < _nextFontDump) return;
            _nextFontDump = Time.unscaledTime + 5f;
            _fontRuns++;
        }
        if (!DsTheme.HasFont) return;      // nothing to describe yet
        _fontsDone = true;

        try { DumpFonts(mode); }
        catch (System.Exception e) { Debug.LogWarning("[DsProbe] font dump failed: " + e); }
    }

    static void DumpFonts(int mode)
    {
        // Every character the panel was seen to get wrong, plus a control set
        // that renders correctly, so a font that is simply fine is obvious.
        const string Probe = "MmUuCcSsIiFfAa\u2019'\u2018\u201C\u201D\u2026-";

        // The asset inventory only needs saying once.
        if (_fontRuns <= 1)
        {
            var fonts = Resources.FindObjectsOfTypeAll<TMProOld.TMP_FontAsset>();
            Debug.Log("[DsProbe] === " + fonts.Length + " font assets ===");
            for (int i = 0; i < fonts.Length; i++)
            {
                var f = fonts[i];
                if (f == null || string.IsNullOrEmpty(f.name)) continue;
                Debug.Log("[DsProbe] font " + Describe(f) + " " + Report(f, Probe));
            }

            var b = DsTheme.Body;
            if (b != null)
            {
                // Which capitals the BODY face actually owns. "HasCharacter" can
                // be satisfied by a fallback, and a glyph served from a fallback
                // atlas is exactly what renders as the wrong shape.
                var caps = new StringBuilder();
                for (char c = 'A'; c <= 'Z'; c++) if (!InOwnAtlas(b, c)) caps.Append(c);
                Debug.Log("[DsProbe] body '" + b.name + "' capitals NOT in its own atlas: " +
                          (caps.Length == 0 ? "(none)" : caps.ToString()));
            }
            Debug.Log("[DsProbe] chosen display='" + (DsTheme.Display != null ? DsTheme.Display.name : "-") +
                      "' body='" + (DsTheme.Body != null ? DsTheme.Body.name : "-") + "'");
        }

        // The strings the panel is ACTUALLY drawing, rather than a guess at
        // them. This is what tells a missing glyph apart from a string that
        // does not hold the characters it appears to.
        var labels = Resources.FindObjectsOfTypeAll<TMProOld.TextMeshProUGUI>();
        int reported = 0;
        for (int i = 0; i < labels.Length && reported < 12; i++)
        {
            var t = labels[i];
            if (t == null || t.gameObject.layer != DsPresentation.LAYER) continue;
            string s = null;
            try { s = t.text; } catch { }
            if (string.IsNullOrEmpty(s)) continue;

            TMProOld.TMP_FontAsset f = null;
            try { f = t.font; } catch { }
            if (f == null) continue;

            string rep = Report(f, s);
            if (rep.StartsWith("ok") && mode < 3) continue;
            reported++;
            Debug.Log("[DsProbe] label '" + t.name + "' font=" + f.name +
                      " mat=" + MaterialOf(t) + " " + rep + " text=\"" + s + "\" codes=" + Codes(s));
        }
        if (reported == 0 && _fontRuns <= 1)
            Debug.Log("[DsProbe] every live label's font owns every character it draws");
    }

    /// <summary>
    /// The material a label draws with, and the atlas that material samples.
    /// A font whose dictionary holds a glyph that still renders as the wrong
    /// shape is usually this: the right glyph rects against the wrong texture.
    /// </summary>
    static string MaterialOf(TMProOld.TextMeshProUGUI t)
    {
        try
        {
            var m = t.fontSharedMaterial;
            if (m == null) return "(none)";
            string tex = "(no tex)";
            try { if (m.mainTexture != null) tex = m.mainTexture.name + " " + m.mainTexture.width + "x" + m.mainTexture.height; }
            catch { }
            string fontAtlas = "(?)";
            try { if (t.font != null && t.font.atlas != null) fontAtlas = t.font.atlas.name + " " + t.font.atlas.width + "x" + t.font.atlas.height; }
            catch { }
            return m.name + " tex=" + tex + " fontAtlas=" + fontAtlas;
        }
        catch { return "(err)"; }
    }

    /// <summary>Is this character in the font's OWN atlas, ignoring fallbacks?</summary>
    static bool InOwnAtlas(TMProOld.TMP_FontAsset f, char ch)
    {
        try
        {
            if (f.characterDictionary != null) return f.characterDictionary.ContainsKey((int)ch);
        }
        catch { }
        return true;
    }

    /// <summary>
    /// Per character: absent everywhere, or present only through a fallback.
    /// The distinction is the whole point -- a glyph drawn from a fallback
    /// atlas is what comes out as the wrong shape rather than as a box.
    /// </summary>
    static string Report(TMProOld.TMP_FontAsset f, string probe)
    {
        var absent = new StringBuilder();
        var viaFallback = new StringBuilder();
        var seen = new HashSet<char>();

        for (int c = 0; c < probe.Length; c++)
        {
            char ch = probe[c];
            if (ch == ' ' || ch == '\n' || !seen.Add(ch)) continue;

            bool own = InOwnAtlas(f, ch);
            bool any = true;
            try { any = f.HasCharacter(ch); } catch { }

            if (!any) absent.Append(" U+").Append(((int)ch).ToString("X4"));
            else if (!own) viaFallback.Append(" U+").Append(((int)ch).ToString("X4"));
        }

        if (absent.Length == 0 && viaFallback.Length == 0) return "ok";
        var sb = new StringBuilder();
        if (absent.Length > 0) sb.Append("ABSENT:").Append(absent);
        if (viaFallback.Length > 0) sb.Append(" FALLBACK-ONLY:").Append(viaFallback);
        return sb.ToString();
    }

    static string Codes(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length && i < 24; i++)
        {
            if (s[i] == ' ') { sb.Append(" _"); continue; }
            sb.Append(' ').Append(((int)s[i]).ToString("X4"));
        }
        return sb.ToString();
    }

    static string Describe(TMProOld.TMP_FontAsset f)
    {
        var sb = new StringBuilder(f.name);
        // The vendored TMP is the OLD one: a single static atlas and a plain
        // dictionary, with no runtime glyph population. A character is either
        // baked in or it cannot be drawn at all.
        try { sb.Append(" chars=").Append(f.characterDictionary == null ? -1 : f.characterDictionary.Count); }
        catch { }
        try { sb.Append(" fallbacks=").Append(f.fallbackFontAssets == null ? 0 : f.fallbackFontAssets.Count); }
        catch { }
        try { sb.Append(" atlas=").Append(f.atlas == null ? "none" : f.atlas.width + "x" + f.atlas.height); }
        catch { }
        try { sb.Append(" face='").Append(f.fontInfo.Name).Append("' pt=").Append(f.fontInfo.PointSize); }
        catch { }
        return sb.ToString();
    }

    public static void DumpHud(IList<Transform> roots)
    {
        Debug.Log("[DsProbe] === native health/silk render roots ===");
        for (int i = 0; i < roots.Count; i++)
            if (roots[i] != null) Walk(roots[i], 0);
        Debug.Log("[DsProbe] === end health/silk ===");
    }

    public static void MaybeRun()
    {
        if (_done || !DsConfig.Bool("probe", false)) return;
        if (!DsGameData.InGame) return;
        _done = true;

        try { Dump(); }
        catch (System.Exception e) { Debug.LogWarning("[DsProbe] failed: " + e); }
    }

    static bool _spritesDumped;

    /// <summary>
    /// Write the game's own sprites out as PNGs, so a person can LOOK at one.
    ///
    /// Set `sprite_dump=<substring>` in the dualscreen_v2 file and every loaded
    /// sprite whose name contains it lands in the external files directory as
    /// `sprites/<name>.png`. `sprite_dump=1` takes everything, which is a great
    /// many files and occasionally what you want.
    ///
    /// This exists because the decompile is not enough. Half the art this panel
    /// borrows is referenced by the placeholder GUID `0000000deadbeef15dead...`
    /// and has no PNG on disk at all, so the only way to answer "is THAT the
    /// right glyph" is to take it off the running game. Several rounds have
    /// been lost to reasoning about sprites from their names.
    ///
    /// Atlas pages are not CPU-readable, so the texture cannot simply be asked
    /// for its pixels. It is blitted into a RenderTexture first, which the GPU
    /// will do for any texture, and read back from there -- and only the
    /// sprite's own rect within the page, or every dump would be the whole
    /// atlas.
    /// </summary>
    public static void MaybeDumpSprites()
    {
        if (_spritesDumped) return;
        string want = DsConfig.Str("sprite_dump", null);
        if (string.IsNullOrEmpty(want)) return;
        if (!DsGameData.InGame) return;
        _spritesDumped = true;

        try
        {
            string dir = System.IO.Path.Combine(Application.persistentDataPath, "sprites");
            System.IO.Directory.CreateDirectory(dir);

            bool all = want == "1";
            var seen = new System.Collections.Generic.HashSet<string>();
            var sprites = Resources.FindObjectsOfTypeAll<Sprite>();

            // Gathered per page, because a page is decoded once and every
            // sprite on it is then cut from the copy.
            var byPage = new System.Collections.Generic.Dictionary<Texture, System.Collections.Generic.List<Sprite>>();
            int listed = 0;
            for (int i = 0; i < sprites.Length; i++)
            {
                var s = sprites[i];
                if (s == null || s.texture == null) continue;
                if (!seen.Add(s.name)) continue;

                // EVERY sprite is listed; only matches are written out.
                //
                // Listing is a log line and writing is two PNGs, and the
                // difference is not small: `sprite_dump=1` used to write files
                // for everything, which locked the device hard enough to need
                // adb to recover it. A name and a rect is all that is wanted
                // nine times in ten -- it is what turns a glyph spotted on a
                // dumped atlas page back into something askable-for -- so the
                // cheap half now runs for everything and the expensive half
                // stays behind the filter.
                var lr = s.textureRect.width > 0f ? s.textureRect : s.rect;
                Debug.Log("[DsProbe] sprite " + s.name +
                          " rect " + Mathf.RoundToInt(lr.x) + "," + Mathf.RoundToInt(lr.y) +
                          " " + Mathf.RoundToInt(lr.width) + "x" + Mathf.RoundToInt(lr.height) +
                          " page " + s.texture.name + " " + s.texture.width + "x" + s.texture.height);
                listed++;

                if (!all && s.name.IndexOf(want, System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                System.Collections.Generic.List<Sprite> list;
                if (!byPage.TryGetValue(s.texture, out list))
                    byPage[s.texture] = list = new System.Collections.Generic.List<Sprite>();
                list.Add(s);
            }

            // `sprite_dump=1` means "list everything", not "write everything".
            int written = 0;
            if (!all)
            {
                foreach (var pair in byPage)
                    written += DumpPage(pair.Key, pair.Value, dir);
            }

            Debug.Log("[DsProbe] sprite_dump '" + want + "': listed " + listed +
                      " sprite(s), wrote " + written + " from " + byPage.Count + " page(s) to " + dir);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[DsProbe] sprite dump failed: " + e);
        }
    }

    /// <summary>
    /// Decode one atlas page, write it out, and cut every wanted sprite from
    /// the decoded copy.
    ///
    /// Decoding the page is the only reliable step here. Atlas pages are
    /// BC7-compressed and not CPU-readable, and neither obvious shortcut works
    /// on them: CopyTexture cannot write BC7 blocks into an RGBA32 target and
    /// silently yields nothing (a whole dump of forty-four sprites came back
    /// blank), while Blit-then-crop draws through a shader that arrives
    /// vertically flipped on Vulkan, so each crop landed on a neighbour. A
    /// full-page Blit has neither problem -- there is no rect to get wrong --
    /// so the page is read back once and the sprites are cut on the CPU.
    ///
    /// Each sprite is cut BOTH ways up and written as `name.png` and
    /// `name.flip.png`, because whether the readback flipped is a property of
    /// the driver and not worth another build to find out: one of the two is
    /// right, and which one is obvious at a glance.
    /// </summary>
    static int DumpPage(Texture tex, System.Collections.Generic.List<Sprite> list, string dir)
    {
        RenderTexture rt = null;
        RenderTexture previous = RenderTexture.active;
        Texture2D page = null;
        int written = 0;
        try
        {
            int pw = tex.width, ph = tex.height;
            rt = RenderTexture.GetTemporary(pw, ph, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            page = new Texture2D(pw, ph, TextureFormat.RGBA32, false);
            page.ReadPixels(new UnityEngine.Rect(0, 0, pw, ph), 0, 0);
            page.Apply();

            System.IO.File.WriteAllBytes(
                System.IO.Path.Combine(dir, "_page_" + Safe(tex.name) + ".png"), page.EncodeToPNG());

            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                var r = s.textureRect.width > 0f ? s.textureRect : s.rect;
                int x = Mathf.Clamp(Mathf.RoundToInt(r.x), 0, pw - 1);
                int y = Mathf.Clamp(Mathf.RoundToInt(r.y), 0, ph - 1);
                int w = Mathf.Clamp(Mathf.RoundToInt(r.width), 1, pw - x);
                int h = Mathf.Clamp(Mathf.RoundToInt(r.height), 1, ph - y);

                // The rect is logged so a glyph picked out of the page by eye
                // can be turned back into a sprite NAME, which is the only
                // durable way to ask for it later.
                Debug.Log("[DsProbe] sprite " + s.name + " rect " + x + "," + y +
                          " " + w + "x" + h + " page " + tex.name + " " + pw + "x" + ph);

                string safe = Safe(s.name);
                if (Cut(page, x, y, w, h, System.IO.Path.Combine(dir, safe + ".png"))) written++;
                Cut(page, x, ph - y - h, w, h, System.IO.Path.Combine(dir, safe + ".flip.png"));
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[DsProbe] could not dump page " + tex.name + ": " + e.Message);
        }
        finally
        {
            RenderTexture.active = previous;
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
            if (page != null) UnityEngine.Object.Destroy(page);
        }
        return written;
    }

    static bool Cut(Texture2D page, int x, int y, int w, int h, string path)
    {
        Texture2D cut = null;
        try
        {
            if (x < 0 || y < 0 || x + w > page.width || y + h > page.height) return false;
            cut = new Texture2D(w, h, TextureFormat.RGBA32, false);
            cut.SetPixels(page.GetPixels(x, y, w, h));
            cut.Apply();
            System.IO.File.WriteAllBytes(path, cut.EncodeToPNG());
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[DsProbe] could not cut " + path + ": " + e.Message);
            return false;
        }
        finally
        {
            if (cut != null) UnityEngine.Object.Destroy(cut);
        }
    }

    static string Safe(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' ? c : '_');
        }
        return sb.ToString();
    }

    static void Dump()
    {
        var pane = FindInventoryRoot();
        if (pane == null) { Debug.Log("[DsProbe] no inventory root found"); return; }

        // Optionally start somewhere specific, e.g. probe_root=Spool. The whole
        // inventory is hundreds of lines; a subtree is a page.
        string want = DsConfig.Str("probe_root", null);
        if (!string.IsNullOrEmpty(want))
        {
            var all = pane.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i].name == want) { pane = all[i]; break; }
        }

        Debug.Log("[DsProbe] === hierarchy under '" + pane.name + "' ===");
        Walk(pane, 0);
        Debug.Log("[DsProbe] === end ===");
    }

    static Transform FindInventoryRoot()
    {
        // The inventory lives under the HUD camera's gameplay child and is
        // literally named "Inventory" (GameManager.SetupGameRefs).
        var all = Resources.FindObjectsOfTypeAll<InventoryPaneList>();
        for (int i = 0; i < all.Length; i++)
            if (all[i] != null && all[i].gameObject.scene.IsValid())
                return all[i].transform;
        return null;
    }

    // Depth-limited, but the limit is a knob: the first version stopped at six
    // levels and cut off exactly the part that mattered -- the silk hearts live
    // deeper than that, so a dump that looked complete was hiding the thing
    // being hunted.
    static void Walk(Transform t, int depth)
    {
        if (depth > DsConfig.Int("probe_depth", 12)) return;

        var sb = new StringBuilder();
        sb.Append("[DsProbe] ");
        for (int i = 0; i < depth; i++) sb.Append("  ");
        sb.Append(t.name);
        sb.Append(t.gameObject.activeSelf ? "" : " (inactive)");

        var sr = t.GetComponent<SpriteRenderer>();
        if (sr != null)
            sb.Append("  [SpriteRenderer sprite=")
              .Append(sr.sprite != null ? sr.sprite.name : "null").Append(']');

        var tk = t.GetComponent<tk2dSprite>();
        if (tk != null)
            sb.Append("  [tk2d sprite=").Append(tk.CurrentSprite != null ? tk.CurrentSprite.name : "null").Append(']');
        var renderer = t.GetComponent<Renderer>();
        if (renderer != null)
            sb.Append("  [layer=").Append(t.gameObject.layer).Append(" enabled=").Append(renderer.enabled)
              .Append(" bounds=").Append(renderer.bounds).Append(']');
        var canvas = t.GetComponent<Canvas>();
        if (canvas != null)
            sb.Append("  [Canvas mode=").Append(canvas.renderMode).Append(" layer=").Append(t.gameObject.layer)
              .Append(" active=").Append(canvas.isActiveAndEnabled).Append(']');
        var image = t.GetComponent<UnityEngine.UI.Image>();
        if (image != null)
            sb.Append("  [Image sprite=").Append(image.sprite != null ? image.sprite.name : "null")
              .Append(" fill=").Append(image.fillAmount).Append(" layer=").Append(t.gameObject.layer).Append(']');

        // What a PlayerDataTestResponse is actually testing, and its answer.
        // Several widgets are gated by these, and "which objects should be
        // visible" is unanswerable without seeing the test behind them.
        var gate = t.GetComponent<PlayerDataTestResponse>();
        if (gate != null)
        {
            sb.Append("  [gate ");
            try
            {
                var f = typeof(PlayerDataTestResponse)
                    .GetField("test", BindingFlags.NonPublic | BindingFlags.Instance);
                var test = f != null ? f.GetValue(gate) as PlayerDataTest : null;
                sb.Append(test == null ? "none"
                        : (test.IsDefined ? "fulfilled=" + test.IsFulfilled : "undefined"));
            }
            catch (System.Exception e) { sb.Append("err ").Append(e.GetType().Name); }
            sb.Append(']');
        }

        var comps = t.GetComponents<Component>();
        for (int i = 0; i < comps.Length; i++)
        {
            if (comps[i] == null) continue;
            string n = comps[i].GetType().Name;
            if (n == "Transform" || n == "SpriteRenderer") continue;
            sb.Append("  <").Append(n).Append('>');
        }

        Debug.Log(sb.ToString());
        for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1);
    }
}
#endif
