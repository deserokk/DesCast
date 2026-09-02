using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace DesCast;

/// <summary>
/// Turns a link to a folder of pictures into the list of pictures in it.
///
/// ⭐⭐ This is what makes a board maintain itself. Point a screen at an album and adding a
/// poster becomes "drop it in the album" — every screen picks it up on the next refresh, the
/// manifest never changes, and nobody edits a file. It turns a board from a picture someone
/// updates into a channel that keeps itself current.
///
/// ⚠ Listings are refreshed slowly and the images themselves are cached as normal. Sixteen
/// clients checking a folder every few minutes is nothing; checking per slide would be rude.
/// </summary>
internal sealed class Albums
{
    /// <summary>
    /// ⚠ Slow on purpose. A new poster appearing within a few minutes is fine; the failure
    /// this avoids is a room full of clients hammering an API on every slide change.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private sealed class Entry
    {
        public IReadOnlyList<string> Images = Array.Empty<string>();
        public DateTimeOffset LastAttempt = DateTimeOffset.MinValue;
        public bool Loaded;
        public bool Fetching;
        public string? Error;
    }

    private readonly Dictionary<string, Entry> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Configuration config;

    public Albums(Configuration config) => this.config = config;

    private static readonly Regex ImgurAlbum = new(
        @"^https?://(?:www\.|m\.)?imgur\.com/(?:a|gallery)/([A-Za-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GitHubFolder = new(
        @"^https?://github\.com/([^/]+)/([^/]+)/tree/([^/]+)/(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsAlbum(string source)
        => ImgurAlbum.IsMatch(source) || GitHubFolder.IsMatch(source);

    /// <summary>
    /// The pictures in an album, or an empty list while it is still being fetched.
    /// ⚠ Never blocks and never throws — this is called from the draw path.
    /// </summary>
    public IReadOnlyList<string> Images(string albumUrl)
    {
        var url = albumUrl.Trim();
        if (!cache.TryGetValue(url, out var entry))
        {
            entry = new Entry();
            cache[url] = entry;
        }

        var due = DateTimeOffset.UtcNow - entry.LastAttempt
                  > (entry.Loaded ? RefreshInterval : TimeSpan.FromSeconds(20));

        if (!entry.Fetching && due)
        {
            entry.LastAttempt = DateTimeOffset.UtcNow;
            _ = RefreshAsync(url, entry);
        }

        return entry.Images;
    }

    public string? ErrorFor(string albumUrl)
        => cache.TryGetValue(albumUrl.Trim(), out var e) ? e.Error : null;

    public int CountFor(string albumUrl)
        => cache.TryGetValue(albumUrl.Trim(), out var e) ? e.Images.Count : 0;

    private async Task RefreshAsync(string url, Entry entry)
    {
        entry.Fetching = true;
        try
        {
            var images = ImgurAlbum.Match(url) is { Success: true } imgur
                ? await ImgurAsync(imgur.Groups[1].Value).ConfigureAwait(false)
                : await GitHubAsync(GitHubFolder.Match(url)).ConfigureAwait(false);

            // ⚠ Keep the previous listing on an empty result rather than blanking the
            // screen. An album that momentarily lists nothing is far more likely to be a
            // hiccup than a deliberate emptying.
            if (images.Count > 0 || !entry.Loaded)
            {
                entry.Images = images;
                entry.Loaded = true;
            }

            entry.Error = images.Count == 0 ? "That album appears to be empty." : null;
        }
        catch (Exception ex)
        {
            entry.Error = entry.Loaded
                ? $"Could not refresh the album ({ex.Message}). Showing the last listing."
                : $"Could not read the album: {ex.Message}";
            Plugin.Log.Warning($"Album fetch failed for '{url}': {ex.Message}");
        }
        finally
        {
            entry.Fetching = false;
        }
    }

    /// <summary>
    /// ⚠ Imgur's API needs a client id. Unlike a bot token this is semi-public and safe to
    /// hand out — it identifies the application, not a user, and grants nothing but read
    /// access to public content. It still has to be registered once, so it is a setting
    /// rather than something shipped blank.
    /// </summary>
    private async Task<IReadOnlyList<string>> ImgurAsync(string albumId)
    {
        var clientId = (config.ImgurClientId ?? string.Empty).Trim();
        if (clientId.Length == 0)
            throw new InvalidOperationException(
                "Imgur albums need a client id — register one at imgur.com/oauth2/addclient " +
                "and paste it into settings. A GitHub folder needs no key at all.");

        var json = await Plugin.FetchTextAsync(
            $"https://api.imgur.com/3/album/{albumId}/images",
            ("Authorization", $"Client-ID {clientId}")).ConfigureAwait(false);

        var images = new List<string>();
        foreach (var item in JObject.Parse(json)["data"] ?? new JArray())
        {
            var link = item["link"]?.ToString();
            if (!string.IsNullOrEmpty(link)) images.Add(link);
        }
        return images;
    }

    /// <summary>
    /// ⭐ A GitHub folder needs no key whatsoever, which makes it the zero-setup option: commit
    /// pictures to a folder and the listing is public. The trade is that officers need GitHub.
    /// </summary>
    private static async Task<IReadOnlyList<string>> GitHubAsync(Match m)
    {
        if (!m.Success) throw new InvalidOperationException("That is not an album link.");

        var (owner, repo, branch, path) =
            (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);

        var json = await Plugin.FetchTextAsync(
            $"https://api.github.com/repos/{owner}/{repo}/contents/{path}?ref={branch}",
            ("Accept", "application/vnd.github+json")).ConfigureAwait(false);

        var images = new List<string>();
        foreach (var item in JArray.Parse(json))
        {
            if (item["type"]?.ToString() != "file") continue;

            var download = item["download_url"]?.ToString();
            if (string.IsNullOrEmpty(download)) continue;

            // ⚠ Filter by extension: a folder holding a readme alongside the posters would
            // otherwise put a text file on the wall.
            var lower = download.ToLowerInvariant();
            if (lower.EndsWith(".png") || lower.EndsWith(".jpg") || lower.EndsWith(".jpeg")
                || lower.EndsWith(".gif") || lower.EndsWith(".webp") || lower.EndsWith(".bmp"))
                images.Add(download);
        }

        // ⚠⚠ Sort by name. Every client must derive the same slide from the same clock, and
        // that only holds if everyone sees the list in the same order — GitHub's ordering is
        // not contractual, so pin it here.
        images.Sort(StringComparer.OrdinalIgnoreCase);
        return images;
    }
}
