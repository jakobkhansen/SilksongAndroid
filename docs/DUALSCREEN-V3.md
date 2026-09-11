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

The health header renders the live masks, crest/bind frame and silk spool into
a small GPU render texture. It also includes their crest-change and over-blue
HUD effects, but not the currency counters or tool icons. Native objects stay
in place and their controllers remain active. Renderer layers change only
between a camera's pre-cull and post-render callbacks, then are restored; they
are not changed during native updates or slot creation. Layer 3 is reserved
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

Every tab should be reworked to follow the contents of the screenshots. We don't have the
dividers yet from our designer, so we can keep using the white lines we have.

On the crest tab, we need to rotate the two extra tools on the bottom to fix them in.
Ignore the icons being rotated in the screenshot, they should still be upright. Also
disregard the "unequip skills" button text, this menu is not interactable with the
controller, only touch.

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
