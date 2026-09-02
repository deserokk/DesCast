using System;
using System.Collections.Generic;
using System.Numerics;
using Newtonsoft.Json;

namespace DesCast;

/// <summary>
/// Where a screen is, how big it is, and which house it belongs to.
///
/// ⭐ This is the whole placement model. A screen is a flat rectangle in world space —
/// a centre, a rotation, and a width/height in metres. Everything else in the plugin is
/// either producing pixels to put on it or deciding whether to draw it at all.
///
/// ⚠ World coordinates, deliberately, not interior-relative. Houses of the same size are
/// instances of the same map and share a coordinate space, so a screen placed by
/// coordinate alone would render inside every identically-sized interior on the server.
/// <see cref="Ward"/>/<see cref="Plot"/>/<see cref="Room"/> is what stops that, and it
/// also makes a house relocation a three-field edit rather than a re-placement job.
/// </summary>
[Serializable]
public class ScreenPlacement
{
    /// <summary>Shown in the editor list. Purely for the human.</summary>
    public string Name { get; set; } = "Screen";

    /// <summary>Centre of the panel, in world coordinates.</summary>
    public Vector3 Position { get; set; } = Vector3.Zero;

    /// <summary>
    /// Yaw / pitch / roll in degrees. Yaw alone covers almost every real placement —
    /// a screen on a wall is a vertical rectangle turned to face into the room.
    /// </summary>
    public Vector3 RotationDegrees { get; set; } = Vector3.Zero;

    /// <summary>Panel size in metres. A character is roughly 1.8 tall, for scale.</summary>
    public float Width { get; set; } = 3.0f;

    /// <summary>
    /// Panel height in metres. 16:9 against the default width.
    /// ⚠ Ignored while <see cref="FitToImage"/> is on — see <see cref="HeightFor"/>.
    /// </summary>
    public float Height { get; set; } = 1.6875f;

    /// <summary>
    /// Derive height from the image's own proportions instead of <see cref="Height"/>,
    /// keeping <see cref="Width"/> as the anchor. On for a panel that should show a
    /// picture undistorted; ⭐ off for a fixture whose size is part of the furniture —
    /// an upright conference board stays the same shape whatever poster is on it today.
    /// </summary>
    public bool FitToImage { get; set; } = true;

    public bool Enabled { get; set; } = true;

    /// <summary>0 = invisible, 1 = solid. Multiplies the whole panel.</summary>
    public float Opacity { get; set; } = 1.0f;

    // ── Which house this belongs to ───────────────────────────────────────────────

    /// <summary>
    /// ⭐⭐ The game's own house identifier, and the whole identity of a placement.
    ///
    /// This single 64-bit value packs the **world**, the **district** (its territory type
    /// — Empyreum and Mist are different numbers), the ward index, the plot index and the
    /// room. That matters more than it sounds: ward 2 plot 47 exists in every district on
    /// every server, so matching on ward and plot alone would have rendered this screen
    /// inside a stranger's house on another world. ⚠ 0 means unset, which renders nowhere.
    /// </summary>
    public ulong HouseId { get; set; }

    // ⚠ Legacy identity from the first build, kept only so screens placed before HouseId
    // existed can be recognised once and upgraded in place. Delete these three once no
    // config in the wild still carries them — they are dead the moment HouseId is stamped.
    public short Ward { get; set; } = -1;
    public short Plot { get; set; } = -1;
    public short Room { get; set; } = -1;

    /// <summary>
    /// ⚠ Legacy single-image field, superseded by <see cref="Sources"/>. Kept only so an
    /// existing screen migrates instead of going blank; delete once no config carries it.
    /// </summary>
    public string ImagePath { get; set; } = string.Empty;

    /// <summary>
    /// What this screen shows, in order. A file path or an https URL; one entry is a
    /// static sign, several is a slideshow. Empty shows the test card.
    ///
    /// ⚠⚠ <c>ObjectCreationHandling.Replace</c> — without it Newtonsoft appends the saved
    /// entries to whatever the initialiser already put here, so the list grows by its own
    /// length on every load, silently, forever.
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> Sources { get; set; } = new();

    /// <summary>Seconds each slide is held before the next one.</summary>
    public float DwellSeconds { get; set; } = 15f;

