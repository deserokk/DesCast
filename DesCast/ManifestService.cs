using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace DesCast;

/// <summary>
/// Fetches the shared room definition and keeps the last good copy.
///
/// ⚠ Single point of failure by design: if the manifest is unreachable, every shared
/// screen would go dark at once. So a failed refresh <b>keeps the previous copy</b> and
/// reports the problem, rather than replacing a working room with nothing. A stale board
/// is almost always better than no board.
/// </summary>
public sealed class ManifestService
{
    /// <summary>
    /// How often to re-fetch. Boards want eventual consistency, not synchrony — if one
    /// person's board says pull 210 and another says 214, nothing breaks and nobody misses
    /// a mechanic. So this is deliberately slow.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private readonly Configuration config;

    private DateTimeOffset lastAttempt = DateTimeOffset.MinValue;
    private string? loadedFrom;
    private bool fetching;

    /// <summary>Screens from the last successful fetch. Never cleared by a failure.</summary>
    public IReadOnlyList<ScreenPlacement> Screens { get; private set; } = Array.Empty<ScreenPlacement>();

    /// <summary>Non-null when the most recent attempt failed. Shown in the editor.</summary>
    public string? Error { get; private set; }

    /// <summary>When the current contents were successfully fetched.</summary>
    public DateTimeOffset? LoadedAt { get; private set; }

    public bool IsFetching => fetching;

    public ManifestService(Configuration config) => this.config = config;

    /// <summary>
    /// Called every frame; does nothing almost every time. ⚠ Cheap by construction — a
    /// timestamp comparison — because anything in a draw path runs at frame rate.
    /// </summary>
    public void Tick()
    {
        var url = (config.ManifestUrl ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(url))
        {
            if (Screens.Count > 0) Screens = Array.Empty<ScreenPlacement>();
            loadedFrom = null;
            Error = null;
            return;
        }

        if (fetching) return;

        // Re-fetch immediately when the URL changes, otherwise on the slow cadence.
        var due = url != loadedFrom || DateTimeOffset.UtcNow - lastAttempt > RefreshInterval;
        if (!due) return;

        lastAttempt = DateTimeOffset.UtcNow;
        _ = RefreshAsync(url);
    }

    /// <summary>Force a re-fetch now, for the editor's refresh button.</summary>
    public void RefreshNow()
    {
        lastAttempt = DateTimeOffset.MinValue;
        loadedFrom = null;
    }

    private async Task RefreshAsync(string url)
    {
        fetching = true;
        try
        {
            var json = await Plugin.FetchTextAsync(url).ConfigureAwait(false);

            var manifest = JsonConvert.DeserializeObject<Manifest>(json)
                           ?? throw new InvalidOperationException("The file parsed to nothing.");

            var placements = new List<ScreenPlacement>(manifest.Screens.Count);
            var skipped = 0;
            foreach (var entry in manifest.Screens)
            {
                var placement = entry.ToPlacement();
                if (placement is null) { skipped++; continue; }
                placements.Add(placement);
            }

            Screens = placements;
            LoadedAt = DateTimeOffset.UtcNow;
            loadedFrom = url;

            // ⚠ Partial success is still a problem to report. An entry with a broken house
            // id simply never appears, and silence would make that look like a placement
            // mistake in-game rather than a typo in the file.
            Error = skipped > 0
                ? $"{skipped} entr{(skipped == 1 ? "y has" : "ies have")} no valid \"house\" value and will not show."
                : null;
        }
        catch (Exception ex)
        {
            // Keep whatever we already had. Going dark on a transient network blip would
            // be a far worse failure than showing yesterday's poster.
            Error = Screens.Count > 0
                ? $"Could not refresh the manifest ({ex.Message}). Still showing the last good copy."
                : $"Could not load the manifest: {ex.Message}";
            Plugin.Log.Warning($"Manifest fetch failed for '{url}': {ex.Message}");
        }
        finally
        {
            fetching = false;
        }
    }
}
