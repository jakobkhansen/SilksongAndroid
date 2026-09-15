// DsRuleArt -- the dividers, as the designer drew them.
//
// These are OURS, not the game's: three small PNGs commissioned for this panel,
// kept in docs/dividers/ and embedded here as base64 because the patches are
// compiled ON THE DEVICE from source and have no asset pipeline to read a file
// through. All three together are under two kilobytes, which is cheaper than
// any plumbing that would avoid inlining them.
//
// What the art actually is, which is what decides how it may be drawn:
//
//   Horizontal  1100x2   a hairline that FADES TO NOTHING AT BOTH ENDS -- fully
//   Vertical       2x608 opaque only across the middle ~45%, ramping out to
//                        alpha 0 at each tip. Authored at very near its final
//                        size (the panel's rules are ~1200 and ~624), so it is
//                        stretched whole: the taper then scales with the line
//                        and a short rule looks like a short rule rather than
//                        like a long one with its ends chopped off. This is why
//                        it is NOT nine-sliced -- there is no flat end to
//                        preserve, the end IS the decoration.
//
//   Section      129x43  not a rule at all but an END CAP: a vertical tick down
//                        the far left, with a horizontal stroke leaving it at
//                        mid-height and fading out to the right. It is drawn at
//                        its own size and never stretched -- stretching it
//                        would smear the tick into a bar.
//
// Every accessor may return null. A PNG that fails to decode is not worth
// taking the panel down for, so DsWidgets falls back to the plain box rule it
// drew before these existed.

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using UnityEngine;

public static class DsRuleArt
{
    // The section cap's own geometry, in its own pixels. The stroke does not sit
    // at the art's vertical centre, so placing the cap means knowing which row
    // it leaves from -- otherwise it hangs below the line it is meant to be.
    public const float SectionW = 129f;
    public const float SectionH = 43f;
    public const float SectionLine = 20.5f;

