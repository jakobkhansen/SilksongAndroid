using System;
using System.Linq;
using UnityEngine;

static class Program
{
    static int assertions;

    static void Main()
    {
        Mapping();
        Layout();
        CanvasEdges();
        ShellGestures();
        HudVisibility();
        HudScopes();
        HudFraming();
        FastTap();
        Cancellation();
        Resize();
        Pinch();
        DragAndFling();
        Console.WriteLine("Dual-screen layout, HUD routing and gestures: " + assertions + " assertions passed");
    }

    static void Assert(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    static DsTouch.Point P(TouchPhase phase, float x, float y, double time, int id = 0) =>
        new DsTouch.Point { Phase = phase, Position = new Vector2(x, y), Time = time, FingerId = id };

    static void Types(DsInput input, params DsGestureType[] expected)
    {
        Assert(input.Gestures.Select(g => g.Type).SequenceEqual(expected),
            "Unexpected gestures: " + string.Join(", ", input.Gestures.Select(g => g.Type)));
    }

    static void Mapping()
    {
        // Insets, density, a resized surface and a different canvas scale.
        var surfaces = new[] {
            new Vector2(1240, 1080), new Vector2(1240, 969),
            new Vector2(992, 864), new Vector2(2480, 2160),
        };
        var canvases = new[] {
            new Vector2(1240, 1080), new Vector2(1309, 1023),
        };
        foreach (var surface in surfaces)
        foreach (var canvas in canvases)
        foreach (float inset in new[] { 0f, 55f, 111f })
        foreach (float y in new[] { 1f, 80f, 159f, 300f, 800f, 952f, 1000f, 1079f })
        for (int tab = 0; tab < 5; tab++)
        {
            float x = (tab + 0.5f) * 1240f / 5f;
            Vector2 rendered = new Vector2(x / canvas.x * surface.x,
                                           y / canvas.y * surface.y + inset);
            Vector2 local = rendered - new Vector2(0, inset);
            Vector2 mapped = DsTouch.MapToCanvas(
                new Vector2(local.x / surface.x, local.y / surface.y), canvas, 1080);
            Assert(Math.Abs(mapped.x - x) < 0.001f && Math.Abs(1080 - mapped.y - y) < 0.001f,
                "A rendered point did not map back into its own layout");
            var layout = new DsLayout(1240, 1080);
            int hit = layout.TabAt(layout.ToLayout(mapped), 5);
            // A float round trip at the exact divider can land on either side.
            bool atDivider = Math.Abs(y - layout.Tabs.y) < 0.001f;
            Assert(atDivider ? hit == tab || hit == -1 : hit == (y >= layout.Tabs.y ? tab : -1),
                "Wrong bottom tab: surface=" + surface + " canvas=" + canvas + " inset=" + inset +
                " y=" + y + " mapped-y=" + layout.ToLayout(mapped).y.ToString("R") + " hit=" + hit);
        }
    }

    static void Layout()
    {
        var thor = new DsLayout(1240, 1080);
        Assert(thor.Hud.height == 240f && thor.Tabs.height == 176f && thor.Body.height == 664f,
            "The Thor layout does not reserve the requested HUD and tab space");
        foreach (float width in new[] { 992f, 1240f, 1920f })
        foreach (float height in new[] { 720f, 969f, 1080f, 1440f })
        {
            var layout = new DsLayout(width, height);
            Assert(layout.Hud.y == 0f && Math.Abs(layout.Hud.yMax - layout.Body.y) < 0.001f, "HUD/body boundary differs");
            Assert(Math.Abs(layout.Body.yMax - layout.Tabs.y) < 0.001f &&
                   Math.Abs(layout.Tabs.yMax - height) < 0.001f, "Body/tab boundary differs");
            Assert(layout.Body.height > 0 && layout.Hud.width == width, "Invalid content bounds");
            Assert(layout.TabAt(new Vector2(width * 0.5f, 40), 5) == -1, "Old top tabs are still live");
            for (int count = 1; count <= 5; count++)
            for (int i = 0; i < count; i++)
            {
                Rect tab = layout.TabRect(i, count);
                Assert(layout.TabAt(tab.center, count) == i, "Tab centers disagree with hit testing");
                Assert(Math.Abs(tab.yMax - height) < 0.001f, "Tab does not touch the bottom edge");
            }
            Assert(layout.TabAt(new Vector2(-1, height - 1), 5) == -1, "Left overflow selected a tab");
            Assert(layout.TabAt(new Vector2(width, height - 1), 5) == -1, "Right overflow selected a tab");
            Assert(layout.TabAt(new Vector2(1, height), 5) == -1, "Bottom overflow selected a tab");
            Assert(layout.TabAt(layout.Tabs.center, 0) == -1, "Empty tab bar selected a tab");
            Assert(layout.TabAt(new Vector2(1, layout.Tabs.y), 5) == 0, "Exact top edge missed the tab");
            Assert(layout.TabAt(new Vector2(1, layout.Tabs.y - 0.125f), 5) == -1, "Body edge selected a tab");

            foreach (var local in new[] {
                new Rect(20, 72, width - 40, layout.Body.height - 92),
                new Rect(width - 270, 8, 250, 52),
                new Rect(20, 20, 400, layout.Body.height - 40),
            })
            {
                Rect panel = layout.InBody(local);
                Assert(panel.y == local.y + layout.Body.y && panel.x == local.x, "Body offset was not applied");
                Assert(panel.yMin >= layout.Body.yMin && panel.yMax <= layout.Body.yMax,
                    "Content hitbox overlaps the HUD or bottom tabs");
            }
        }
        Throws<ArgumentOutOfRangeException>(() => new DsLayout(0, 1080));
        Throws<ArgumentOutOfRangeException>(() => new DsLayout(1240, float.NaN));
        Throws<ArgumentOutOfRangeException>(() => DsLayout.Current.TabRect(5, 5));
    }

    static DsGesture Gesture(DsGestureType type, Vector2 point, DsLayout layout) =>
        new DsGesture { Type = type, Position = new Vector2(point.x, layout.InputHeight - point.y), Scale = 1f };

    static void CanvasEdges()
    {
        DsPresentation.LayoutSize = Vector2.zero;
        Assert(DsLayout.Current.Width == 1240 && DsLayout.Current.Height == 1080,
            "A not-yet-laid-out canvas blocked shell startup");
        DsPresentation.LayoutSize = new Vector2(1240, 1080);
        foreach (Vector2 canvas in new[] {
            new Vector2(1240, 1080), new Vector2(1309, 1023), new Vector2(1160, 1155),
        })
        foreach (Vector2 surface in new[] { new Vector2(1240, 1080), new Vector2(1240, 969) })
        foreach (float inputHeight in new[] { 969f, 1080f })
        {
            var layout = new DsLayout(canvas.x, canvas.y, inputHeight);
            Assert(Math.Abs(layout.Tabs.yMax / canvas.y * surface.y - surface.y) < 0.001f,
                "Tab strip does not end at the physical surface edge");
            Assert(layout.MatchesSize(canvas), "Current canvas requested another rebuild");
            Assert(!layout.MatchesSize(canvas + new Vector2(0, 1)), "Canvas resize was not detected");
            for (int i = 0; i < 5; i++)
            {
                var tab = layout.TabRect(i, 5);
                Vector2 pixels = new Vector2(tab.center.x / canvas.x * surface.x, tab.center.y / canvas.y * surface.y);
                Vector2 mapped = DsTouch.MapToCanvas(
                    new Vector2(pixels.x / surface.x, pixels.y / surface.y), canvas, inputHeight);
                Assert(layout.TabAt(layout.ToLayout(mapped), 5) == i,
                    "Bottom tab did not hit after canvas scaling");
                Vector2 bottom = DsTouch.MapToCanvas(new Vector2((i + 0.5f) / 5f, 0.999f), canvas, inputHeight);
                Assert(layout.TabAt(layout.ToLayout(bottom), 5) == i, "Physical bottom edge missed the tab strip");
            }
        }
    }

    static void ShellGestures()
    {
        var layout = new DsLayout(1240, 1080);
        var routing = new DsShellInput();
        int tab;
        for (int i = 0; i < 5; i++)
        {
            Vector2 point = layout.TabRect(i, 5).center;
            Assert(routing.Route(Gesture(DsGestureType.Down, point, layout), layout, 5, out tab) == DsGestureTarget.None,
                "Tab press reached the content");
            routing.Route(Gesture(DsGestureType.Up, point, layout), layout, 5, out tab);
            Assert(routing.Route(Gesture(DsGestureType.Tap, point, layout), layout, 5, out tab) == DsGestureTarget.Tab &&
                   tab == i, "Bottom tap did not select its tab");
        }
        foreach (Vector2 start in new[] { layout.Hud.center, layout.Tabs.center })
        {
            routing.Route(Gesture(DsGestureType.Down, start, layout), layout, 5, out tab);
            foreach (var type in new[] { DsGestureType.Drag, DsGestureType.Pinch, DsGestureType.Tap })
                Assert(routing.Route(Gesture(type, layout.Body.center, layout), layout, 5, out tab) == DsGestureTarget.None,
                    "A gesture from the frame leaked into the body");
        }
        routing.Route(Gesture(DsGestureType.Down, layout.Body.center, layout), layout, 5, out tab);
        Assert(routing.Route(Gesture(DsGestureType.Drag, layout.Body.center, layout), layout, 5, out tab) == DsGestureTarget.Body,
            "Body drag was lost");
        Assert(routing.Route(Gesture(DsGestureType.Drag, layout.Tabs.center, layout), layout, 5, out tab) == DsGestureTarget.None,
            "Dragging into tabs scrolled the content");
        Assert(routing.Route(Gesture(DsGestureType.Up, layout.Tabs.center, layout), layout, 5, out tab) == DsGestureTarget.Body,
            "Drag release outside the body was lost");
        Assert(routing.Route(Gesture(DsGestureType.Pinch, layout.Body.center, layout), layout, 5, out tab) == DsGestureTarget.Body,
            "Pinch after the recognizer's Up was lost");
        Assert(routing.Route(Gesture(DsGestureType.Tap, layout.Tabs.center, layout), layout, 5, out tab) == DsGestureTarget.None,
            "A body gesture switched tabs");
        routing.Reset();
        Assert(routing.Route(Gesture(DsGestureType.Tap, layout.Body.center, layout), layout, 5, out tab) == DsGestureTarget.None,
            "A tap survived suspension");
    }

    static void HudVisibility()
    {
        Assert(DsHudRouting.CanvasCanPresent(true, RenderMode.ScreenSpaceCamera, 0, true, 1, 1),
            "A live second-screen camera was rejected by the overlay-only canvas display field");
        Assert(!DsHudRouting.CanvasCanPresent(true, RenderMode.ScreenSpaceCamera, 1, true, 0, 1),
            "A primary camera was accepted because the canvas's unused display field named the second screen");
        Assert(!DsHudRouting.CanvasCanPresent(true, RenderMode.ScreenSpaceCamera, 1, false, 1, 1),
            "A disabled camera was treated as ready");
        Assert(!DsHudRouting.CanvasCanPresent(true, RenderMode.ScreenSpaceCamera, 1, false, -1, 1),
            "A missing camera was treated as ready");
        Assert(!DsHudRouting.CanvasCanPresent(false, RenderMode.ScreenSpaceCamera, 0, true, 1, 1),
            "A disabled canvas was treated as ready");
        Assert(DsHudRouting.CanvasCanPresent(true, RenderMode.ScreenSpaceOverlay, 1, false, -1, 1),
            "Overlay mode incorrectly required a render camera");
        Assert(!DsHudRouting.CanvasCanPresent(true, RenderMode.ScreenSpaceOverlay, 0, true, 1, 1),
            "A primary overlay was accepted because of its unused camera");
        Assert(!DsHudRouting.CanvasCanPresent(false, RenderMode.ScreenSpaceOverlay, 1, false, -1, 1),
            "A disabled overlay was treated as ready");
        Assert(!DsHudRouting.CanvasCanPresent(true, RenderMode.WorldSpace, 1, true, 1, 1),
            "An unsupported world-space canvas was treated as a secondary display");
        Assert(!DsHudRouting.HasPresentedFrame(true, -1, 0), "Missing capture was considered presented");
        Assert(!DsHudRouting.HasPresentedFrame(false, 99, 100), "Transparent HUD was considered presented");
        Assert(!DsHudRouting.HasPresentedFrame(true, 100, 100), "First capture hid the top HUD before canvas rebuild");
        Assert(!DsHudRouting.HasPresentedFrame(true, 98, 100), "Stale capture was considered presented");
        Assert(DsHudRouting.HasPresentedFrame(true, 99, 100), "Previous capture was not handed to the canvas");
        foreach (bool show in new[] { false, true })
        foreach (bool ready in new[] { false, true })
        foreach (bool native in new[] { false, true })
        foreach (int captured in new[] { -1, 99, 100 })
            Assert(DsHudRouting.HideOnPrimary(show, ready, native, captured, 100) ==
                   (!show && ready && native && captured == 100), "Unsafe primary HUD visibility");
        Assert(!DsHudRouting.HideOnPrimary(false, true, true, -1, -1), "An unrendered HUD suppressed the original");
        Assert(DsHudRouting.IsPrimaryCamera(0, false, 32), "Primary HUD camera not recognized");
        Assert(!DsHudRouting.IsPrimaryCamera(1, false, 32), "Secondary camera was treated as primary");
        Assert(!DsHudRouting.IsPrimaryCamera(0, true, 32), "Map render texture was treated as primary");
        Assert(DsHudRouting.IsPrimaryCamera(0, true, 32, true), "Resolution-scaled primary HUD was not recognized");
        Assert(!DsHudRouting.IsPrimaryCamera(1, true, 32, true), "A secondary target was treated as primary");
        Assert(!DsHudRouting.IsPrimaryCamera(0, false, 64), "A non-HUD camera was changed");
        int foreign = (1 << 17) | 32;
        Assert(DsHudRouting.RestoreMask(foreign, 32 | DsHudRouting.CaptureMask) ==
               (foreign | DsHudRouting.CaptureMask), "Mask restoration overwrote unrelated bits");
        Assert(DsHudRouting.RestoreMask(foreign | DsHudRouting.CaptureMask, 32) == foreign,
            "Mask restoration did not restore a cleared capture bit");
    }

    sealed class RenderTarget
    {
        public int Layer = DsHudRouting.SourceLayer;
        public bool Alive = true, FailCapture, FailRestore;
    }

    static DsHudRenderScope<RenderTarget> RenderScope() => new DsHudRenderScope<RenderTarget>(
        t => t != null && t.Alive, t => t.Layer, (t, layer) => {
            if (t.FailCapture && layer == DsHudRouting.CaptureLayer) throw new InvalidOperationException("capture");
            if (t.FailRestore && layer == DsHudRouting.SourceLayer)
            {
                t.FailRestore = false;
                throw new InvalidOperationException("restore");
            }
            t.Layer = layer;
        });

    static void HudScopes()
    {
        var first = new RenderTarget();
        var second = new RenderTarget();
        var world = new RenderTarget { Layer = 0 };
        var scope = RenderScope();
        scope.Begin(new[] { first, second, first, world, null });
        Assert(scope.Count == 2 && first.Layer == 3 && second.Layer == 3 && world.Layer == 0,
            "Capture changed an unowned renderer or routed a duplicate");
        Throws<InvalidOperationException>(() => scope.Begin(new[] { first }));
        scope.Restore();
        Assert(!scope.Active && first.Layer == 5 && second.Layer == 5, "Render scope did not restore the HUD");
        scope.Restore();

        scope.Begin(new[] { first, second });
        first.Layer = 7;
        second.Alive = false;
        scope.Restore();
        Assert(!scope.Active && first.Layer == 7, "Restoration overwrote a foreign change or retained destroyed objects");

        first.Layer = 5;
        var broken = new RenderTarget { FailCapture = true };
        Throws<InvalidOperationException>(() => scope.Begin(new[] { first, broken }));
        Assert(!scope.Active && first.Layer == 5 && broken.Layer == 5, "Failed capture left the HUD hidden");

        broken.FailCapture = false;
        scope.Begin(new[] { first, broken });
        broken.FailRestore = true;
        Throws<AggregateException>(() => scope.Restore());
        Assert(scope.Active && first.Layer == 5 && scope.Count == 1, "One restore failure blocked other restorations");
        scope.Restore();
        Assert(!scope.Active && broken.Layer == 5, "A failed restoration could not be retried");
    }

    static void HudFraming()
    {
        var viewport = new Vector2(918, 200);
        var anchor = new Vector3(-2.13f, 0.19f, -2f);
        foreach (float pitch in new[] { 0.5f, 0.8f, 0.94f, 1.4f })
        foreach (int masks in new[] { 1, 5, 10, 20, 40 })
        {
            float rightmost = anchor.x + (masks - 1) * pitch;
            var frame = new DsHudFrame(anchor, pitch, rightmost, viewport, 1f, 1f);
            if (masks <= 10)
                Assert(Math.Abs(frame.PixelPitch - 55f) < 0.001f, "Normal mask spacing did not reach the larger target");
            else
                Assert(frame.PixelPitch < 55f, "Overflowing lifeblood was not fitted into the header");

            float halfWidth = frame.HalfHeight * viewport.x / viewport.y;
            var bounds = frame.LayoutBounds;
            Assert(frame.Position.x - halfWidth <= bounds.min.x + 0.001f &&
                   frame.Position.x + halfWidth >= bounds.max.x - 0.001f, "Health layout was cropped horizontally");
            Assert(frame.Position.y - frame.HalfHeight <= bounds.min.y + 0.001f &&
                   frame.Position.y + frame.HalfHeight >= bounds.max.y - 0.001f, "Health layout was cropped vertically");
            Assert(frame.Position.z < bounds.min.z, "HUD camera was placed behind the art");
            float pixelsPerUnit = frame.PixelPitch / pitch;
            float firstX = (anchor.x - frame.Position.x) * pixelsPerUnit + viewport.x * 0.5f;
            float firstY = viewport.y * 0.5f - (anchor.y - frame.Position.y) * pixelsPerUnit;
            Assert(Math.Abs(firstX - frame.PixelPitch * 3.6f) < 0.001f &&
                   Math.Abs(firstY - frame.PixelPitch * 1.4f) < 0.001f, "HUD moved away from its stable top-left anchor");
            Assert(new DsHudFrame(anchor, pitch, rightmost, viewport, 1f, 0.5f).HalfHeight > frame.HalfHeight,
                "Zoom-out did not widen framing");
        }
        var five = new DsHudFrame(anchor, 0.8f, anchor.x + 4 * 0.8f, viewport, 1f, 1f);
        var ten = new DsHudFrame(anchor, 0.8f, anchor.x + 9 * 0.8f, viewport, 1f, 1f);
        Assert(five.Position == ten.Position && five.HalfHeight == ten.HalfHeight,
            "Unlocking normal masks shrank or recentered the HUD");
        var repeated = new DsHudFrame(anchor, 0.8f, anchor.x + 4 * 0.8f, viewport, 1f, 1f);
        Assert(repeated.Position == five.Position && repeated.PixelPitch == five.PixelPitch,
            "Framing retained an expanded animation bound");
        var small = new DsHudFrame(anchor, 0.8f, anchor.x + 4 * 0.8f, viewport * 0.5f, 0.5f, 1f);
        Assert(Math.Abs(small.PixelPitch - 27.5f) < 0.001f, "HUD scaling did not follow the smaller canvas");
        Throws<ArgumentException>(() => new DsHudFrame(new Vector3(float.NaN, 0, 0), 1f, 10f, viewport, 1f, 1f));
        Throws<ArgumentOutOfRangeException>(() => new DsHudFrame(anchor, 0f, 10f, viewport, 1f, 1f));
        Throws<ArgumentOutOfRangeException>(() => new DsHudFrame(anchor, 1f, 10f, Vector2.zero, 1f, 1f));
        Throws<ArgumentOutOfRangeException>(() => new DsHudFrame(anchor, 1f, 10f, viewport, 0f, 1f));
        Throws<ArgumentOutOfRangeException>(() => new DsHudFrame(anchor, 1f, 10f, viewport, 1f, 0f));
    }

    static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { assertions++; return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    static void FastTap()
    {
        var input = new DsInput();
        input.Poll(new[] { P(TouchPhase.Began, 300, 1040, 1), P(TouchPhase.Ended, 300, 1040, 1.01) }, 1);
        Types(input, DsGestureType.Down, DsGestureType.Up, DsGestureType.Tap);
        input.Poll(Array.Empty<DsTouch.Point>(), 1);
        Types(input);
        input.Poll(new[] { P(TouchPhase.Began, 400, 1040, 2), P(TouchPhase.Ended, 400, 1040, 2.1) }, 1);
        Types(input, DsGestureType.Down, DsGestureType.Up, DsGestureType.Tap);
    }

    static void Cancellation()
    {
        var input = new DsInput();
        input.Poll(new[] { P(TouchPhase.Began, 20, 30, 1), P(TouchPhase.Canceled, 20, 30, 1.1) }, 1);
        Types(input, DsGestureType.Down, DsGestureType.Up);
        input.Poll(new[] { P(TouchPhase.Moved, 25, 35, 1.2), P(TouchPhase.Ended, 25, 35, 1.3) }, 1);
        Types(input);
        input.Poll(new[] { P(TouchPhase.Began, 20, 30, 2) }, 1);
        input.Cancel();
        Types(input, DsGestureType.Up);
        input.Poll(new[] { P(TouchPhase.Ended, 20, 30, 2.1) }, 1);
        Types(input);
    }

    static void Resize()
    {
        var input = new DsInput();
        input.Poll(new[] { P(TouchPhase.Began, 20, 30, 1) }, 1);
        input.Poll(new[] { P(TouchPhase.Moved, 40, 60, 1.1), P(TouchPhase.Ended, 40, 60, 1.2) }, 2);
        Types(input, DsGestureType.Up);
        input.Poll(new[] { P(TouchPhase.Began, 20, 30, 2), P(TouchPhase.Ended, 20, 30, 2.1) }, 2);
        Types(input, DsGestureType.Down, DsGestureType.Up, DsGestureType.Tap);
    }

    static void Pinch()
    {
        var input = new DsInput();
        input.Poll(new[] {
            P(TouchPhase.Began, 100, 100, 1, 2),
            P(TouchPhase.Began, 300, 100, 1.01, 7),
            P(TouchPhase.Moved, 400, 100, 1.02, 7),
        }, 1);
        Types(input, DsGestureType.Down, DsGestureType.Up, DsGestureType.Pinch);
        Assert(Math.Abs(input.Gestures.Last().Scale - 1.5f) < 0.001f, "Incorrect pinch scale");
        input.Poll(new[] {
            P(TouchPhase.Ended, 100, 100, 1.1, 2),
            P(TouchPhase.Moved, 450, 100, 1.2, 7),
            P(TouchPhase.Ended, 450, 100, 1.3, 7),
        }, 1);
        Types(input);
    }

    static void DragAndFling()
    {
        var input = new DsInput();
        input.Poll(new[] {
            P(TouchPhase.Began, 100, 100, 1),
            P(TouchPhase.Moved, 300, 100, 1.05),
            P(TouchPhase.Ended, 300, 100, 1.06),
        }, 1);
        Types(input, DsGestureType.Down, DsGestureType.Drag, DsGestureType.Up, DsGestureType.Fling);
        input.Poll(new[] {
            P(TouchPhase.Began, 100, 100, 2),
            P(TouchPhase.Moved, 300, 100, 2.05),
            P(TouchPhase.Ended, 300, 100, 3),
        }, 1);
        Types(input, DsGestureType.Down, DsGestureType.Drag, DsGestureType.Up);
        input.Poll(new[] { P(TouchPhase.Began, 100, 100, 4), P(TouchPhase.Ended, 200, 100, 4.5) }, 1);
        Assert(input.Gestures.All(g => g.Type != DsGestureType.Tap), "Release movement became a tap");
    }
}

// Only the scene-dependent lookup is replaced. The tests execute the actual
// gesture recognizer and coordinate mapping against Unity's managed types.
public static class DsPresentation
{
    public static int PanelW => 1240;
    public static int PanelH => 1080;
    public static Vector2 LayoutSize { get; set; } = new Vector2(1240, 1080);
    public static Vector2 FromSurface(Vector2 point) =>
        DsTouch.MapToCanvas(point, new Vector2(1240, 1080), 1080);
}
