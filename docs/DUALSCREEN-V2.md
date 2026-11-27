# Dual screen, second attempt

A plan. Living document — it is edited as the thing gets built, and the
decisions in it are meant to be argued with.

**Current input and lifetime boundary:** Unity still renders directly to its
second display. `tools/depot-to-apk/shell/SecondaryDisplay.java` captures that
window's surface-local pointer events before Unity sees them, and hides the
window when the game activity stops. The earlier Input System/uGUI fence below
is historical: it did not cover cutscenes reading `Input.anyKeyDown`.

---

## 1. What used to exist, and why it was thrown away

V1 was ~900 lines that **mirrored** the game onto the second panel. It is
deleted; this section is why, and it is kept because every constraint below is
still true of the game and would bite the same way a second time.

```
hudCamera  ──pose/projection copied──▶  capture Camera (culls one private layer)
                                              │ targetTexture
                                              ▼
                                    RenderTexture 1728×1080
                                              │ AsyncGPUReadback
                                              ▼
                                    managed byte[] hand-off
                                              │ worker thread: vertical flip + memcpy
                                              ▼
                                    mmap'd file, 3 slots  ──frameReady(slot)──▶ Java
                                              │
                                              ▼
                          Presentation → Bitmap.copyPixelsFromBuffer → ImageView(FIT_CENTER)
```

It works, and the transport half of it is genuinely good. The *content* half
is the problem, and it is not fixable in place:

* **The aspect is wrong and cannot be made right.** The game's inventory is
  authored for 16:9. The panel is 1240×1080. `FIT_CENTER` therefore letterboxes
  it: measured from a live screenshot, **~40 % of the second panel is black**.
  Cropping instead (the current 1728×1080 is already a mild crop) eats the
  frame art at the edges. There is no crop or fit that makes a 16:9 layout
  fill a 1.15:1 panel.
* **It only shows something when the game's own menu is open.** The second
  screen is dead weight during play. The whole point of a second screen is
  that it is *always* showing something useful.
* **Everything is a fight with the game.** Re-layering the inventory subtree
  every frame, keeping `Inventory ScreenPlane` off the capture layer, cloning
  the map quads with fresh materials so `NestedFadeGroup` cannot fade our copy,
  re-running `MapNextAreaDisplay.Refresh` to undo `SetupMap`'s direct
  `SetActive`, cloning the No-Map symbol because the L1 FSM fights us for it.
  Each of these is a correct fix for a real problem, and each one exists only
  because we are stealing the game's own live UI instead of drawing our own.
* **It cannot grow.** "Add a crests screen" has no meaning when the content is
  whatever the game happens to be drawing.
* **It is noisy.** Read off the running device: during scene transitions
  `Update` calls `GameManager.instance`, `GameCameras.instance` and
  `HeroController.instance` unguarded, and each miss is a `FindObjectOfType`
  plus a `LogError` **with a stack trace, three times a frame**. Driving
  `TryOpenQuickMap` ourselves also makes `MapNextAreaDisplay.Refresh` log
  `did not have map scene parent`. None of this is fatal; all of it is the
  sound of code operating something it does not own.

So: **keep the shape of the transport, rebuild it, and replace the source
entirely.** Draw our own UI, at the panel's own resolution, into the same pipe.

---

## 2. Measured facts

Everything below was read off the device, not assumed.

| | |
| --- | --- |
| Second display | Android `displayId 4`, name `Screen-2` |
| Native panel | 1080×1240 portrait; **1240×1080 in the app's landscape rotation** |
| Aspect | 1240 : 1080 = 31:27 ≈ **1.148 : 1** (not 6:3 — the plan targets the real number) |
| Refresh | 120 Hz capable (modes 3/4: 120 and 60) |
| Flags | `FLAG_PRESENTATION`, `FLAG_SECURE`, own touchscreen (`touch EXTERNAL`) |
| Density | 369 dpi (~2.31×) |
| Main display | 1920×1080 landscape |
| Graphics API | **Vulkan only** — `PlayerImage.kt` sets API 21 and shader slices are stripped to Vulkan |
| Unity | **6000.0.52f1** (Unity 6); the game was built with 6000.0.50f1 — see `tools/depot-to-apk/README.md` |
| Depot has | `UnityEngine.UI.dll`, `Unity.TextMeshPro.dll`, `Coffee.SoftMaskForUGUI.dll`, `Unity.Addressables.dll` |
| CoreModule has | `AsyncGPUReadback.RequestIntoNativeArray`, `NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray` (both verified present in the shipped assembly) |
| Free layers | `TagManager.asset` leaves indices **3 and 6** unnamed and unused |

Two things the device disagreed with the code about:

* `dumpsys` reports the second display's *app* area as **1240×969**, not
  1240×1080 — 111 px of system bars. The screenshot shows V1's image centred in
  the full 1240×1080, so the Presentation window is probably already
  fullscreen, but "probably" is not good enough: **M1 logs the measured view
  size and, if it is short, forces the Presentation immersive** rather than
  quietly losing 10 % of the panel.
* The shared framebuffer is at
  `/sdcard/Android/data/com.jakobkhansen.silksong/cache/dualscreen_fb.bin` — 22 MB on
  **external, FUSE-backed storage** (`ext_data_rw`). See §3.

**Target: 60 fps on the second panel.**

`adb` note, since it cost time: `screencap -d 4` (the *logical* id) silently
writes a 0-byte file. It wants the **physical** id from
`dumpsys SurfaceFlinger --display-id`:

```sh
adb shell screencap -d 4630946482288158084 /sdcard/s.png && adb pull /sdcard/s.png
```

---

## 3. Decision 1 — how the pixels get there

> **SETTLED 2026-08-20 by the M0 spike: Option A. Unity's own multi-display
> support drives the panel, and does not disturb the main screen.** Everything
> below about shared memory, readback and the Java Presentation is kept as the
> reasoning behind the fallback — but it is now a road not taken.

### The measurement

`DualScreenSpike.cs`, configured at runtime by flags in its marker file so that
one device build could test every combination (a build is ~3 min of APK plus
~4 min of on-device IL2CPP; bisecting by rebuilding would have cost a quarter
of an hour per guess).

With a camera **and** a uGUI canvas live on display 1 — confirmed by a
`rig=True` heartbeat logged inside the same window as the capture, which
matters, see below — the **main** display was sampled through
`dumpsys SurfaceFlinger --latency` on the GameActivity BLAST layer:

| | main display, rig live on display 1 |
| --- | --- |
| refresh | 8.33 ms (120 Hz) |
| effective fps | **120.2** |
| interval p50 / p95 / p99 | 8.32 / 8.32 / **8.32 ms** |
| interval max | **8.32 ms** |
| missed vsyncs | **0 of 253 frames**, across two samples |

p99 equal to max equal to the refresh period is as clean as this can be. No
flicker by eye either. The panel showed our pattern — four corner markers at
1240×1080, a sweeping bar, a hue-cycling background — so the surface covers the
whole panel and is genuinely live.

Three things this cost, worth writing down because each was nearly a wrong
conclusion:

* **A first measurement was invalid and looked fine.** It was taken without
  confirming the spike was actually running, and the device also runs a
  second-screen launcher app (`rip.moth.cocoonshell`) that draws on the same
  panel. "The panel showed something" proves nothing here. Every claim about
  the panel now has to be paired with a log line showing the rig alive.
* **`Display.displays[1].renderingWidth/Height` reads `0x0` forever** on this
  platform, even while Unity is demonstrably rendering to the panel at
  1240×1080. It is not a readiness signal. A guard that waited for it refused
  to build the rig at all, and the panel stayed dark — the guard was wrong, not
  the platform. (`systemWidth/Height` does populate, but only *after* rendering
  starts, so it cannot be waited on either.)
* **Do not render to the display immediately after `Activate()`.** One run
  died with SIGBUS (`BUS_ADRALN`) on Unity's `UnityGfxDeviceW` thread ~190 ms
  after building the rig 10 frames post-activate. Every run that waited ~1 s
  first has been stable. `Activate()` is asynchronous and returning from it
  means nothing; since there is no readiness flag to poll, the rig is built on
  a short timer instead.

### What that deletes

The entire capture-and-push apparatus:

* the `RenderTexture` and the mirror camera,
* `AsyncGPUReadback` — and with it the ~50 ms touch-to-photon latency, the
  in-flight-request-outlives-the-mapping crash, and the `Allocator.None` /
  `AtomicSafetyHandle` question,
* the 22 MB memory-mapped file, its slots, its page alignment, its
  external-vs-internal-storage problem, and the publish protocol with its ARM64
  memory-ordering hazard,
* the worker thread and its vertical flip,
* `Bitmap.copyPixelsFromBuffer`, the `ImageView`, and the Java-side local-echo
  trick that existed only to hide readback latency,
* `DualScreenBridge.java` and `DualScreenPresentation.java` — **entirely**, now
  that input is answered too.

What replaces it is: **a `Camera` with `targetDisplay = 1`.**

Render-on-dirty also stops being load-bearing. It existed to avoid paying for a
readback on frames that changed nothing; with no readback there is nothing to
avoid. It stays available if the panel ever costs measurable frame time — and
so does the `Canvas.ForceUpdateCanvases` ordering trap that comes with it, which
is now a footnote rather than an acceptance criterion.

### Input: answered, and it brought a new problem

Unity **does** receive touches from the second panel. That is the good half.

The bad half is that Option A removes the thing that used to keep them apart:
V1's `Presentation` window swallowed panel touches, so Unity never saw them.
Without it, panel touches land on the game — observed directly, touching the
bottom screen operates the menu on the top one, in both directions.

Measured, by injecting known taps and comparing:

| | legacy `Input.touches` | Input System |
| --- | --- | --- |
| main-screen tap `(1500,900)` of 1920×1080 | `raw=(1000,120)` — **scaled** into Unity's 1280×720 render space | `displayIndex=0` |
| panel tap `(300,300)` of 1240×1080 | `raw=(300,780)` — **unscaled** panel pixels, y mirrored in 1080 | `displayIndex=1` |

