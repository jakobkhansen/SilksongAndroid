package dev.silksong.shell;

import android.app.Activity;
import android.app.Dialog;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.view.Display;
import android.view.InputDevice;
import android.view.MotionEvent;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.inspector.WindowInspector;

import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.List;

// Unity still owns the Presentation and its render surface. We own its touch
// listener and visibility: disabling a Unity camera cannot hide an Android
// window, and filtering uGUI cannot filter Input.anyKeyDown in a cutscene.
final class SecondaryDisplay implements SurfaceHolder.Callback
{
    private static final String TAG = "SilksongDualScreen";
    private final Activity activity;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final TouchBuffer touches = new TouchBuffer();
    private final View.OnTouchListener touchListener = this::onTouch;
    private final View.OnGenericMotionListener motionListener = this::onGenericMotion;
    private final View.OnLayoutChangeListener layoutListener =
        (view, left, top, right, bottom, oldLeft, oldTop, oldRight, oldBottom) -> updateSurface();
    private boolean enabled;
    private boolean started;
    private boolean destroyed;
    private boolean discardGesture;
    private View root;
    private SurfaceView surface;

    private final Runnable monitor = new Runnable()
    {
        @Override public void run()
        {
            if (!enabled || destroyed) return;
            try
            {
                findSurface();
                updateSurface();
                handler.postDelayed(this, 250);
            }
            catch (ReflectiveOperationException | RuntimeException e)
            {
                Log.e(TAG, "Cannot take ownership of the secondary window", e);
                setEnabledOnUiThread(false);
            }
        }
    };

    SecondaryDisplay(Activity activity) { this.activity = activity; }

    void setEnabled(boolean value)
    {
        activity.runOnUiThread(() -> {
            if (!destroyed) setEnabledOnUiThread(value);
        });
    }

    private void setEnabledOnUiThread(boolean value)
    {
        enabled = value;
        handler.removeCallbacks(monitor);
        if (value) monitor.run();
        else
        {
            if (root != null) root.setVisibility(View.GONE);
            touches.setSurface(0, 0, false);
        }
    }

    void onStart()
    {
        started = true;
        if (enabled) setEnabledOnUiThread(true);
    }

    void onStop()
    {
        started = false;
        // Do not use focus loss: touching a different display can change focus
        // while the game is still visible. onStop means it really was hidden.
        if (root != null) root.setVisibility(View.GONE);
        touches.reset();
        updateSurface();
    }

    void onDestroy()
    {
        setEnabledOnUiThread(false);
        destroyed = true;
        if (surface != null)
        {
            surface.getHolder().removeCallback(this);
            surface.removeOnLayoutChangeListener(layoutListener);
        }
        root = null;
        surface = null;
    }

    double[] poll() { return touches.drain(); }

    private void findSurface() throws ReflectiveOperationException
    {
        if (root != null && root.isAttachedToWindow() &&
            surface != null && surface.isAttachedToWindow()) return;

        if (surface != null)
        {
            surface.getHolder().removeCallback(this);
            surface.removeOnLayoutChangeListener(layoutListener);
        }
        root = null;
        surface = null;
        touches.setSurface(0, 0, false);

        int mainDisplay = activity.getWindowManager().getDefaultDisplay().getDisplayId();
        for (View candidate : windowViews())
        {
            Display display = candidate.getDisplay();
            if (display == null || display.getDisplayId() == mainDisplay ||
                !candidate.isAttachedToWindow()) continue;
            SurfaceView found = findSurfaceView(candidate);
            if (found == null) continue;
            root = candidate;
            surface = found;
            surface.setOnTouchListener(touchListener);
            surface.setOnGenericMotionListener(motionListener);
            surface.addOnLayoutChangeListener(layoutListener);
            surface.getHolder().addCallback(this);
            discardGesture = false;
            Log.i(TAG, "Captured secondary surface on display " + display.getDisplayId());
            keepOpenOnBack(candidate);
            return;
        }
    }

    // The secondary window is Unity's Presentation, a Dialog, so a back
    // gesture on that display cancels it. Unity never recreates it and its
    // Vulkan swapchain then dereferences the dead surface (issue #26).
    // There is no public route from a window's views to its Dialog;
    // DecorView.mWindow is an "unsupported" hidden API, which Android still
    // allows for reflection (verified on the Thor, Android 13).
    private static void keepOpenOnBack(View decor)
    {
        try
        {
            for (Class<?> c = decor.getClass(); c != null && c != View.class; c = c.getSuperclass())
            {
                Field field;
                try { field = c.getDeclaredField("mWindow"); }
                catch (NoSuchFieldException e) { continue; }
                field.setAccessible(true);
                Object window = field.get(decor);
                Window.Callback callback = window instanceof Window ? ((Window) window).getCallback() : null;
                if (callback instanceof Dialog)
                {
                    ((Dialog) callback).setCancelable(false);
                    Log.i(TAG, "Secondary window no longer closes on back");
                    return;
                }
                break;
            }
            Log.w(TAG, "Secondary window is not a Dialog; back can still close it");
        }
        catch (ReflectiveOperationException | RuntimeException e)
        {
            Log.w(TAG, "Cannot stop back from closing the secondary window", e);
        }
    }

