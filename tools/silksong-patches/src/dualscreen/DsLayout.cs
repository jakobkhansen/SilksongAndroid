#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using UnityEngine;

public readonly struct DsLayout
{
    public const float HudHeight = 240f;
    public const float TabHeight = 176f;

    public readonly float Width, Height, InputHeight;
    public readonly Rect Hud, Body, Tabs;

    public static DsLayout Current
    {
        get
        {
            Vector2 size = DsPresentation.LayoutSize;
            if (size.x == 0f || size.y == 0f)
                size = new Vector2(DsPresentation.PanelW > 0 ? DsPresentation.PanelW : 1240,
                                   DsPresentation.PanelH > 0 ? DsPresentation.PanelH : 1080);
            return new DsLayout(size.x, size.y, DsPresentation.PanelH > 0 ? DsPresentation.PanelH : size.y);
        }
    }

    public DsLayout(float width, float height, float? inputHeight = null)
    {
        if (float.IsNaN(width) || float.IsInfinity(width) || width <= 0f)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (float.IsNaN(height) || float.IsInfinity(height) || height <= 0f)
            throw new ArgumentOutOfRangeException(nameof(height));

        Width = width;
        Height = height;
        InputHeight = inputHeight ?? height;
        if (float.IsNaN(InputHeight) || float.IsInfinity(InputHeight) || InputHeight <= 0f)
            throw new ArgumentOutOfRangeException(nameof(inputHeight));
        float scale = Mathf.Min(1f, height / 1080f);
        Hud = new Rect(0f, 0f, width, HudHeight * scale);
        Tabs = new Rect(0f, height - TabHeight * scale, width, TabHeight * scale);
        Body = new Rect(0f, Hud.yMax, width, Tabs.yMin - Hud.yMax);
    }

    // The input bridge retains its y-up reference height across canvas scaling.
    // The visible frame must instead end at the actual canvas rect's bottom.
    public Vector2 ToLayout(Vector2 panelPoint) => new Vector2(panelPoint.x, InputHeight - panelPoint.y);

    public bool MatchesSize(Vector2 size) =>
        Mathf.Abs(Width - size.x) < 0.5f && Mathf.Abs(Height - size.y) < 0.5f;

    public Rect InBody(Rect local) =>
        new Rect(Body.x + local.x, Body.y + local.y, local.width, local.height);

    public Rect TabRect(int index, int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
        float width = Width / count;
        return new Rect(index * width, Tabs.y, width, Tabs.height);
    }

    public int TabAt(Vector2 layoutPoint, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0 || !Tabs.Contains(layoutPoint)) return -1;
        return Mathf.Min(count - 1, (int)(layoutPoint.x / (Width / count)));
    }
}

public enum DsGestureTarget { None, Body, Tab }

public sealed class DsShellInput
{
    bool _body;
    int _tab = -1;

    public void Reset() { _body = false; _tab = -1; }

    public DsGestureTarget Route(DsGesture gesture, DsLayout layout, int tabCount, out int tab)
    {
        tab = -1;
        Vector2 point = layout.ToLayout(gesture.Position);
        bool inBody = layout.Body.Contains(point);
        if (gesture.Type == DsGestureType.Down)
        {
            _body = inBody;
            _tab = layout.TabAt(point, tabCount);
        }
        if (gesture.Type == DsGestureType.Tap)
        {
            if (_tab >= 0 && layout.TabAt(point, tabCount) == _tab)
            {
                tab = _tab;
                return DsGestureTarget.Tab;
            }
            return _body && inBody ? DsGestureTarget.Body : DsGestureTarget.None;
        }
        if (gesture.Type == DsGestureType.Pinch) _tab = -1;

        // Up can land outside the body, including when a pinch cancels a drag.
        // Retain the origin until the next Down: Tap/Fling follow Up.
        return _body && (inBody || gesture.Type == DsGestureType.Up)
            ? DsGestureTarget.Body : DsGestureTarget.None;
    }
}
#endif
