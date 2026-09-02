using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace DesCast;

/// <summary>
/// Getting pictures onto the GPU without spending more memory than they are worth.
///
/// ⚠⚠ <b>The file format has nothing to do with what a picture costs on the graphics card.</b>
/// A 400 KB JPEG and a 12 MB PNG of the same photograph both become the same thing the
/// moment they are decoded: width × height × four bytes, uncompressed. Compression is
/// undone before the GPU ever sees it. Choosing JPEG saves download time and not one byte
/// of video memory.
///
/// ⭐⭐ <b>The only lever that moves that number is resolution</b>, and there is a great deal
/// of slack in it. A screen on a wall covers perhaps a thousand pixels of somebody's
/// monitor. A 6-megapixel phone photo carries several times more detail than can ever
/// reach an eye through that panel, and charges 24 MB for the privilege. Halving each side
/// quarters the cost and changes nothing anyone can see.
/// </summary>
internal static class ImageDecode
{
    /// <summary>
    /// Longest edge we will keep, by default.
    ///
    /// ⭐ 2048 is deliberately generous: a screen filling half a 4K display is around 2000
    /// pixels across, so this is "as much detail as the biggest reasonable case can show"
    /// rather than a compromise. It costs about 9 MB for a 16:9 picture rather than the
    /// 24 MB a modern phone photo arrives at.
    /// </summary>
    public const int DefaultMaxEdge = 2048;

    /// <summary>
    /// Decode and shrink, or return null to say "let Dalamud handle this one".
    ///
    /// Null comes back in two quite different cases, and both are correct:
    /// <list type="bullet">
    /// <item>the picture is already within <paramref name="maxEdge"/>, so there is nothing
    /// to gain and no reason to take it off the well-tested path;</item>
    /// <item>⚠ GDI+ cannot read it. It does not know WebP, which albums allow, so this is
    /// a real case and not a theoretical one. Dalamud's own decoder is broader than ours;
    /// falling back to it means an unusual format still works, just at full size.</item>
    /// </list>
    /// </summary>
    public static byte[]? TryDownscale(byte[] bytes, int maxEdge, out int width, out int height,
                                       out int sourceWidth, out int sourceHeight)
    {
        width = height = sourceWidth = sourceHeight = 0;
        if (maxEdge <= 0) return null;

        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var image = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);

            sourceWidth = image.Width;
            sourceHeight = image.Height;

            var longest = Math.Max(sourceWidth, sourceHeight);
            if (longest <= maxEdge || longest == 0) return null;

            var scale = maxEdge / (float)longest;
            width = Math.Max(1, (int)MathF.Round(sourceWidth * scale));
            height = Math.Max(1, (int)MathF.Round(sourceHeight * scale));

            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.Clear(Color.Transparent);
            g.DrawImage(image, new Rectangle(0, 0, width, height));

            return ToBgra(bmp, width, height);
        }
        catch
        {
            // Unreadable by GDI+. Not an error — the caller falls back.
            width = height = 0;
            return null;
        }
    }

    /// <summary>Raw BGRA bytes out of a bitmap, ready for the GPU.</summary>
    public static byte[] ToBgra(Bitmap bmp, int w, int h)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buffer = new byte[w * h * 4];

            // ⚠ Stride is not width×4 — GDI+ pads rows to a four-byte boundary, and copying
            // the block wholesale shears every image whose width is not a multiple of four.
            for (var y = 0; y < h; y++)
                Marshal.Copy(data.Scan0 + (y * data.Stride), buffer, y * w * 4, w * 4);

            return buffer;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>
    /// Upload BGRA bytes. Format32bppArgb is B,G,R,A in memory on a little-endian machine,
    /// which is exactly what Bgra32 wants — no channel swap needed.
    /// </summary>
    public static IDalamudTextureWrap Upload(byte[] bgra, int w, int h, string name)
        => Plugin.Textures.CreateFromRaw(RawImageSpecification.Bgra32(w, h), bgra, name);

    public static IDalamudTextureWrap Upload(Bitmap bmp, int w, int h, string name)
        => Upload(ToBgra(bmp, w, h), w, h, name);
}
