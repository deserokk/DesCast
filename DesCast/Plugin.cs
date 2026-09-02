using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace DesCast;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITextureProvider Textures { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;

    private ulong locationLabelFor;
    private string? locationLabel;

    /// <summary>
    /// "Balmung  ·  Empyreum  ·  Ward 2  ·  Plot 47".
    ///
    /// ⚠ Ward and plot are displayed **plus one**. The game stores them as zero-based
    /// indices and shows them one-based throughout its own UI, so printing the raw index
    /// reads as an off-by-one bug to anyone comparing against the housing menu.
    /// Cached per house, because resolving two Excel sheets belongs nowhere near a
    /// per-frame draw call.
    /// </summary>
    internal string DescribeLocation(GameView.HouseLocation loc)
    {
        if (locationLabelFor == loc.Id && locationLabel is { } cached) return cached;

        var world = Data.GetExcelSheet<Lumina.Excel.Sheets.World>()
                        .GetRowOrDefault(loc.WorldId)?.Name.ExtractText();

        var district = Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
                           .GetRowOrDefault(loc.TerritoryTypeId)?.PlaceName
                           .ValueNullable?.Name.ExtractText();

        var parts = new List<string>(5);
        if (!string.IsNullOrEmpty(world)) parts.Add(world);
        if (!string.IsNullOrEmpty(district)) parts.Add(district);
        parts.Add($"Ward {loc.WardNumber}");
        parts.Add($"Plot {loc.PlotNumber}");
        if (loc.RoomNumber > 0) parts.Add($"Room {loc.RoomNumber}");

        locationLabel = string.Join("  ·  ", parts);
        locationLabelFor = loc.Id;
        return locationLabel;
    }

    private const string CommandName = "/descast";

    internal Configuration Config { get; }
    internal GameView Game { get; }
    internal ScreenRenderer Renderer { get; }
    internal ManifestService Manifest { get; }
    internal Albums Albums { get; }
    internal CompanyBoard Board { get; }

    /// <summary>
    /// Downloaded bytes on disk. ⭐ Static because the fetch path is static, and there is
    /// exactly one of these for the plugin.
    /// </summary>
    internal static DownloadCache Cache { get; private set; } = null!;

    private readonly WindowSystem windows = new("DesCast");
    private readonly PlacementWindow placementWindow;

    /// <summary>
    /// One entry per image path we have been asked for. Holds the loaded texture, or the
    /// in-flight load, or the reason it failed — never just an absence, because an absence
    /// is what made a broken path look like a working checkerboard.
    /// </summary>
    private sealed class ContentEntry
    {
        public IDalamudTextureWrap? Wrap;

        /// <summary>Set instead of <see cref="Wrap"/> when the source turned out to move.</summary>
        public AnimatedImage? Animation;

        public string? Error;
        public bool Loading;

        /// <summary>Width ÷ height, or 0 while unknown.</summary>
        public float Aspect;

        /// <summary>Video memory this entry holds, for the room's running total.</summary>
        public long Bytes;

        /// <summary>What a GIF gave up to fit its budget, so the editor can say so.</summary>
        public string? Note;

        /// <summary>
        /// When a screen in the room last had a use for this. ⚠ Not "when it was last
        /// drawn" — a slideshow only draws one slide at a time, so drawing is far too
        /// narrow a signal and would evict the other four mid-rotation.
        /// </summary>
        public DateTimeOffset LastWanted = DateTimeOffset.UtcNow;
    }

    private readonly Dictionary<string, ContentEntry> content = new();
    private IDalamudTextureWrap? testCard;

    /// <summary>
    /// How many images may be fetching at once.
    ///
    /// ⭐ One, deliberately. Walking into a room with three screens on rotation would
    /// otherwise start six downloads and six decodes in the same frame — and the decode
    /// and upload land back on the render thread, so a burst becomes a visible stall
    /// exactly when someone first sees the room. Serialised, each arrives a moment later
    /// and nothing hitches; the test card covers the gap, which is what it is for.
    /// </summary>
    private const int MaxConcurrentLoads = 1;

    private int loadsInFlight;

    /// <summary>Image-load failures, keyed by the path as the user typed it. For the editor.</summary>
    internal IReadOnlyDictionary<string, string> ContentErrors
    {
        get
        {
            var errors = new Dictionary<string, string>();
            foreach (var (path, entry) in content)
                if (entry.Error is { } e) errors[path] = e;
            return errors;
        }
    }

    /// <summary>
    /// Notes about loaded content — currently only what a GIF gave up to fit its budget.
    /// Keyed the same way as <see cref="ContentErrors"/>.
    /// </summary>
    internal IReadOnlyDictionary<string, string> ContentNotes
    {
        get
        {
            var notes = new Dictionary<string, string>();
            foreach (var (path, entry) in content)
                if (entry.Note is { } n) notes[path] = n;
            return notes;
        }
    }

    /// <summary>
    /// What everything currently loaded is costing in video memory, and how much of it
    /// moves.
    ///
    /// ⭐⭐ This exists because the person who fills a room with screens is never the
    /// person who finds out it was too many — they have already loaded it all and the
    /// frame rate they see is the one their machine can afford. Guests arrive later, on
    /// worse hardware, with no idea why the room is a slideshow. Putting a number in front
    /// of the owner is the only point at which anyone can act on it.
    ///
    /// ⚠ An honest lower bound, not a true figure. It counts our own decoded pictures
    /// and nothing else — not the game's, not other plugins'.
    /// </summary>
    internal (long Bytes, int Images, int Animations) ContentMemory
    {
        get
        {
            long bytes = 0;
            var images = 0;
            var animations = 0;

            foreach (var entry in content.Values)
            {
                if (entry.Bytes <= 0) continue;

                bytes += entry.Bytes;
                if (entry.Animation != null) animations++;
                else images++;
            }

            return (bytes, images, animations);
        }
    }

    /// <summary>
    /// Where the editor starts warning about the running total. Not a limit and nothing is
    /// refused at it — it is the point past which "this room is heavy" is worth saying out
    /// loud to the one person who can do something about it.
    /// </summary>
    internal const long MemoryWarnBytes = 256L * 1024 * 1024;

    /// <summary>
    /// ⚠ Windows' "Copy as path" (shift-right-click) wraps the path in double quotes, and
    /// pasting that verbatim is the overwhelmingly likely way anyone will fill this field.
    /// Strip them rather than making the user notice.
    /// </summary>
    internal static string NormalisePath(string raw) => raw.Trim().Trim('"');

    /// <summary>The shape of the picture currently on a screen, or 0 while unknown.</summary>
    internal float AspectOf(ScreenPlacement s)
    {
        var path = NormalisePath(s.CurrentSource(DateTimeOffset.UtcNow));
        return !string.IsNullOrWhiteSpace(path)
               && content.TryGetValue(path, out var e) && e.Wrap is not null
            ? e.Aspect
            : 0f;
    }

    /// <summary>
    /// The height this screen is actually being drawn at, for the editor to display.
    /// Falls back to the stored height while the image is unknown or still decoding.
    /// </summary>
    internal float HeightOf(ScreenPlacement s)
    {
        var path = NormalisePath(s.CurrentSource(DateTimeOffset.UtcNow));
        var aspect = !string.IsNullOrWhiteSpace(path)
                     && content.TryGetValue(path, out var e) && e.Wrap is not null
            ? e.Aspect
            : 0f;
        return s.HeightFor(aspect);
    }

    /// <summary>Reused each frame so the draw path allocates nothing.</summary>
    private readonly List<ScreenRenderer.Panel> drawList = new();

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Initialise(PluginInterface);

        // Fold pre-slideshow screens into their source list at load, not lazily on first
        // draw. ⚠ The editor reads Sources directly, so migrating only when a screen
        // happens to be rendered would show an empty list to anyone who opened the window
        // from somewhere else — a config that looks lost when it is merely stale.
        var migratedAtLoad = Config.MigrateManifestUrls();
        foreach (var s in Config.Screens) migratedAtLoad |= s.MigrateSources();
        if (migratedAtLoad) Config.Save();

        Game = new GameView();
        Renderer = new ScreenRenderer(Game);
        Manifest = new ManifestService(Config);
        Albums = new Albums();
        Cache = new DownloadCache(PluginInterface.ConfigDirectory);
        Board = new CompanyBoard(Config);

        placementWindow = new PlacementWindow(this);
        windows.AddWindow(placementWindow);

        Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the DesCast placement editor. /descast refresh checks for new pictures now.",
        });

        PluginInterface.UiBuilder.Draw += OnDraw;

        // ⚠⚠ Screens are drawn through ImGui, so Dalamud's plugin-UI hiding takes them
        // with it — and hiding the UI is the *first* thing anyone does before taking a
        // screenshot. A decorated house is largely for screenshots, so a screen that
        // vanishes exactly when you want to photograph it is close to useless.
        //
        // These flags keep our draw callback running while the game UI is hidden, in
        // cutscenes and in gpose. ⚠ They apply to the whole plugin, so OnDraw decides for
        // itself what should still be visible — the panels yes, the editor window no.
        PluginInterface.UiBuilder.DisableUserUiHide = true;
        PluginInterface.UiBuilder.DisableAutomaticUiHide = true;
        PluginInterface.UiBuilder.DisableCutsceneUiHide = true;
        PluginInterface.UiBuilder.DisableGposeUiHide = true;
        PluginInterface.UiBuilder.OpenConfigUi += () => placementWindow.IsOpen = true;
        PluginInterface.UiBuilder.OpenMainUi += () => placementWindow.IsOpen = true;

        if (Config.OpenOnLoad) placementWindow.IsOpen = true;
    }

    /// <summary>
    /// ⭐ The probe gets command access as well as buttons, because testing it means having
    /// the game's own Free Company window open — which is exactly when hunting for a button
    /// in another window is most awkward.
    /// </summary>
    private void OnCommand(string command, string args)
    {
        // ⭐⭐ This command is what buys the long polling interval. Checking an album every
        // five minutes forever is a real cost to somebody on metered internet, and it exists
        // only for the rare moment when a poster has just gone up. Making that moment a
        // deliberate action lets the automatic interval be an hour instead — Chris,
        // 2026-09-02: *"if something is posted and they want to see it quickly they can enter
        // that command."*
        if (args.Trim().Equals("refresh", StringComparison.OrdinalIgnoreCase))
        {
            Albums.RefreshNow();
            Manifest.RefreshNow();

            // ⚠ Drop the decoded pictures too. Without this, a replaced poster at the same
            // URL keeps showing the old bytes — which is exactly the case somebody types
            // this command for.
            ForgetAllContent();

            Chat.Print("DesCast: checking for new pictures.");
            return;
        }

        if (args.Trim().Equals("ui", StringComparison.OrdinalIgnoreCase))
        {
            var size = ImGui.GetMainViewport().Size;
            var text = UiRects.Dump(size.X, size.Y);
            var path = System.IO.Path.Combine(PluginInterface.ConfigDirectory.FullName, "ui.log");
            try
            {
                if (!PluginInterface.ConfigDirectory.Exists) PluginInterface.ConfigDirectory.Create();
                System.IO.File.AppendAllText(path, text + Environment.NewLine);
                Chat.Print($"DesCast: interface dumped to {path}");
            }
            catch (Exception ex)
            {
                Chat.Print($"DesCast: could not write the dump: {ex.Message}");
            }
            return;
        }

        placementWindow.Toggle();
    }

    /// <summary>
    /// Cheap enough to run every frame: a location read and a walk of a list that has
    /// single digits in it.
    /// </summary>
    private bool AnyScreensHere(GameView.HouseLocation location)
    {
        foreach (var s in Config.Screens)
            if (s.Enabled && location.Matches(s)) return true;
        foreach (var s in Manifest.Screens)
            if (s.Enabled && location.Matches(s)) return true;
        return false;
    }

    /// <summary>
    /// Everything that should be on a wall in this room: the user's own screens plus
    /// whatever the shared manifest puts here. ⭐ Both are the same type, so nothing
    /// downstream — sizing, slideshow timing, the renderer — knows or cares which is which.
    /// </summary>
    private IEnumerable<ScreenPlacement> ScreensHere(GameView.HouseLocation location)
    {
        foreach (var s in Config.Screens)
            if (s.Enabled && location.Matches(s)) yield return s;
        foreach (var s in Manifest.Screens)
            if (s.Enabled && location.Matches(s)) yield return s;
    }

    /// <summary>
    /// ⚠⚠ Everything that touches D3D happens here, on this one thread, in this order.
    ///
    /// The depth copy looks like it belongs on Framework.Update — it is a GPU copy of a
    /// finished scene and has nothing to do with UI. It does not belong there. Dalamud's
    /// framework update and its draw callback run on <b>different threads</b>, and the
    /// game's D3D11 immediate context is not thread-safe, so issuing the copy from one
    /// while the render pass runs on the other corrupts state and crashes the game. Cost
    /// one crash to learn; Xiv Media Player captures inside its draw callback for exactly
    /// this reason.
    ///
    /// ⭐⭐ The location gate below is the plugin's whole performance story. The depth copy
    /// is the only per-frame cost we pay regardless of content, so it is skipped entirely
    /// unless a screen exists in this exact room. Anywhere else in the game — a raid, a
    /// duty, the overworld, someone else's house — nothing runs at all. Not a figure of
    /// speech: no copy, no pass, no allocation.
    /// </summary>
    private void OnDraw()
    {
        // The editor still hides with the rest of the interface — it is a settings window,
        // and nobody wants it in a screenshot. Only the screens themselves ignore the hide.
        if (!GameGui.GameUiHidden) windows.Draw();

        // ⚠⚠ Above every gate below, and that placement is the whole point. The rest of
        // this method is skipped the moment you are not stood in a room with screens — which
        // is precisely when the pictures from the last room should be handed back. Put the
        // sweep after the gate and memory is only ever released while you are still looking
        // at something, which is to say never.
        SweepContent();

        if (!Config.Enabled) return;
        if (!ClientState.IsLoggedIn) return;

        Board.Tick();
        Manifest.Tick();

        // ⚠ Outside a house we draw nothing at all. A screen belongs to a specific ward,
        // plot and room; rendering it anywhere else would put your briefing room inside
        // a stranger's identically-shaped interior. Silence in the wrong house is correct.
        var location = Game.GetLocation();
        if (location is null) return;
        if (!AnyScreensHere(location.Value)) return;

        if (!Game.Ready && !Game.Initialise()) return;
        if (!Renderer.Initialised && !Renderer.Initialise()) return;

        Game.CaptureDepth();

        var cam = Game.GetCamera();
        if (cam is null) return;

        drawList.Clear();
        var migrated = false;
        foreach (var s in Config.Screens)
            if (s.Enabled && location.Value.Matches(s))
                migrated |= location.Value.MigrateIdentity(s);
        if (migrated) Config.Save();

        foreach (var s in ScreensHere(location.Value))
        {
            var now = DateTimeOffset.UtcNow;

            // ⭐ Albums are expanded here, so everything downstream sees a plain list of
            // pictures. The wall-clock slide index then runs over the album's contents
            // exactly as it does over a hand-written list — adding a poster to an album
            // lengthens the rotation for everyone with nothing else changing.
            var slides = ExpandSources(s);

            // ⭐⭐ Every slide this screen could show counts as wanted, not just the one on
            // the wall right now. A five-picture album at thirty seconds a slide leaves any
            // given picture untouched for two minutes; treating "not currently drawn" as
            // "not needed" would throw four of them away and re-download them on a loop.
            foreach (var slide in slides) TouchContent(slide);

            var handle = GetContentHandle(SlideAt(slides, s, now, 0), out var imageAspect);
            if (handle == 0) continue;

            // Warm the next slide while this one is still up, so a change never flashes
            // the placeholder. Cheap: after the first pass it is a dictionary hit.
            // ⭐ Work the fit out here rather than in the shader: both aspect ratios are
            // already known on this side, and it keeps the shader branchless.
            var panelAspect = s.Width / MathF.Max(s.HeightFor(imageAspect), 0.0001f);
            var (uvScale, clipOutside) = FitUv(s.Fit, panelAspect, imageAspect);

            // The incoming slide, prefetched anyway — a transition just uses what was already
            // being warmed so a change never flashes the placeholder.
            var next = SlideAt(slides, s, now, 1);
            var nextAspect = 0f;
            var nextHandle = next.Length > 0 ? GetContentHandle(next, out nextAspect) : 0;

            var progress = s.ChangeProgressAt(now, slides.Count);
            var (nextUv, _) = FitUv(s.Fit, panelAspect, nextAspect);

            // ⭐ Aspect is resolved here, per frame, from whatever the image turned out to
            // be — never written back into the placement. While the image is still
            // decoding the aspect is 0 and the panel keeps its stored size, so a screen
            // does not visibly jump on load unless the picture genuinely is a new shape.

            drawList.Add(new ScreenRenderer.Panel(
                s.Position,
                s.AxisX,
                s.AxisYFor(imageAspect),
                s.Opacity,
                s.Brightness,
                s.Contrast,
                s.Saturation,
                s.Tint,
                s.EdgeSoftness,
                uvScale,
                clipOutside,
                s.Thickness,
                s.EdgeColour,
                handle,
                nextHandle,
                nextUv,
                progress,
                (int)s.Change));
        }
        if (drawList.Count == 0) return;

        var viewport = ImGui.GetMainViewport();
        var size = viewport.Size;

        if (!Renderer.Render(
                (int)size.X, (int)size.Y,
                cam.Value,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(drawList),
                Config.ReverseDepth,
                Config.DisableOcclusion,

                // ⭐⭐ Nothing to keep off when there is no interface on screen. Hiding the
                // UI is precisely what someone does to look at a screen properly — so
                // holes punched for a hotbar that is no longer drawn are at their most
                // visible exactly when the picture matters most. Bunny, 2026-09-02.
                Config.AvoidGameUi && !GameGui.GameUiHidden))
            return;

        var output = Renderer.OutputHandle;
        if (output == 0) return;

        // The composite is already a finished, correctly-occluded picture of the room's
        // screens over a transparent background, so presenting it is one full-screen
        // image in the background draw list — under every ImGui window, over the game.
        ImGui.GetBackgroundDrawList(viewport)
             .AddImage(new ImTextureID(output), viewport.Pos, viewport.Pos + size);
    }

    /// <summary>
    /// A screen's sources with any albums replaced by their contents.
    ///
    /// ⚠ Reused per screen per frame, so it allocates only when a screen actually contains an
    /// album — the common case of a couple of plain links returns the list it was given.
    /// </summary>
    private List<string> ExpandSources(ScreenPlacement s)
    {
        var anyAlbum = false;
        foreach (var src in s.Sources)
            if (Albums.IsAlbum(src)) { anyAlbum = true; break; }

        if (!anyAlbum) return s.Sources;

        var expanded = new List<string>(s.Sources.Count + 8);
        foreach (var src in s.Sources)
        {
            if (!Albums.IsAlbum(src)) { expanded.Add(src); continue; }
            foreach (var image in Albums.Images(src)) expanded.Add(image);
        }
        return expanded;
    }

    /// <summary>
    /// The slide <paramref name="offset"/> steps from the current one, derived from the wall
    /// clock so every client in the room lands on the same picture without exchanging anything.
    /// </summary>
    private static string SlideAt(
        List<string> slides, ScreenPlacement s, DateTimeOffset now, int offset)
    {
        if (slides.Count == 0) return string.Empty;
        if (slides.Count == 1) return slides[0];

        var dwell = MathF.Max(s.DwellSeconds, 1f);
        var index = (int)(now.ToUnixTimeMilliseconds() / 1000.0 / dwell % slides.Count);
        return slides[(index + offset) % slides.Count];
    }

    /// <summary>
    /// How far to scale the texture coordinates about the centre so a picture sits
    /// correctly on a panel of a different shape.
    ///
    /// Below 1 on an axis crops that axis (fill); above 1 insets it, leaving emptiness
    /// (letterbox). ⚠ Returns 1,1 when the image's shape is not known yet — a picture still
    /// downloading must not make the panel jump.
    /// </summary>
    private static (Vector2 Scale, bool ClipOutside) FitUv(
        ScreenPlacement.Fitting mode, float panelAspect, float imageAspect)
    {
        if (mode == ScreenPlacement.Fitting.Stretch
            || imageAspect <= 0.0001f || panelAspect <= 0.0001f)
            return (Vector2.One, false);

        var ratio = panelAspect / imageAspect;

        return mode == ScreenPlacement.Fitting.Fill
            // Cover: shrink the coordinates on the axis with room to spare, cropping it.
            ? (ratio > 1f ? new Vector2(1f, 1f / ratio) : new Vector2(ratio, 1f), false)
            // Contain: expand that axis instead, and clip what falls outside.
            : (ratio > 1f ? new Vector2(ratio, 1f) : new Vector2(1f, 1f / ratio), true);
    }

    /// <summary>
    /// Resolve a screen's content to a shader resource view. Textures are cached for the
    /// life of the plugin — reloading a PNG every frame would be the classic "work that
    /// belongs in a constructor sitting in a draw loop" mistake.
    /// </summary>
    private nint GetContentHandle(string rawPath, out float aspect)
    {
        aspect = 0f;
        var testCardHandle = (nint)(EnsureTestCard()?.Handle.Handle ?? 0);

        var path = NormalisePath(rawPath);
        if (string.IsNullOrWhiteSpace(path))
        {
            // ⚠ The test card is 16:9 and fitting to it would resize a panel to match a
            // placeholder. Report "unknown" so the stored size stands until a real image
            // arrives.
            return testCardHandle;
        }

        if (content.TryGetValue(path, out var entry))
        {
            // ⭐ A GIF resolves to whichever frame the wall clock says, and the renderer
            // never learns that anything moved — it is handed a texture and a placement,
            // the same as a poster. That is what keeps playback out of the shader.
            if (entry.Animation is { Frames.Length: > 0 } anim)
            {
                aspect = entry.Aspect;
                return anim.HandleAt(DateTimeOffset.UtcNow);
            }

            // Still decoding, or failed — either way show the test card, and the editor
            // reports which of the two it is.
            if (entry.Wrap is not { } w) return testCardHandle;
            aspect = entry.Aspect;
            return (nint)w.Handle.Handle;
        }

        // ⚠ Over the limit: do not create an entry. Leaving it unknown means the next
        // frame asks again, which is the whole queue — no list, no ordering, no way for a
        // dropped request to be lost.
        if (loadsInFlight >= MaxConcurrentLoads) return testCardHandle;

        entry = new ContentEntry { Loading = true };
        content[path] = entry;
        System.Threading.Interlocked.Increment(ref loadsInFlight);

        // ⚠⚠ Never block here. This runs on the render thread, and waiting on a decode
        // stalls the whole game's frame — and waiting on a task that may itself want the
        // render thread is how a stall becomes a hang. Start the load, show the test card
        // meanwhile, and pick the result up on a later frame.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                byte[] bytes;

                if (IsWebUrl(path))
                {
                    bytes = await FetchImageAsync(path).ConfigureAwait(false);
                }
                else
                {
                    if (!System.IO.File.Exists(path))
                        throw new System.IO.FileNotFoundException("No file at that path.");

                    // ⚠ Same ceiling as a download. The bytes have to be in hand to tell a
                    // moving GIF from a still one, so a local file is no longer streamed
                    // straight into the decoder and its size has to be checked here too.
                    var size = new System.IO.FileInfo(path).Length;
                    if (size > MaxImageBytes)
                        throw new InvalidOperationException(
                            $"That file is {size / (1024 * 1024)} MB; the limit is {MaxImageBytes / (1024 * 1024)} MB.");

                    bytes = await System.IO.File.ReadAllBytesAsync(path).ConfigureAwait(false);
                }

                // ⭐ A GIF that turns out to hold one frame is just a picture, and Decode
                // says so by returning null — so the ordinary path still handles it and
                // nothing special-cases the extension.
                var maxEdge = Config.MaxImageEdge;

                var animation = AnimatedImage.IsGif(bytes)
                    ? AnimatedImage.Decode(bytes, System.IO.Path.GetFileName(path),
                                           AnimatedImage.DefaultBudgetBytes, maxEdge)
                    : null;

                if (animation != null)
                {
                    entry.Aspect = animation.Aspect;
                    entry.Bytes = animation.Bytes;
                    entry.Note = animation.Compromise;
                    entry.Animation = animation;  // assigned last, as below
                    entry.Loading = false;
                }
                else
                {
                    // ⭐⭐ Shrink oversized pictures before they reach the GPU. This is the
                    // single biggest thing anyone can do about a heavy room — five phone
                    // photographs at full resolution cost 120 MB, and the same five capped
                    // to 2048 cost about a quarter of that with nothing visibly given up.
                    //
                    // ⚠ Returns null when the picture is already small enough, and also when
                    // GDI+ cannot read the format at all. Both mean the same thing here:
                    // hand it to Dalamud's decoder, which is the broader of the two.
                    var scaled = ImageDecode.TryDownscale(
                        bytes, maxEdge, out var sw, out var sh, out var ow, out var oh);

                    IDalamudTextureWrap wrap;
                    if (scaled != null)
                    {
                        wrap = ImageDecode.Upload(scaled, sw, sh, $"DesCast {System.IO.Path.GetFileName(path)}");
                        entry.Note = $"Scaled from {ow}×{oh} to {sw}×{sh} — "
                                   + $"{(long)ow * oh * 4 / (1024 * 1024)} MB down to {(long)sw * sh * 4 / (1024 * 1024)} MB.";
                    }
                    else
                    {
                        wrap = await Textures.CreateFromImageAsync(bytes).ConfigureAwait(false);
                    }

                    entry.Aspect = wrap.Height > 0 ? (float)wrap.Width / wrap.Height : 0f;
                    entry.Bytes = (long)wrap.Width * wrap.Height * 4;
                    entry.Wrap = wrap;   // assigned last: Aspect must be readable the instant
                    entry.Loading = false; // the draw thread sees a non-null Wrap
                }

            }
            catch (Exception ex)
            {
                entry.Error = ex.Message;
                entry.Loading = false;
                Log.Warning($"Could not load screen image '{path}': {ex.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref loadsInFlight);
            }
        });

        return testCardHandle;
    }


    // ── Remote images ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// One client for the plugin's lifetime. ⚠ A new HttpClient per request exhausts
    /// sockets under any repeated use — the standard .NET trap, and a board refreshing on
    /// a timer is exactly repeated use.
    /// </summary>
    private static readonly System.Net.Http.HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    /// <summary>Anything above this is refused rather than pulled into memory.</summary>
    private const long MaxImageBytes = 32L * 1024 * 1024;

    internal static bool IsWebUrl(string s)
        => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Turn what a person actually copies out of their browser into something that is a
    /// picture.
    ///
    /// ⚠ An Imgur link off the address bar (imgur.com/AbCdEfG) is an HTML page, not an
    /// image, and handing HTML to an image decoder produces a baffling error. Rewrite the
    /// single-image case to the direct CDN form. Albums genuinely need the API and are a
    /// later feature, so they are refused with an explanation rather than mangled.
    /// </summary>
    internal static string ResolveImageUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

        var host = uri.Host.TrimStart('w', '.').ToLowerInvariant();
        if (host is not ("imgur.com" or "m.imgur.com")) return url;

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length == 0 || segments[0].Length == 0) return url;

        if (segments[0] is "a" or "gallery")
            throw new InvalidOperationException(
                "That is an Imgur album. Album support is not built yet — open the image " +
                "itself and use its direct link (i.imgur.com/....png).");

        var id = System.IO.Path.GetFileNameWithoutExtension(segments[^1]);
        return $"https://i.imgur.com/{id}.png";
    }

    /// <summary>
    /// Serialise placed screens as the shared-room file. ⭐ Indented on purpose — a human
    /// is going to open this in a browser and edit an image link in it, and minified JSON
    /// would make that miserable.
    /// </summary>
    internal string ExportManifest(IReadOnlyList<ScreenPlacement> screens, string whereLabel)
    {
        var manifest = new Manifest();
        foreach (var s in screens) manifest.Screens.Add(ManifestScreen.From(s, whereLabel));
        return Newtonsoft.Json.JsonConvert.SerializeObject(manifest, Newtonsoft.Json.Formatting.Indented);
    }

    /// <summary>
    /// Whether this looks like a bare Pastebin id: exactly eight letters and digits, with
    /// at least one digit and one letter so ordinary words are not mistaken for one.
    /// </summary>
    internal static bool IsPasteId(string s)
    {
        if (s.Length != 8) return false;

        var hasDigit = false;
        var hasLetter = false;
        foreach (var c in s)
        {
            if (char.IsAsciiDigit(c)) hasDigit = true;
            else if (char.IsAsciiLetter(c)) hasLetter = true;
            else return false;
        }
        return hasDigit && hasLetter;
    }

    /// <summary>
    /// Build a company manifest that points at member room files rather than copying their
    /// contents.
    ///
    /// ⭐⭐ Chris' design, and better than copying entries: a copy is a snapshot that goes stale
    /// the moment a member changes a poster, and someone has to re-paste it. A list of links means
    /// members edit their own rooms freely, the officer owns only the roster, and removing someone
    /// is deleting one line and republishing.
    /// </summary>
    internal string BuildCompanyManifest(IReadOnlyList<string> memberFiles)
    {
        var manifest = new Manifest();
        foreach (var raw in memberFiles)
        {
            var url = raw.Trim();
            if (url.Length > 0) manifest.Include.Add(url);
        }
        return Newtonsoft.Json.JsonConvert.SerializeObject(manifest, Newtonsoft.Json.Formatting.Indented);
    }

    /// <summary>Fetch a text document — the manifest. Same client, same limits.</summary>
    internal static System.Threading.Tasks.Task<string> FetchTextAsync(
        string url, params (string Name, string Value)[] headers)
        => FetchTextAsync(url, expectHtml: false, headers);

    /// <param name="expectHtml">
    /// ⚠ The web-page guard below exists so that pasting a *page* link where a data file was
    /// wanted gives a useful error instead of a parser complaining about "&lt;". An album is
    /// read from a real web page on purpose, so it has to opt out — otherwise the guard
    /// rejects the one caller that legitimately wants HTML, which is exactly what it did.
    /// </param>
    internal static async System.Threading.Tasks.Task<string> FetchTextAsync(
        string url, bool expectHtml, params (string Name, string Value)[] headers)
    {
        var resolved = ResolveTextUrl(url);

        using var request = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get, resolved);
        request.Headers.TryAddWithoutValidation("User-Agent", "DesCast/0.1 (FFXIV Dalamud plugin)");
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);

        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}.");

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        // ⚠ Catch the web-page case here, where we can say something useful, instead of
        // letting the JSON parser complain about an unexpected "<" at position 0 — an
        // error that tells the reader nothing about what they actually did wrong.
        if (!expectHtml && body.TrimStart().StartsWith('<'))
            throw new InvalidOperationException(
                "That link returned a web page, not the file itself. Use the raw link — " +
                "on Pastebin that is the RAW button; on GitHub or a gist, the Raw button.");

        return body;
    }

    /// <summary>
    /// Turn a link copied out of a browser's address bar into one that returns the file.
    ///
    /// ⚠ Every paste host has a pretty page and a raw endpoint, and the address bar always
    /// holds the pretty one. Rewriting is kinder than explaining, and it is the same class
    /// of problem as an imgur page link where the direct image was wanted.
    /// </summary>
    internal static string ResolveTextUrl(string url)
    {
        url = url.Trim();

        // A bare paste id expands to the full raw address. Two reasons, both Chris':
        //
        // Length — the Company Board is three short pages, and "0GzA4vpc" costs eight
        // characters where the full address costs thirty-three. Several rooms then fit
        // where one barely did.
        //
        // Deniability — the board is read by every member, including people on a vanilla
        // client, and it lives on Square's servers. A bare token is not obviously
        // anything; a raw pastebin link is obviously machine-readable configuration. Same
        // instinct as tagging the line "Screens:" rather than naming the plugin.
        //
        // ⚠ Pastebin ids are exactly eight alphanumerics — a tight enough shape to
        // recognise without swallowing text that was meant to be something else. A gist
        // still needs its full address, because its id alone does not identify the file.
        if (IsPasteId(url)) return $"https://pastebin.com/raw/{url}";

        // ⚠ A bare "pastebin.com/raw/xxxx" is what a person writes on a notice board —
        // nobody types the scheme by hand. Without it this never parses as an address and
        // the fetch throws on something that looked perfectly correct to whoever wrote it.
        if (!url.Contains("://") && url.Contains('.') && !url.Contains(' '))
            url = "https://" + url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

        var host = uri.Host.ToLowerInvariant();
        var seg = uri.AbsolutePath.Trim('/').Split('/');

        switch (host)
        {
            // pastebin.com/AbCdEfG  ->  pastebin.com/raw/AbCdEfG
            case "pastebin.com" when seg.Length == 1 && seg[0].Length > 0:
                return $"https://pastebin.com/raw/{seg[0]}";

            // gist.github.com/user/id  ->  .../raw
            case "gist.github.com" when seg.Length == 2:
                return $"https://gist.github.com/{seg[0]}/{seg[1]}/raw";

            // github.com/owner/repo/blob/branch/path  ->  raw.githubusercontent.com/...
            case "github.com" when seg.Length > 4 && seg[2] == "blob":
                return $"https://raw.githubusercontent.com/{seg[0]}/{seg[1]}/"
                       + string.Join('/', seg[3..]);

            default:
                return url;
        }
    }

    /// <summary>
    /// A picture's bytes, from disk if we already have them.
    ///
    /// ⭐⭐ The cache is what makes releasing video memory free. Without it, leaving for a
    /// duty and coming back re-downloads the whole room — which was true for about an hour
    /// between the eviction sweep landing and this arriving.
    /// </summary>
    private static System.Threading.Tasks.Task<byte[]> FetchImageAsync(string url)
        => Cache.GetAsync(url, (etag, lastModified) => DownloadImageAsync(url, etag, lastModified));

    /// <summary>
    /// The network half. Returns a null body to mean "the server says it has not changed",
    /// which costs a few hundred bytes instead of the file.
    /// </summary>
    private static async System.Threading.Tasks.Task<(byte[]? Body, string? ETag, string? LastModified)>
        DownloadImageAsync(string url, string? etag, string? lastModified)
    {
        var resolved = ResolveImageUrl(url);

        using var request = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get, resolved);
        // Some CDNs, Imgur among them, serve differently or refuse outright without one.
        request.Headers.TryAddWithoutValidation("User-Agent", "DesCast/0.1 (FFXIV Dalamud plugin)");

        // ⭐ Ask the server whether what we hold is still current, rather than asking for the
        // file again. Both GitHub and Imgur's CDN answer these, and the answer is nearly
        // always "unchanged" — a header instead of a megabyte.
        if (!string.IsNullOrEmpty(etag))
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        else if (!string.IsNullOrEmpty(lastModified))
            request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);

        using var response = await Http.SendAsync(
            request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            return (null, etag, lastModified);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}.");

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"That URL returned {mediaType}, not an image. Use a direct link to the picture itself.");

        if (response.Content.Headers.ContentLength is { } declared && declared > MaxImageBytes)
            throw new InvalidOperationException($"Image is {declared / (1024 * 1024)} MB; the limit is 32 MB.");

        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        if (bytes.LongLength > MaxImageBytes)
            throw new InvalidOperationException("Image exceeds the 32 MB limit.");

        return (bytes,
                response.Headers.ETag?.Tag,
                response.Content.Headers.LastModified?.ToString("R"));
    }

    /// <summary>
    /// Drop a cached image so the next frame fetches it again. For the editor's reload
    /// button — a hosted board that has been updated at the source is otherwise stuck on
    /// whatever was downloaded first.
    /// </summary>
    internal void ForgetContent(string rawPath)
    {
        var path = NormalisePath(rawPath);
        if (!content.Remove(path, out var entry)) return;

        entry.Wrap?.Dispose();
        entry.Animation?.Dispose();
    }

    /// <summary>
    /// Note that something in the current room still has a use for this picture. Only
    /// stamps entries that already exist — wanting a picture is not the same as asking for
    /// it, and loading is the draw path's job.
    /// </summary>
    private void TouchContent(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return;
        if (content.TryGetValue(NormalisePath(rawPath), out var entry))
            entry.LastWanted = DateTimeOffset.UtcNow;
    }

    /// <summary>How long a picture is kept after the last room that wanted it.</summary>
    /// <remarks>
    /// ⚠ Generous on purpose. Walking between the hall and a private room changes house id,
    /// so a short window would throw a room's pictures away every time somebody stepped out
    /// and back — turning a memory fix into a download loop, which is worse than the leak.
    /// </remarks>
    private static readonly TimeSpan ContentIdleTimeout = TimeSpan.FromMinutes(2);

    private DateTimeOffset lastSweep = DateTimeOffset.MinValue;

    /// <summary>
    /// Release pictures no room has wanted for a while.
    ///
    /// ⚠⚠ Nothing was ever released before this existed. The cache was documented as living
    /// for the life of the plugin, which is right about not reloading a PNG every frame and
    /// wrong about never handing one back — so touring four rooms accumulated all four rooms.
    /// Chris spotted it from the readout: five pictures in the hall, then seven on walking
    /// into a room holding two. Reported 2026-09-02.
    /// </summary>
    private void SweepContent()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - lastSweep < TimeSpan.FromSeconds(10)) return;
        lastSweep = now;

        List<string>? drop = null;

        foreach (var (path, entry) in content)
        {
            // ⚠ Never evict a load in flight. Its task holds the entry and will write a
            // texture into it; dropping the dictionary key would leak exactly the texture
            // this method exists to reclaim.
            if (entry.Loading) continue;

            if (now - entry.LastWanted < ContentIdleTimeout) continue;

            (drop ??= new List<string>()).Add(path);
        }

        if (drop == null) return;

        foreach (var path in drop)
        {
            if (!content.Remove(path, out var entry)) continue;
            entry.Wrap?.Dispose();
            entry.Animation?.Dispose();
        }

        Log.Debug($"DesCast released {drop.Count} cached picture(s).");
    }

    /// <summary>
    /// Drop every cached picture. For the detail setting — changing it has no effect on
    /// anything already decoded, and a setting that only applies to pictures you have not
    /// looked at yet is worse than no setting.
    /// </summary>
    internal void ForgetAllContent()
    {
        foreach (var e in content.Values)
        {
            e.Wrap?.Dispose();
            e.Animation?.Dispose();
        }

        content.Clear();
    }

    /// <summary>
    /// A generated checkerboard with a coloured border. Its whole job is to answer three
    /// questions at a glance the first time a panel appears: is it the right way up, is it
    /// mirrored, and is the aspect ratio what I asked for.
    /// </summary>
    private IDalamudTextureWrap? EnsureTestCard()
    {
        if (testCard != null) return testCard;

        const int w = 640, h = 360;
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            var checkerOn = ((x / 40) + (y / 40)) % 2 == 0;
            byte r = checkerOn ? (byte)40 : (byte)25;
            byte g = checkerOn ? (byte)44 : (byte)28;
            byte b = checkerOn ? (byte)52 : (byte)34;

            // Distinct edges: red along the top, green down the left. If red is at the
            // bottom the panel is flipped; if green is on the right it is mirrored.
            if (y < 12) { r = 200; g = 60; b = 60; }
            else if (x < 12) { r = 60; g = 190; b = 90; }

            px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
        }

        try
        {
            testCard = Textures.CreateFromRaw(RawImageSpecification.Bgra32(w, h), px, "DesCast test card");
        }
        catch (Exception ex)
        {
            Log.Error($"Could not build the test card: {ex.Message}");
        }
        return testCard;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= OnDraw;
        Commands.RemoveHandler(CommandName);
        windows.RemoveAllWindows();

        foreach (var e in content.Values)
        {
            e.Wrap?.Dispose();
            e.Animation?.Dispose();
        }
        content.Clear();
        testCard?.Dispose();

        Board.Dispose();
        Renderer.Dispose();
        Game.Dispose();
    }
}
