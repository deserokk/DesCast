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
    internal CompanyBoard Board { get; }

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
        public string? Error;
        public bool Loading;

        /// <summary>Width ÷ height, or 0 while unknown.</summary>
        public float Aspect;
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
    /// ⚠ Windows' "Copy as path" (shift-right-click) wraps the path in double quotes, and
    /// pasting that verbatim is the overwhelmingly likely way anyone will fill this field.
    /// Strip them rather than making the user notice.
    /// </summary>
    internal static string NormalisePath(string raw) => raw.Trim().Trim('"');

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
        Board = new CompanyBoard(Config);

        placementWindow = new PlacementWindow(this);
        windows.AddWindow(placementWindow);

        Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the DesCast placement editor.",
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
            var handle = GetContentHandle(s.CurrentSource(now), out var imageAspect);
            if (handle == 0) continue;

            // Warm the next slide while this one is still up, so a change never flashes
            // the placeholder. Cheap: after the first pass it is a dictionary hit.
            var next = s.NextSource(now);
            if (next.Length > 0) GetContentHandle(next, out _);

            // ⭐ Aspect is resolved here, per frame, from whatever the image turned out to
            // be — never written back into the placement. While the image is still
            // decoding the aspect is 0 and the panel keeps its stored size, so a screen
            // does not visibly jump on load unless the picture genuinely is a new shape.
            drawList.Add(new ScreenRenderer.Panel(
                s.Position,
                s.AxisX,
                s.AxisYFor(imageAspect),
                s.Opacity,
                handle));
        }
        if (drawList.Count == 0) return;

        var viewport = ImGui.GetMainViewport();
        var size = viewport.Size;

        if (!Renderer.Render(
                (int)size.X, (int)size.Y,
                cam.Value,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(drawList),
                Config.ReverseDepth,
                Config.DisableOcclusion))
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
                IDalamudTextureWrap wrap;

                if (IsWebUrl(path))
                {
                    var bytes = await FetchImageAsync(path).ConfigureAwait(false);
                    wrap = await Textures.CreateFromImageAsync(bytes).ConfigureAwait(false);
                }
                else
                {
                    if (!System.IO.File.Exists(path))
                        throw new System.IO.FileNotFoundException("No file at that path.");

                    await using var stream = System.IO.File.OpenRead(path);
                    wrap = await Textures.CreateFromImageAsync(stream).ConfigureAwait(false);
                }

                entry.Aspect = wrap.Height > 0 ? (float)wrap.Width / wrap.Height : 0f;
                entry.Wrap = wrap;   // assigned last: Aspect must be readable the instant
                entry.Loading = false; // the draw thread sees a non-null Wrap

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

    /// <summary>Fetch a text document — the manifest. Same client, same limits.</summary>
    internal static async System.Threading.Tasks.Task<string> FetchTextAsync(string url)
    {
        var resolved = ResolveTextUrl(url);

        using var request = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get, resolved);
        request.Headers.TryAddWithoutValidation("User-Agent", "DesCast/0.1 (FFXIV Dalamud plugin)");

        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}.");

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        // ⚠ Catch the web-page case here, where we can say something useful, instead of
        // letting the JSON parser complain about an unexpected "<" at position 0 — an
        // error that tells the reader nothing about what they actually did wrong.
        if (body.TrimStart().StartsWith('<'))
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

    private static async System.Threading.Tasks.Task<byte[]> FetchImageAsync(string url)
    {
        var resolved = ResolveImageUrl(url);

        using var request = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get, resolved);
        // Some CDNs, Imgur among them, serve differently or refuse outright without one.
        request.Headers.TryAddWithoutValidation("User-Agent", "DesCast/0.1 (FFXIV Dalamud plugin)");

        using var response = await Http.SendAsync(
            request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

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

        return bytes;
    }

    /// <summary>
    /// Drop a cached image so the next frame fetches it again. For the editor's reload
    /// button — a hosted board that has been updated at the source is otherwise stuck on
    /// whatever was downloaded first.
    /// </summary>
    internal void ForgetContent(string rawPath)
    {
        var path = NormalisePath(rawPath);
        if (content.Remove(path, out var entry)) entry.Wrap?.Dispose();
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

        foreach (var e in content.Values) e.Wrap?.Dispose();
        content.Clear();
        testCard?.Dispose();

        Renderer.Dispose();
        Game.Dispose();
    }
}
