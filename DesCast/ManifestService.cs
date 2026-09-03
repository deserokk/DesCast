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

    /// <summary>
    /// How deep an include chain may go. ⚠ A company file listing member files is one level; a
    /// member listing a friend's is two. Beyond that is almost certainly a mistake, and the cap
    /// means a malicious or accidental chain cannot fan out indefinitely.
    /// </summary>
    private const int MaxIncludeDepth = 3;

    private sealed class Subscription
    {
        public IReadOnlyList<ScreenPlacement> Screens = Array.Empty<ScreenPlacement>();
        public IReadOnlyList<string> Include = Array.Empty<string>();
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
    /// <summary>
    /// ⚠ Returns a copy rather than walking the live lists. See <see cref="Status"/> for why
    /// nothing in this class hands out a lazy enumerator any more.
    /// </summary>
    private List<string> RootUrls()
    {
        var urls = new List<string>(config.CompanyBoardUrls.Count + config.ManifestUrls.Count);
        urls.AddRange(config.CompanyBoardUrls);
        urls.AddRange(config.ManifestUrls);
        return urls;
    }

    /// <summary>
    /// Every manifest we should be holding: the roots the user subscribes to, plus everything
    /// those pull in, transitively.
    ///
    /// ⚠⚠ Cycle-safe by construction. A file that includes itself, or two that include each
    /// other, would otherwise fetch forever — and the person who wrote them would have no way to
    /// tell, because from in game it just looks like the screens never appear. The visited set
    /// makes a cycle harmless rather than fatal.
    /// </summary>
    private HashSet<string> ResolveWanted()
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frontier = new List<string>();

        foreach (var raw in RootUrls())
        {
            var url = raw.Trim();
            if (url.Length > 0 && wanted.Add(url)) frontier.Add(url);
        }

        for (var depth = 0; depth < MaxIncludeDepth && frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var url in frontier)
            {
                if (!subscriptions.TryGetValue(url, out var sub)) continue;
                foreach (var raw in sub.Include)
                {
                    var child = raw.Trim();
                    if (child.Length > 0 && wanted.Add(child)) next.Add(child);
                }
            }
            frontier = next;
        }

        return wanted;
    }

    /// <summary>
    /// Per-subscription state, for the editor to show. Never throws, never blocks.
    ///
    /// ⚠⚠ <b>Built as a list, not yielded.</b> A lazy iterator here walks
    /// <c>config.ManifestUrls</c> while the caller is still drawing — and the caller is an
    /// editor whose whole job is to change that list. Editing a URL in the text box assigns
    /// through the list indexer, which bumps its version counter, and the next step of the
    /// loop throws "Collection was modified".
    ///
    /// ⚠ It was invisible with one manifest subscribed, because the loop had already
    /// finished. The second entry is what made it fire — so the bug arrived exactly when
    /// somebody first shared a room, which is the worst possible moment for it.
    ///
    /// ⭐ A snapshot also makes the caller's life simple: it may add, remove or edit
    /// entries freely while iterating, and merely sees the previous frame's list for the
    /// remainder of that frame. Nobody can perceive one frame.
    /// Found by Chris and Bunny, 2026-09-02.
    /// </summary>
    public List<(string Url, int Count, string? Error, DateTimeOffset? LoadedAt, bool Fetching)> Status()
    {
        var status = new List<(string, int, string?, DateTimeOffset?, bool)>(config.ManifestUrls.Count);

        foreach (var raw in config.ManifestUrls)
        {
            var url = raw.Trim();
            status.Add(subscriptions.TryGetValue(url, out var sub)
                ? (url, sub.Screens.Count, sub.Error, sub.LoadedAt, sub.Fetching)
                : (url, 0, null, null, false));
        }

        return status;
    }

    /// <summary>
    /// Manifests reached through an include rather than subscribed to directly — a company file's
    /// member rooms. ⭐ Listed separately in the editor so it is obvious where a screen came from,
    /// and so a member's broken file is visibly theirs rather than looking like the company's.
    /// </summary>
    public List<(string Url, int Count, string? Error)> IncludedStatus()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in RootUrls()) roots.Add(r.Trim());

        // ⚠ Materialised for the reason given on Status, and for a second one: this walks
        // the subscriptions dictionary, which Tick adds to as manifests are discovered. A
        // lazy walk here would break the moment a company file pulled in a member's room
        // while the editor happened to be open — rare, undebuggable, and entirely avoidable.
        var included = new List<(string, int, string?)>();

        foreach (var (url, sub) in subscriptions)
        {
            if (roots.Contains(url)) continue;
            included.Add((url, sub.Screens.Count, sub.Error));
        }

        return included;
    }

    /// <summary>
    /// Called every frame; almost always does nothing. ⚠ Kept to dictionary lookups and a
    /// timestamp comparison, because anything on a draw path runs at frame rate.
    /// </summary>
    public void Tick()
    {
        var changed = false;
        var wanted = ResolveWanted();

        foreach (var url in wanted)
        {
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
        var all = new List<ScreenPlacement>();

        foreach (var (url, sub) in subscriptions)
        {
            // ⭐ A paused room keeps its subscription and keeps refreshing — it simply
            // stops contributing screens. Pausing is meant to be the reversible answer to
            // "not right now", so the code must survive it; removing the room is the
            // irreversible one, and it costs being given the code again.
            if (config.IsPaused(url)) continue;
            all.AddRange(sub.Screens);
        }

        Screens = all;
    }

    /// <summary>Recompute after something outside changed which rooms count. For the pause toggle.</summary>
    public void RefreshFlattened() => Reflatten();

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
            sub.Include = manifest.Include ?? new List<string>();
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
