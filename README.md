# DesCast

Flat screens you can place anywhere inside a house in FFXIV.

They sit in world space — they foreshorten correctly as you move around them, and they are
properly hidden by walls, furniture and people walking in front of them. A screen shows a
picture from your PC or from a link, or several on a rotation.

## Install

Add this to Dalamud's custom plugin repositories, once:

```
https://raw.githubusercontent.com/deserokk/DalamudPlugins/main/pluginmaster.json
```

Then install **DesCast** from the plugin installer. `/descast` opens the window.

## Using it

Stand in your house and press **Place a screen in front of me**. A panel appears about two
metres ahead, facing you, showing a test card. Nudge it into position with the buttons, set
its width, and point it at something.

**Images** can be a file on your PC or a web address. Paste a link to the picture itself
rather than the page it sits on — an `imgur.com/...` link is converted for you. Transparent
PNGs keep their transparency, so a crest or a cut-out sign floats rather than sitting in a
black box.

**Add more than one image** and the screen cycles through them. Which slide is showing comes
from the clock rather than a timer, so everyone in the room sees the same one without
anything being sent between you.

**Fit height to image** keeps pictures undistorted. Turn it off for a fixture whose size is
part of the furniture — a notice board that should stay the same shape whatever is on it.

## Sharing screens with your Free Company

By default your screens are yours alone. To give everyone the same boards:

1. Place them, then press **Copy screens as shared file**.
2. Paste that into a gist or pastebin and take the link.
3. Put the link in **Shared screens** — yours, and everyone else's.

Everyone pointed at the same link sees the same room, and it stays there whether or not the
person who placed it is online. Each board keeps its own images, its own rotation speed and
its own size.

## If something does not appear

The window explains itself rather than going quiet. It will tell you when you are not in a
house, when you have no build permission there, when an image could not be loaded and why,
and when the shared file could not be reached.

Two settings exist for the one thing that can go wrong invisibly:

- **Reverse depth** — if screens show through walls, or never appear at all, flip this.
- **Ignore walls (debug)** — draws the panel over everything. If a screen appears with this
  on and vanishes with it off, the panel is in the right place and only the wall test is
  wrong.

## Notes

Screens are stored per house, identified by the game's own house id, so nothing of yours
appears in a stranger's identically-shaped interior.

Nothing runs unless there is a screen in the room you are standing in. Everywhere else in
the game the plugin does no work at all.

## Licence

AGPL-3.0. See [LICENSE](LICENSE).