    private List<View> windowViews() throws ReflectiveOperationException
    {
        if (Build.VERSION.SDK_INT >= 29) return WindowInspector.getGlobalWindowViews();

        // WindowInspector was added in Q. These read-only accessors also exist
        // on O/P; no Unity-private fields or obfuscated class names are needed.
        Class<?> type = Class.forName("android.view.WindowManagerGlobal");
        Object manager = type.getMethod("getInstance").invoke(null);
        String[] names = (String[]) type.getMethod("getViewRootNames").invoke(manager);
        Method getRoot = type.getMethod("getRootView", String.class);
        List<View> views = new ArrayList<>();
        for (String name : names)
        {
            Object view = getRoot.invoke(manager, name);
            if (view instanceof View) views.add((View) view);
        }
        return views;
    }

    private static SurfaceView findSurfaceView(View view)
    {
        if (view instanceof SurfaceView) return (SurfaceView) view;
        if (view instanceof ViewGroup)
        {
            ViewGroup group = (ViewGroup) view;
            for (int i = 0; i < group.getChildCount(); i++)
            {
                SurfaceView found = findSurfaceView(group.getChildAt(i));
                if (found != null) return found;
            }
        }
        return null;
    }

    private void updateSurface()
    {
        if (root == null || surface == null) return;
        boolean visible = enabled && started;
        int visibility = visible ? View.VISIBLE : View.GONE;
        if (root.getVisibility() != visibility) root.setVisibility(visibility);
        touches.setSurface(surface.getWidth(), surface.getHeight(),
            visible && surface.isAttachedToWindow() && surface.getHolder().getSurface().isValid());
    }

    @Override public void surfaceCreated(SurfaceHolder holder) { updateSurface(); }
    @Override public void surfaceChanged(SurfaceHolder holder, int format, int width, int height)
    {
        updateSurface();
    }
    @Override public void surfaceDestroyed(SurfaceHolder holder)
    {
        touches.setSurface(0, 0, false);
    }

    private boolean onGenericMotion(View view, MotionEvent event)
    {
        if ((event.getSource() & InputDevice.SOURCE_CLASS_POINTER) != 0) return true;
        return activity.onGenericMotionEvent(event);
    }

    private boolean onTouch(View view, MotionEvent event)
    {
        if (!enabled || !started) return true;
        updateSurface();
        int action = event.getActionMasked();
        if (action == MotionEvent.ACTION_DOWN) discardGesture = false;
        if (discardGesture) return true;
        int width = view.getWidth(), height = view.getHeight();
        if (width <= 0 || height <= 0)
        {
            Log.w(TAG, "Cancelled touch on an unmeasured secondary surface");
            touches.reset();
            discardGesture = true;
            return true;
        }

        int first = 0, count = event.getPointerCount(), phase;
        switch (action)
        {
            case MotionEvent.ACTION_DOWN:
            case MotionEvent.ACTION_POINTER_DOWN:
                phase = 0; // Unity TouchPhase.Began
                first = event.getActionIndex();
                count = first + 1;
                break;
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_POINTER_UP:
                phase = 3; // Ended
                first = event.getActionIndex();
                count = first + 1;
                break;
            case MotionEvent.ACTION_MOVE:
                phase = 1; // Moved
                break;
            case MotionEvent.ACTION_CANCEL:
                phase = 4; // Canceled
                break;
            default:
                return true;
        }

        for (int i = first; i < count; i++)
        {
            if (!touches.offer(event.getPointerId(i), phase,
                event.getX(i) / width, event.getY(i) / height, event.getEventTime()))
            {
                Log.w(TAG, "Secondary touch queue overflow; cancelled the gesture");
                discardGesture = true;
                break;
            }
        }
        // Never inject this event into Unity: even a release can become a
        // synthetic mouse event, and the game's raw input bypasses uGUI.
        return true;
    }

    static final class TouchBuffer
    {
        static final int HEADER = 4, STRIDE = 5, CAPACITY = 256;
        private final double[] pending = new double[STRIDE * CAPACITY];
        private int count, width, height;
        private long generation;
        private boolean ready;

        synchronized void setSurface(int w, int h, boolean available)
        {
            boolean next = available && w > 0 && h > 0;
            if (width == w && height == h && ready == next) return;
            width = w;
            height = h;
            ready = next;
            reset();
        }

        synchronized void reset()
        {
            count = 0;
            generation++;
        }

        synchronized boolean offer(int id, int phase, double x, double y, long time)
        {
            if (!ready) return true;
            if (count == CAPACITY)
            {
                reset();
                return false;
            }
            int at = count++ * STRIDE;
            pending[at] = id;
            pending[at + 1] = phase;
            pending[at + 2] = x;
            pending[at + 3] = y;
            pending[at + 4] = time;
            return true;
        }

        synchronized double[] drain()
        {
            double[] batch = new double[HEADER + count * STRIDE];
            batch[0] = generation;
            batch[1] = width;
            batch[2] = height;
            batch[3] = ready ? 1 : 0;
            System.arraycopy(pending, 0, batch, HEADER, count * STRIDE);
            count = 0;
            return batch;
        }
    }
}
