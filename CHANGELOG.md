# Changelog

All of this shipped on 2026-09-01, in one evening, with Bunny and Q testing live. The
reasons matter more than the version numbers — most of these are bugs that only appear
when a second person uses the thing.

## 0.1.17

**Restored the action bar walker deleted in 0.1.16.** Hotbar buttons are component nodes,
and a component's children hang off its own node list rather than `ChildNode` — so the
general tree walk never reaches the icons. The two code paths looked equivalent and were
not. Deleting working code because it resembles other working code is its own failure mode.

## 0.1.16

**Measure what interface elements paint, on a named list only.** The target info bar cut a
band across the screen far wider than its label, because its box is sized for the longest
possible target name.

The fix combined two earlier attempts that were each half right: painted-node measurement
was correct but was being applied to elements that should never be covered, and the named
list was correct but used whole-addon rectangles.

## 0.1.15

**Cover the target info bar.** People target and examine each other constantly in a house,
so it matters more here than most of the combat interface. Both layouts are listed — the
game splits target info into three elements when "display target info independently" is on.

## 0.1.14

**The chat element is `ChatLog`, not `_ChatLog`.** Nearly every other one is prefixed with
an underscore. The guessed name matched nothing, and a name matching nothing looks exactly
like a name that matched something empty — so chat alone stayed covered while everything
else worked.

## 0.1.13

**Per-button hotbar rectangles.** A box round a whole bar covers the gaps in it, and people
leave gaps deliberately — Q groups his buttons to one side and parks his job gauge in the
space left over. Touching buttons merge into runs, so a centred row is still one rectangle.

## 0.1.12

**Name the interface elements instead of trying to detect them.** Three attempts at a
general rule all failed the same way: they measured the box a panel *reserves* rather than
what it shows, so the debuff tray — mid-screen, permanently present, usually empty — kept
biting rectangles out of posters.

Pictomancy, the library behind Splatoon's automatic UI clipping, settles it with 881 lines
of hand-written per-element code including one function per job gauge. There is no clever
rule, which is why nobody has one.

## 0.1.8 – 0.1.11

**Keep screens off the game's interface**, and three failed attempts at measuring it.
Everything drawn through ImGui lands on top of the game's UI, because Dalamud renders after
the game has finished — so a screen between the camera and your hotbars covered them. Found
by Q within minutes of first use.

## 0.1.7

**Removing the line from the company board now withdraws the screens.** Change detection
compared the board text and the parsed result, but an empty result was ignored rather than
applied — so an officer could publish screens to the whole company and never take them down.

## 0.1.6

**Read the company board from the login announcement.** The game prints it into chat when
you log in, which was spotted in a screenshot after an afternoon of working around the
interface. Listening costs nothing and needs no action at all.

⚠ Sender must be empty. The announcement is a system message; anything a player types has a
sender. Without that check, someone saying "Company Board: Screens: `<their link>`" in open
chat would put their pictures on your walls.

## 0.1.5

**Stopped the stall on first entering a room.** Two costs landed in the same frame: shader
compilation (~170ms, one-time) ran inside the draw callback, and image loading was
unbounded, so three screens on rotation started six downloads at once. The compiler moved to
a worker thread; images load one at a time.

## 0.1.4

**Accept a link written without `https://`.** Nobody types a scheme onto a notice board.

## 0.1.3

**Read screen links from the Free Company board.** An officer writes `Screens: 0GzA4vpc` and
every member picks it up with nothing to configure — the game supplies distribution,
persistence and rank-enforced authority for free.

A bare eight-character paste id expands to its full address: shorter on a board limited to
three short pages, and a bare token does not announce itself the way a raw link does.

## 0.1.2

**Subscribe to several shared rooms, not one.** A house is not a single shared space — the
company hall is published by officers, a private room belongs to whoever lives in it. One
URL forced those to be the same file and therefore the same editor.

## 0.1.1

**Keep screens visible when the game interface is hidden.** Screens draw through ImGui, so
Dalamud's UI hiding took them with it — and hiding the UI is the first thing anyone does
before a screenshot, which is much of the point of a decorated house. Found by Bunny within
minutes of first install.

## 0.1.0

First release. World-space screens in housing: correct perspective, correctly occluded by
walls, furniture and people, with transparency preserved. Images from disk or a link,
several cycling on a wall-clock derived index so everyone in the room agrees without any
messages passing between them. Screens are keyed to the game's own house id, so nothing
renders in a stranger's identically-shaped interior.
