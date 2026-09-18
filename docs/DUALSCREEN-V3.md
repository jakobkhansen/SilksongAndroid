# Dualscreen v3

We are implementing a new UI for the Bottom screen, the goal is to simulate the Silksong
UI more closely, using the same UI elements + some more features.

## First visual slice

The first implementation is limited to a shared health/silk header and bottom
icon tabs. Tab contents keep their existing layouts, fitted into the smaller
body; the background stays pure black. `DsLayout` supplies the same header,
body and footer bounds to drawing and touch hit-testing. These use the actual
canvas rect, not an assumed display height, so the icons stay at the physical
bottom edge with insets or canvas scaling. A canvas resize rebuilds the shell
and cancels any gesture already in progress without changing Android input
isolation.

At 1240x1080, the HUD occupies 240 pixels and the tab strip 176, leaving a
664-pixel content body. The tab strip has 88-pixel icons and a roomier, separate
caret frame. The health display targets 55 pixels between native mask slots,
a 10% increase over the first on-device sizing pass, rather than shrinking to
fit each animation's sprite bounds. Slot jitter is excluded from the camera's layout anchors; the
native sprites still animate normally. Only an unusually long health/lifeblood
row or silk bar reduces the normal scale to fit the available width.

Tabs use each native `InventoryPane.ListIcon`, in Inventory, Crest, Tasks,
Journal, Map order. Map remains the initial screen. A separate, static pair of
the existing caret sprites marks the selected tab. Until native icons load,
their text labels remain usable; missing art is logged and retried. This does
not open the game's inventory or mark its items as seen.

The health header renders the live masks, crest/bind frame, silk spool and
equipped tool/skill icons into a small GPU render texture. It also includes
their crest-change and over-blue HUD effects, but not the currency counters.
Tool charge rings are native world-space uGUI canvases. Their canvas roots and
graphics are routed before canvas batching in LateUpdate, held across every
camera render, and restored at frame end. Per-camera layer changes work for
sprites but are too late for the already-built UI batches. Primary camera masks
include the ring layer when showing the top HUD or falling back to it. The
capture uses the header's bottom padding as well, fitting the complete rings
without shrinking the 55-pixel health spacing or changing the content area.
Native objects stay in place and their controllers remain active. Sprite
renderer layers change between a camera's pre-cull and post-render callbacks;
canvas layers are restored after rendering. Native updates and slot creation
see the original layers. Layer 3 is reserved
for this capture, separate from the second-screen UI's layer 6. If it is already
in use when binding, capture is refused and the normal HUD is retained.

The launcher's **Show health HUD on top screen** toggle also shows this HUD on
the main display. It defaults off and takes effect at the next game launch.
Main-screen hiding requires a presentable bottom canvas and a successful HUD
render in the same frame; the first capture also waits for the canvas hand-off.
For a camera-backed canvas, readiness checks `worldCamera.targetDisplay`;
`Canvas.targetDisplay` is only meaningful in overlay mode. Using the latter
for both modes rejected the live Thor canvas before HUD binding.
Pausing, losing the second display, rebuilding the shell, or a capture failure
restores normal main-screen rendering. The game's own cinematic/HUD visibility
still applies. In particular, we never disable or duplicate `BlueHealth`,
`SilkSpool`, or the HUD state machines: they also maintain gameplay state.

Unlock filtering, notification-dot behaviour, sliding transitions, animated
carets, content redesign, item use, equipping and marker editing remain deferred.
Native availability getters are not uniformly read-only; some update shared
inventory caches, so they must not simply be polled by the tab strip.

### HUD diagnostics

The optional `dualscreen_v2` file in the game's external files directory accepts
these developer overrides, read once per process:

| Key | Purpose |
| --- | --- |
| `hud_probe=1` | Dump the bound health/silk render hierarchies once, with sprite names, layers and renderer bounds (`DsProbe`). |
| `hud_diag=1` | Log capture/primary-hide frame numbers, live renderer count, health, lifeblood, silk, layout bounds and mask spacing in pixels every two seconds (`DsHud`). |
| `hud_show_top=1` | Keep the original top HUD too, overriding the launcher toggle for diagnosis. |
| `hud_zoom=100` | Framing zoom percentage, 50-200; values above 100 deliberately crop more tightly. |
| `hud_x=0`, `hud_y=0` | Camera offsets in hundredths of a world unit along the HUD's axes. |

Readiness failures also log the canvas mode, canvas/camera display indices,
active states and actual layout size even without diagnostic overrides.

