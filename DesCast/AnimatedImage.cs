using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace DesCast;

/// <summary>
/// A GIF, decoded into the frames a screen can actually show.
///
/// A GIF is "a series of images" in the same way a video is, which is to say almost: the
/// frames are usually stored as partial patches over whatever was on screen before, each
/// carrying a disposal rule (leave it, clear it, put the previous one back). Decode them
/// naively and you get a smear of half-frames.
///
/// ⭐ We do not implement any of that. GDI+ composites for us — SelectActiveFrame hands
/// back a finished frame — which is the whole reason this file is short. The same
/// dependency is what will rasterise text later, so it is not a new cost.
///
/// ⭐⭐ Playback is derived from the wall clock, exactly like the slideshow. Nobody sends
/// a message and nobody is in charge, and everyone standing in the room is on the same
/// frame of the same GIF. A reaction gif actually lands together.
/// </summary>
internal sealed class AnimatedImage : IDisposable
{
    /// <summary>
    /// ⚠ Frames never stream — every one is a texture resident for as long as the GIF is
    /// on a wall. This is the whole memory story, and why the budget below is not
    /// optional.
    /// </summary>
    public IDalamudTextureWrap[] Frames { get; private set; } = Array.Empty<IDalamudTextureWrap>();

    /// <summary>Running end time of each frame, milliseconds from the loop's start.</summary>
    private int[] endMs = Array.Empty<int>();

    public int TotalMs { get; private set; }
    public float Aspect { get; private set; }

    /// <summary>Video memory this GIF is holding, for the room's running total.</summary>
    public long Bytes { get; private set; }

    /// <summary>What had to be given up to fit the budget, or null if nothing did.</summary>
    public string? Compromise { get; private set; }

    /// <summary>
    /// Per-GIF ceiling. A meme at 400×300 costs about half a megabyte a frame, so this is
    /// roughly a hundred frames at that size — generous for anything anyone actually
    /// posts, and a hard stop on the ninety-second movie clip someone will inevitably try.
    /// </summary>
    public const long DefaultBudgetBytes = 48L * 1024 * 1024;

    /// <summary>
    /// ⚠ A ceiling independent of the byte budget. A tiny GIF with two thousand frames
    /// costs little memory but two thousand texture handles, and the driver cares about
    /// that even when the byte count says it should not.
    /// </summary>
    private const int MaxFrames = 300;

    /// <summary>
    /// ⚠ How soft we are willing to let a GIF get before dropping frames instead.
    /// Shrinking is the better trade a long way down — a slightly soft GIF still reads as
    /// the thing it is, while one missing every third frame reads as broken — but past
    /// this it is mush, and stutter becomes the lesser evil.
    /// </summary>
    private const float MinScale = 0.35f;

    /// <summary>Whether these bytes are a GIF at all. Cheap, so it gates the expensive test.</summary>
    public static bool IsGif(byte[] b)
        => b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8'
           && (b[4] == '7' || b[4] == '9') && b[5] == 'a';

    /// <summary>
    /// Decode, or return null if this is not an animated GIF — including a GIF with a
    /// single frame, which is just a picture and belongs on the ordinary path.
    /// ⚠ Runs off the render thread. It is slow by nature: every frame is composited,
    /// resized and uploaded.
    /// </summary>
    public static AnimatedImage? Decode(byte[] bytes, string name, long budgetBytes, int maxEdge)
    {
        if (!IsGif(bytes)) return null;

        using var ms = new MemoryStream(bytes, writable: false);
        using var image = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);

        var dim = FrameDimension.Time;
        var count = image.GetFrameCount(dim);
        if (count <= 1) return null;

        var delays = ReadDelays(image, count);

        // Size first, frames second — see MinScale.
        var (w0, h0) = (image.Width, image.Height);

        // ⭐ The same resolution cap stills get, applied before the budget rather than
        // instead of it. A short GIF at 2000 pixels across fits the byte budget comfortably
        // and is still several times more detail than a wall panel can show.
        var scale = 1f;
        var longest = Math.Max(w0, h0);
        if (maxEdge > 0 && longest > maxEdge) scale = maxEdge / (float)longest;

        var capped = scale;
        while (scale > MinScale * capped && FrameCost(w0, h0, scale) * count > budgetBytes)
            scale -= 0.05f * capped;

        var w = Math.Max(1, (int)(w0 * scale));
        var h = Math.Max(1, (int)(h0 * scale));
        var perFrame = (long)w * h * 4;

        var affordable = (int)Math.Max(2, budgetBytes / perFrame);
        var keep = Math.Min(count, Math.Min(affordable, MaxFrames));