    const string HorizontalPng = "iVBORw0KGgoAAAANSUhEUgAABEwAAAACCAYAAABCHEm1AAAACXBIWXMAAAsTAAALEwEAmpwYAAABoklEQVRoge1Yy47DMAjEUbX//717iPfQWoumMICTw1bLSFYTmAH8UGN7zDkf8sSUX+hnSdh3MMj7IDaPMxxb1occyyYicjic1Y4gxxFoGV/UuxUniu3ZMrrDiSXK5sWJfKwGVttVf1aLc5Jpei5R760hlgd9Gb0QnQQ2jIF8pre4FU6kY1zUMD3apjzn5uvVGo1Go9H4b/h+tVPev5GIafjYGQJ9lp5xkV/hYEyWw4vj2ay4FsfTISfSn6RO9GXa0kda5r+iZf4z4c/4xMkVxY841THRsbyaMnPJ1kBU14qNmszas353fGK8WxzGv4r0GeLhEHewOuAlZ3zUzEKcTP41Yd7BSQ9+NS/iFPvSxAJOutYOg+P1DZ+rc1Adb6yJ1Zrtxydgtw94AZEdbytfRnvXWHtxcAO1kwPXRXV8LP1Ofnz/1LXZaDQajcZd0PsW/M5WDyy4l4j0Op8+cGX1UUys6a4DmHcgZNz1jP3MaKPLLKav5PuLwD7odeH5dvu9o/MuKaLYen1aFx4esP8n4WbB8mZq2l1jqLH+BypxbtnX/wDHRTzww1ZPywAAAABJRU5ErkJggg==";
    const string VerticalPng = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAJgCAYAAACgFbJcAAAACXBIWXMAAAsTAAALEwEAmpwYAAAA8ElEQVRYhe2UQQ7DMAgEFyf//2Jf0tATUSlgcNJKPXAbwa7BDoSYmQBgADgBAEgA+2fKidiUBVsiEY/3NrR4JC6JUCV1Hmg1vmsol99YBaxrA8AS4eUD7S0qrm2lxJJ4yaXuXnmWi+9zDyiGyVded1VGK0glQxssUWX4TQSV1GTNJ7t8E4iZWe6FhoaGhoaGhoaGhoZ/gR3AQ4C/DUdRzEPAuGapQiRoY+KyYF2V1CHvfLyXIAVB9cQ1afUnwD48Y7Gf0lDR3HRVxdkALL2PLZoNyaWpy4bfX71gQZKUc6D8QDKXFo9YY8T6H8VJxJZ4AXfUrn8nVmcKAAAAAElFTkSuQmCC";
    const string SectionPng = "iVBORw0KGgoAAAANSUhEUgAAAIEAAAArCAYAAABfJ+vYAAAACXBIWXMAAAsTAAALEwEAmpwYAAACIklEQVR4nO3au24TURDG8Z9XkVBegXCRaKigouIJeAregYaSB0hBieihpoACIiQkJDoiECUgkEABhVsBSFySOIdivcrGrOMIiAd55y8dzZy1vf7s/Txn1ruDUsoCtiS9pcKRaBFJLBWORYtIYqlwNFpEEksuB0maIEkTJLInSOTZQYJBKaXgEDaixSQxVKOYfUGPaUyQfUGPaUywFKoiCaUxwYlQFUkoTWP4CqfxJVhPEkBTCY7j0thjizPWkgTRVALqewrO4AkOYxmPsILX+BqiMDlw2iaABziP6zg72raJW7iBVTydqcLkwBk3AfVBPjnh+W9xU10dVrF2gNqSGdFlgv3yGHfx4R/q+V/4itIxtifMtzvySWO4x3w4IU57bRnl41racx2fB39ngnlmTfeBafKtVhzP22OzFffKN/aZT9vXsPXew9Z83EC7jLzwh1/SfdxWN4zzyLdR7KoGk8b4r29apeiqGnttG07Z56Rq1TW0onET/MAFXPb7KeJDdYN4D8/M5zLQS8ZNsIyreI5rWFc3gnfwEu9nqi6ZCe2e4AVO4ftovqQui5/tNBbJHNKuBBftGADezFhLEkRTCVZwLlpMEkNz7eBKqIoklMYEn0JVJKE0Jsiuv8c0JlgPVZGEMiil/FTfbZz0lEouBb0nTZCo5DWA3pMmSHI5SLISJGoTvIsWkcSSy0GiwsdoEUkslfzLuPcMSimLdt9MkvSMX7NjF8wmF8D9AAAAAElFTkSuQmCC";

    static Sprite _h, _v, _s;
    static bool _hTried, _vTried, _sTried;

    /// <summary>The long hairline, for a rule across a boundary.</summary>
    public static Sprite Horizontal
    {
        get
        {
            if (!_hTried) { _hTried = true; _h = Decode(HorizontalPng, "DsRuleH"); }
            return _h;
        }
    }

    /// <summary>The same line down a gutter.</summary>
    public static Sprite Vertical
    {
        get
        {
            if (!_vTried) { _vTried = true; _v = Decode(VerticalPng, "DsRuleV"); }
            return _v;
        }
    }

    /// <summary>The end cap that marks a group inside a grid.</summary>
    public static Sprite Section
    {
        get
        {
            if (!_sTried) { _sTried = true; _s = Decode(SectionPng, "DsRuleSection"); }
            return _s;
        }
    }

    static Sprite Decode(string base64, string name)
    {
        try
        {
            byte[] png = Convert.FromBase64String(base64);

            // Size and format are placeholders: LoadImage replaces both from the
            // PNG's own header.
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = name };
            if (!tex.LoadImage(png))
            {
                UnityEngine.Object.Destroy(tex);
                Debug.LogWarning("[DsRuleArt] " + name + ": PNG did not decode");
                return null;
            }

            // Clamp matters: these are stretched, and a wrapped sample at the
            // very edge would bring the opaque middle back around to the tip
            // that is supposed to have faded out.
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.hideFlags = HideFlags.HideAndDontSave;

            var sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                                       new Vector2(0.5f, 0.5f), 100f);
            sprite.name = name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DsRuleArt] " + name + " failed: " + e.Message);
            return null;
        }
    }
}
#endif