No new runtime entry point is needed: the existing dual-screen shell owns the
HUD component and its texture. Local regressions cover layout, touch routing,
stable framing and reversible render scopes. The first Thor pass confirmed
native tab icons, touch, live health/silk rendering and default top-HUD hiding.
The follow-up confirmed the 240/176-pixel frame bands and steady 50-pixel mask
spacing while startup animations changed the active renderer set. Screenshots
show the larger health row and roomier tab carets. Remaining gameplay-specific
HUD states still need broader device coverage. A final 10% size increase was
also confirmed on the Thor at steady 55-pixel spacing without clipping the
health row or silk bar.

## Healthbar

We want to move the healthbar from top screen to the bottom screen, look at screenshots.
It should be hidden on the top screen, but a settings toggle should be able to show it
again.

## Tabs

Instead of the top named tabs, we want to take the icons that Silksong uses in its own
menu and align them near the bottom. If possible, we want to do the same as Silksong where
menu icons show up when you unlock the tabs (get your first quest unlocks Tasks tab, get
first map unlocks map tab, etc).

Also, if possible we want a sliding animation between tabs. So when you press on inventory
from tasks screen, it should slide in the content from the left, and if pressing the map
from tasks, it should slide the content in from the right.

## Cursor

There are two different cursors, the selected tab and the highlighted item in each tab.
These should use the same "carets" as we use on the current menu, but preferably we want
also an animation system here, where the caret slides from the previously selected item to
the next. Remember, there are two separate cursors, one for the selected item/task, etc
and one for the selected tab. We should also make the mask icons, nail, etc selectable
with the carets around them if possible.

## Tab contents

Every tab should be reworked to follow the contents of the screenshots.

On the crest tab, we need to rotate the two extra tools on the bottom to fix them in.
Ignore the icons being rotated in the screenshot, they should still be upright. Also
disregard the "unequip skills" button text, this menu is not interactable with the
controller, only touch.

### Dividers

The designer's dividers are in, replacing the plain white lines. The art is in
`docs/dividers/` and embedded in `DsRuleArt` as base64, because the patches are compiled
on the device from source and have no asset pipeline to read a file through; all three
together are under two kilobytes.

The ends **are** the decoration. Both hairlines are fully opaque only across their middle
45% and ramp to alpha 0 at each tip, and both are authored at very near their final size
(1100 and 608, against rules of ~1200 and ~624). They are therefore stretched whole rather
than nine-sliced — the taper scales with the line, so a short rule reads as a short rule
instead of a long one with its ends cut off.

`Inventory Divider` is not a rule but an **end cap**: a vertical tick down the far left
with a stroke leaving it at mid-height and fading out to the right. It is drawn at its own
size and never stretched, and it marks the groups inside an icon grid.

Every rule on the panel goes through `DsWidgets.HRule`/`VRule`, so the HUD rule, the
tab-strip rule, the column gutters and the Tasks dividers all take the art without those
screens changing. Each accessor may return null and the widgets fall back to the plain box
rule.

One divider is **not** ours, and could not be: the ornament either side of Tasks'
COMPLETED caption. See the Tasks section below.

### Tasks

Tasks now has the shape the screenshots do, which is the shape the game's own quest pane
has — and which the previous two versions both missed:

|              | v1                     | v2                        | now                        |
| ------------ | ---------------------- | ------------------------- | -------------------------- |
| layout       | four-column icon grid  | one column, full width    | **two across**             |
| what a cell says | icon only          | name + progress counter   | icon + **type** + name     |
| finished quests | mixed in            | under a caption           | under a caption, **hideable** |

The first was wrong because quests are read by NAME rather than recognised by picture. The
second was right about that and wrong about everything else, because a quest is not
identified by its name alone. The game sets the quest's **type** in small caps above it —
"Last Dive" is a place, `DESCEND / Last Dive` is an instruction — and that line is half the
sentence. It comes from `QuestType.DisplayName` and is tinted `QuestType.TextColor`, set in
caps by the label's own style rather than by rewriting the string, because the type name is
localised and upper-casing text in code is a per-language decision we are in no position to
make.

Every rule of the grouping is the game's, and each is taken from a named method so it can
be checked rather than believed:

| What | Where it comes from |
| --- | --- |
| two across | `InventoryItemGrid`'s `RowSplit` on the Quests section |
| a quest is prioritised | `InventoryItemQuestManager.IsInMainQuestSection` — a `MainQuest` that is not complete |
| the type line and its colour | `InventoryItemQuest.SetQuest`, `QuestType.DisplayName`/`TextColor` |
| two groups, current and completed | `GetGridSections` |
| the toggle exists at all | `isCompletedQuestsVisible`, offered only while `completedQuests.Count > 0` |

The prioritised quest takes a **full-width row with a larger icon**, centred in it, which
is the game's own `itemListLayout` alignment and what the designs show. It is not one of
several things to choose between; it is the thing being done.

Two places where we knowingly differ, both because this panel is a touch screen:

