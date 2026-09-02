# Changelog

All of this shipped on 2026-09-01, in one evening, with Bunny and Q testing live. The
reasons matter more than the version numbers — most of these are bugs that only appear
when a second person uses the thing.

## 0.4.0

**Animated GIFs.** Frames are decoded once and held as textures, and which one shows is
derived from the wall clock exactly like the slideshow — so everyone standing in the room
is on the same frame of the same GIF, with no messages and nobody in charge. A reaction
gif lands together.

GDI+ composites the frames, so none of the format's awkward parts (partial frames stacked
on each other, three different disposal rules) are ours to get wrong. Delays of 0 or 10ms
are treated as 100ms, which is what every browser does and what a large share of real GIFs
depend on.

**Pictures are capped at 2048 pixels on the long edge.** Five photographs in a room came to
120 MB, because a modern phone photo is six megapixels and costs 24 MB decoded no matter
what it weighs on disk.

⚠⚠ The file format is not the lever here, and it is worth being clear about why: a JPEG
and a PNG of the same photograph cost **exactly the same** on the graphics card, because
compression is undone before the card sees the pixels. JPEG saves download time and zero
video memory.

⭐ Resolution is the whole story, and there is enormous slack in it — a screen on a wall
covers maybe a thousand pixels of a monitor, so a six-megapixel photo is carrying several
times more detail than can reach anyone's eye. The cap takes those five pictures from
120 MB to roughly 30 with nothing visibly given up. Adjustable under "Picture detail",
including off.

**A memory budget, and a number the room's owner can see.** A GIF holds every frame at
once, so one long one costs more than a wall of posters. Each is capped at 48 MB — shrunk
first, frames dropped only if shrinking is not enough, and the editor says which happened.
Dropped frames never change the duration, so the clock stays honest.

The editor also shows the running total for everything loaded, and warns past 256 MB.
⭐ Nothing is refused: the point is that whoever decorates a room never experiences the
cost of overdoing it. They loaded it gradually, on the machine that could afford it. The
guest who walks in later pays it all at once and has no idea why.

**No interface holes when the interface is hidden.** Hiding the UI is exactly what someone
does to look at a screen properly, so rectangles bitten out for a hotbar that is no longer
drawn were at their most visible precisely when the picture mattered most. (Bunny.)

**The party list is measured, not reserved.** Its box holds eight members whether or not
eight are in the party, so two people in a house produced a tall column cut out of a
screen. It now uses the painted-node measurement that target info uses, for the same
reason.

⚠ This was correct in 0.1.16 and regressed in 0.1.19, when painted-node measurement was
pulled back to target info only. That revert was right about chat — and one element too
broad: chat's actual bug was the component-subtree walk, fixed separately in 0.1.18.
(Spotted by Chris, who remembered it working.)

**Screens no longer occlude each other backwards.** Panels were tested against the scene
per pixel but never against each other, so overlapping screens resolved by list order — a
distant picture would sit on top of a near one purely because it was added later. They now
draw back to front. (Reported by Bunny.)

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