So the legacy stream is two coordinate spaces mixed together with overlapping
ranges and no attribution — `Display.RelativeMouseAt` returns `(0,0,0)` for
everything on this platform, so it is not the discriminator it is elsewhere.

**The new Input System is.** `Touchscreen.current.touches[i].displayIndex`
reported `0` for 120 top-screen touches and `1` for every bottom-screen touch,
with no crossover. That is the clean answer, and it means no Java survives.

Which leaves fencing the game off from display-1 touches. The game's event
system is `HollowKnightInputModule : StandaloneInputModule`
(`InControl/HollowKnightInputModule.cs`), and `StandaloneInputModule` exposes
**`inputOverride`**, a public `BaseInput` property that replaces where uGUI
reads its pointers from. Verified to compile against the depot's
`UnityEngine.UI`. So a `BaseInput` subclass that hides display-1 touches
fences the game's entire menu system off, without modifying a line of game
code. Our own screens read the Input System directly and take the touches the
game no longer sees.

Correlating the two streams looks straightforward: legacy `fingerId=0`
corresponded to Input System `touchId=1`, i.e. the Input System's ids are the
same pointers, one-based. To be confirmed in M2 rather than assumed.

`InventoryTouchInput` — our own main-screen touch support — needs the same
filter, for the same reason.

### Option A — Unity multi-display (`Display.displays[1].Activate()`)  ← **chosen, measured**



Zero copies. A real `Camera` with `targetDisplay = 1`, native 1240×1080, and
Unity gets touch on that display for free via `Touch.displayIndex`.

Rejected once already: on this device's Adreno/Vulkan path Unity presents two
swapchains per frame and the secondary present contends with the primary,
**flickering the main screen**. That is why `DualScreenBridge` exists at all.

**That prior was wrong, or has expired.** The spike above measured no
contention whatsoever on Unity 6000.0.50f1 — p99 present interval exactly equal
to the refresh period. Whether the old observation was a different Unity, a
different driver, or a misattribution no longer matters; the number is the
number, and it was cheap to get. The lesson worth keeping is that the belief
sat unexamined in a Javadoc for long enough to shape the whole architecture
around it.

The `Touch.displayIndex` half of the claim *was* wrong, though — see above.

Everything from here down is the road not taken.

At 1240×1080 ARGB8888 a frame is **5.36 MB**. At 60 fps every full pass over a
frame costs **321 MB/s** of memory bandwidth. The old pipeline makes **four**
such passes (readback→managed, flip→mmap, mmap→Bitmap, Bitmap→GPU) — 1.3 GB/s,
and it was only ever asked for 30 fps. That was the budget B had to beat.


### Option B — the current pipe, with the fat cut out  ← **chosen**

Same shape, three of the four passes removed or moved off the hot path, and
three bugs fixed that only show up when you ask it for 60 fps:

1. **`AsyncGPUReadback.RequestIntoNativeArray`, aliased directly onto the mmap
   slot.** `NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>`
   wraps the mapped pointer with `Allocator.None`, so the GPU readback **lands
   in shared memory directly**. The managed `byte[]` hand-off, the main-thread
   `CopyTo`, and the worker's flip-copy all disappear; main-thread cost per
   frame becomes ~0.
   Two things this needs and does not get for free: the array has **no
   `AtomicSafetyHandle`**, so `NativeArrayUnsafeUtility.SetAtomicSafetyHandle`
   must be called or a development build throws; and the mapping **must outlive
   every in-flight request** (see teardown, below).
   *If Unity refuses `Allocator.None` here*, fall back to one persistent
   `NativeArray` plus a single `UnsafeUtility.MemCpy` on the worker thread —
   still one pass, still off the main thread. Decided in M1, not later.
2. **The vertical flip stops being a copy.** GPU readback is bottom-up, Android
   is top-down; today that is a 1080-row `Marshal.Copy` loop. Set
   `ImageView.setScaleY(-1f)` instead — a matrix on the draw, no pixels
   touched. Free.
3. **Render only when something changed.** The camera is disabled and
   `camera.Render()` is called explicitly, only on dirty frames. A still
   inventory screen then costs *nothing*: no render, no readback, no publish.
   60 fps is a ceiling for animation and map panning, not a treadmill; V1 pushed
   30 fps of identical frames forever.
   The trap here is **ordering**, and it is easy to miss: uGUI rebuilds through
   `CanvasUpdateRegistry` on `Canvas.willRenderCanvases`, and TMP rebuilds on
   the same event — both of which run in the player loop *after* `LateUpdate`.
   Rendering from `LateUpdate` would therefore draw the *previous* frame's
   canvas mesh, and for a screen that dirties once and then idles, the change
   might never reach the panel at all. So a dirty render is
   `Canvas.ForceUpdateCanvases()` (plus `ForceMeshUpdate()` on dirtied text)
   **and then** `camera.Render()`. This is in M1's acceptance criteria.
   The game is built-in pipeline — it references no URP or SRP assembly — so
   `Camera.Render()` is supported and does what it says.
4. **The buffer moves to internal storage**, and the protocol stops trying to
   be clever. Both below.


Java still does one `copyPixelsFromBuffer` (5.36 MB, ~0.5 ms) plus the texture
upload, on the Presentation's own UI thread, which is otherwise idle. Two
passes total instead of four, and neither is on the game's thread.

#### The shared block, in full

One mapping, mapped **read-write from both sides** (V1 mapped it read-only in
Java, which forecloses any return channel).

```
0            Header, 4096 B
  0   u32    magic 'DSC2', version, width, height, slotCount, slotBytes, flags
4096         slot 0
4096+n       slot 1                n = slotBytes, rounded UP to a 4096 multiple
4096+2n      slot 2                (1240*1080*4 = 5,356,800 is NOT a page
                                    multiple; pad to 5,357,568 so every slot
                                    starts on a page, not just slot 0)
```

**Where the file lives matters more than what is in it.** Today it is at
`/sdcard/Android/data/<pkg>/cache/` — external, FUSE-backed. Writing 321 MB/s
through FUSE is exactly the sort of thing that works at 30 fps and falls over at
60. Java creates it in `context.getNoBackupFilesDir()` instead — internal,
ext4/f2fs, page-cache-backed, and *not* subject to the cache-trimming that
`getCacheDir()` invites — and hands the path back to Unity, rather than Unity
guessing with `Application.temporaryCachePath`. Java owns creation, sizing and
deletion.

**Publishing.** Unity writes pixels into slot `seq % 3`, then calls
`frameReady(seq)` — a single JNI call, a few microseconds, sixty times a second
at the very most. Java's Presentation posts to its handler and draws:
`copyPixelsFromBuffer` from `slot = seq % 3`, `setImageBitmap`, then records
`drawnSeq`.

An earlier draft of this plan replaced that JNI call with a `frameSeq` word in a
shared control page, polled by a `Choreographer` callback, and claimed it gave
us the panel's own vsync and 120 Hz for free. **Both halves were wrong**, and
the way they were wrong is worth writing down:

* `Choreographer.getInstance()` is **per-`Looper`**. A `Presentation` runs on
  the app's main thread, so its Choreographer is driven by the **primary**
  display's vsync, not the panel's. Getting the panel's own vsync means running
  the Presentation's view tree on a dedicated `HandlerThread` — a real change to
  the Java threading model, not a free win. 120 Hz is not free; it is a
  separate project.
* Publishing through a plain `MappedByteBuffer.getInt()` is **not
  memory-model-correct**. A single aligned 32-bit access is *atomic* on ARM64,
  which is what the draft argued — but atomicity is not *ordering*. `getInt()`
  is a plain load with no acquire, and the `if (seq != drawnSeq)` test is a
  control dependency, which ARM does not honour for load-load ordering. Java
  could see a fresh sequence number and stale pixels. The failure would be an
  occasional torn frame under load: invisible in testing, miserable later.

A JNI call has the barrier semantics we actually need, for free, as a side
effect of crossing the boundary. It is also *less* code. Sixty JNI calls a
second is not a cost worth inventing a lock-free protocol to avoid — the 21 MB/s
wall V1 hit was **pixels crossing JNI**, never the call itself.

Consequences worth stating plainly:

* **Idle is genuinely free.** Nothing dirty means no render, no readback, no
  `frameReady`, and Java never wakes.
* **Three slots**, and the publisher **stalls when `seq - drawnSeq >= 3`**.
  Without that check Unity can reuse slot 0 for `seq+3` while Java is still
  copying `seq` — rare, load-dependent tearing, and free to prevent since
  `drawnSeq` is already there.
* **Teardown drains first.** An `AsyncGPUReadback` in flight writes into the
  mapped slot two or three frames later. If the mapping has been disposed by
  then — on quit, on pause, on the panel being unplugged — that is a write to
  unmapped memory and a native crash with no useful stack. So:
  `AsyncGPUReadback.WaitAllRequests()` **before** disposing the view, the
  `MemoryMappedFile` or the render textures, on every one of those paths.

**Input goes back the other way the same boring way.** Java accumulates
`GestureDetector` / `ScaleGestureDetector` events into a lock-guarded queue;
Unity drains it with one `getGestures()` JNI call per frame returning a packed
`float[]`. Batched, so nothing is missed the way V1's single-slot snapshot could
miss a tap between two `Update`s — but with no second lock-free protocol and no
second memory model to get wrong. See §6.

#### What B cannot fix: latency

`AsyncGPUReadback` completes 2–3 frames after the render. Touch sampled at
frame N reaches the panel around N+3, so **~50 ms touch-to-photon**. Tapping a
menu entry will not notice. Dragging the map will.

There is no fix inside B — C does not help either, it removes copies, not
readback latency. Only A removes it, which is one more reason M0 is worth an
hour. The cheap 90 % mitigation is borrowed from Option D: **let Java draw the
touch feedback itself**, a highlight ring or ripple composited over the pushed
frame at zero latency, while the authoritative highlight arrives three frames
later underneath it. About fifty lines, and it makes taps feel instant.


### Option C — native window, `ANativeWindow_lock` + post

