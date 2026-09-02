using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace DesCast;

/// <summary>
/// The placement editor. ⭐ For now this is the actual product: before there is anything
/// worth showing on a screen, the useful thing is being able to stand in a room, drop a
/// panel where a TV might go, and walk around it to see whether the sightlines work.
/// It is a house-design instrument first and a settings window second.
/// </summary>
public sealed class PlacementWindow : Window
{
    private readonly Plugin plugin;
    private int selected = -1;
    private DateTimeOffset copiedAt = DateTimeOffset.MinValue;

    public PlacementWindow(Plugin plugin)
        : base("DesCast##descast-main")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 320),
            MaximumSize = new Vector2(900, 1400),
        };
    }

    public override void Draw()
    {
        var cfg = plugin.Config;

        // ── Status. Everything that can silently stop a screen appearing says so here,
        //    because a blank wall is indistinguishable from "nothing placed yet". ──
        var location = plugin.Game.GetLocation();
        if (location is null)
        {
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.35f, 1f),
                "Not inside a house. Screens only render indoors.");
        }
        else
        {
            var loc = location.Value;
            ImGui.Text(plugin.DescribeLocation(loc));
            if (!plugin.Game.CanPlaceHere())
                ImGui.TextColored(new Vector4(1f, 0.75f, 0.35f, 1f),
                    "No build permission here — you can view, but not place.");
        }

        if (plugin.Game.Error is { } gerr)
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), gerr);
        if (plugin.Renderer.Error is { } rerr)
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), rerr);

        ImGui.Separator();

        var enabled = cfg.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled)) { cfg.Enabled = enabled; cfg.Save(); }

        var reverse = cfg.ReverseDepth;
        if (ImGui.Checkbox("Reverse depth", ref reverse)) { cfg.ReverseDepth = reverse; cfg.Save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "If screens show through walls, or never appear at all, flip this.\n" +
                "It selects which way round the game measures distance, and there are\n" +
                "only two possible answers.");

        var noOcclude = cfg.DisableOcclusion;
        if (ImGui.Checkbox("Ignore walls (debug)", ref noOcclude)) { cfg.DisableOcclusion = noOcclude; cfg.Save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Draws the panel over everything, ignoring what is in front of it.\n" +
                "If a screen appears with this on and vanishes with it off, the panel\n" +
                "is in the right place and only the wall test is wrong.");

        ImGui.Separator();

        // ── Shared rooms ──────────────────────────────────────────────────────────────
        // ⭐ A list, not one. The FC hall is published by officers; a private room belongs
        // to whoever lives in it. Subscribing to several keeps both without either party
        // needing edit rights over the other's file.
        ImGui.TextDisabled("Shared rooms");

        // ── From the Free Company board ───────────────────────────────────────────────
        // ⚠ Three states, and they are three different problems for the user: never read
        // it, read it and found nothing, read it and found links. Collapsing them into
        // "no screens" would leave someone with no idea which one they are in.
        if (cfg.CompanyBoardSeenAt is null)
        {
            ImGui.TextColored(new Vector4(0.65f, 0.8f, 1f, 1f),
                "Open your Free Company window once to pick up your company's screens.");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "The game only hands the board text to plugins after that window has " +
                    "been opened. It is remembered afterwards, so this is a one-time thing.");
        }
        else if (cfg.CompanyBoardUrls.Count == 0)
        {
            ImGui.TextDisabled("Company board: read, but no screen links on it.");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("An officer can add a line like:  Screens: 0GzA4vpc");
        }
        else
        {
            foreach (var boardUrl in cfg.CompanyBoardUrls)
                ImGui.TextDisabled($"   {boardUrl}   (from your company board)");
        }


        var removeUrl = -1;
        var i2 = 0;
        foreach (var (surl, count, serr, loadedAt, fetching) in plugin.Manifest.Status())
        {
            ImGui.PushID(1000 + i2);

            var editable = surl;
            ImGui.SetNextItemWidth(300f);
            if (ImGui.InputText("##murl", ref editable, 512))
            {
                cfg.ManifestUrls[i2] = editable.Trim();
                cfg.Save();
            }

            ImGui.SameLine();
            if (ImGui.Button("x")) removeUrl = i2;

            if (fetching)
                ImGui.TextDisabled("   checking...");
            else if (loadedAt is { } at)
                ImGui.TextDisabled($"   {count} screen(s), updated {(int)(DateTimeOffset.UtcNow - at).TotalMinutes} min ago");

            // ⚠ Fail visibly. An unreachable file means shared screens go blank, and a
            // blank wall is indistinguishable from an empty room.
            if (serr is not null)
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.45f, 1f), $"   {serr}");

            ImGui.PopID();
            i2++;
        }

        if (removeUrl >= 0 && removeUrl < cfg.ManifestUrls.Count)
        {
            cfg.ManifestUrls.RemoveAt(removeUrl);
            cfg.Save();
        }

        if (ImGui.Button("Subscribe to a room"))
        {
            cfg.ManifestUrls.Add(string.Empty);
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "A link to a file listing screens. Everyone pointed at the same link sees " +
                "the same room, and it stays there whether or not whoever placed it is " +
                "online.\n\nSubscribe to several: the company hall from your officers, and " +
                "each private room from whoever lives in it.");

        if (cfg.ManifestUrls.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("Refresh all")) plugin.Manifest.RefreshNow();
        }

        // ── Placing ───────────────────────────────────────────────────────────────────
        var canPlace = location is not null && plugin.Game.CanPlaceHere();
        if (!canPlace) ImGui.BeginDisabled();
        if (ImGui.Button("Place a screen in front of me"))
            PlaceInFrontOfPlayer(location);
        if (!canPlace) ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Drops a panel about two metres ahead at eye height, facing you.");

        // ⭐ Authoring by placement, not by typing coordinates. Chris' idea: arrange the
        // room in game, copy, paste into whatever hosts the file. Nobody should ever have
        // to work out a world coordinate by hand.
        var mine = cfg.Screens.FindAll(s => location is not null && location.Value.Matches(s));
        if (mine.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button($"Copy {mine.Count} screen(s) as shared file"))
            {
                var label = location is null ? string.Empty : plugin.DescribeLocation(location.Value);
                ImGui.SetClipboardText(plugin.ExportManifest(mine, label));
                copiedAt = DateTimeOffset.UtcNow;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Copies your screens in this house as the shared-room file.\n\n" +
                    "Paste it into a gist or pastebin, then put that link in the " +
                    "\"Shared screens\" box above — yours and everyone else's.");

            if (DateTimeOffset.UtcNow - copiedAt < TimeSpan.FromSeconds(3))
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.6f, 1f), "copied");
            }
        }

        ImGui.Separator();

        // ── The screens in this house ─────────────────────────────────────────────────
        var any = false;
        for (var i = 0; i < cfg.Screens.Count; i++)
        {
            var s = cfg.Screens[i];
            if (location is not null && !location.Value.Matches(s)) continue;
            any = true;

            ImGui.PushID(i);
            if (ImGui.Selectable($"{s.Name}##sel", selected == i)) selected = i;
            ImGui.PopID();
        }

        if (!any) ImGui.TextDisabled("No screens of your own in this room.");

        // Shared screens, listed but not editable — they belong to the file, and letting
        // someone drag one here would produce a change that silently vanishes on the next
        // refresh. Edit the file instead.
        var shared = 0;
        foreach (var s in plugin.Manifest.Screens)
        {
            if (location is null || !location.Value.Matches(s)) continue;
            if (shared++ == 0) ImGui.TextDisabled("Shared:");
            ImGui.TextDisabled($"   {s.Name}  ({s.Sources.Count} image(s))");
        }
        if (shared > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip("From the shared file. Edit that file to change these.");


        if (selected >= 0 && selected < cfg.Screens.Count)
        {
            ImGui.Separator();
            DrawEditor(cfg.Screens[selected]);
        }
    }

    private void PlaceInFrontOfPlayer(GameView.HouseLocation? location)
    {
        var player = Plugin.Objects.LocalPlayer;
        if (player is null || location is null) return;

        // FFXIV's character rotation is radians about the vertical axis, with forward
        // running along (sin, 0, cos).
        var yaw = player.Rotation;
        var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));

        var screen = new ScreenPlacement
        {
            Name = $"Screen {plugin.Config.Screens.Count + 1}",
            Position = player.Position + forward * 2.0f + new Vector3(0f, 1.3f, 0f),
            HouseId = location.Value.Id,
        };
        screen.FaceToward(player.Position);

        plugin.Config.Screens.Add(screen);
        selected = plugin.Config.Screens.Count - 1;
        plugin.Config.Save();
    }

    private void DrawEditor(ScreenPlacement s)
    {
        var cfg = plugin.Config;
        var dirty = false;

        var name = s.Name;
        if (ImGui.InputText("Name", ref name, 64)) { s.Name = name; dirty = true; }

        var on = s.Enabled;
        if (ImGui.Checkbox("Visible", ref on)) { s.Enabled = on; dirty = true; }

        ImGui.Spacing();
        ImGui.TextDisabled("Position (metres)");

        var pos = s.Position;
        if (ImGui.DragFloat3("##pos", ref pos, 0.02f)) { s.Position = pos; dirty = true; }

        // Nudge buttons, because dragging a float in a window while judging a sightline
        // in the world is genuinely awkward and this is meant to be used while decorating.
        if (ImGui.Button("← left")) { s.Position += LeftOf(s) * 0.1f; dirty = true; }
        ImGui.SameLine();
        if (ImGui.Button("right →")) { s.Position -= LeftOf(s) * 0.1f; dirty = true; }
        ImGui.SameLine();
        if (ImGui.Button("up")) { s.Position += new Vector3(0f, 0.1f, 0f); dirty = true; }
        ImGui.SameLine();
        if (ImGui.Button("down")) { s.Position -= new Vector3(0f, 0.1f, 0f); dirty = true; }

        ImGui.Spacing();
        ImGui.TextDisabled("Facing");

        var yaw = s.RotationDegrees.Y;
        if (ImGui.DragFloat("Yaw (turn)", ref yaw, 0.5f, -180f, 180f))
        {
            s.RotationDegrees = s.RotationDegrees with { Y = yaw };
            dirty = true;
        }

        var pitch = s.RotationDegrees.X;
        if (ImGui.DragFloat("Pitch (tilt)", ref pitch, 0.5f, -90f, 90f))
        {
            s.RotationDegrees = s.RotationDegrees with { X = pitch };
            dirty = true;
        }

        var roll = s.RotationDegrees.Z;
        if (ImGui.DragFloat("Roll (lean)", ref roll, 0.5f, -180f, 180f))
        {
            s.RotationDegrees = s.RotationDegrees with { Z = roll };
            dirty = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Yaw turns the panel to face you. Pitch tilts it forward or back, "
                + "for a screen angled down at a seated audience. Roll leans it sideways.");

        if (ImGui.Button("Face me"))
        {
            var player = Plugin.Objects.LocalPlayer;
            if (player is not null) { s.FaceToward(player.Position); dirty = true; }
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Size (metres)");

        var w = s.Width;
        if (ImGui.DragFloat("Width", ref w, 0.02f, 0.2f, 30f))
        {
            var ratio = s.Height / MathF.Max(s.Width, 0.0001f);
            s.Width = w;
            s.Height = w * ratio; // keep the shape while dragging
            dirty = true;
        }

        var fit = s.FitToImage;
        if (ImGui.Checkbox("Fit height to image", ref fit)) { s.FitToImage = fit; dirty = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "On: width is what you set, height follows the picture's own shape,\n" +
                "so nothing is ever stretched.\n\n" +
                "Off: the panel keeps the size you gave it whatever is shown on it —\n" +
                "for a fixture whose dimensions are part of the furniture, like an\n" +
                "upright notice board that should not change shape between posters.");

        // While fitting, height and the ratio presets are not what decides anything —
        // showing them live would invite the user to set a value that does nothing.
        if (s.FitToImage)
        {
            ImGui.TextDisabled($"Height {plugin.HeightOf(s):0.00} m — from the image");
        }
        else
        {
            var h = s.Height;
            if (ImGui.DragFloat("Height", ref h, 0.02f, 0.2f, 30f)) { s.Height = h; dirty = true; }

            if (ImGui.Button("16:9")) { s.Height = s.Width * 9f / 16f; dirty = true; }
            ImGui.SameLine();
            if (ImGui.Button("4:3")) { s.Height = s.Width * 3f / 4f; dirty = true; }
            ImGui.SameLine();
            if (ImGui.Button("1:1")) { s.Height = s.Width; dirty = true; }
        }

        var opacity = s.Opacity;
        if (ImGui.SliderFloat("Opacity", ref opacity, 0f, 1f)) { s.Opacity = opacity; dirty = true; }

        ImGui.Spacing();
        ImGui.TextDisabled("Showing");

        // ── Sources. One entry is a sign; several is a slideshow. ────────────────────
        var removeAt = -1;
        var now = DateTimeOffset.UtcNow;
        var showing = s.SlideIndexAt(now);

        for (var i = 0; i < s.Sources.Count; i++)
        {
            ImGui.PushID(i);

            // Mark the slide currently on the wall, so a list of near-identical URLs is
            // not a guessing game about which one you are looking at.
            if (s.Sources.Count > 1)
            {
                // ⚠ ASCII only. Dalamud's UI font does not carry the arrow and geometric
                // shape blocks, so a ▶ renders as whatever the fallback happens to be —
                // it came out as "=" in testing, which reads as a bug rather than a cursor.
                ImGui.TextColored(
                    i == showing ? new Vector4(0.5f, 0.9f, 0.6f, 1f) : new Vector4(0.4f, 0.4f, 0.4f, 1f),
                    i == showing ? ">" : " ");
                ImGui.SameLine();
            }

            var entry = s.Sources[i];
            ImGui.SetNextItemWidth(320f);
            if (ImGui.InputText("##src", ref entry, 512))
            {
                s.Sources[i] = Plugin.NormalisePath(entry);
                dirty = true;
            }

            ImGui.SameLine();
            if (ImGui.Button("×")) removeAt = i;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove this one.");

            if (Plugin.IsWebUrl(s.Sources[i]))
            {
                ImGui.SameLine();
                if (ImGui.Button("Reload")) plugin.ForgetContent(s.Sources[i]);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Fetch it again. A downloaded image is cached, so replacing it " +
                        "at the source needs this before the change shows up here.");
            }

            // ⚠ A failed load falls back to the test card, which looks exactly like a
            // screen that is simply working. Say so, or a typo is indistinguishable
            // from success.
            if (plugin.ContentErrors.TryGetValue(s.Sources[i], out var err))
                ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f), $"   {err}");

            ImGui.PopID();
        }

        if (removeAt >= 0) { s.Sources.RemoveAt(removeAt); dirty = true; }

        if (ImGui.Button("Add image")) { s.Sources.Add(string.Empty); dirty = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "A file on this PC, or a web address starting with https://\n\n" +
                "File: shift-right-click in Explorer, \"Copy as path\" — the quotes it " +
                "adds are stripped for you.\n\n" +
                "Web: must link to the picture itself, not the page it sits on. An " +
                "imgur.com/... link is rewritten to the direct image automatically.\n\n" +
                "Add more than one and the screen cycles through them.");

        if (s.Sources.Count == 0)
            ImGui.TextDisabled("Nothing set — showing the test card.");

        if (s.Sources.Count > 1)
        {
            var dwell = s.DwellSeconds;
            if (ImGui.DragFloat("Seconds per slide", ref dwell, 0.5f, 1f, 600f))
            {
                s.DwellSeconds = dwell;
                dirty = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Which slide is showing comes from the clock, not from a timer this " +
                    "client started — so everyone in the room sees the same one without " +
                    "anything being sent between you.");
        }

        ImGui.Spacing();
        if (ImGui.Button("Delete this screen"))
        {
            cfg.Screens.Remove(s);
            selected = -1;
            dirty = true;
        }

        if (dirty) cfg.Save();
    }

    /// <summary>Panel-left in world terms, for the nudge buttons.</summary>
    private static Vector3 LeftOf(ScreenPlacement s)
    {
        var ax = s.AxisX;
        return ax.LengthSquared() < 1e-6f ? Vector3.UnitX : -Vector3.Normalize(ax);
    }
}
