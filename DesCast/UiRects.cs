using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DesCast;

/// <summary>
/// Screen rectangles of the game's own interface, so screens can be kept from painting
/// over it.
///
/// ⚠⚠ The problem this solves: everything drawn through ImGui lands <b>on top of the game's
/// UI</b>, because Dalamud renders after the game has finished — including its hotbars,
/// chat and target info. A screen on a wall behind you is fine; a screen between the camera
/// and your hotbars is not, and Q hit it within minutes of first walking in.
///
/// ⭐ The fix is to collect every visible panel's rectangle and discard our pixels inside
/// them. Xiv Media Player does the same, and Splatoon exposes it as a setting for the same
/// reason: it is occasionally wrong in the other direction, so it wants a switch.
///
/// ⚠ These are bounding boxes, not pixel outlines. A hotbar's box covers the gaps between
/// its buttons, so this over-culls slightly — a small rectangular bite out of the picture
/// rather than a perfect silhouette. Being able to see your hotbar is worth more.
/// </summary>
internal static unsafe class UiRects
{
    /// <summary>
    /// Rectangles are handed to the shader in a fixed-size constant buffer, so there is a
    /// ceiling. Thirty-two is comfortably more than the number of panels visible at once
    /// in normal play, and the largest ones are taken first.
    /// </summary>
    public const int Max = 32;

    /// <summary>
    /// Fill <paramref name="into"/> with visible interface rectangles as
    /// (left, top, right, bottom) in screen pixels. Returns how many were written.
    /// </summary>
    public static int Collect(Span<Vector4> into, float viewportW, float viewportH)
    {
        var count = 0;
        try
        {
            var manager = RaptureAtkUnitManager.Instance();
            if (manager == null) return 0;

            var units = &manager->AllLoadedUnitsList;
            for (var i = 0; i < units->Count && count < into.Length; i++)
            {
                var unit = units->Entries[i].Value;
                if (unit == null || !unit->IsVisible || unit->RootNode == null) continue;

                var w = unit->RootNode->Width * unit->Scale;
                var h = unit->RootNode->Height * unit->Scale;
                if (w <= 0f || h <= 0f) continue;

                // ⚠ Skip anything effectively fullscreen. Several always-loaded addons are
                // invisible containers the size of the screen, and culling against one of
                // those would hide every screen in the house with no clue why.
                if (w >= viewportW * 0.95f && h >= viewportH * 0.95f) continue;

                // ⚠ And skip slivers. A one-pixel-tall panel is not something anyone is
                // trying to read, and each rectangle costs shader work on every pixel.
                if (w < 12f || h < 12f) continue;

                into[count++] = new Vector4(unit->X, unit->Y, unit->X + w, unit->Y + h);
            }
        }
        catch
        {
            // Never let interface enumeration break rendering — worst case we draw over a
            // hotbar for a frame, which is the bug we started with rather than a new one.
            return count;
        }

        return count;
    }
}
