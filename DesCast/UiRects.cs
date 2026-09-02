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
                if (unit == null || !unit->IsVisible || !unit->IsReady || unit->RootNode == null)
                    continue;

                // ⚠ A faded panel is still "visible" by the flag. Alpha is what says
                // whether anything is actually on screen.
                if (unit->Alpha == 0) continue;

                // ⚠⚠ Measure what the panel actually draws, not the box it reserves.
                //
                // Two earlier attempts were wrong in the same direction. The root node is
                // an addon's declared canvas and is routinely far bigger than its content
                // — the party list's is wide enough to punch a hole across a screen. And
                // the window bounds still cover reserved-but-empty panels: the debuff
                // tray sits in the middle of the display at all times, drawing nothing
                // when you have no debuffs, and took a bite out of a poster for it.
                //
                // Unioning the visible children answers the real question — is there
                // anything here to cover up? — and an empty tray contributes nothing.
                if (!VisibleContentBounds(unit->RootNode, out var left, out var top, out var right, out var bottom))
                    continue;

                var w = right - left;
                var h = bottom - top;
                if (w <= 0f || h <= 0f) continue;

                // ⚠ Skip anything effectively fullscreen. Several always-loaded addons are
                // invisible containers the size of the screen, and culling against one of
                // those would hide every screen in the house with no clue why.
                if (w >= viewportW * 0.9f && h >= viewportH * 0.9f) continue;

                // ⚠ And skip slivers. A few pixels of something is not what anyone is
                // trying to read, and each rectangle costs shader work on every pixel.
                if (w < 16f || h < 16f) continue;

                into[count++] = new Vector4(left, top, right, bottom);
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

    /// <summary>
    /// Union of the screen bounds of everything actually drawn under <paramref name="root"/>.
    /// Returns false when nothing is — an empty panel, which must not be culled against.
    ///
    /// ⚠ Node-budgeted. This runs every frame across every loaded addon, and some of them
    /// have deep trees; a fixed ceiling keeps a pathological one from turning the draw path
    /// into a tree walk. Running out early only means a slightly loose box, never a hang.
    /// </summary>
    private static bool VisibleContentBounds(
        AtkResNode* root, out float left, out float top, out float right, out float bottom)
    {
        left = top = float.MaxValue;
        right = bottom = float.MinValue;

        if (root == null || (root->NodeFlags & NodeFlags.Visible) == 0) return false;

        var budget = 96;
        var any = false;

        // Iterative rather than recursive: an unexpected cycle in game data would take the
        // whole game down with a stack overflow, and there is no catching that.
        var stack = stackalloc nint[32];
        var depth = 0;
        stack[depth++] = (nint)root->ChildNode;

        while (depth > 0 && budget > 0)
        {
            var node = (AtkResNode*)stack[--depth];

            for (; node != null && budget-- > 0; node = node->PrevSiblingNode)
            {
                if ((node->NodeFlags & NodeFlags.Visible) == 0) continue;

                var w = node->Width * node->ScaleX;
                var h = node->Height * node->ScaleY;

                if (w > 0f && h > 0f)
                {
                    if (node->ScreenX < left) left = node->ScreenX;
                    if (node->ScreenY < top) top = node->ScreenY;
                    if (node->ScreenX + w > right) right = node->ScreenX + w;
                    if (node->ScreenY + h > bottom) bottom = node->ScreenY + h;
                    any = true;
                }

                if (node->ChildNode != null && depth < 32)
                    stack[depth++] = (nint)node->ChildNode;
            }
        }

        return any;
    }
}
