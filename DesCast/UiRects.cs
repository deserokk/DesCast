using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DesCast;

/// <summary>
/// Screen rectangles of the game's interface, so screens can be kept from painting over it.
///
/// ⚠⚠ The problem: everything drawn through ImGui lands on top of the game's UI, because
/// Dalamud renders after the game has finished. A screen between the camera and your
/// hotbars covers them.
///
/// ⭐⭐ <b>There is no general rule for this, and three attempts to find one all failed the
/// same way.</b> Root-node size, window bounds, and the union of visibly-drawn children
/// each measured the box a panel *reserves* rather than what it shows — so the debuff tray,
/// which sits mid-screen permanently and draws nothing when you have no debuffs, kept
/// biting rectangles out of posters.
///
/// Pictomancy — the library behind Splatoon's "automatically clip around native UI" — settles
/// it: <b>881 lines of hand-written, per-element code</b>, with a dedicated function for the
/// party list, the chat box, the minimap, the cross hotbar, and one for every job gauge
/// individually. Nobody found a clever rule because there isn't one.
///
/// ⭐ So this is explicit too, and deliberately smaller: the handful of elements that
/// actually matter in a house, plus anything with a window frame. Being explicit is the
/// feature — nothing surprises, and an element that should not be culled simply is not
/// listed. Chris: the alliance list is not worth covering, because being in an alliance
/// raid while stood in your own house essentially never happens.
/// </summary>
internal static unsafe class UiRects
{
    /// <summary>
    /// Ceiling imposed by the shader's fixed-size constant buffer. ⚠ Raised for per-button
    /// action bar rectangles — merging adjacent buttons into runs keeps the real count far
    /// below this, but ten bars of twelve buttons is the worst case to survive.
    /// </summary>
    public const int Max = 64;

    /// <summary>
    /// HUD elements worth protecting indoors. ⚠ Deliberately short. Everything absent from
    /// this list is a considered omission, not an oversight — status trays and the alliance
    /// list are left out because covering them costs more than it saves.
    /// </summary>
    private static readonly string[] Hud =
    {
        "_ChatLog",         // chat
        "_PartyList",
        "_ActionBar", "_ActionBar01", "_ActionBar02", "_ActionBar03", "_ActionBar04",
        "_ActionBar05", "_ActionBar06", "_ActionBar07", "_ActionBar08", "_ActionBar09",
        "_ActionBarEx", "_ActionCross",
        "_ParameterWidget", // HP and MP
        "_MainCommand",     // the menu along the bottom
        "_NaviMap",         // minimap
    };

    /// <summary>
    /// Job gauges are named per job — JobHudGNB0, JobHudPLD0 and so on — so they are
    /// matched by prefix rather than listed. ⭐ Chris asked for the gauge specifically.
    /// </summary>
    private const string GaugePrefix = "JobHud";

    private static bool IsActionBar(string name)
        => name.StartsWith("_ActionBar", StringComparison.Ordinal);