    /// <summary>
    /// Which slide is showing, derived from the wall clock rather than from a timer we
    /// started.
    ///
    /// ⭐⭐ This is the whole reason a slideshow needs no synchronisation. Every client
    /// computes the same index from the same clock and the same list, so eight people
    /// standing in the room see the same poster without a single message passing between
    /// them. Same input, same output — the principle the whole project is built on.
    /// </summary>
    public int SlideIndexAt(DateTimeOffset now)
    {
        if (Sources.Count <= 1) return 0;
        var dwell = MathF.Max(DwellSeconds, 1f);
        return (int)(now.ToUnixTimeMilliseconds() / 1000.0 / dwell % Sources.Count);
    }

    /// <summary>The source showing right now, or empty for the test card.</summary>
    public string CurrentSource(DateTimeOffset now)
        => Sources.Count == 0 ? string.Empty : Sources[SlideIndexAt(now)];

    /// <summary>
    /// The one after it. ⚠ Fetched early on purpose — a slide change that begins its
    /// download at the moment it becomes visible shows the test card for a beat, which
    /// reads as a glitch rather than a slideshow.
    /// </summary>
    public string NextSource(DateTimeOffset now)
        => Sources.Count <= 1 ? string.Empty : Sources[(SlideIndexAt(now) + 1) % Sources.Count];

    /// <summary>
    /// Fold a pre-slideshow screen into <see cref="Sources"/>. ⭐ A migration, not a
    /// default: a changed initialiser cannot reach a config that already exists.
    /// </summary>
    public bool MigrateSources()
    {
        if (Sources.Count > 0 || string.IsNullOrWhiteSpace(ImagePath)) return false;
        Sources.Add(ImagePath);
        ImagePath = string.Empty;
        return true;
    }

    /// <summary>
    /// Rotation as a matrix. System.Numerics is row-vector, which matches both the
    /// game's convention and the <c>row_major</c> declaration in the shader.
    /// </summary>
    public Matrix4x4 RotationMatrix => Matrix4x4.CreateFromYawPitchRoll(
        RotationDegrees.Y * MathF.PI / 180f,
        RotationDegrees.X * MathF.PI / 180f,
        RotationDegrees.Z * MathF.PI / 180f);

    /// <summary>Half-width vector: centre + this = middle of the right edge.</summary>
    public Vector3 AxisX => Vector3.Transform(Vector3.UnitX * (Width * 0.5f), RotationMatrix);

    /// <summary>Half-height vector: centre + this = middle of the top edge.</summary>
    public Vector3 AxisY => Vector3.Transform(Vector3.UnitY * (Height * 0.5f), RotationMatrix);

    /// <summary>
    /// The height actually used this frame, given the image's shape.
    /// <paramref name="imageAspect"/> is width ÷ height; pass 0 when it is not known yet
    /// (still decoding, or no image), in which case the stored height stands.
    ///
    /// ⚠ Deliberately computed, never written back to <see cref="Height"/>. Fitting is a
    /// display decision; persisting it would silently destroy a size the user set by hand
    /// the moment they pointed the panel at a differently-shaped picture.
    /// </summary>
    public float HeightFor(float imageAspect)
        => FitToImage && imageAspect > 0.0001f ? Width / imageAspect : Height;

    /// <summary>Half-height vector using <see cref="HeightFor"/>.</summary>
    public Vector3 AxisYFor(float imageAspect)
        => Vector3.Transform(Vector3.UnitY * (HeightFor(imageAspect) * 0.5f), RotationMatrix);

    /// <summary>
    /// Points out of the front face. Used only to decide whether we are looking at the
    /// back of the panel.
    /// </summary>
    public Vector3 Normal => Vector3.Normalize(Vector3.Cross(AxisX, AxisY));

    /// <summary>
    /// Face the panel at a point — used by "place in front of me", so a new screen
    /// arrives already turned toward the character rather than edge-on and invisible.
    /// </summary>
    public void FaceToward(Vector3 target)
    {
        var d = target - Position;
        d.Y = 0f; // keep screens upright; a tilted TV is almost never what is wanted
        if (d.LengthSquared() < 1e-6f) return;
        d = Vector3.Normalize(d);
        RotationDegrees = new Vector3(0f, MathF.Atan2(d.X, d.Z) * 180f / MathF.PI, 0f);
    }

    public ScreenPlacement Clone() => (ScreenPlacement)MemberwiseClone();
}