Drops Java out of the path entirely: `ANativeWindow_fromSurface` on the
Presentation's `SurfaceView`, then memcpy from the readback straight into the
locked buffer and `unlockAndPost`. One pass, off the UI thread.

Needs a `JNIEnv*` in C#, which `AndroidJNI` does not hand out, so it needs a few
lines of C compiled into the device build. We already run clang on the phone,
so this is *possible* — but it is an optimisation, not an architecture, and it
does nothing for latency. **Only if B measurably fails to hold 60 fps.**
Cheaper things to try first, in order: RGB565 (halves both remaining passes),
then sub-rect readback (`AsyncGPUReadback.Request` takes x/width/y/height, and
a menu's dirty region is usually small).

### Option D — draw the whole second screen in Android views

No Unity involvement: push item data and icons to Java once, draw with the
Android UI toolkit. Crisp text, native scrolling, zero per-frame cost, zero
latency.

Genuinely tempting, and rejected on two specifics rather than on principle:

* **Fonts.** The game's look is largely its typeface. TMP ships SDF atlases,
  not TTFs, and the source fonts are not necessarily in the depot. Rendering
  every string in Unity and shipping it as a bitmap is possible but grim for
  dynamic text.
* **The map.** The screen that most wants a second panel is composed of live
  `RenderTexture` quads that only Unity can produce.

So D would mean two renderers and a font problem, to save 0.5 ms. Its one good
idea — Java drawing its own immediate touch feedback — is stolen into B above.

> **Decision: A, measured.** B is documented above as the fallback if A is ever
> disqualified — by another device, a Unity upgrade, or the touch question
> going badly. C and RGB565 only ever mattered inside B.

---

## 4. Decision 2 — what gets drawn

Our own UI. Built at runtime, in Unity, from the game's own sprites, fonts,
localised strings and save data — **never** by capturing the game's menu.

* **Rendered by** one dedicated `Camera` with `targetTexture` = our RT and a
  culling mask of exactly one private layer, so it can never draw to the main
  panel and no other camera can ever draw ours.
* **Layer choice.** `TagManager.asset` leaves indices 3 and 6 unnamed. V1
  squats on 6; V2 keeps 6 but never moves the game's objects onto it — only our
  own. Isolation is enforced from **both ends**: our camera's mask is exactly
  `1 << 6`, and bit 6 is cleared from every other camera's `cullingMask`.

  It is worth being precise about *which* camera is the threat, because an
  earlier draft of this plan got it wrong. `GameCameras.MoveMenuToHUDCamera()`
  does `hudCamera.cullingMask = 32` and `mainCamera.cullingMask &= ~32`
  (`GameCameras.MoveMenuToHUDCamera`), on a 0.5 s `Invoke` from
  `HUDCamera.OnEnable`. The
  first *excludes* bit 6 from the HUD camera; the second only clears bit 5.
  **Neither ever sets bit 6 on anything.** The HUD camera is not the problem.

  The real carriers are cameras whose serialised mask is `Everything` —
  `mainCamera` most likely, plus any transient boss, cutscene or effect camera
  that spawns mid-scene. And there is **no Unity event for "a camera was
  created"**, so "strip when a new camera appears" is not implementable as
  written. What is implementable: sweep `Camera.allCameras` on every scene load
  and on a slow tick (a few times a second — `allCameras` is a handful of
  entries and this is not a hot path). A transient camera created between two
  sweeps could flash our layer onto the main screen for a frame, which is the
  residual risk, and it is why M1 verifies against the *main* camera rather than
  the menu path.


* **Toolkit:** uGUI (`Canvas` in `ScreenSpaceCamera` mode on our camera) plus
  **`TMProOld.TextMeshProUGUI`** — *not* stock `TMPro`. Team Cherry vendored an
  old TextMeshPro into `Assembly-CSharp` under the namespace `TMProOld`, and the
  game's font assets are `TMProOld.TMP_FontAsset`. They are a different type
  from the stock ones and the two do not interoperate: a stock
  `TMPro.TextMeshProUGUI` simply cannot be given a game font. Verified:
  `TMProOld.TextMeshProUGUI : TMP_Text, ILayoutElement` exists and uses
  `UnityEngine.UI`, so uGUI works — but *only* through the fork.
  `UnityEngine.UI.dll` and `Assembly-CSharp.dll` are both in the depot and the
  on-device compile references the whole depot
  (`PackageCompiler.patchReferences`), so this is ordinary typed C#, not
  reflection.

* **Authored at the Android render view's measured size**, usually 1240×1080.
  `DsPanelScaler` keeps the geometric-mean scaling of `CanvasScaler` with
  `matchWidthOrHeight = 0.5`, but reads actual view dimensions. Stock
  `CanvasScaler` substitutes `Display.renderingWidth/Height` for a secondary
  canvas, even when Android reports both as zero. Both camera and canvas target
  display 1; a changed surface size rebuilds the layout while retaining the tab.
* **Opaque black background** for OLED panels, shared by the shell, tab strip,
  title card and map clear colour. Selected tabs and controls retain their
  contrasting fills.
* **Nothing lives under a scene root.** The whole rig hangs off one
  `DontDestroyOnLoad` object, so scene loads cannot take it, and nothing we
  create is ever a child of a game object.
* **`Time.timeScale` is zero whenever the game's own menu is open.** Everything
  here uses `Time.unscaledDeltaTime`, and animation is driven from
  `Time.unscaledTime`.
* **Independent of the game's menu.** It renders whether or not the inventory
  is open, and it never touches, re-layers, re-parents or disables a single
  game object. That property is the whole design; if a screen ever needs to
  break it, that screen is wrong.
* **Hit testing** is **arithmetic in our own layout space**, not
  `RectTransformUtility`. Android supplies `MotionEvent.getX/getY` relative to
  the same view that displays the pixels. Dividing by that view's width/height
  gives viewport fractions; multiplying by the actual root canvas rect gives
  layout coordinates from its top-left. `DsPresentation` then expresses these
  in the y-up convention the gesture consumers already use.
  This accounts for scaling and insets without guessing a native panel height,
  using main-screen resolution, or projecting through Unity's sometimes-0×0
  display metrics. Tabs, content, drag/pinch and the test-card crosshair all use
  this one mapping. There is no `EventSystem` or `GraphicRaycaster`.

* **Every screen is wrapped in a `try`/`catch`.** A screen that throws is
  disabled and replaced with an apology, and the shell keeps running. Nothing
  the second screen does may ever take the game down.
* **No unguarded singletons.** `GameManager.instance`, `GameCameras.instance`
  and friends log an error and run a `FindObjectOfType` on every miss — the
  V1 log spam in §1. V2 goes through one `DsGameData` accessor that caches, uses
  `HasInstance`-style guards where they exist, and re-resolves on scene change
  rather than on every frame.


### Where the art comes from

Nothing is authored by us and nothing is committed. Sprites, fonts and strings
are pulled from the running game:

* **Fonts** — the game uses **two**, and using the wrong one for prose is very
  visible. `trajan_bold_tmpro` is a caps display face whose lowercase glyphs are
  malformed (an `l` renders as a stub with a black foot); `perpetua_tmpro` is
  the serif text face with real lowercase. The game's own menus split them the
  same way, so `DsTheme` exposes `Display` for tabs and titles and `Body` for
  descriptions.
  Two traps on the way there, both of which produced Arial: **our own** labels
  are TMP components too, created with whatever the lookup returned earlier, so
  reading the font off a live component finds our own mistake and makes it
  permanent — the search now enumerates `TMP_FontAsset` directly and picks by
  name; and TMP's built-in fallback is a perfectly valid asset named
  `ARIAL SDF`, so it must be rejected explicitly. The lookup is also retried,
  because the game's UI does not exist when the panel comes up, and the shell is
  rebuilt once real fonts appear.


* **Icons** — the `Sprite` fields on the game's item ScriptableObjects
  (appendix). Already loaded by the game; we only take a reference. Nothing is
  copied, converted or read back.
  Drawn with `Image.useSpriteMesh = true`, which is not optional: the icons are
  atlas-packed, and a UI `Image` drawn as a plain quad samples the sprite's
  whole rectangle *including its padding* — which in an atlas is the
  neighbouring sprite. That showed up as fragments of other icons in the corners
  of every cell.

* **Frame / chrome** — the corner filigree, dividers and selector are sprites on
  the game's own inventory prefabs. Found once by walking the inventory
  subtree, cached by name, re-laid-out by us at our aspect. This is a *read* of
  the prefab, done once — not the per-frame re-layering V1 did.
* **Strings** — the game's localisation API, so the second screen is localised
  for free and never carries English text of ours.

Two things this buys that are easy to miss: our menu updates when the game
patches its art, and the repo still contains no game content.

The failure mode to design for is **"the asset is not loaded yet"** — the game
uses Addressables, and an item's icon may legitimately not be resident before
the player has opened that pane once. Every lookup therefore returns
`Sprite` *or null*, every widget renders a placeholder for null, and the cache
is re-checked on a slow timer rather than assumed to be complete. A screen that
is half-populated on first open and complete a second later is fine; a screen
that throws is not.


---

## 5. Decision 3 — structure

> **"Read-only" is not the same as "safe", and that is the most important thing
> learned so far.**
>
> The inventory screen showed two items instead of a full inventory, *and the
> game's own inventory became wrong*. Nothing we wrote writes anything, so this
> looked impossible — until `CollectableItemManager.GetCollectedItems()` turned
> out not to be a read. It fills a **static cache** the game's own inventory
> pane reads back, and it calls `IsInHiddenMode()`, which consults
> `HeroController.instance.Config.ForceBareInventory` and **increments a shared
> `Version`** whenever that answer changes. We polled it on a timer, including
> on the main menu where there is no hero — so the answer flipped, the version
> moved, and the game's cache was rebuilt from our context with the
> bare-inventory list. Both symptoms, one cause.
>
> Two rules, now enforced in `DsGameData` rather than remembered:
>
> 1. **Read nothing unless `InGame`.** A gameplay scene, a live `PlayerData`,
>    and an existing `HeroController`. On a menu the managers are half-built and
>    every answer is wrong even when it is harmless.
> 2. **Prefer the master list plus `PlayerData` to a convenience accessor.** An
>    accessor that caches is one whose behaviour depends on *when we call it*,
>    and a second screen may never influence that.
>
> Audited: `ToolItemManager.GetAllTools`/`GetAllCrests`,
> `EnemyJournalManager.GetAllEnemies` and `QuestManager.GetAllQuests` are pure
> list reads. `QuestManager.GetAcceptedQuests` is version-cached but benign —
> avoided anyway. `CollectableItemManager.GetCollectedItems` is unsafe and is
> replaced by enumerating `PlayerData.Collectables` and resolving names through
> `GetItemByName`, which is a pure master-list lookup.

Modular from the first line, because "add a screen" is the whole requirement.


```
src/dualscreen/
  DualScreenV2.cs        bootstrap, lifetime, hot-plug, pause/resume
  DsPresentation.cs      display bring-up, camera, canvas, layer discipline
  DsConfig.cs            runtime knobs from a file, so a question costs a restart not a build
  DsProbe.cs             dumps the game's inventory hierarchy on request
  DsTouch.cs             which screen a touch came from, and fencing the game off
  DsInput.cs             gestures from this panel's touches
  DsGameData.cs          the in-game gate, and the rules about what is safe to read
  DsGameArt.cs           borrowing the game's own inventory artwork
  DsTheme.cs             the game's fonts, colours, metrics
  DsWidgets.cs           rect / panel / label / icon builders
  DsIconGrid.cs          the scrolling icon grid + detail pane every screen uses
  DsHornetPanel.cs       needle, shards, the skill ring, the currencies
  DsShell.cs             chrome: tab strip, screen switching, gesture routing
  DsScreens.cs           Inventory, Tasks, Journal
  DsLoadoutScreen.cs     the crest and its tools
  IDsScreen.cs           the contract
  DsTestCard.cs          the M1 rig, kept behind a flag
```

Two things ship switched off rather than compiled out, because both answer
questions that otherwise cost a seven-minute build to ask:

* **`testcard=1`** — the M1 rig, for telling a rendering problem from a content
  problem.
* **`probe=1`** — dumps the game's inventory hierarchy to logcat: every name,
  component, and which `SpriteRenderer` holds which sprite. Written after three
  builds were spent guessing where the needle art lived (see below).

Both are read from `<persistentDataPath>/dualscreen_v2` once per process, so
changing them costs an app restart.

A `check.ps1` beside the sources compiles all of this against the depot in
about ten seconds. That matters more than it sounds: the device build is ~3
minutes of APK plus ~4 minutes of on-device IL2CPP, so a wrong API name used to
cost seven minutes to discover. It has already caught
`CollectableItem.ReadSource.TrueAmount` (does not exist),
`TMP_Text.overflowMode` (is `OverflowMode` in the fork) and a missing `TK2D`
reference before any of them reached the phone.

Subdirectories are safe: the csproj globs `src/**/*.cs`, Gradle's `stagePatches`
copies recursively, and the on-device compile uses `walkTopDown()`.

```csharp
interface IDsScreen
{
    string  Id      { get; }        // "inventory", stable, used for persistence
    string  Title   { get; }        // localised, shown in the tab strip
    Sprite  TabIcon { get; }
    bool    Available { get; }      // e.g. Map hidden in a zone with no map

    void Build(RectTransform host, DsTheme theme);  // once, lazily, on first show
    void OnShow();
    void OnHide();
    void Tick(float dt);            // unscaled dt; only while visible
    bool Dirty { get; }             // "I changed" -> drives render + push
    void OnGesture(in DsGesture g);
}
```

`DsShell` owns the tab strip, routes gestures, and asks the active screen
whether anything changed. Adding a screen is one file plus one `Register(...)`.

Rules that keep the modularity real rather than nominal:

* **A screen only ever touches its own `host` RectTransform.** It does not know
  the RT, the camera, the transport, or that a second screen exists at all. It
  is testable in isolation on the main display by pointing the camera somewhere
  else.
* **`Build` is lazy.** Screens are constructed on first show, not at boot, so an
  unused Journal screen costs nothing and a broken one cannot stop startup.
* **`Dirty` is the only render trigger.** A screen that returns `true` forever
  burns 60 fps of readback; that is a bug in the screen, and the debug overlay
  names the culprit.
* **Screens are read-only until M9.** No screen may write player state. The
  interface has no hook for it yet, deliberately.

**Screen order** (the tab strip, left to right): Inventory · Crest · Tasks ·
Journal · Map. Inventory is the initial screen, as asked. The last-used screen
persists across runs in a one-line file beside the launcher's settings.

(Tools and Crests were drafted as two tabs and shipped as one, `DsLoadoutScreen`
— see M2. Map goes on the right-hand end because it is the one screen with its
own render rig behind it, and a broken rig should cost the tab it is on rather
than the tab the player lands on.)

**Outside a save there are no tabs at all.** The shell hides the strip and the
whole body and shows a title card instead. It first borrowed the game's own
"Hollow Knight Silksong" logo from `UIManager.gameTitle`; it now shows the
port's own, `docs/Bottom_Screen_Logo.webp` — see `DUALSCREEN-V3.md`.

This belongs to the shell rather than to each screen, because "no save is
loaded" is a fact about the panel, not about the Journal; the alternative was
five separate grey "Main menu" labels, which is both a duplicated decision and a
thing that looks like a bug. Two details are load-bearing:

* **Idle is the default state.** The shell is built before anything is known
  about the save, and defaulting to a screen opened the panel on an empty
  Inventory. The title card is the honest answer to "no idea yet".
* **Only *leaving* gameplay is delayed.** `DsGameData.InGame` goes false for a
  few frames during any scene load, so raising the card immediately would flash
  it every time the player walked through a door. Returning is instant, and
  before the first time we have ever been in game there is nothing to protect,
  so the delay does not apply at boot.

While the card borrowed the game's logo, finding it also wanted an identity
check rather than a type match: `LogoLanguage` is not unique to the title — Team
Cherry's studio logo is localised through the same component, and during the
intro it is the only one loaded, so an unfiltered `FindObjectsOfTypeAll`
reliably returned the wrong logo and cached it for the session. The lookup
matched the game's own object name (`"LogoTitle"`) and kept upgrading until the
sprite came from `UIManager` itself.

Note that this is **not** the game's own pane order, and deliberately so. The
game has five panes — `InventoryPaneList.PaneTypes { Inv=0, Tools=1, Quests=2,
Journal=3, Map=4 }` — and **no Crests pane at all**: crests are a horizontal
sub-scroller *inside* Tools (`InventoryToolCrestList` / `InventoryToolCrest` /
`InventoryToolCrestSlot`), reached through
`InventoryItemToolManager.EquipStates.SwitchCrest`. On a 16:9 pane shared with
the tool grid there is no room for them to be anything else. We have a whole
panel and a tab strip, so crests get promoted to a screen of their own. That
promotion is a small thing, and it is also the argument for the entire project:
owning the menu means the layout can follow the data instead of the data being
squeezed into someone else's layout.


---

## 6. Decision 4 — input

**The rule: the Android render surface owns its pointer events.**

`SecondaryDisplay` finds Unity's secondary `SurfaceView` among this process's
window roots, excluding the activity's own display. It installs a consuming
touch listener before `DsPresentation` brings up the UI. Unity creates this
window lazily on the first secondary render, so bringup waits the graphics
settling period, renders a blank camera, then waits for input capture before
building the canvas. Waiting for the window before creating a camera deadlocks.
No secondary down,
move, up or cancel is injected into Unity's native input, so none can become a
legacy touch, a synthetic mouse click, or `Input.anyKeyDown`. Primary-screen
touches are untouched. Pointer hover/scroll is also kept off the game; non-pointer
motion still goes to the player's normal input handler.

Window discovery uses `WindowInspector` on Android 10+, with the older
`WindowManagerGlobal` read-only root accessors on Android 8/9. It does not
depend on a hard-coded Android display ID or Unity's obfuscated Java fields.
Discovery keeps watching for a replaced or reattached surface. Failure is
logged and leaves the second-screen UI dormant, not live with leaking input.

**Nothing on the panel reads the gamepad.** L1/R1 were briefly wired to change
tabs and that was wrong: they are the game's own bindings, and the second screen
must never consume an input the player is using to play. The panel is driven by
touch alone.

### Why the uGUI fence was insufficient

The previous `BaseInput` override protected uGUI's pointer processing, but
`InputHandler.CutsceneInput` and `StagCutsceneInput` read `Input.anyKeyDown`
directly. Other direct mouse readers and simultaneous touches could bypass it
too. Input must be separated before those global states are created, rather
than repaired one game consumer at a time. `InventoryTouchInput` consequently
needs no display filter now: its legacy stream contains only the game's input.

### Events across JNI

`DsTouch` drains one ordered batch per Unity frame. The header is
`[generation, viewWidth, viewHeight, ready]`; each event is
`[fingerId, TouchPhase, x/viewWidth, y/viewHeight, uptimeMilliseconds]`.
The double array preserves timestamp precision on devices with long uptimes.
There are still no pixels, textures or readbacks crossing JNI.

Down and up remain separate events even if both arrive between frames.
`DsInput` recognizes taps, drags, flings and pinch from that stream. A cancelled
or interrupted gesture never becomes a tap. Resizing, hiding or replacing the
surface clears the queue and changes its generation; a held finger cannot
resume as a new tap after that boundary. Overflow is logged and cancels the
gesture rather than replaying a partial queue.

### What this replaces

V1 polled a single `[x, y, down, seq, w, h]` snapshot over JNI each frame,
mapped it back through the letterbox to a *main screen* pixel, and replayed it
as a fake pointer into the **game's** inventory: three coordinate spaces and a
JNI allocation per frame, to press a button we did not draw. An intermediate
draft of this plan replaced that with a lock-free gesture ring in shared
memory, then with a batched `getGestures()` JNI call.

Those forwarding and framebuffer paths remain deleted. The current Java helper
only captures the secondary surface's own events and visibility; it does not
mirror or operate the game's inventory. `InventoryTouchInput` remains the
main screen's touch support.


---

## 7. Milestones

Each one ends at something visible on the panel, and each one is independently
revertable. Roughly a device build each (~3 min for shell-only changes, longer
when a patch change forces IL2CPP to reconvert).

Throughout M1–M8 V1 stayed in the tree, switched off by a marker file, so there
was always a working second screen to fall back to and to compare against. It
outlived its usefulness at M8 — the one domain it had actually solved, however
painfully — and was deleted with the milestone.

**M0 — spike: does Unity multi-display still flicker?** ✅ **DONE.** No. Main
screen held 120.2 fps with p99 == max == the 8.33 ms refresh and zero missed
vsyncs while a camera and canvas were live on the panel. Touches arrive too,
and the Input System attributes them to a display. §3 rewritten; the transport
and the Java bridge are deleted rather than rebuilt. The spike itself has since
been deleted too, its job done.

**M1 — the display-1 rig.** ✅ **DONE.** `DualScreenV2` + `DsPresentation` +
`DsConfig`: display activation with a settling delay, camera and
`CanvasScaler` canvas at 1240×1080, layer-6 discipline swept off every other
camera, hot-plug and pause/resume, and `DsTouch` fencing the game off from panel
touches. Verified: all four edge markers visible, main display held **120.2 fps
with p99 = max = the 8.33 ms refresh and zero missed vsyncs** while the rig was
live, and neither screen's touches reach the other. The Java bridge is now dead
code, pending deletion with V1.

**M2 — chrome, gestures and the first screens.** ✅ **DONE**, and it went
further than planned: rather than stub screens, five real ones ship —
Inventory, Tools, Crests, Tasks, Journal — all reading live game data through
`DsIconGrid` (a pooled, scrolling icon grid with a detail pane).
`DsTheme` takes the game's own fonts (Trajan for display, Perpetua for body),
`DsShell` owns the tab strip, `DsInput` turns panel touches into
tap/drag/fling/long-press.

What it cost, all of it found on the device and all of it recorded above: the
`GetCollectedItems` side effects (§5), the self-referential font lookup and the
Trajan-for-prose mistake (§4), atlas bleed in every icon corner (§4), taps
mapped through `RectTransformUtility` landing in the middle of the grid (§4),
scrolled cells drawing over the tab strip, and a `FindObjectsOfType` running
every frame.

**M3 — the Inventory screen, properly.** ✅ Rosaries and shell shards, the
needle, mask and spool, the silk skills in their ring — a layout designed for
1.15:1 rather than a grid that happens to fit (`DsHornetPanel`). V1's mirror,
its Java bridge and the forwarded-pointer path in `InventoryTouchInput` are all
deleted.


**M4 — Crests.** Currently a grid of crest icons. The real screen wants the
per-crest slot layout — `ToolCrest.Slots` carries positions and types — showing
what is socketed where.

**M5 — Tools.** Currently a flat grid. Wants grouping by `ToolItemType` and the
storage counts made prominent.

**M6 — Quests / Tasks.** ✅ A dedicated layout rather than a grid: main quests
at the top, a rule, the other accepted ones, and the finished pile behind a
button. "Prioritised" is the game's own rule rather than a heuristic —
`InventoryItemQuestManager.IsInMainQuestSection` is `quest is MainQuest &&
!IsCompleted`, so it is a type test, and `MainQuest` is a real class deriving
from `FullQuestBase`. Its `IsHidden` filter is adopted too. A quest that can be
handed in shows the game's own `CanCompleteIcon`.

Still wanted: `QuestTarget` progress counters, which are the part you most want
at a glance and the one thing the description does not give you.

**M7 — Journal.** Currently icons with kill counts. Wants the notes text and a
completion summary.


**M8 — Map.** The most valuable screen, and the one the research reframed three
times. The third reframing is the one that makes it cheap, so it is worth
following how the picture changed.

**First** the map looked like a `RenderTexture` composite. **Then** like
world-space `SpriteRenderer`s that nothing but V1's quad-cloning could reach.
**Both are half right**, and the half that matters was missed until the third
pass:

* The content is world-space `SpriteRenderer`s on layer 5, in a subtree the
  game `Instantiate`s once per gameplay scene load
  (`InventoryMapManager.EnsureGameMapSpawned`, reached from
  `GameCameras.StartScene` through `HUDCamera.EnsureGameMapSpawned` — **not**
  lazily when a pane opens, so it is there from the moment the scene is).
* **Two cameras** render it, separated **by Z slice, not by layer**:
  `Map Camera` clips `[42,50]` and sees the rooms, `Decorator Camera` clips
  `[30,42]` and sees pins, next-area arrows and text. Both orthographic, both
  `size 8.710664`, both `cullingMask = 32` — layer 5 alone. The `GameMap` root
  sits at local `z = 43`; pins and decorations are authored ~2.5 nearer, which
  is what drops them into the other slice.
* `CameraRenderToMesh` then hands each camera a `RenderTexture` and shows it on
  a quad, `Game Map Quad` and `Game Map Decorator Quad`.

So it is both: live sprites, composited by two cameras. **And a composite is
just a framing of a scene, which is a thing we can do for ourselves.**

### The decision: the game owns the content, we own the camera

V2 builds **its own pair of cameras**, duplicating the game's two exactly — same
rotation, same clip planes, same culling mask — rendering into **our own**
`RenderTexture`, shown by a `RawImage` on the layer-6 canvas. Position and
orthographic size are *ours*. That is the whole trick: framing is what zoom and
pan *are*, so we get both without writing to a single `Transform` the game owns.

Measured against V1's list of fights:

| V1 had to | V2 |
| --- | --- |
| clone the quads with fresh materials so the fade could not reach its copy | never touches the quads |
| re-layer the game's subtree onto its private layer every frame | never moves a game object to layer 6 |
| drive `TryOpenQuickMap`, then re-run `MapNextAreaDisplay.Refresh` to undo it | drives `WorldMap()`, which needs neither |
| clone the No-Map symbol, because the L1 FSM fought it for the original | reads the *sprite* and draws its own |

The fade question is **settled, not dodged**, and it is why any of this works:
`InventoryMapManager.sceneMapFade` is a component on **`Game Map Quads`**, the
parent of the two display quads, and the alpha leaves are on the quads
themselves. The live content is in a different subtree (`Game Map Rendering` →
the spawned `Game_Map_Hornet`) with no fade controller from its root down. **The
fade is on the display, not on the thing displayed** — so rendering the thing
displayed is immune to it, which is exactly what V1 bought with a clone and a
fresh material per quad.

### Making the content visible, and framing it

One thing is not ours: whether the zone's objects are *active*.
`GameMap.DisableAllAreas()` deactivates every direct child of the `GameMap` bar
a five-name allow-list, and the game calls it whenever a map closes. It and
`EnableUnlockedAreas` are both private, so there are two public ways in — and
**the two views want different ones**:

| | call | content | framing |
| --- | --- | --- | --- |
| Area | `TryOpenQuickMap(out name)` | the current zone only | the game's Map Camera, copied |
| Full map | `WorldMap()` | every unlocked zone | fitted to measured content bounds |

`TryOpenQuickMap` also rescales the `GameMap` to `1.4725` and moves it to that
zone's anchor. An earlier draft of this plan treated that transform write as the
thing to avoid at any price — V1 and the game had taken turns undoing each other
over it — and built *both* views on `WorldMap()`, framing the area view on the
`Compass Icon` at a zoom derived from the game's numbers.

**On the device that drew an empty rectangle**, twice. The reasoning was wrong in
two places at once: `WorldMap()` enables *every* zone, so the area view was never
the area view; and the hand-computed window did not land where the rooms were,
so it showed a blank patch of a map that was demonstrably present — 854 drawable
sprites, all on layer 5, measured by a probe added for exactly that question.

The correction is this section's own idea applied one level further: **the game
already knows how to frame its quick map, so ask it instead of reconstructing
it.** `TryOpenQuickMap` moves the map so the zone sits in front of the game's Map
Camera; copying that camera's position and `orthographicSize` reproduces the L1
view exactly, with no arithmetic of ours left to be wrong about. And the
transform write costs nothing after all, because our framing is expressed
*relative* to that transform — which was the design from the start. The thing
being avoided had already been made harmless.

Pan and zoom are then ours: a drag offsets the camera, a pinch divides the
half-height, and neither touches anything the game owns.

The sting both paths share is in `EnableUnlockedAreas`' tail, which ends with
`CameraRenderToMesh.SetActive(GameMap, true)` — switching the game's **own**
display quads on, over gameplay, on the **main** display. The paired
`SetActive(..., false)` is the only thing that undoes it, and it lives in a
`finally`: if the game code throws part-way through, a plain catch would skip it
and leave the game's map splashed across the player's screen. Asking for a map on
the second screen must never put one on the first.

Content is **re-asserted, not set once**, because the game disables the areas
again the next time it closes a map. The detector is `DisableAllAreas`' own rule
read backwards: if no direct child outside the allow-list is active, the areas
are dark. Rate-limited, and only while the map tab is on screen. A mode switch
forces one immediately, since the areas the previous mode left active would
otherwise look perfectly healthy.

While the game *is* showing its own map the areas are active, the detector stays
quiet and we do not intervene — the player's quick map is never yanked out from
under them.

### Framing follows the transform, not the world

Our cameras cannot sit at fixed world coordinates, because the game moves and
scales the `GameMap` underneath them — `1.4725` for the quick map, `0.39`→`1.15`
across the inventory's zoom lerp. A fixed camera would show the right place until
the player pressed L1 and the wrong one afterwards. So every quantity is derived
from the map's own transform, or copied from the game's own camera, each frame:

* **Where and how much.** In the area view, both are the game's Map Camera —
  position and `orthographicSize` — because `TryOpenQuickMap` has already put the
  zone in front of it. In the full map, the centre and extent are fitted to the
  measured bounds of the active sprites.
* **Scale.** Half-height is carried in *map* units and multiplied by the
  `GameMap`'s `lossyScale`, so the view holds its size whatever the game does.
* **Z and rotation.** Copied, so `[42,50]` and `[30,42]` keep meaning what they
  mean. Only x and y are ours.

**And the rig's own scale must be cancelled.** This cost a build, and nothing
about it looks wrong. The rig hangs off the screen's `RectTransform` — the right
place, since it then dies with the shell rebuild and nothing has to remember to
tear it down. But that is under a `ScreenSpaceCamera` canvas, which carries a
`localScale` of `(2 × orthographicSize) / screenHeight`; our display camera never
sets `orthographicSize`, so it is Unity's default 5 and the scale is ~`0.0093`.
A camera's view matrix is the inverse of its transform's `localToWorld`, **scale
included**, so both map cameras inherited it and rendered a region ~108× too
small. Flat colour, no error, no warning. `NormaliseScale()` sets `localScale` to
one and then to the reciprocal of the resulting `lossyScale`, every frame.

### No map for this zone

`HeroController.HasNoMap(GameMap)` is `public static` and answers exactly that
question without opening anything;
`gameMap.HasAnyMapForZone(gameMap.GetCurrentMapZone())` is the same test one
level down. Neither mutates the scene — unlike `TryOpenQuickMap`, whose `false`
return is what V1 had to use as the signal, paying a full map teardown for an
answer.

The symbol itself is **read, not cloned**. V1 built copies of the
`No_Map_symbol` renderers on its private layer and forced their alpha to 1 every
frame, because the quick-map FSM's `FadeGroup` fades the originals. We want the
`Sprite`, not the renderer: take `.sprite` off
`Quick Map/No Map/No_Map_symbol`'s `SpriteRenderer` and draw it as an ordinary
uGUI `Image`. After the read, nothing of the game's is involved, so there is
nothing left to fight.

The game's own asset names are not recoverable from its classes — the symbol is
a `Sprite` reference on a prefab, not a named constant — so the lookup goes by
object name, with a `Resources.FindObjectsOfTypeAll<SpriteRenderer>()` fallback,
and a null sprite degrades to a line of text rather than to a blank panel.

### Staging

**M8.1 — the area map.** ✅ The tab: the current zone as the L1 map shows it, or
the No-Map symbol when the zone has none. Drag to pan, long-press to recentre.

**M8.2 — FULL MAP.** ✅ A button in the header switching the framing to every
unlocked zone at once, opened on Hornet rather than on the middle of Pharloom,
with pinch to zoom and a RESET beside it. Both modes start with
`TryOpenQuickMap` — the only public call that *positions* the map — and World
then reveals the rest with `WorldMap()`, which moves nothing. So the two modes
differ only in one call and one zoom factor, not in an implementation.

**M8.3 — the parts that want a design pass.** Pin visibility
(`MapPin.CurrentState`), marker placement, and whether the area view should
follow the player *within* a zone rather than sit at the zone's anchor. Held
back until the render path had been proven on the panel, which it now has.

With M8 on the device, **V1 lost its last reason to exist** and has been
deleted: `DualScreen.cs`, `DualScreenSpike.cs`, both Java classes, and the
forwarded-pointer path in `InventoryTouchInput`.

### What the device corrected

Kept because all of these were written here as though settled, and the panel
disagreed with each:

1. **`WorldMap()` for both views was wrong.** It enables every zone, so the area
   view was the whole world. `TryOpenQuickMap` is the area view's call, and its
   transform write — the thing this plan was arranged to avoid — is harmless,
   because the framing was already relative to that transform.
2. **Reconstructing the framing was wrong.** Centring on the `Compass Icon` at a
   zoom derived from the game's numbers drew an empty patch. Copying the game's
   Map Camera reproduces the view exactly and has nothing to get wrong. (The
   compass came back for the *full* map, where a wide framing is forgiving of it
   being a little off and "where am I" is the question being asked.)
3. **A camera under a uGUI canvas inherits the canvas's scale**, and Unity's view
   matrix includes it. ~0.0093 here, so the map rendered ~108× too small, as flat
   colour with no error. See the framing section.
4. **"The zone's objects are active" is not the same as "the RIGHT zone's
   objects are active".** The dark-detector only asks the first question, so
   after walking into a new zone the previous one's rooms kept it perfectly
   satisfied and the panel kept drawing Bellhart from inside Greymoor. Zone
   changes now force a re-frame.
5. **`TryOpenQuickMap` returning false enables nothing**, which makes it a
   *destructive* answer to ignore: the previous zone stays up and gets drawn. Its
   return value is the authoritative "no map here", outranking
   `HeroController.HasNoMap`, and it is paired with `CloseQuickMap()` to take the
   stale map down before the No-Map symbol goes up.
6. **Scene transitions flicker, for two different reasons.** Between zones, a
   door takes the game out of a gameplay scene for a few frames,
   `DsGameData.InGame` goes false, and the map blinked to the idle panel and
   back. Within a zone, every room change spawns a fresh `GameMap` that the
   game's own `SetupMap` does not populate for about a second, so the panel was
   drawing a half-built map. Both are fixed by freezing the cameras and leaving
   the last good frame in the render texture — the idle case for as long as it
   lasts, the spawn case for a settling window. Holding is scoped to the *idle*
   state, never to a no-map answer, because that would be correction 5
   reintroduced as a feature.
7. **The game frames for 16:9 and this panel is 1.33:1.** Copying the game's
   `orthographicSize` therefore cropped a quarter of the width off the wider
   zones. Fixed by measuring the zone and fitting it, which also makes the zoom
   per-zone instead of one constant for all of Pharloom.
8. **The four objects that survive `DisableAllAreas` also survive it during a
   measurement.** `Compass Icon`, `Shade Pos`, `Map Markers` and `Flea Tracker
   Markers` are exactly the allow-list the dark-detector relies on — and because
   they are always active, they landed in the zone bounds even when they belonged
   to another zone entirely. A corpse marker left across the world stretched the
   fit over half of it. The bounds now take only what the *rooms* camera would
   draw, using the game's own `[42,50]` / `[30,42]` depth split rather than a
   blacklist of names. One fact, two consequences, and the second was missed the
   first time.

9. **The `GameMap` is not respawned per scene.** The settle-freeze that was meant
   to cover room transitions was hung off "a new `GameMap` instance appeared",
   on the assumption that a gameplay scene load spawns one.
   `EnsureGameMapSpawned` only spawns when there is *no* map, so a single
   instance persists for the session and the trigger never fired once. The
   signal is `GameManager.sceneName`. Between-zone travel had looked fixed only
   because it takes the out-of-gameplay path, which holds for its own reasons.
10. **A 250 ms re-assert is a 250 ms blank.** The game tears the areas down on a
   scene load, and the dark-detector only ran on the assert timer, so there was
   up to a quarter-second of genuinely empty map being drawn. The detector now
   runs every frame — it is a walk over ~38 direct children — and the cameras
   freeze while dark, so the render texture holds the previous room's frame.
11. **Per-zone fit and a stable full map are different requirements.** The full
   map's height was a multiple of the zone fit, which is per-zone by design, so
   crossing a border rescaled the whole world under the player. It is latched on
   entry instead: the area view follows the zone, the full map stays where it
   was put.

Also settled by measurement, having been listed as unproven: two cameras into one
`RenderTexture` with `clearFlags = Depth` on the decorator pass **does** composite
correctly on this device, and the content is all on layer 5 exactly as the mask
assumed — though the mask is now copied from the source camera rather than
hardcoded, since there was no reason to keep guessing at a value the game will
hand over.

### A note on the display font

`DsTheme` warns that Trajan is a caps face whose lowercase glyphs are broken —
an `l` renders as a stub with a black foot. Everything else in that face is
already uppercase (the tab strip, `RESET`, `FULL MAP`), so the zone-name header
was the first mixed-case string to reach it, and "Shellwood" rendered as
"She??wood".

The rule now lives at the decision point, in `DsWidgets.Label`: the display face
is for strings **you wrote and know are uppercase**; anything carrying text from
the game takes the body face, which is what the game uses for the same strings
in its own UI. Two other labels had the same latent fault — the icon grid's
detail title and the loadout's crest name — and would have shown it on any name
containing an `l`.

Worth being blunt about because it does not present as a wrong argument at a
call site: it presents as a broken font, or a broken atlas, or a missing glyph
range. It cost three rounds.

### Borrowing the game's art, and the two ways it goes wrong

Both of the second screen's borrowed images — the No-Map symbol and the title
logo, since replaced by our own — needed the same two lessons, which is enough
of a pattern to write down.

**Match the instance, not the type.** `LogoLanguage` is not unique to the title:
Team Cherry's studio logo is localised through the same component, and during
the intro it is the *only* one loaded, so `FindObjectsOfTypeAll<LogoLanguage>()`
returns the studio logo. A lookup that stopped at its first hit then kept it for
the session. Match the game's own object name (`"LogoTitle"`), and treat
anything short of the authoritative source as provisional.

**uGUI's `Image` centres the rect, not the picture.** `useSpriteMesh` draws the
sprite's own tight geometry, which cannot reach a neighbouring atlas entry —
keep it. Centring then comes from anchoring to the parent's centre and sizing
the rect to the sprite's own aspect, so `preserveAspect` has nothing left to do.
Turning `useSpriteMesh` off to "fix" the centring only trades the offset for
bleed along the atlas seams; it was tried, and reverted.

### Trusting the game's state, and the compass

`Compass Icon` is where the game puts Hornet on the map — but only if the player
has the tool that tracks her. Without it the game does not clear the icon, it
simply never moves it, so it holds a stale position indefinitely. Measured: local
`(-31.77, 22.74)` while the zone being drawn was centred on `(-5.17, -10.03)`
with extents `(5.77, 1.51)` — a reading some 26 units outside the map, which
centred the full map on empty space with all the content off one corner.

An "is it at the origin?" test passes such a value happily. The test that works
is whether it is somewhere the map actually exists, so the compass must now be
active, off-origin, **and** inside the measured bounds. Failing that the centre
falls back to the current area, and failing that to everything mapped.

The general form: a value the game leaves behind is not the same as a value the
game is maintaining, and only the second one is safe to read.

### What is still not proven

* **What `TryOpenQuickMap` costs at the rate we re-assert it.** It should run
  once per map close; the risk is a detector that flaps. Early logs showed three
  re-asserts in eight seconds, which is worth watching.
* **Whether the game's own quick map and ours can be open at once** without one
  disturbing the other's framing, since both now drive the same call.
* **Where the `Compass Icon` actually sits** in an unmapped room, or between
  rooms during a transition.

**M9 — interaction.** Equip a crest, socket a tool, pin a quest, *from the
second screen*, writing through the uniform `SerializableNamedList.SetData`
API. Held back deliberately: it is the only part that writes to game state, and
everything before it is read-only and therefore incapable of corrupting a save.

### Lifecycle, which is not a milestone but is not optional either

Handled in M1, because retrofitting it after M8 is miserable:

* **No second display** — do nothing, log once, cost nothing.
* **Hot-plug.** `Display.onDisplaysUpdated` reacquires Unity's display, and the
  Android helper rebinds its input listener to a replacement surface. The rig
  is reused, not duplicated; a size change rebuilds its layout.
* **Minimize / restore.** `PlayerActivity.onStop` hides the Android presentation
  root, releasing its visible surface rather than leaving the last frame over
  the launcher. `onStart` restores it. Focus loss is not a hide signal: touching
  the other screen can change focus while the game is still visible.
* **Pause / resume.** C# suspends the camera, canvas and gestures. Bringup is
  serialized and honors the paused state, including during its asynchronous
  startup. A recreated surface gets the same settling period as first launch
  before rendering resumes. Quitting stops the window helper and destroys the
  rig; there are no readbacks to drain in the current transport.



### Not in scope

Stated so it does not creep in: no settings UI on the second screen, no
save/load, no shop or bench interaction, no chat/notes, no support for a third
display, and no attempt to work on a device without a second panel beyond
cleanly doing nothing.

---

## 8. Risks

| Risk | Response |
| --- | --- |
| ~~Multi-display still flickers~~ | **Retired by M0.** 120.2 fps, p99 == max == refresh, zero missed vsyncs with the rig live. |
| ~~In-flight readback / shared-memory ordering / `copyPixelsFromBuffer` cost / render-on-dirty rebuild ordering~~ | **All retired by M0** — they were properties of the transport that no longer exists. Kept in §3 for the fallback. |
| **Panel touches operate the game** | Captured at the Android surface before global Unity input is created. A uGUI-only fence did not cover raw `Input.anyKeyDown` in cutscenes. |
| ~~Legacy `fingerId` ↔ Input System `touchId` correlation is wrong~~ | **Retired.** No streams are correlated now: the secondary surface supplies its own ordered pointer IDs and events. |
| **A Unity or game API is simpler than it looks** | The recurring theme, and now four instances: `GetCollectedItems` is not a read; `Display.renderingWidth` is not a readiness signal; `ScreenPointToLocalPointInRectangle` returns a wrong answer rather than failing; `Image` samples an atlas sprite's padding unless told not to. **Where we control the maths, do the maths**, and confirm a signature with `check.ps1` before trusting it. |


| Rendering to display 1 too soon after `Activate()` | One run died with SIGBUS on the graphics thread. `Activate()` is async, `renderingWidth` never populates, and there is no readiness flag — so the rig is built on a short timer. Fragile by nature; if it ever recurs, lengthen the delay rather than trusting a field. |
| Item ScriptableObjects are Addressables-only and not resident before the pane is first opened | **Mostly resolved by research** — every pane enumerates through a static public manager method on a live scene object (appendix). What remains Addressables-backed is `GlobalSettings.UI` and individual sprites, so lookups stay null-tolerant and widgets draw placeholders. |
| Stock `TMPro` used by mistake instead of the game's `TMProOld` fork | Caught in planning, not in a debugger. The two type sets have identical names; only `TMProOld` accepts the game's font assets. Called out in §4 and enforced by a `using` alias in `DsTheme`. |
| **A second `GameMap` (M8) corrupts the global `MapPin._activePins`** | Confirmed in the game's own code, not suspected: `GameMap.Awake` subscribes and adds pins, and its `OnDestroy` calls `MapPin.ClearActivePins()`, which clears the static list wholesale. M8 therefore renders the game's own map with its own cameras and never instantiates a second one. |
| A transient camera with an `Everything` mask flashes our layer onto the main screen | Sweep `Camera.allCameras` on scene load and on a slow tick. There is no camera-created event, so a one-frame flash between sweeps is the residual risk, accepted knowingly. |
| A game update renames a field we read | The on-device compile references the depot, so a rename is a **compile error on the device**, not a null at runtime — that is exactly why the patches are shipped as source. It fails loudly, which is the right failure. |
| We drift from the game's look | Steal sprites and fonts, author none. Screenshot the panel each milestone and compare against the game's own menu. |
| A screen throws and takes the game with it | Every screen is wrapped; a throwing screen is disabled and the shell survives. |
| Writing game state corrupts a save | M9 is last. Everything before it is read-only, and the interface has no write hook until then. |
| Another app owns the second panel | This device runs `rip.moth.cocoonshell` as its second-screen launcher. Unity took the panel from it without complaint, but it is why a screencap alone never counts as evidence. |
| The 186th-assembly IL2CPP crash in `README.md` | Stale — the patches assembly ships today. Recorded here so it is not rediscovered and treated as a blocker. |


---

## 9. Open questions

*Answers pencilled in so work is not blocked; each is cheap to change.*

1. **Does the second screen follow context** (Map during play, Inventory when
   the menu opens) or stay put? *Pencilled: stay put, plus one "follow the
   game" setting, default on — a dead panel during play is the thing V1 got
   most wrong. Implementation is cheap: subscribe to
   `InventoryPaneList.OpeningInventory` / `ClosingInventory` /
   `MovedPaneIndex` rather than polling.*

2. **120 Hz?** The panel offers it, but *not for free* — a `Presentation` runs on
   the main thread, and `Choreographer` is per-`Looper`, so it follows the
   **primary** display's vsync. The panel's own vsync means running its view
   tree on a dedicated `HandlerThread`. *Pencilled: 60, driven by the primary,
   and revisit only if M1's numbers make it worth a threading change.*
3. **Dim or blank during cinematics and boss fights?** *Pencilled: dim on
   `GameState.CUTSCENE` only, since that is when the panel is a distraction and
   never useful.*
4. **One `dualscreen_enabled` setting, or per-screen toggles?** *Pencilled:
   one, plus the follow-context toggle. Per-screen is a settings screen we said
   we would not build.*
5. **Does the panel keep rendering when the game is backgrounded?** *Answered,
   and promoted out of this list into Lifecycle (§7): dismiss on pause, re-show
   on resume, and handle hot-plug, all in M1.*
6. **Does a file-backed mapping need to be a file at all?** Both sides are one
   process, so ashmem via `MemoryFile`, or a plain native allocation whose
   address Java is handed, would remove the file, the sizing, the storage
   question and the `DangerousGetHandle` self-test V1 needs. The obstacle is
   that C#'s `MemoryMappedFile` wants a path. *Pencilled: keep the file, in
   `getNoBackupFilesDir()`. Revisit if it ever misbehaves — it is a contained
   change.*

   ---

## Appendix — what the screens are built from

The game's own API, as the screens use it. Class and member names are from the
shipped `Assembly-CSharp`, so they can be confirmed against any copy of the
game — `check.ps1` compiles every one of them against the depot. Nothing here
is guessed; where a name proved wrong on the device it is corrected in place.

### The headline: enumeration is free

The single biggest risk in §8 — "the items are Addressables-only and might not
be resident" — is **mostly not real**. Every pane's data is reachable through a
static, public method on a manager that is a live scene `MonoBehaviour` from
boot. No `Resources.Load`, no Addressables handle, no waiting for the player to
open a pane.

| Screen | Item type | Enumerate all | Per-item state |
| --- | --- | --- | --- |
| Inventory | `CollectableItem` | `CollectableItemManager.GetCollectedItems()` | `.CollectedAmount` `.IsVisible` `.IsSeen` |
| Tools | `ToolItem` | `ToolItemManager.GetAllTools()`, `GetUnlockedTools()` | `.IsUnlocked` `.IsUnlockedNotHidden` `.IsEquipped` `.Type` `.SavedData` |
| Crests | `ToolCrest` | `ToolItemManager.GetAllCrests()` | `.IsUnlocked` `.IsEquipped` `.Slots` `.SaveData` |
| Tasks | `BasicQuestBase` | `QuestManager.GetAllQuests()`, `GetAcceptedQuests()`, `GetActiveQuests()` | `.IsAccepted`, `FullQuestBase.IsCompleted` `.CanComplete` `.Targets` |
| Journal | `EnemyJournalRecord` | `EnemyJournalManager.GetAllEnemies()`, `GetKilledEnemies()`, `GetRequiredEnemies()` | `.KillCount` `.KillsRequired` `.IsVisible` |
| Map | `GameMap.ZoneInfo` per `GlobalEnums.MapZone` | `GameManager.instance.gameMap`, `HasAnyMapForZone(zone)` | `GameMapScene.IsMapped` `.IsVisited` |

Save data is uniform: every container derives from
`SerializableNamedList<TData,TContainer>` with `GetData(name)`,
`SetData(name,data)`, `GetValidNames(pred)`, `Enumerate()`. That is what M9
will write through, and it is the same shape for tools, crests, quests,
collectables and kills.

### Icons and names, by exact field

* `ToolItem` — `GetInventorySprite(IconVariants)`, `GetHudSprite(IconVariants)`,
  `InventorySpriteBase`, `DisplayName`, `Description` (both `LocalisedString`).
  `ToolItemType { Red=0, Blue=1, Yellow=2, Skill=3 }` gives the grouping.
* `ToolCrest` — `CrestSprite`, `CrestSilhouette`, `CrestGlow`,
  `SlotInfo[] Slots` (`Position`, `Type`, nav indices), `DisplayName`,
  `Description`.
* `EnemyJournalRecord` — `IconSprite`, `EnemySprite`, `DisplayName`,
  `Description`, `Notes` (sheet `"Journal"`).
* `CollectableItem` — `GetIcon(ReadSource)`, `GetDisplayName(ReadSource)`,
  `GetDescription(ReadSource)`.
* Quests — `QuestType.Icon` / `CanCompleteIcon` / `LargeIcon`,
  `FullQuestBase.RewardIcon`, `QuestTarget { Counter, Count, ItemName }`.
* Tab icons — `InventoryPane.ListIcon`, one `Sprite` per pane, no guessing.

There is **no sprite atlas to query**: the project has exactly two
`SpriteAtlas` assets (`Menu Cursors`, `Swamp_Extras`) and neither is the
inventory. Every icon is a direct `Sprite` field. "Steal the reference" is not a
workaround here, it is the only available mechanism, which is reassuring.

The cursor is not one sprite either: `InventoryCursor` is four corner-bracket
`Transform`s plus a `back` and a `backGlow` `SpriteRenderer` tinted per item via
`InventoryItemSelectable.CursorColor` / `CursorGlowScale`. We rebuild that
ourselves from the same four sprites.

### Text

`TeamCherry.Localization` (a plugin DLL, present in the depot):

```csharp
Language.Get(key)                 Language.Get(key, sheetTitle)
Language.Has(key, sheetTitle)     Language.CurrentLanguage()
struct LocalisedString { string Sheet, Key; ... implicit operator string }
```

Every display string in the game is a `LocalisedString`, resolved by assigning
it to a `string`. Sheets seen in code: `"Journal"`, `"Quests"`, `"UI"`,
`"MainMenu"`, `"Prompts"`, `"Map Zones"`. Zone names are
`Language.Get(mapZone.ToString(), "Map Zones")`.

**Fonts:** the reliable way to get the right one is to read `.font` off a live
`TMProOld.TextMeshPro` in the inventory — `InventoryItemManager.nameText` /
`descriptionText`, `InventoryPaneList.currentPaneText`,
`JournalItemManager.notesText`. `FontManager` exists but is a language-switch
hook, not a registry, so there is no "give me the inventory font" call to make.

### Open/close and context

* Root inventory object is literally named `"Inventory"`, a child of
  `HUDCamera.GameplayChild`; `GameManager.inventoryFSM` is the `PlayMakerFSM`
  on it.
* `PlayerData.instance.isInventoryOpen` is the flag.
* `InventoryPaneList` raises `event Action OpeningInventory`, `ClosingInventory`
  and `event Action<int> MovedPaneIndex`. **Follow-context can subscribe to
  these instead of polling** — which is how open question 1 gets implemented
  without a single per-frame singleton lookup.
* `GlobalEnums.GameState { INACTIVE, MAIN_MENU, LOADING, ENTERING_LEVEL,
  PLAYING, PAUSED, EXITING_LEVEL, CUTSCENE, PRIMER }` — `CUTSCENE` is the hook
  for open question 3.

### Map

* `GameMap` backs both the quick map and the inventory map — **one instance**,
  spawned per gameplay scene load by `InventoryMapManager.EnsureGameMapSpawned`
  (driven from `GameCameras.StartScene` → `HUDCamera.EnsureGameMapSpawned`, so
  it exists before any pane is opened), and reachable as
  `GameManager.instance.gameMap`. `ZoneInfo` per `MapZone` holds
  `WideMapZoomPosition`, `QuickMapPosition`, `LocalBounds`, `NameOverride`,
  `Maps` — but `ZoneInfo`, `mapZoneInfo`, `DisableAllAreas` and
  `EnableUnlockedAreas` are all **private**.
* Useful public API: `GetCurrentMapZone()`, `HasAnyMapForZone(MapZone)`,
  `HasMapForScene(name, out bool)`, `GetZoomPosition(MapZone)`,
  `UpdateMapPosition(Vector2)`, `KeepWithinBounds`, `GetMapScrollBounds(...)`,
  `StartPan()` / `StopPan()`, `SetupMap(bool pinsOnly)`,
  `TryOpenQuickMap(out string)`, `CloseQuickMap()`, `WorldMap()`.
  `HeroController.HasNoMap(GameMap)` is `public static`.
* `GameMapScene` per room: `States { Hidden, Rough, Full }`, `IsMapped`,
  `IsVisited`, `TryGetSpriteBounds(Transform, out Bounds)`.
* **The render path.** Two orthographic cameras under
  `HudCamera/In-game/Game Map Rendering`, both `cullingMask = 32` and
  `size 8.710664`, split **by Z slice**: `Map Camera` clips `[42,50]` (rooms,
  `depth 90`), `Decorator Camera` clips `[30,42]` (pins, arrows, text,
  `depth 91`). The `GameMap` root is placed at local `z = 43`; pins and
  decorations are authored ~`-2.5` in local z, which is what separates them.
  `CameraRenderToMesh` gives each a `RenderTexture` and displays it on
  `Game Map Quad` / `Game Map Decorator Quad` under `Game Map Quads`.
* **The fade is on the quads, not the content.** `InventoryMapManager`'s
  `sceneMapFade` is a component on `Game Map Quads`; the alpha leaves are on
  its two child quads. The live content lives in a *different* subtree with no
  fade controller. This is what lets V2 re-render the content itself and ignore
  the fade entirely, and it is the single fact M8 turns on.
* **`CameraRenderToMesh.SetActive(GameMap, bool)` is a process-wide static**
  over a list keyed only by an enum, called from exactly two places
  (`DisableAllAreas`, `EnableUnlockedAreas`). Anything of ours carrying that
  component would be toggled by the game's own map opening and closing — which
  is why V1 cloned bare `MeshRenderer`s rather than the component, and why V2
  builds plain `Camera`s that the enum cannot reach.
* `InventoryMapManager` owns the `GameMap gameMapPrefab` and an
  `InventoryWideMap` parchment overview whose zones are
  `InventoryItemWideMapZone` with `EnumerateMapZones()` / `IsUnlocked`. Zoom is
  a `localScale` lerp from `0.39` to `1.15` over `zoomCurve`. The wide map is a
  per-zone `SpriteRenderer` collage (`Wide_map__0007_Bonetown` and friends), not
  a render target — the one place a plain sprite *is* available. Note the
  manager exposes **no** public accessor for itself or its `gameMap`;
  `FindObjectOfType` is the only way in, and `GameManager.instance.gameMap` is
  the better one.
* Pins: `MapPin.PinVisibilityStates { PinsAndKey, Pins, None }`,
  `MapPin.CurrentState`, `MapMarkerMenu` / `MapMarkerButton` / `MapKey`.
* **Rejected: spawning a second `GameMap` of our own.** It was the obvious
  answer and it does not survive the source. `GameMap.Awake` subscribes
  `SceneManager.sceneLoaded`, so a second instance runs the handler twice per
  load; it then `AddPin()`s every child into
  `MapPin._activePins`, a `private static` **global**, so our clone's pins would
  join the game's pin-count and visibility logic; and worst,
  `GameMap.OnDestroy` calls `MapPin.ClearActivePins()`, which clears that whole
  static list — tearing our clone down would wipe the real map's pins. Kept
  here because it is the first idea anyone has, including us.

### Layers and sorting

Layer 5 is `UI` and is the *only* layer `hudCamera` renders
(`cullingMask = 32`). Layers **3 and 6 are blank**. Sorting layers end
`... HUD, Inventory`, so `Inventory` draws above `HUD` — useful if we ever
place a `SpriteRenderer` rather than a uGUI graphic.

### Corrections this forced on the plan

1. **`TMProOld`, not `TMPro`.** Would have been a silent, confusing failure —
   the types exist under both names.
2. **There is no Crests pane** to mirror; we are inventing one (§5).
3. **The map is world-space sprites *and* a camera composite** — M8 was
   rewritten for the first half, again when `MapPin`'s global state killed the
   clean-clone idea, and a third time when the second half turned up: two
   cameras split by Z slice, with the fade applied to the display quads rather
   than to the content. That last one turned M8 from the hardest screen into
   one of the cheapest, and it was missed twice because "not a `RenderTexture`
   composite" was recorded as settled.
4. **`MoveMenuToHUDCamera` was a red herring.** An earlier draft justified the
   per-scene layer strip with it; the call never sets bit 6 on anything. The
   real carriers are `Everything`-masked cameras (§4).
5. **Item enumeration is free**, so the §8 "not resident" risk shrinks to
   `GlobalSettings.UI` (genuinely Addressables-backed, key
   `"GlobalSettings/UI.asset"`) and to individual sprites. Null-tolerance stays;
   the panic does not.

### Corrections the design review forced

Recorded because each was stated in an earlier draft as though it were settled:

6. **`Choreographer` follows the primary display, not the panel.** It is
   per-`Looper`, and a `Presentation` runs on the main thread. "The panel's own
   vsync" and "120 Hz for free" were both false; the shared-memory publish that
   depended on them is gone (§3).
7. **Atomic is not ordered.** A single aligned 32-bit store is atomic on ARM64,
   but `MappedByteBuffer.getInt()` is a plain load with no acquire, and a
   control dependency does not order load-load on ARM. The lock-free publish
   protocol was quietly incorrect; a JNI call gives the barrier as a side
   effect, and is less code (§3).
8. **In-flight readbacks outlive the mapping.** Nothing in the draft drained
   them before teardown, which is a native crash on quit, pause or unplug (§3).
9. **uGUI and TMP rebuild after `LateUpdate`**, so render-on-dirty as drafted
   would have drawn the previous mesh — and for a dirty-once screen, never
   corrected (§3). Also confirmed while checking: the game is **built-in
   pipeline**, referencing no URP or SRP assembly, so `Camera.Render()` is
   genuinely supported.
10. **Slots were not page-aligned** despite the layout comment claiming they
    were: 1240·1080·4 = 5,356,800, which is 256-aligned but not 4096-aligned.
    Padded (§3).
11. **Hot-plug was missing entirely** — the one lifecycle event most likely on a
    handheld with a second panel (§7).
