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
