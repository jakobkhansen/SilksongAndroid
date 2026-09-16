// DsTrashArt — the bin in the marker strip.
//
// Ours, not the game's: it was drawn for this panel and lives in docs/, so it
// is embedded as base64 for the same reason the dividers are -- the patches are
// compiled ON THE DEVICE from source and have no asset pipeline to read a file
// through. See DsRuleArt, which does the same for the same reason.
//
// The source is docs/Trash.webp; this is that file as a PNG, because
// Texture2D.LoadImage reads PNG and JPG and nothing else.

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using UnityEngine;

public static class DsTrashArt
{
    const string Png = "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";

    static Sprite _sprite;
    static bool _tried;

    /// <summary>Null if the art will not decode, which is not worth failing over.</summary>
    public static Sprite Sprite
    {
        get
        {
            if (_tried) return _sprite;
            _tried = true;
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "DsTrash" };
                if (!tex.LoadImage(Convert.FromBase64String(Png)))
                {
                    UnityEngine.Object.Destroy(tex);
                    Debug.LogWarning("[DsTrashArt] PNG did not decode");
                    return null;
                }
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                tex.hideFlags = HideFlags.HideAndDontSave;
                _sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                                        new Vector2(0.5f, 0.5f), 100f);
                _sprite.name = "DsTrash";
                _sprite.hideFlags = HideFlags.HideAndDontSave;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DsTrashArt] failed: " + e.Message);
            }
            return _sprite;
        }
    }
}
#endif