    /// <summary>
    /// Rectangles for the individual buttons of one action bar, with touching buttons
    /// merged into a single run.
    ///
    /// ⭐ Merging matters twice over: a centred twelve-button row collapses to one
    /// rectangle instead of twelve, and a bar split into clusters keeps the gaps between
    /// them — which is exactly where Q keeps his job gauge.
    ///
    /// ⚠ Slots 9 to 20 of the node list are the twelve buttons. That is a hard-coded layout
    /// detail taken from Pictomancy, and it is the kind of thing a patch can move — if
    /// hotbars start being covered oddly, suspect these indices first.
    /// </summary>
    private static int CollectActionBarButtons(AtkUnitBase* unit, Span<Vector4> into)
    {
        const int firstSlot = 9, lastSlot = 20;
        const float joinGap = 2f;   // buttons this close are treated as one run

        var uld = &unit->UldManager;
        if (uld->NodeList == null) return 0;

        Span<Vector4> buttons = stackalloc Vector4[lastSlot - firstSlot + 1];
        var found = 0;

        for (var slot = firstSlot; slot <= lastSlot && slot < uld->NodeListCount; slot++)
        {
            var node = uld->NodeList[slot];
            if (node == null || (node->NodeFlags & NodeFlags.Visible) == 0) continue;

            var w = node->Width * unit->Scale;
            var h = node->Height * unit->Scale;
            if (w <= 0f || h <= 0f) continue;

            buttons[found++] = new Vector4(
                node->ScreenX, node->ScreenY, node->ScreenX + w, node->ScreenY + h);
        }

        // Merge any button that touches one already accumulated. Buttons arrive in slot
        // order, which is layout order, so a single pass catches every run.
        var count = 0;
        for (var i = 0; i < found; i++)
        {
            var b = buttons[i];
            var merged = false;

            for (var j = 0; j < count; j++)
            {
                var e = into[j];
                var overlaps = b.X <= e.Z + joinGap && b.Z >= e.X - joinGap
                            && b.Y <= e.W + joinGap && b.W >= e.Y - joinGap;
                if (!overlaps) continue;

                into[j] = new Vector4(
                    MathF.Min(e.X, b.X), MathF.Min(e.Y, b.Y),
                    MathF.Max(e.Z, b.Z), MathF.Max(e.W, b.W));
                merged = true;
                break;
            }

            if (!merged && count < into.Length) into[count++] = b;
        }

        return count;
    }

    public static int Collect(Span<Vector4> into, float viewportW, float viewportH)
    {
        var count = 0;
        try
        {
            var stage = AtkStage.Instance();
            if (stage == null || stage->RaptureAtkUnitManager == null) return 0;

            var units = &stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;

            for (var i = 0; i < units->Count && count < into.Length; i++)
            {
                var unit = units->Entries[i].Value;
                if (unit == null || !unit->IsVisible || unit->RootNode == null) continue;
                if (unit->Scale == 0f || unit->Alpha == 0) continue;

                var name = unit->NameString;
                if (string.IsNullOrEmpty(name)) continue;

                // ⭐ Anything with a window frame is something the player deliberately
                // opened — inventory, character sheet, a shop. Those always want covering,
                // and the frame is exactly the region they occupy. This one check handles
                // every window in the game without naming any of them.
                var isWindow = unit->WindowNode != null;

                var wanted = isWindow
                             || name.StartsWith(GaugePrefix, StringComparison.Ordinal)
                             || Array.IndexOf(Hud, name) >= 0;
                if (!wanted) continue;

                // ⭐⭐ Action bars get per-button treatment rather than one box round the
                // whole bar. Chris and Q arrange theirs very differently — Q groups his
                // buttons to one side and parks his job gauge in the gap he leaves — so a
                // bar-shaped rectangle would cover the gauge sitting in the hole, and a
                // good chunk of empty screen with it.
                if (IsActionBar(name))
                {
                    count += CollectActionBarButtons(unit, into[count..]);
                    continue;
                }

                float left = unit->X, top = unit->Y, w, h;

                if (isWindow)
                {
                    // The frame's own node, which is tighter than the addon's canvas.
                    var frame = &unit->WindowNode->AtkResNode;
                    w = frame->Width * unit->Scale;
                    h = frame->Height * unit->Scale;
                }
                else
                {
                    w = unit->RootNode->Width * unit->Scale;
                    h = unit->RootNode->Height * unit->Scale;
                }

                if (w <= 0f || h <= 0f) continue;

                // ⚠ Ignore anything effectively fullscreen: some always-loaded addons are
                // invisible screen-sized containers, and culling one would hide every
                // screen in the house with no clue why.
                if (w >= viewportW * 0.9f && h >= viewportH * 0.9f) continue;

                into[count++] = new Vector4(left, top, left + w, top + h);
            }
        }
        catch
        {
            // Never let interface enumeration break rendering. Worst case we draw over a
            // hotbar, which is the bug we started with rather than a new one.
        }

        return count;
    }
}
