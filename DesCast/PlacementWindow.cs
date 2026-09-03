using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;

namespace DesCast;

/// <summary>
/// The everyday window: which rooms you follow, and the handful of settings anybody
/// actually changes.
///
/// ⭐⭐ Modelled deliberately on a Mare client (Snowcloak), because that is a shape every
/// FFXIV player already holds in their head. Chris' framing is "Mare for Media", and the
/// point of borrowing wholesale is that it costs the least technical person **zero new
/// concepts** — a featured code at the top, a list of things you follow, a pause and a menu
/// on each row. She is not learning our interface, she is applying one she has.
///
/// ⚠ Everything about placing or editing a screen lives in <see cref="BuildWindow"/>.
/// Nothing here should grow geometry, or the split stops meaning anything.
/// </summary>
public sealed class PlacementWindow : Window
{
    private readonly Plugin plugin;
    private DateTimeOffset copiedAt = DateTimeOffset.MinValue;
    private string copiedWhat = string.Empty;

    private bool addingRoom;
    private int newRoomKind;
    private string newRoomCode = string.Empty;

    /// <summary>What is typed into the paste box at the top.</summary>
    private string addBuffer = string.Empty;

    /// <summary>Row whose rename box is open, or empty.</summary>
    private string renaming = string.Empty;
    private string renameBuffer = string.Empty;

