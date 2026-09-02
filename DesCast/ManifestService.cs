using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace DesCast;

/// <summary>
/// Fetches every shared room definition the user subscribes to, and keeps the last good
/// copy of each.
///
/// ⭐⭐ Several subscriptions rather than one, because a house is not a single shared space.
/// The Free Company hall is published by officers; a private room belongs to whoever lives
/// in it. Merging independent files lets both exist without either party needing edit
/// rights over the other's — and since every entry is scoped to a house id that includes
/// the room number, the hall's file physically cannot place anything in someone's bedroom.
///
/// ⚠ A failed refresh <b>keeps that subscription's previous copy</b>. Going dark on a
/// network blip would take every shared screen in the house out at once, and a stale board
/// is almost always better than no board.
/// </summary>
public sealed class ManifestService
{
    /// <summary>
    /// Boards want eventual consistency, not synchrony — if one person's board says pull
    /// 210 and another says 214 for a few minutes, nobody misses a mechanic. Slow on
    /// purpose.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait before retrying a subscription that has never loaded.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private sealed class Subscription
    {
        public IReadOnlyList<ScreenPlacement> Screens = Array.Empty<ScreenPlacement>();
        public string? Error;
        public DateTimeOffset? LoadedAt;
        public DateTimeOffset LastAttempt = DateTimeOffset.MinValue;
        public bool Fetching;
    }

    private readonly Configuration config;
    private readonly Dictionary<string, Subscription> subscriptions = new();

    /// <summary>Every screen from every subscription, flattened for the draw path.</summary>
    public IReadOnlyList<ScreenPlacement> Screens { get; private set; } = Array.Empty<ScreenPlacement>();

    public ManifestService(Configuration config) => this.config = config;

    /// <summary>
    /// Everything subscribed to: the user's own list plus whatever the Free Company board
    /// published. ⭐ Two sources, one merge point, so nothing downstream has to care which
    /// a screen came from.
    /// </summary>
    private IEnumerable<string> AllUrls()
    {
        foreach (var u in config.CompanyBoardUrls) yield return u;
        foreach (var u in config.ManifestUrls) yield return u;
    }

    /// <summary>Per-subscription state, for the editor to show. Never throws, never blocks.</summary>
    public IEnumerable<(string Url, int Count, string? Error, DateTimeOffset? LoadedAt, bool Fetching)> Status()
    {
        foreach (var raw in config.ManifestUrls)
        {
            var url = raw.Trim();
            if (subscriptions.TryGetValue(url, out var sub))
                yield return (url, sub.Screens.Count, sub.Error, sub.LoadedAt, sub.Fetching);
            else
                yield return (url, 0, null, null, false);
        }
    }

    /// <summary>
    /// Called every frame; almost always does nothing. ⚠ Kept to dictionary lookups and a
    /// timestamp comparison, because anything on a draw path runs at frame rate.
    /// </summary>
    public void Tick()
    {
        var changed = false;
        var wanted = new HashSet<string>();

        foreach (var raw in AllUrls())
        {
            var url = raw.Trim();
            if (url.Length == 0) continue;
            if (!wanted.Add(url)) continue;   // the same link on the board and in your list is one subscription

            if (!subscriptions.TryGetValue(url, out var sub))
            {
                sub = new Subscription();
                subscriptions[url] = sub;
                changed = true;
            }

            if (sub.Fetching) continue;

            var since = DateTimeOffset.UtcNow - sub.LastAttempt;
            var due = sub.LoadedAt is null ? since > RetryInterval : since > RefreshInterval;
            if (!due) continue;

            sub.LastAttempt = DateTimeOffset.UtcNow;
            _ = RefreshAsync(url, sub);
        }

        // Forget subscriptions the user removed, so their screens stop rendering at once
        // rather than lingering until a restart.
        if (subscriptions.Count != wanted.Count)
        {
            foreach (var key in new List<string>(subscriptions.Keys))
                if (!wanted.Contains(key)) { subscriptions.Remove(key); changed = true; }
        }

        if (changed) Reflatten();
    }

    /// <summary>Force every subscription to re-fetch now.</summary>
    public void RefreshNow()
    {
        foreach (var sub in subscriptions.Values)
            sub.LastAttempt = DateTimeOffset.MinValue;
    }

    private void Reflatten()
    {
        var total = 0;
        foreach (var sub in subscriptions.Values) total += sub.Screens.Count;

        var all = new List<ScreenPlacement>(total);
        foreach (var sub in subscriptions.Values) all.AddRange(sub.Screens);
        Screens = all;
    }

    private async Task RefreshAsync(string url, Subscription sub)
    {
        sub.Fetching = true;
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

            sub.Screens = placements;
            sub.LoadedAt = DateTimeOffset.UtcNow;

            // ⚠ Partial success is still worth reporting. An entry with a broken house id
            // simply never appears, and silence makes that look like a placement mistake
            // in game rather than a typo in the file.
            sub.Error = skipped > 0
                ? $"{skipped} entr{(skipped == 1 ? "y has" : "ies have")} no valid \"house\" value."
                : null;
        }
        catch (Exception ex)
        {
            sub.Error = sub.Screens.Count > 0
                ? $"Could not refresh ({ex.Message}). Showing the last good copy."
                : $"Could not load: {ex.Message}";
            Plugin.Log.Warning($"Manifest fetch failed for '{url}': {ex.Message}");
        }
        finally
        {
            sub.Fetching = false;
            Reflatten();
        }
    }
}