- The game binds the toggle to `MenuActions.Super` and draws a **Y** glyph beside it.
  Nothing here is reachable by controller, so it takes the header action bar every other
  screen's controls live in, reading `HIDE COMPLETED` / `SHOW COMPLETED`. It is absent
  entirely when nothing is finished — a button that hides an empty group is a control the
  player has to press in order to learn it does nothing.
- The game's divider between the prioritised quest and the rest is an invisible `Spacer`.
  Set two across, the priorities stop in the middle of a row and the break is genuinely
  hard to see, so ours is drawn. It appears only when a prioritised quest actually exists.

What the cell gave up to gain the type line is the per-quest counter. Three things carry
the "how far along" signal without it: the game's own `CanCompleteIcon` replaces the icon
when a quest is ready to hand in, the name goes bold with it, and the description pane
still breaks every target down individually. A number beside a name in a 384-pixel cell
would have cost the name the room the design gives it — and a name is the one thing here
that has to arrive whole, so a long one shrinks to fit rather than wrapping or truncating.

#### The COMPLETED ornament is read from the running game

The caption is flanked by a small filigree stroke, mirrored left and right, with a hairline
running outward from each. That art is the game's, and it is the one divider on the panel
that **cannot** be embedded the way `DsRuleArt`'s three are.

The reason is worth recording, because the obvious approach fails silently. In a decompile
of the game the sprite reference on those objects is the placeholder GUID
`0000000deadbeef15deadf00d0000000` — no `.meta` anywhere declares it, and the same id
stands in for the quest icons and the button glyphs too. Anything that resolves it "finds"
whichever unrelated PNG it happened to match. There is no file on disk to lift.

The object graph survives the decompile even though the art does not, and that is enough:
`InventoryItemQuestManager.completedHeading` is a `Transform` holding a `Title Text` with
`Fluer Left` and `Fluer Right` under it. `DsGameArt.QuestDivider()` walks exactly that path
on the live scene object and takes the sprite off the first `SpriteRenderer` it finds,
inactive children included — the heading is switched off whenever the player has nothing
finished, which is precisely when a new save is most likely to be looking at this screen.
Left and right are the same art mirrored, so one sprite is all there is to find.

Null is the normal early answer, not an error: the pane is built when the game's own
inventory first opens, and before that there is nothing to read. The divider falls back to
a plain rule, and whether the ornament has arrived is part of the list's rebuild signature,
so the moment the game hands it over the divider is rebuilt with it — without which the
fallback would persist until something unrelated happened to change the list.

### Three columns

Inventory, Crest and Journal now share one shape, which is the shape the screenshots
have: a **chooser**, the **subject**, and a **description** — left to right, each column
bounded by a rule down the gutter beside it rather than by a box around it.

| Tab | Left | Centre | Right |
| --- | --- | --- | --- |
| Inventory | Hornet's own standing | the collectables, 3 across | selected item |
| Crest | the equipped crest and its ring | every tool, 3 across | selected tool |
| Journal | the creatures, 3 across | the selected creature | what is known about it |
| Tasks | the quest list | — | selected quest |

The description used to be the bottom of a column: a band across the whole panel on
Inventory, and tucked under the crest or the portrait elsewhere. That put the prose as far
as it could be from the thing it described, and it cost the grid the vertical room it
needed. As its own column it sits beside the selection, and the grids run the full height
of the body and **scroll**, which is the shape a collection that grows actually has.

Two consequences worth recording, because both were silent:

- The character column is now narrower than the Hornet composition's natural width, so
  `DsHornetPanel` fits its art to **both** axes rather than to height alone. Laying out at
  the requested width pushed the silk spool — which sits shoulder to shoulder with the
  mask, with four pixels to spare at the old 520 — off the end of the panel.
- `DsWidgets.Label` takes TMP's `Left`, which is *middle*-left. In a short band that reads
  as top-aligned; in a full-height column the prose floats in the middle of the panel with
  a gap under its own title. Description panes take `TopLeft`.

## Interactivity

We want to make the Dual screen menu completely replace the in-game menu, this means we
need to be able to consume items, switch tools and crest, and place and remove map markers
from the bottom screen.

For consuming items, we should have just a button on the right side next to the healthbar
which says "USE" or something like that. Preferably only visible when the item is usable.

Same for changing tools, we should have an UNEQUIP/EQUIP button next to the healthbar.
Should only be visible when sitting on a bench, since you can't switch tools when not on a
bench usually.

For the markers, we want a markers button, this button should put us in "marker mode",
this mode should hide the tabs on the bottom and replace them with the unlocked markers
icons. You can then tap one of the marker icons on the bottom to select it, and then tap
the screen to place a marker. When in marker mode, tapping an existing marker on the map
should remove it. The same button should take you out of marker mode. The user should
still be able to pan and zoom the map when in marker mode, so only a single tap should
place/delete marker. When the user enters marker mode, the map should automatically switch
to "full map" mode.