    public PlacementWindow(Plugin plugin)
        : base("DesCast##descast-main")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 300),
            MaximumSize = new Vector2(800, 1200),
        };
    }

    public override void Draw()
    {
        var cfg = plugin.Config;

        DrawFeaturedCode();
        DrawMyRooms();
        ImGui.Spacing();
        DrawRoomList();

        ImGui.Spacing();
        ImGui.Separator();
        DrawFooter();
    }

    // ── The code you hand people ──────────────────────────────────────────────────────

    /// <summary>
    /// ⭐⭐⭐ The Free Company's code, featured, and it costs nobody any setup: the game
    /// announces the company board in chat at login and we read it from there. Install, log
    /// in, and here is the thing you give a visiting friend.
    ///
    /// ⚠ Three states, and they are three different problems for the reader: never read the
    /// board, read it and found nothing, read it and found a code. Collapsing them into "no
    /// screens" leaves somebody with no idea which one they are in.
    /// </summary>
    private void DrawFeaturedCode()
    {
        var cfg = plugin.Config;

        if (cfg.CompanyBoardUrls.Count == 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.65f, 0.8f, 1f, 1f));
            ImGui.TextWrapped(cfg.CompanyBoardSeenAt is null
                ? "Your company's rooms arrive next time you log in."
                : "Your company has not put a room code on its board yet.");
            ImGui.PopStyleColor();

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(cfg.CompanyBoardSeenAt is null
                    ? "The game announces the company board in chat when you log in, and it\n" +
                      "is read from there. Opening your Free Company window once also does it."
                    : "An officer can add a line to the company board like:\n\n    Screens: 0GzA4vpc");
            return;
        }

        foreach (var code in cfg.CompanyBoardUrls)
        {
            var label = cfg.NameFor(code);
            if (label.Length == 0) label = "Your company";

            Centred(label, 1.55f, TitleColour);
            if (Centred(code, 1.25f, CodeColour, asButton: true))
            {
                ImGui.SetClipboardText(code);
                Copied("code");
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Click to copy.\n\nGive this to anyone you want to see your company's\n" +
                    "screens — they paste it into their own DesCast and that is all.");
        }
    }

    /// <summary>
    /// The user's own rooms, sitting with the company's rather than among the rooms they
    /// follow — because these are the codes they *hand out*, and that is a different job from
    /// the codes they were handed.
    /// </summary>
    private void DrawMyRooms()
    {
        var cfg = plugin.Config;

        var removeAt = -1;
        for (var i = 0; i < cfg.MyRooms.Count; i++)
        {
            var room = cfg.MyRooms[i];
            ImGui.PushID(3000 + i);

            var name = cfg.NameFor(room.Code);
            if (name.Length == 0) name = room.Label;

            // ⭐ A step smaller than the company's, so there is one obvious primary rather
            // than a stack of equally loud headings.
            Centred(name, 1.25f, TitleColour);
            if (Centred(room.Code, 1.1f, CodeColour, asButton: true))
            {
                ImGui.SetClipboardText(room.Code);
                Copied("code");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Click to copy.\n\nRight-click for more.");

            // ⚠⚠ A menu, not an immediate delete. Right-click sat on the same small
            // target as left-click, and what it would have thrown away is quite possibly
            // the only record anywhere of that room’s code — we cannot regenerate it and
            // neither can the user. One extra click is the right price for that.
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) ImGui.OpenPopup("my-room-menu");

            if (ImGui.BeginPopup("my-room-menu"))
            {
                if (ImGui.MenuItem("Copy code")) { ImGui.SetClipboardText(room.Code); Copied("code"); }
                if (ImGui.MenuItem("Give it a name"))
                {
                    renaming = room.Code;
                    renameBuffer = cfg.NameFor(room.Code);
                }
                ImGui.Separator();
                if (ImGui.MenuItem("Forget this room")) removeAt = i;
                ImGui.EndPopup();
            }

            if (renaming == room.Code)
            {
                ImGui.SetNextItemWidth(180f);
                ImGui.SetKeyboardFocusHere();
                if (ImGui.InputText("##myrename", ref renameBuffer, 64,
                                    ImGuiInputTextFlags.EnterReturnsTrue))
                {
                    var typed = renameBuffer.Trim();
                    if (typed.Length == 0) cfg.RoomNames.Remove(room.Code);
                    else cfg.RoomNames[room.Code] = typed;
                    cfg.Save();
                    renaming = string.Empty;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel")) renaming = string.Empty;
            }

            ImGui.PopID();
        }

        if (removeAt >= 0)
        {
            cfg.MyRooms.RemoveAt(removeAt);
            cfg.Save();
        }

        // ⭐ Adding one is deliberately tucked away: it is a once-per-room act by somebody
        // who has just published, not something a participant ever needs to find.
        if (!addingRoom)
        {
            if (ImGui.SmallButton("+ one of my rooms")) addingRoom = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "After you publish a room, paste its code here so you have\n" +
                    "something to hand people. Nothing is checked — it only decides\n" +
                    "whether the code sits up here or down in the list.");
            return;
        }

        ImGui.SetNextItemWidth(150f);
        ImGui.Combo("##kind", ref newRoomKind, OwnRoom.Kinds, OwnRoom.Kinds.Length);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(130f);
        var submitted = ImGui.InputTextWithHint(
            "##mycode", "room code", ref newRoomCode, 512, ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Check)) submitted = true;

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Times))
        {
            addingRoom = false;
            newRoomCode = string.Empty;
        }

        if (!submitted) return;

        var code = Plugin.CollapseToCode(newRoomCode);
        if (code.Length > 0)
        {
            cfg.MyRooms.Add(new OwnRoom { Kind = newRoomKind, Code = code });
            cfg.Save();
        }

        addingRoom = false;
        newRoomCode = string.Empty;
    }

    // ── Rooms you follow ──────────────────────────────────────────────────────────────

    /// <summary>
    /// ⭐ Grouped "Here" and "Elsewhere" rather than online/offline. A Mare client can group
    /// by presence because it has a server and identities; we have neither and need neither,
    /// since a room works whether or not anybody is home. What matters to somebody holding
    /// this window open is which of these they are standing in.
    /// </summary>
    private void DrawRoomList()
    {
        var cfg = plugin.Config;
        var here = plugin.Game.GetLocation();

        // ⭐⭐ A box you paste into, not a button that makes an empty row to fill in.
        // Snowcloak's shape, and the difference matters: somebody has just been handed a
        // code in a tell and wants to use it. "Add" then "type here" is the same work in a
        // worse order, and it leaves a blank row behind if they change their mind.
        var addWidth = ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight()
                       - ImGui.GetStyle().ItemSpacing.X;

        ImGui.SetNextItemWidth(MathF.Max(120f, addWidth));
        var submitted = ImGui.InputTextWithHint(
            "##add", "paste a room code", ref addBuffer, 512,
            ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Plus)) submitted = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "An eight-character code for a room — like a Mare code.\n\n" +
                "Everyone using the same one sees the same room, and it stays there\n" +
                "whether or not whoever placed it is online.\n\n" +
                "Paste the whole link if that is what you were given; it will be\n" +
                "shortened to the code for you.");

        if (submitted)
        {
            var code = Plugin.CollapseToCode(addBuffer);

            // ⚠ Silently ignore a duplicate rather than adding a second row for the same
            // room. Pasting the code you already have is a normal mistake, not an error
            // worth a message.
            if (code.Length > 0 && !cfg.ManifestUrls.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                cfg.ManifestUrls.Add(code);
                cfg.Save();
            }

            addBuffer = string.Empty;
        }

        ImGui.Spacing();

        var status = plugin.Manifest.Status();
        if (status.Count == 0 && cfg.CompanyBoardUrls.Count == 0)
        {
            ImGui.TextDisabled("No rooms yet.");
            return;
        }

        // ⚠ A room counts as "here" when it actually puts a screen in this house, not when
        // its code happens to be selected — otherwise every room claims to be here.
        var anyHere = here is not null && plugin.AnyScreensFrom(here.Value);

        if (anyHere)
        {
            ImGui.TextDisabled("Here");
            ImGui.Indent(8f);
            ImGui.TextUnformatted(plugin.DescribeLocation(here!.Value));
            ImGui.Unindent(8f);
            ImGui.Spacing();
        }

        ImGui.TextDisabled(anyHere ? "Rooms you follow" : "Rooms");

        var removeAt = -1;
        for (var i = 0; i < status.Count; i++)
        {
            var row = status[i];
            ImGui.PushID(1000 + i);

            if (DrawRoomRow(i, row.Url, row.Count, row.Error, row.LoadedAt, row.Fetching))
                removeAt = i;

            ImGui.PopID();
        }

        if (removeAt >= 0 && removeAt < cfg.ManifestUrls.Count)
        {
            cfg.ManifestUrls.RemoveAt(removeAt);
            cfg.Save();
        }

        // Rooms reached through a company file's include list. Listed separately so it is
        // obvious where a screen came from — and so a member's broken file reads as theirs
        // rather than as the company's.
        var inc = 0;
        foreach (var (iurl, icount, ierr) in plugin.Manifest.IncludedStatus())
        {
            if (inc++ == 0)
            {
                ImGui.Spacing();
                ImGui.TextDisabled("Listed by those rooms");
            }

            var nick = plugin.Config.NameFor(iurl);
            ImGui.TextDisabled($"   {(nick.Length > 0 ? nick : iurl)}  —  {Ui.Count(icount, "screen")}");
            if (ierr is not null) Ui.ErrorText(ierr, 20f);
        }
    }

    /// <summary>Returns true if this row asked to be removed.</summary>
    private bool DrawRoomRow(
        int index, string code, int count, string? error, DateTimeOffset? loadedAt, bool fetching)
    {
        var cfg = plugin.Config;
        var remove = false;
        var paused = cfg.IsPaused(code);

        if (renaming == code)
        {
            ImGui.SetNextItemWidth(200f);
            ImGui.SetKeyboardFocusHere();
            if (ImGui.InputText("##rename", ref renameBuffer, 64, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                var name = renameBuffer.Trim();
                if (name.Length == 0) cfg.RoomNames.Remove(code);
                else cfg.RoomNames[code] = name;
                cfg.Save();
                renaming = string.Empty;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel")) renaming = string.Empty;
            return false;
        }

        var label = cfg.NameFor(code);
        if (label.Length == 0) label = code;

        // ⭐ Left-click copies, right-click renames — Snowcloak's gestures exactly. ⚠ Rows
        // must stay inert otherwise: give a row another primary action and click-to-copy
        // immediately collides with it.
        // ⭐ Icon buttons, as Snowcloak uses — Chris' suggestion, and better than the text
        // buttons for three reasons: they match the client we are borrowing the whole shape
        // from, they leave far more width for a room's name, and they are a fixed size, so
        // the row cannot be pushed apart by a longer word.
        //
        // ⚠⚠ The width is still *measured* rather than assumed. A flat reservation was the
        // original bug — 60px for two buttons that needed ninety — and because the label
        // claimed whatever was left, widening the window only widened the label and the
        // buttons stayed clipped off the edge. Anything right-aligned has to be measured or
        // resizing cannot rescue it. Found by Chris, 2026-09-02.
        //
        // ⚠ Icon-only buttons carry no words, so both of them keep a tooltip. That is not
        // decoration: an icon nobody recognises is a button nobody presses.
        var style = ImGui.GetStyle();
        var iconWidth = ImGui.GetFrameHeight();
        var buttonsWidth = iconWidth * 2f + style.ItemSpacing.X;

        var labelWidth = MathF.Max(80f,
            ImGui.GetContentRegionAvail().X - buttonsWidth - style.ItemSpacing.X);

        if (paused) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.55f, 0.6f, 1f));
        ImGui.Selectable($"{(paused ? "|| " : "")}{label}##row", false, ImGuiSelectableFlags.None,
            new Vector2(labelWidth, 0f));
        if (paused) ImGui.PopStyleColor();

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            ImGui.SetClipboardText(code);
            Copied("code");
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            renaming = code;
            renameBuffer = cfg.NameFor(code);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                $"{code}\n\nClick to copy the code.\nRight-click to give it a name.");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(paused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause))
        {
            cfg.RoomPaused[code] = !paused;
            cfg.Save();
            plugin.Manifest.RefreshFlattened();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(paused
                ? "Start showing this room's screens again."
                : "Stop showing this room's screens without forgetting the code.");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Bars)) ImGui.OpenPopup("row-menu");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Rename, copy, refresh, remove.");

        if (ImGui.BeginPopup("row-menu"))
        {
            if (ImGui.MenuItem("Copy code")) { ImGui.SetClipboardText(code); Copied("code"); }
            if (ImGui.MenuItem("Give it a name")) { renaming = code; renameBuffer = cfg.NameFor(code); }
            if (ImGui.MenuItem("Check for new pictures")) plugin.Manifest.RefreshNow();
            ImGui.Separator();
            if (ImGui.MenuItem("Remove this room")) remove = true;
            ImGui.EndPopup();
        }

        // Status under the row, quiet unless it is bad news.
        ImGui.Indent(12f);
        if (error is not null)
            Ui.ErrorText(error);
        else if (fetching)
            ImGui.TextDisabled("checking...");
        else if (paused)
            ImGui.TextDisabled("paused");
        else if (loadedAt is { } at)
            ImGui.TextDisabled(Ui.Count(count, "screen"));
        ImGui.Unindent(12f);

        return remove;
    }

    // ── Footer ────────────────────────────────────────────────────────────────────────

    private void DrawFooter()
    {
        ImGui.Spacing();
        ImGui.Separator();

        // ⚠ The way into the arranging panel. It also opens itself in the game's own layout
        // mode, but a button that always works matters more than a clever trigger: if the
        // detection is ever wrong, this is still here.
        var canPlace = plugin.Game.CanPlaceHere();
        if (!canPlace) ImGui.BeginDisabled();
        if (ImGui.Button("Arrange screens in this room")) plugin.BuildWindowOpen = true;
        if (!canPlace) ImGui.EndDisabled();

        if (!canPlace)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(plugin.Game.GetLocation() is null
                ? "(only inside a house)"
                : "(you cannot build here)");
        }

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog)) plugin.SettingsWindowOpen = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Settings");

        if (DateTimeOffset.UtcNow - copiedAt < TimeSpan.FromSeconds(2))
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.6f, 1f), $"{copiedWhat} copied");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────

    private void Copied(string what)
    {
        copiedAt = DateTimeOffset.UtcNow;
        copiedWhat = char.ToUpperInvariant(what[0]) + what[1..];
    }

    /// <summary>
    /// Centred text at a chosen size.
    ///
    /// ⭐⭐ Size is the whole point here. Chris, 2026-09-02, comparing against Snowcloak:
    /// *"our top code is normal where they make it the largest part of the window to grab
    /// attention."* He is right, and it is not decoration — **the code is what this window
    /// is for.** A window whose most important element is the same size as its labels makes
    /// the reader hunt for the thing they came to find.
    ///
    /// ⚠ Measure *after* setting the scale. CalcTextSize honours the current font scale, so
    /// measuring first centres the text for a size it is not going to be drawn at.
    /// </summary>
    private static bool Centred(string text, float scale, Vector4 colour, bool asButton = false)
    {
        ImGui.SetWindowFontScale(scale);

        var width = ImGui.CalcTextSize(text).X;
        var offset = (ImGui.GetContentRegionAvail().X - width) * 0.5f;
        if (offset > 0f) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);

        var clicked = false;

        if (asButton)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.08f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 1f, 1f, 0.14f));
            ImGui.PushStyleColor(ImGuiCol.Text, colour);
            clicked = ImGui.Button(text);
            ImGui.PopStyleColor(4);
        }
        else
        {
            ImGui.TextColored(colour, text);
        }

        // ⚠⚠ Always restore. The scale is window-wide, so leaving it set draws every
        // remaining control at the wrong size — and the failure is silent and total.
        ImGui.SetWindowFontScale(1f);
        return clicked;
    }

    private static readonly Vector4 TitleColour = new(0.62f, 0.82f, 1f, 1f);
    private static readonly Vector4 CodeColour = new(0.45f, 0.68f, 0.95f, 1f);
}
