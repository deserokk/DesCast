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
    /// ⚠⚠ An hour, raised from five minutes on 2026-09-02, and the reason is somebody
    /// specific: Q is on metered internet. Pictures are paid for once and then cached, but a
    /// listing check repeats for as long as anyone stands in the room — so over an evening
    /// the polling costs more than the pictures do. It is the ongoing cost, not the one-off,
    /// that deserved the attention.
    ///
    /// ⭐ What makes an hour acceptable is <c>/descast refresh</c>. The automatic interval
    /// only has to cover "eventually"; the case that actually wants immediacy — a poster
    /// going up while people are stood there looking at the wall — is somebody deciding to
    /// look, and they can say so. Chris' design.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    private sealed class Entry
    {
        public IReadOnlyList<string> Images = Array.Empty<string>();
        public DateTimeOffset LastAttempt = DateTimeOffset.MinValue;
        public bool Loaded;
        public bool Fetching;
        public string? Error;
    }

    private readonly Dictionary<string, Entry> cache = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Check every album again on the next frame. For <c>/descast refresh</c> — see the
    /// interval above for why this exists rather than being unnecessary.
    /// </summary>
    public void RefreshNow()
    {
        foreach (var entry in cache.Values) entry.LastAttempt = DateTimeOffset.MinValue;
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
    /// Imgur's album contents, without an API key.
    ///
    /// ⚠⚠ **Imgur has closed public API registration.** Their documented endpoint for it now
    /// redirects to the homepage, and an account's Applications page lists only apps you have
    /// authorised — there is no "create" anywhere. So the documented route is not available to
    /// new users at all, and a client-id setting would have been a field nobody could fill.
    ///
    /// ⭐ The public embed page carries the album inline and needs nothing. Verified against a
    /// real album 2026-09-01: five images, five hash/ext pairs, no spurious matches, and the
    /// derived links serve real image bytes.
    ///
    /// ⚠ This is a page rather than a documented API, so it can change without notice. It fails
    /// the way everything else here does — visibly, with the last good listing kept — rather than
    /// by going blank, and a GitHub folder remains the stable alternative.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ImgurAsync(string albumId)
    {
        var html = await Plugin.FetchTextAsync(
            $"https://imgur.com/a/{albumId}/embed?pub=true",
            expectHtml: true,
            ("Accept", "text/html")).ConfigureAwait(false);

        var images = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Each image appears as a hash with its extension a short way behind it. Bounded
        // lookahead so a malformed page cannot make this scan the whole document per match.
        foreach (Match m in Regex.Matches(
                     html,
                     @"""hash""\s*:\s*""([A-Za-z0-9]+)"".{0,300}?""ext""\s*:\s*""([^""]*)""",
                     RegexOptions.Singleline))
        {
            var hash = m.Groups[1].Value;
            if (!seen.Add(hash)) continue;

            // ⚠ Extensions come through as ".jpg?1" on edited images; anything past the
            // question mark is a cache-buster and breaks the link if kept.
            var ext = m.Groups[2].Value.Split('?')[0];
            if (ext.Length == 0) ext = ".jpg";

            images.Add($"https://i.imgur.com/{hash}{ext}");
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
