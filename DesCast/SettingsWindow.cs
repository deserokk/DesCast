using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace DesCast;

/// <summary>
/// Everything that is not sharing a room.
///
/// ⭐⭐ Split out on 2026-09-02, before it was needed rather than after. There are only a
/// handful of settings today — but audio and video each bring their own, and a main window
/// that has quietly become a settings page is very hard to reverse once people have learned
/// where things are. Chris: *"we don't have a ton of settings now, but when we add
/// audio/video stuff? We'll need a place for settings so we don't flood the main window."*
///
/// ⭐ A left-hand list rather than tabs, copied from Snowcloak. Tabs run out of width and
/// start hiding themselves behind arrows exactly when there are enough of them to need
/// finding; a vertical list grows downward, keeps every section legible, and can carry a
/// section whose name is more than one word.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly Plugin plugin;
    private Section section = Section.General;

    private enum Section
    {
        General,
        Interface,
        Company,
        Storage,
        Trouble,
    }

    private static readonly (Section Id, string Label)[] Sections =
    {
        (Section.General,   "General"),
        (Section.Interface, "Interface"),
        (Section.Company,   "Company"),
        (Section.Storage,   "Storage"),
        (Section.Trouble,   "Something looks wrong"),
    };

    public SettingsWindow(Plugin plugin)
        : base("DesCast settings##descast-settings")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 360),
            MaximumSize = new Vector2(1100, 1200),
        };
    }

    public override void Draw()
    {
        // ⚠ Fixed-width nav, content takes the rest. The nav must not resize with the
        // window or the section names reflow, which makes a stable list feel unstable.
        ImGui.BeginChild("nav", new Vector2(170f, 0f), true);

        foreach (var (id, label) in Sections)
        {
            if (ImGui.Selectable(label, section == id)) section = id;
        }

        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("content", new Vector2(0f, 0f), false);

        switch (section)
        {
            case Section.General: DrawGeneral(); break;
            case Section.Interface: DrawInterface(); break;
            case Section.Company: DrawCompany(); break;
            case Section.Storage: DrawStorage(); break;
            case Section.Trouble: DrawTrouble(); break;
        }

        ImGui.EndChild();
    }

    // ── General ───────────────────────────────────────────────────────────────────────

    private void DrawGeneral()
    {
        var cfg = plugin.Config;

        Heading("General");

        var enabled = cfg.Enabled;
        if (ImGui.Checkbox("Screens are on", ref enabled)) { cfg.Enabled = enabled; cfg.Save(); }

        ImGui.Spacing();

        // ⭐ Priced in what it costs rather than in pixels. "1536" means nothing to anybody;
        // "about 5 MB a picture" is the decision actually being made, and it is the only
        // setting here that moves the figures under Storage.
        var edges = new[] { 0, 4096, 2048, 1536, 1024 };
        var edgeLabels = new[]
        {
            "Original size (whatever the file is)",
            "Very high — about 35 MB a picture",
            "High — about 9 MB a picture",
            "Medium — about 5 MB a picture",
            "Low — about 2 MB a picture",
        };

        var edgeIndex = Array.IndexOf(edges, cfg.MaxImageEdge);
        if (edgeIndex < 0) edgeIndex = 3;

        ImGui.SetNextItemWidth(300f);
        if (ImGui.Combo("Picture detail", ref edgeIndex, edgeLabels, edgeLabels.Length))
        {
            cfg.MaxImageEdge = edges[edgeIndex];
            cfg.Save();
            plugin.ForgetAllContent();
        }

        ImGui.TextWrapped(
            "The file format makes no difference to what a picture costs. A JPEG and a PNG " +
            "of the same photograph are identical once decoded — compression is undone before " +
            "the graphics card sees them. Size is the only lever.");
    }

    // ── Interface ─────────────────────────────────────────────────────────────────────

    private void DrawInterface()
    {
        var cfg = plugin.Config;

        Heading("Interface");

        var avoidUi = cfg.AvoidGameUi;
        if (ImGui.Checkbox("Never cover my hotbars and chat", ref avoidUi))
        {
            cfg.AvoidGameUi = avoidUi;
            cfg.Save();
        }

        ImGui.TextWrapped(
            "Stops screens drawing over the game's own interface. It works from each panel's " +
            "rectangle, so it takes a slightly larger bite out of a picture than the buttons " +
            "strictly occupy.");
    }

    // ── Company ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ⭐⭐ The company file builder, behind a switch, because **almost nobody has any
    /// business here.** Chris, 2026-09-02: *"90% of users are not going to have any
    /// permissions."* A member never publishes a company list, and showing them the tools to
    /// do so is the same mistake as opening a settings panel at login — it tells them the
    /// plugin was built for somebody else.
    ///
    /// ⚠ Deliberately *not* a game permission check. Rank does not decide who does this job,
    /// and a check would be wrong for the FC that delegates it and for the venue that has no
    /// company at all. Saying "I am the one who does this" is a claim only one person makes
    /// about themselves, and there is nothing to protect: publishing is a paste either way.
    /// </summary>
    private void DrawCompany()
    {
        var cfg = plugin.Config;

        Heading("Company");

        var admin = cfg.AdminMode;
        if (ImGui.Checkbox("I look after my company's rooms", ref admin))
        {
            cfg.AdminMode = admin;
            cfg.Save();
        }

        ImGui.TextWrapped(
            "Turn this on if you are the officer who publishes the company's room list. " +
            "It is off for everybody else because they never need it.");

        if (!admin) return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped(
            "List each member's own room code here. They keep editing theirs; you only own " +
            "who is on the list. Removing someone is deleting a line and republishing.");

        ImGui.Spacing();

        var removeAt = -1;
        for (var i = 0; i < cfg.BuilderEntries.Count; i++)
        {
            ImGui.PushID(2000 + i);
            var entry = cfg.BuilderEntries[i];
            ImGui.SetNextItemWidth(220f);
            if (ImGui.InputTextWithHint("##be", "room code", ref entry, 512))
            {
                cfg.BuilderEntries[i] = Plugin.CollapseToCode(entry);
                cfg.Save();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove")) removeAt = i;
            ImGui.PopID();
        }

        if (removeAt >= 0) { cfg.BuilderEntries.RemoveAt(removeAt); cfg.Save(); }

        if (ImGui.Button("Add a room")) { cfg.BuilderEntries.Add(string.Empty); cfg.Save(); }

        if (cfg.BuilderEntries.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("Copy the list"))
                ImGui.SetClipboardText(plugin.BuildCompanyManifest(cfg.BuilderEntries));

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Paste this into the file your company board points at.\n\n" +
                    "It contains codes, not copies — so a member changing their own room\n" +
                    "shows up for everyone without you touching this again.");
        }
    }

    // ── Storage ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ⭐⭐ Two figures, and they are worth showing together because people conflate them:
    /// video memory, which is released when you leave a room, and downloaded bytes on disk,
    /// which are kept precisely so that leaving is free.
    ///
    /// ⚠ Whoever decorates a room never experiences the cost of overdoing it — they loaded it
    /// gradually on the machine that could afford it. The guest walks in later and pays it
    /// at once. This is the only moment at which the person who can act on the number is the
    /// person looking at it.
    /// </summary>
    private void DrawStorage()
    {
        Heading("Storage");

        var mem = plugin.ContentMemory;
        if (mem.Bytes > 0)
        {
            var mb = mem.Bytes / (1024.0 * 1024.0);
            var what = mem.Animations > 0
                ? $"{Ui.Count(mem.Images, "image")} and {Ui.Count(mem.Animations, "GIF")}"
                : Ui.Count(mem.Images, "image");

            if (mem.Bytes >= Plugin.MemoryWarnBytes)
            {
                ImGui.TextColored(new Vector4(1f, 0.75f, 0.35f, 1f),
                    $"In video memory: {mb:N0} MB — {what}");
                ImGui.TextWrapped("That is a lot to ask of a guest on weaker hardware.");
            }
            else
            {
                ImGui.Text($"In video memory: {mb:N0} MB — {what}");
            }

            ImGui.TextDisabled("Released a couple of minutes after you leave a room.");
        }
        else
        {
            ImGui.TextDisabled("Nothing loaded right now.");
        }

        ImGui.Spacing();

        var cache = Plugin.Cache.Size();
        ImGui.Text(cache.Files > 0
            ? $"Saved downloads: {cache.Bytes / (1024.0 * 1024.0):N0} MB — {Ui.Count(cache.Files, "file")}"
            : "Saved downloads: nothing yet");

        ImGui.TextDisabled("Kept so a picture is never downloaded twice.");

        if (cache.Files > 0)
        {
            if (ImGui.Button("Clear saved downloads")) Plugin.Cache.Clear();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Costs nothing but downloading them again.");
        }
    }

    // ── Something looks wrong ─────────────────────────────────────────────────────────

    /// <summary>
    /// ⭐ Symptoms, not settings. Somebody with a problem knows what they are *seeing*, not
    /// which internal switch governs it, so every line here names the thing on screen.
    /// </summary>
    private void DrawTrouble()
    {
        var cfg = plugin.Config;

        Heading("Something looks wrong");

        ImGui.TextWrapped("A screen you placed is nowhere to be seen:");
        ImGui.Spacing();

        var noOcclude = cfg.DisableOcclusion;
        if (ImGui.Checkbox("Show screens through walls and people", ref noOcclude))
        {
            cfg.DisableOcclusion = noOcclude;
            cfg.Save();
        }

        ImGui.TextWrapped(
            "If it appears with this on and vanishes with it off, the panel is exactly where " +
            "you put it and something is standing in front of it.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped("Pictures are not appearing or look out of date:");
        ImGui.Spacing();

        if (ImGui.Button("Check for new pictures now"))
        {
            plugin.Albums.RefreshNow();
            plugin.Manifest.RefreshNow();
            plugin.ForgetAllContent();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Same as /descast refresh.");

        if (plugin.Game.Error is { } gerr)
        {
            ImGui.Spacing();
            Ui.ErrorText(gerr);
        }

        if (plugin.Renderer.Error is { } rerr)
        {
            ImGui.Spacing();
            Ui.ErrorText(rerr);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────

    private static void Heading(string text)
    {
        ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), text);
        ImGui.Separator();
        ImGui.Spacing();
    }
}