        var result = new AnimatedImage { Aspect = h0 > 0 ? (float)w0 / h0 : 0f };

        var frames = new List<IDalamudTextureWrap>(keep);
        var ends = new List<int>(keep);
        var elapsed = 0;

        try
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // ⚠ SourceCopy, not the default: a GIF's transparent pixels must replace what
            // is in the bitmap rather than blend with the previous frame still sitting
            // there. Blending them is how you get a ghost of frame one under everything.
            g.CompositingMode = CompositingMode.SourceCopy;

            for (var k = 0; k < keep; k++)
            {
                // Which source frames this kept frame stands in for. Their delays are
                // summed into it, so dropping frames changes the smoothness and never the
                // duration — which is what keeps the wall clock honest and everyone in
                // the room on the same frame.
                var from = (int)((long)k * count / keep);
                var to = (int)((long)(k + 1) * count / keep);
                if (to <= from) to = from + 1;

                var span = 0;
                for (var s = from; s < to && s < count; s++) span += delays[s];

                image.SelectActiveFrame(dim, from);
                g.Clear(Color.Transparent);
                g.DrawImage(image, new Rectangle(0, 0, w, h));

                frames.Add(ImageDecode.Upload(bmp, w, h, $"DesCast gif {name} #{k}"));

                elapsed += Math.Max(span, 10);
                ends.Add(elapsed);
            }
        }
        catch
        {
            foreach (var f in frames) f.Dispose();
            throw;
        }

        result.Frames = frames.ToArray();
        result.endMs = ends.ToArray();
        result.TotalMs = Math.Max(elapsed, 1);
        result.Bytes = perFrame * frames.Count;

        // ⚠ Only report shrinking the *budget* forced. The resolution cap applies to every
        // picture in the plugin and saying so on each one would be noise.
        var squeezed = scale < capped * 0.999f;

        result.Compromise = (squeezed, keep < count) switch
        {
            (true, true) => $"Shrunk to {scale / capped:P0} and reduced to {keep} of {count} frames to fit the memory budget.",
            (true, false) => $"Shrunk to {scale / capped:P0} to fit the memory budget.",
            (false, true) => $"Reduced to {keep} of {count} frames to fit the memory budget.",
            _ => null,
        };

        return result;
    }

    private static long FrameCost(int w, int h, float scale)
        => (long)Math.Max(1, (int)(w * scale)) * Math.Max(1, (int)(h * scale)) * 4;

    /// <summary>
    /// Per-frame delays, in milliseconds.
    ///
    /// ⚠⚠ A delay of 0 or 10ms means "as fast as the machine can" in the format, and every
    /// browser on earth silently treats those as 100ms instead. Skip this clamp and a
    /// large share of real GIFs play at strobe speed — which looks like our bug, not
    /// theirs.
    /// </summary>
    private static int[] ReadDelays(Image image, int count)
    {
        var delays = new int[count];
        byte[]? raw = null;

        try
        {
            // 0x5100 is PropertyTagFrameDelay: one 32-bit hundredths-of-a-second per frame.
            raw = image.GetPropertyItem(0x5100)?.Value;
        }
        catch
        {
            // A GIF carrying no delay block at all is legal. Fall through to the default.
        }

        for (var i = 0; i < count; i++)
        {
            var off = i * 4;
            var hundredths = raw != null && off + 4 <= raw.Length
                ? BitConverter.ToInt32(raw, off)
                : 10;

            if (hundredths <= 1) hundredths = 10;
            delays[i] = hundredths * 10;
        }

        return delays;
    }

    /// <summary>
    /// Which frame is showing right now. Derived from the wall clock, so two people
    /// looking at the same GIF see the same frame without a single message passing between
    /// them.
    /// </summary>
    public int FrameAt(DateTimeOffset now)
    {
        if (Frames.Length <= 1 || TotalMs <= 0) return 0;

        var t = (int)(((now.ToUnixTimeMilliseconds() % TotalMs) + TotalMs) % TotalMs);

        // Linear, and deliberately so: frame counts are in the hundreds at worst and this
        // runs once per screen per frame. A binary search would be faster and harder to
        // read for a saving nothing can measure.
        for (var i = 0; i < endMs.Length; i++)
            if (t < endMs[i]) return i;

        return Frames.Length - 1;
    }

    public nint HandleAt(DateTimeOffset now)
        => Frames.Length == 0 ? 0 : (nint)Frames[FrameAt(now)].Handle.Handle;

    public void Dispose()
    {
        foreach (var f in Frames) f.Dispose();
        Frames = Array.Empty<IDalamudTextureWrap>();
        endMs = Array.Empty<int>();
        Bytes = 0;
    }
}
