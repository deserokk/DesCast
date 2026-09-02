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
        // ⚠ "ChatLog", with no underscore — unlike almost every other HUD element. Guessed
        // it as "_ChatLog" and the match silently never fired, so chat stayed covered while
        // everything else worked. Every other name here is verified against Pictomancy's.
        "ChatLog",
        "ChatLogPanel_0", "ChatLogPanel_1", "ChatLogPanel_2", "ChatLogPanel_3",
        "_PartyList",
        "_ActionBar", "_ActionBar01", "_ActionBar02", "_ActionBar03", "_ActionBar04",
        "_ActionBar05", "_ActionBar06", "_ActionBar07", "_ActionBar08", "_ActionBar09",
        "_ActionBarEx", "_ActionCross",
        // ⭐ Target info. Chris: people target and examine each other constantly in a
        // house, so this being readable matters more here than most of the combat HUD.
        // ⚠ The game splits it into three addons when "display target info independently"
        // is on and uses one when it is not, so both arrangements are listed.
        "_TargetInfo",
        "_TargetInfoMainTarget", "_TargetInfoCastBar", "_TargetInfoBuffDebuff",
        "_FocusTargetInfo",
        "_ParameterWidget", // HP and MP
        "_MainCommand",     // the menu along the bottom
        "_NaviMap",         // minimap
    };

    /// <summary>
    /// Job gauges are named per job — JobHudGNB0, JobHudPLD0 and so on — so they are
    /// matched by prefix rather than listed. ⭐ Chris asked for the gauge specifically.
    /// </summary>
    private const string GaugePrefix = "JobHud";

    /// <summary>
    /// Union the rectangles of everything a named addon actually paints, merging pieces
    /// that touch.
    ///
    /// ⭐⭐ This is the measurement from the earlier automatic attempt, which was right as a
    /// measurement and wrong only in what it was applied to. Used on every addon it culled
    /// panels that draw nothing; used on a named list it does exactly what is wanted —
    /// tight bounds round real content, and nothing at all for an element we never listed.
    ///
    /// ⚠ Only nodes that paint count. Container and collision nodes are flagged visible and
    /// sized to the whole addon whether or not anything inside them draws, which is how the
    /// target info bar came to cut a band across the screen far wider than its label.
    /// </summary>
    private static int CollectPaintedNodes(AtkUnitBase* unit, Span<Vector4> into)
    {
        const float joinGap = 3f;

        if (unit->RootNode == null || into.Length == 0) return 0;

        var count = 0;
        var budget = 160;

        var stack = stackalloc nint[32];
        var depth = 0;
        stack[depth++] = (nint)unit->RootNode->ChildNode;

        while (depth > 0 && budget > 0)
        {
            var node = (AtkResNode*)stack[--depth];

            for (; node != null && budget-- > 0; node = node->PrevSiblingNode)
            {
                if ((node->NodeFlags & NodeFlags.Visible) == 0) continue;

                if (node->ChildNode != null && depth < 32)
                    stack[depth++] = (nint)node->ChildNode;

                var paints = node->Type is NodeType.Image or NodeType.Text
                                          or NodeType.NineGrid or NodeType.Counter;
                if (!paints || node->Alpha_2 == 0) continue;

                var w = node->Width * node->ScaleX * unit->Scale;
                var h = node->Height * node->ScaleY * unit->Scale;
                if (w <= 1f || h <= 1f) continue;

                var r = new Vector4(node->ScreenX, node->ScreenY,
                                    node->ScreenX + w, node->ScreenY + h);

                var merged = false;
                for (var j = 0; j < count; j++)
                {
                    var e = into[j];
                    if (r.X > e.Z + joinGap || r.Z < e.X - joinGap
                        || r.Y > e.W + joinGap || r.W < e.Y - joinGap) continue;

                    into[j] = new Vector4(
                        MathF.Min(e.X, r.X), MathF.Min(e.Y, r.Y),
                        MathF.Max(e.Z, r.Z), MathF.Max(e.W, r.W));
                    merged = true;
                    break;
                }

                if (!merged && count < into.Length) into[count++] = r;
            }
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
                if (!isWindow)
                {
                    count += CollectPaintedNodes(unit, into[count..]);
                    continue;
                }

                // A window keeps its frame rectangle. The frame genuinely is the region it
                // occupies, and unlike a HUD element there is no reserved empty space in it.
                float left = unit->X, top = unit->Y;
                var frame = &unit->WindowNode->AtkResNode;
                var w = frame->Width * unit->Scale;
                var h = frame->Height * unit->Scale;

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
