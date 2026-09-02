using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace DesCast;

/// <summary>
/// Downloaded bytes, kept on disk between sessions.
///
/// ⭐⭐ The third tier, and the one that was missing. A picture must be in video memory to be
/// drawn, and video memory is released once no room wants it — which without this means
/// walking out for a duty and back downloads everything again. Disk is the tier that makes
/// eviction free: **download once, ever.**
///
/// ⚠⚠ This matters to a specific person rather than in the abstract. Q is on metered
/// internet, so paying repeatedly for the same picture is a real cost to him and an invisible
/// one to everybody else. Chris, 2026-09-02, raising it as due diligence before it bit
/// anyone.
///
/// ⭐ Not optional, deliberately. It spends the one resource nobody is short of to save the
/// one somebody is, and "would you like fewer downloads?" is not a question anybody can
/// answer better than we can.
/// </summary>
internal sealed class DownloadCache
{
    private readonly DirectoryInfo root;

    /// <summary>
    /// ⚠ A ceiling so a heavy year of boards cannot quietly fill a disk. Oldest-used go
    /// first. Generous — this is cheap storage, and the whole point is not re-downloading.
    /// </summary>
    private const long MaxBytes = 512L * 1024 * 1024;

    /// <summary>
    /// How long a cached file is trusted without asking the server about it.
    ///
    /// ⭐ Short, because asking is nearly free: with a stored validator the question costs a
    /// few hundred bytes and the answer is usually "unchanged". This is the interval at which
    /// we *check*, not the interval at which we re-download.
    /// </summary>
    private static readonly TimeSpan Freshness = TimeSpan.FromHours(6);

    private sealed class Meta
    {
        public string Url = string.Empty;
        public string? ETag;
        public string? LastModified;
        public DateTimeOffset CheckedAt;
    }

    public DownloadCache(DirectoryInfo configDirectory)
    {
        root = new DirectoryInfo(Path.Combine(configDirectory.FullName, "cache"));
    }

    /// <summary>
    /// ⭐ The file name is a hash of the URL, which Chris arrived at independently. It gives a
    /// name that is a legal filename whatever the URL contained, is stable across sessions,
    /// and collides with nothing.
    /// </summary>
    private static string KeyFor(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        var sb = new StringBuilder(64);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private string BodyPath(string key) => Path.Combine(root.FullName, key + ".bin");
    private string MetaPath(string key) => Path.Combine(root.FullName, key + ".json");

    /// <summary>
    /// Bytes for a URL, from disk when we have them and from the network when we do not.
    ///
    /// <paramref name="fetch"/> is given the stored validators and must return null to mean
    /// "the server said it has not changed" — at which point the cached copy is returned and
    /// nothing was transferred but a header.
    /// </summary>
    public async Task<byte[]> GetAsync(
        string url,
        Func<string?, string?, Task<(byte[]? Body, string? ETag, string? LastModified)>> fetch)
    {
        var key = KeyFor(url);
        var body = BodyPath(key);
        var meta = ReadMeta(key);

        byte[]? cached = null;
        if (meta != null && File.Exists(body))
        {
            try
            {
                cached = await File.ReadAllBytesAsync(body).ConfigureAwait(false);
            }
            catch
            {
                // A half-written or locked file is not worth a failure; fall through and
                // fetch. The download path is always able to answer.
                cached = null;
            }
        }

        // Inside the freshness window we do not even ask. This is the case that makes
        // walking back into a room cost nothing at all.
        if (cached != null && meta != null && DateTimeOffset.UtcNow - meta.CheckedAt < Freshness)
        {
            Touch(body);
            return cached;
        }

        var (fresh, etag, lastModified) = await fetch(
            cached != null ? meta?.ETag : null,
            cached != null ? meta?.LastModified : null).ConfigureAwait(false);

        // Null body means the server answered "not modified". Keep what we have and push
        // the check forward, so the next few hours are free again.
        if (fresh == null)
        {
            if (cached == null)
                throw new InvalidOperationException(
                    "The server reported no change, but nothing was cached to show.");

            if (meta != null)
            {
                meta.CheckedAt = DateTimeOffset.UtcNow;
                WriteMeta(key, meta);
            }

            Touch(body);
            return cached;
        }

        Store(key, url, fresh, etag, lastModified);
        return fresh;
    }

    private void Store(string key, string url, byte[] bytes, string? etag, string? lastModified)
    {
        try
        {
            if (!root.Exists) root.Create();

            File.WriteAllBytes(BodyPath(key), bytes);
            WriteMeta(key, new Meta
            {
                Url = url,
                ETag = etag,
                LastModified = lastModified,
                CheckedAt = DateTimeOffset.UtcNow,
            });

            Trim();
        }
        catch (Exception ex)
        {
            // ⚠ Never fail a picture because the cache could not be written. A full or
            // read-only disk should cost a repeated download, not a blank wall.
            Plugin.Log.Warning($"Could not cache '{url}': {ex.Message}");
        }
    }

    private Meta? ReadMeta(string key)
    {
        try
        {
            var path = MetaPath(key);
            if (!File.Exists(path)) return null;
            return Newtonsoft.Json.JsonConvert.DeserializeObject<Meta>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private void WriteMeta(string key, Meta meta)
    {
        try
        {
            if (!root.Exists) root.Create();
            File.WriteAllText(MetaPath(key), Newtonsoft.Json.JsonConvert.SerializeObject(meta));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Could not write cache metadata: {ex.Message}");
        }
    }

    /// <summary>
    /// Mark a file as recently used, since <see cref="Trim"/> evicts by last write time.
    /// ⚠ Best effort — if it fails, the worst case is a still-wanted file being evicted a
    /// little early, which costs one download.
    /// </summary>
    private static void Touch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
    }

    private void Trim()
    {
        try
        {
            if (!root.Exists) return;

            var files = new List<FileInfo>(root.GetFiles("*.bin"));
            long total = 0;
            foreach (var f in files) total += f.Length;
            if (total <= MaxBytes) return;

            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

            foreach (var f in files)
            {
                if (total <= MaxBytes) break;
                total -= f.Length;

                var key = Path.GetFileNameWithoutExtension(f.Name);
                try { f.Delete(); } catch { }
                try { File.Delete(MetaPath(key)); } catch { }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Could not trim the download cache: {ex.Message}");
        }
    }

    /// <summary>Bytes and file count currently held, for the editor to show.</summary>
    public (long Bytes, int Files) Size()
    {
        try
        {
            if (!root.Exists) return (0, 0);

            long total = 0;
            var files = root.GetFiles("*.bin");
            foreach (var f in files) total += f.Length;
            return (total, files.Length);
        }
        catch
        {
            return (0, 0);
        }
    }

    public void Clear()
    {
        try
        {
            if (root.Exists) root.Delete(recursive: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Could not clear the download cache: {ex.Message}");
        }
    }
}
