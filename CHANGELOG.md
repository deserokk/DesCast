# Changelog

All of this shipped on 2026-09-01, in one evening, with Bunny and Q testing live. The
reasons matter more than the version numbers — most of these are bugs that only appear
when a second person uses the thing.

## 0.4.5

**The editor no longer opens itself when you log in.** It had been doing so behind a config
flag with no interface — a leftover from iterating, where a window up on every login was
convenient for one person and an interruption for everyone else.

⭐ Removed rather than defaulted off, because a default would not have helped anyone who
already had it saved as on. And the flag went with it: a setting nobody could see is not a
setting.

⚠ The window is still one click away from Dalamud's own plugin buttons, or `/descast`. A
participant never needs it at all — install, log in, the screens are there.

## 0.4.4

**Paste a whole link and it becomes the code.** From testing with Bunny: told "just the last
eight characters", she pasted the entire address — as anyone would. It worked, because the
resolver already understood it, but the box kept a long string, so she never saw the code
and could not read it back to anyone.

⭐ The framing that made it land was hers: *"oh, it's like a Mare code."* Every FFXIV player
already holds that model regardless of how tech-averse they are, so the interface now looks
like the thing they know — the button says **Add a room code**, the tooltip names the
comparison, and a pasted paste-link collapses to its eight characters so what you see agrees
with what you were told.

⚠ Only paste-shaped links reduce. Gists and raw GitHub addresses need their full path to
identify a file, so those are left untouched.

## 0.4.3

**Pictures are released when you leave.** Nothing was ever released before this — the cache
was documented as living for the whole session, which is right about not reloading a PNG
every frame and wrong about never handing one back, so touring four rooms held four rooms.
Chris found it from the memory readout: five pictures in the hall, seven after stepping into
a room holding two.

⭐ "Still wanted" means every slide a screen *could* show, not the one currently drawn — a
five-picture album at thirty seconds a slide leaves any given picture untouched for two
minutes, and the obvious signal would have evicted four of them on a permanent loop.

**Downloads are cached on disk, so releasing them costs nothing.** Keyed by a hash of the
URL, kept between sessions: a picture is fetched once, ever. Leaving for a duty and coming
back now touches the network not at all.

⭐ Past six hours it asks the server whether anything changed rather than asking for the
file, so a check costs a couple of hundred bytes instead of a megabyte. Content stays
current *and* nothing is re-downloaded — both, rather than a trade.

⚠⚠ This matters to somebody specific rather than in the abstract: Q is on metered
internet, so paying repeatedly for the same picture is a real cost to him and invisible to
everyone else.

**Album checks moved from five minutes to an hour, and `/descast refresh` was added.** The
polling, not the pictures, was the ongoing cost — pictures are paid for once, a listing check
repeats for as long as anybody stands in the room. An hour is acceptable *because* of the
command: the automatic interval only has to cover "eventually", and wanting a new poster
right now is somebody deciding to look, who can say so.

**Two figures in the editor**, because they are easy to confuse: video memory held (released
when you leave) and downloads saved (kept so that leaving is free). A large number on the
second is the cache working.

**Depth works on panels that fit their picture.** The slider sat inside an unrelated
condition, so the one setting that makes a panel read as an object rather than a decal was
missing for anyone using the default. The block was misplaced and the comment justifying it
had been written to fit the mistake.

**Screens cannot be moved where you cannot build.** Placing already required build
permission; editing now does too. ⭐ Verified per *room*, not per plot — tested by the FC
master standing in a member's private chamber on his own plot, where it correctly refuses.

## 0.4.2

**Editing a manifest URL no longer throws.** The editor listed subscribed manifests by
walking a lazy enumerator over the very list it lets you edit, so assigning through the
text box bumped the list's version counter and the next step of the loop threw *"Collection
was modified"*. The value was saved before it threw, which is why the screen worked and the
error appeared anyway — a confusing pair to be handed.

⚠⚠ It was invisible with one manifest subscribed, because the loop had already finished.
**The second entry is what made it fire** — so the bug waited until somebody first shared a
room and then went off during the thing it was there to enable. Every status list is now
built as a snapshot before it is drawn.

The same trap was waiting in the included-manifests list, where a company file pulling in a
member's room while the editor was open would have done the same thing. Fixed there too.

Found by Chris and Bunny.

## 0.4.1

**GIFs cost roughly a tenth of what they did**, from two changes that give up nothing
anyone can see.

**Frames are merged to about twenty a second.** This, not resolution, is where a GIF's
memory goes — they are small pictures and a great many of them. The delay field is in
hundredths of a second and a large share of real GIFs are written at 2, which is fifty
frames a second: faster than most animation is drawn, faster than many monitors, and past
what the eye resolves as motion. A 50fps meme drops from 100 frames to 34 and plays at
exactly the same speed. A GIF already at 10fps is left completely alone.

⚠⚠ Frames are merged rather than dropped, and the total duration is preserved to the
millisecond. That is load-bearing rather than tidy: the wall clock is the only thing
keeping two people on the same frame, so a loop running even slightly short on one machine
would drift a room apart over a few minutes. Verified against six timing shapes, including
a GIF that holds on its punchline — that pause survives as one long frame instead of forty
identical ones.

**GIFs are held to half the resolution stills are** (floored at 320px). Motion hides
detail: nobody examines a frame that is on screen for a twentieth of a second, which is why
every video format spends fewer bits on the moving parts of a picture.

**Picture detail now defaults to Medium** (1536px, about 5 MB a picture) rather than High.
Chris compared his own boards at every setting down to 1024 and could not tell them apart,
so the higher default was buying detail nobody looks at — charged to the guest least able
to afford it, who is also the person least likely to go and change a setting. Every option
above it stays; that is half the decision rather than a leftover.

⚠ Only affects people who have never touched the setting. An existing choice is not
overwritten.

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
