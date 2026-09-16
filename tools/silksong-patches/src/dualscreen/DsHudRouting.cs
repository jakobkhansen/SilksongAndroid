#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

public static class DsHudRouting
{
    public const int SourceLayer = 5;
    public const int CaptureLayer = 3;
    public const int CaptureMask = 1 << CaptureLayer;

    public static bool CanvasCanPresent(bool canvasActive, RenderMode mode, int canvasDisplay,
                                        bool cameraActive, int cameraDisplay, int targetDisplay)
    {
        if (!canvasActive) return false;
        switch (mode)
        {
            case RenderMode.ScreenSpaceOverlay:
                return canvasDisplay == targetDisplay;
            case RenderMode.ScreenSpaceCamera:
                return cameraActive && cameraDisplay == targetDisplay;
            default:
                return false;
        }
    }

    public static bool HasPresentedFrame(bool imageVisible, int capturedFrame, int currentFrame)
    {
        return imageVisible && capturedFrame >= 0 && capturedFrame == currentFrame - 1;
    }

    public static bool HideOnPrimary(bool showTop, bool secondaryReady, bool nativeVisible,
                                     int capturedFrame, int currentFrame)
    {
        return !showTop && secondaryReady && nativeVisible &&
               capturedFrame >= 0 && capturedFrame == currentFrame;
    }

    public static bool IsPrimaryCamera(int display, bool hasTexture, int mask, bool nativePrimary = false)
    {
        return display == 0 && (!hasTexture || nativePrimary) && (mask & (1 << SourceLayer)) != 0;
    }

    public static int RestoreMask(int current, int original)
    {
        return (current & ~CaptureMask) | (original & CaptureMask);
    }

    public static int PrimaryMask(int original, bool hideHud, bool canvasBatchRouted)
    {
        if (hideHud) return original & ~CaptureMask;
        return canvasBatchRouted ? original | CaptureMask : original;
    }
}

// Sprite scopes surround a camera render; canvas scopes span batching through
// the end of the frame. Both restore the original layer before native updates.
public sealed class DsHudRenderScope<T> where T : class
{
    readonly Func<T, bool> _alive;
    readonly Func<T, int> _getLayer;
    readonly Action<T, int> _setLayer;
    readonly List<T> _changed = new List<T>();

    public bool Active { get; private set; }
    public int Count => _changed.Count;

    public DsHudRenderScope(Func<T, bool> alive, Func<T, int> getLayer, Action<T, int> setLayer)
    {
        _alive = alive;
        _getLayer = getLayer;
        _setLayer = setLayer;
    }

    public void Begin(IList<T> targets)
    {
        if (Active) throw new InvalidOperationException("A HUD render scope is already active");
        Active = true;
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (!_alive(target) || _getLayer(target) != DsHudRouting.SourceLayer) continue;
                _changed.Add(target);
                _setLayer(target, DsHudRouting.CaptureLayer);
            }
        }
        catch
        {
            Restore();
            throw;
        }
    }

    public void Restore()
    {
        List<Exception> failures = null;
        for (int i = _changed.Count - 1; i >= 0; i--)
        {
            var target = _changed[i];
            try
            {
                if (_alive(target) && _getLayer(target) == DsHudRouting.CaptureLayer)
                    _setLayer(target, DsHudRouting.SourceLayer);
                _changed.RemoveAt(i);
            }
            catch (Exception e)
            {
                if (failures == null) failures = new List<Exception>();
                failures.Add(e);
            }
        }
        Active = _changed.Count > 0;
        if (failures != null) throw new AggregateException("Could not restore HUD render layers", failures);
    }
}

public readonly struct DsHudFrame
{
    public const float MaskPixelPitch = 55f;
    // Margins around the health row, in mask pitches. One pitch is
    // MaskPixelPitch on the panel, so 55 px, which is how these convert.
    //
    // The lead is measured from the first MASK, but what sits furthest left is
    // the crest/bind icon beside it, and at 2.9 the frame cut through that
    // icon: sampling the panel showed its leftmost column already carrying 16
    // pixels of ink and climbing, which is a slice through a round shape rather
    // than the edge of one. Margin added on top of a crop is spent widening the
    // art back out before any of it becomes space, which is why two rounds of
    // adding a few pixels looked like they did nothing at all. The 22 px here
    // uncrops the icon and leaves about 5 px of air beside it.
    //
    // Public because the regression that guards the anchor asserts against
    // them: written down twice, they drift apart, and the test then asserts
    // that the HUD has not moved from wherever it has just moved to.
    public const float LeadPitches = 2.9f + 22f / MaskPixelPitch;
    // The space above is governed by the CREST, not by the mask row it is
    // measured from: crests differ in size, and a larger one reaches higher
    // than the masks beside it. Trimmed hard this looked right on a small crest
    // and clipped the moment a bigger one was equipped, so it is back at the
    // 1.4 pitches the HUD was originally framed with, plus 2 px of air so the
    // largest of them -- the Wanderer's Crest -- is not flush against the top
    // edge either. The lead still saves 16 px on the left, where nothing varies.
    public const float AboveRowPitches = 1.0f + 24f / MaskPixelPitch;
    // NOT raised to compensate for the shorter lead above, which was the
    // instinct and is wrong twice over. The band's bottom is top - height, so
    // trimming the margin above the row lowers both edges together: the rings
    // below keep their room and simply move up with everything else. And the
    // height is one of the terms that limits the scale, so growing it shrinks
    // the mask spacing below the 55 px this panel is authored around -- which
    // is what the framing regression caught.
    public const float HeightPitches = 3.6f;

    public readonly Vector3 Position;
    public readonly float HalfHeight;
    public readonly float PixelPitch;
    public readonly Bounds LayoutBounds;

    public DsHudFrame(Vector3 firstMask, float pitch, float rightmost,
                      Vector2 viewport, float uiScale, float zoom)
    {
        if (!Finite(firstMask)) throw new ArgumentException("HUD anchor must be finite", nameof(firstMask));
        if (!Finite(pitch) || pitch <= 0f) throw new ArgumentOutOfRangeException(nameof(pitch));
        if (!Finite(rightmost)) throw new ArgumentOutOfRangeException(nameof(rightmost));
        if (!Finite(viewport.x) || !Finite(viewport.y) || viewport.x <= 0f || viewport.y <= 0f)
            throw new ArgumentOutOfRangeException(nameof(viewport));
        if (!Finite(uiScale) || uiScale <= 0f) throw new ArgumentOutOfRangeException(nameof(uiScale));
        if (!Finite(zoom) || zoom < 0.5f || zoom > 2f)
            throw new ArgumentOutOfRangeException(nameof(zoom));

        float left = firstMask.x - LeadPitches * pitch;
        float right = Mathf.Max(firstMask.x, rightmost) + pitch * 0.75f;
        float top = firstMask.y + AboveRowPitches * pitch;
        float height = HeightPitches * pitch;
        LayoutBounds = new Bounds(new Vector3((left + right) * 0.5f, top - height * 0.5f, firstMask.z),
                                  new Vector3(right - left, height, 0f));

        float pixelsPerUnit = Mathf.Min(MaskPixelPitch * uiScale / pitch,
            Mathf.Min(viewport.x / LayoutBounds.size.x, viewport.y / height)) * zoom;
        PixelPitch = pixelsPerUnit * pitch;
        HalfHeight = viewport.y / (2f * pixelsPerUnit);
        Position = new Vector3(left + viewport.x / (2f * pixelsPerUnit),
                               top - HalfHeight, firstMask.z - 20f);
    }

    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    public static bool Finite(Vector3 value)
    {
        return Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
#endif
