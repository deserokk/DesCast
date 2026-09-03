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

    /// <summary>
    /// Named shapes, so an officer can be told "that board is 9:16, crop to fit" and know
    /// what to make without measuring anything.
    ///
    /// ⭐ The paper sizes are there because a letter or notice laid on a table is a real use
    /// and nobody knows A4 as a ratio. ⚠ Kept short on purpose — a list of thirty is a list
    /// nobody reads, and the free width and height fields cover anything unusual.
    /// </summary>
    public static readonly (string Name, float W, float H)[] AspectPresets =
    {
        // ⭐⭐ The object first, the ratio second. "16:9" means nothing to somebody who does
        // not already know it; "Widescreen TV" means something to everyone. The numbers stay
        // because an officer running a board still needs to tell contributors what to crop
        // to — they are just no longer the part you have to understand.
        ("Widescreen TV  (16:9)",      16f, 9f),
        ("Cinema screen  (21:9)",      21f, 9f),
        ("Old TV  (4:3)",               4f, 3f),
        ("Square  (1:1)",               1f, 1f),
        ("Long banner  (3:1)",          3f, 1f),
        ("Phone / standee  (9:16)",     9f, 16f),
        ("Tall picture  (3:4)",         3f, 4f),
        ("Movie poster  (2:3)",         2f, 3f),
        ("Sheet of paper, upright",   210f, 297f),
        ("Sheet of paper, sideways",  297f, 210f),
        ("US Letter, upright",        8.5f, 11f),
    };

    /// <summary>
    /// The panel's shape as a ratio, for display. ⭐ So the officer running the board can read
    /// off what to hand people rather than being told to work it out.
    /// </summary>
    public string DescribeAspect(float imageAspect)
    {
        var h = HeightFor(imageAspect);
        if (h <= 0.0001f) return "—";

        var ratio = Width / h;

        // Name it if it matches a preset closely enough; a hand-set size lands between them.
        foreach (var (name, pw, ph) in AspectPresets)
            if (MathF.Abs(ratio - pw / ph) < 0.01f) return name;

        return $"{ratio:0.00} : 1";
    }

    /// <summary>How a picture is mapped onto a panel that is not the same shape.</summary>
    public enum Fitting
    {
        /// <summary>Distort to fill the panel exactly. ⚠ Almost never what anyone wants.</summary>
        Stretch,

        /// <summary>Keep the shape and crop the overflow. ⭐ The right default.</summary>
        Fill,

        /// <summary>Keep the shape and leave the rest empty.</summary>
        Letterbox,
    }

    /// <summary>
    /// What to do when the picture and the panel are different shapes.
    ///
    /// ⭐ Matters most for a fixed-size fixture — an in-game picture frame you are filling,
    /// or a notice board whose dimensions are part of the furniture. With
    /// <see cref="FitToImage"/> on, the panel takes the picture's shape and this never
    /// comes up; with it off, this decides between a distorted picture and a cropped one.
    /// </summary>
    public Fitting Fit { get; set; } = Fitting.Fill;

    /// <summary>Contrast. 1 leaves it alone; below flattens, above hardens.</summary>
    public float Contrast { get; set; } = 1.0f;

    /// <summary>Colour strength. 1 is unchanged, 0 is greyscale.</summary>
    public float Saturation { get; set; } = 1.0f;

    /// <summary>
    /// Multiplied into the picture's colour. ⭐ Warm for lamplight, cold for a hologram.
    /// </summary>
    public Vector3 Tint { get; set; } = Vector3.One;

    /// <summary>
    /// How far the panel stands out from its backing, in metres. 0 is a flat picture.
    ///
    /// ⭐⭐ Chris' reasoning, and it is the point of the feature: a flat image held off a wall
    /// reads as a decal hovering in space, so the offset needed to stop it clipping through
    /// scenery looks like a mistake. Give it an edge and it becomes a mounted plaque —
    /// standing proud of the wall is simply how a hung object sits, and the offset stops
    /// being a workaround and becomes the look.
    ///
    /// ⚠ Small values. Two or three centimetres is a picture frame; ten is a crate.
    /// </summary>
    public float Thickness { get; set; } = 0f;

    /// <summary>Colour of the sides. ⭐ Dark by default, the way a frame or bezel reads.</summary>
    public Vector3 EdgeColour { get; set; } = new(0.10f, 0.10f, 0.11f);

    /// <summary>
    /// Fades the outer edge of the panel, as a fraction of its size.
    ///
    /// ⭐ Small values do a lot. A hard-edged rectangle reads as a sticker pasted onto the
    /// world; a few percent of softness is the cheapest thing available for making a screen
    /// look placed rather than stuck on.
    /// </summary>
    public float EdgeSoftness { get; set; } = 0f;

    /// <summary>
    /// Scales the picture's colour. 1 leaves it alone, below 1 dims it, above 1 lifts it.
    ///
    /// ⭐⭐ Separate from <see cref="Opacity"/> on purpose, and they are not interchangeable.
    /// Opacity blends the panel *into* the scene, so dimming with it makes the picture
    /// translucent and washed out — you can see the wall through it. Brightness scales the
    /// colour while the panel stays solid, which is what a bright photo hanging in a dim
    /// room actually needs. ⭐ Asked for by Bunny and Q, who both reached for it
    /// independently after finding opacity did the wrong thing.
    /// </summary>
    public float Brightness { get; set; } = 1.0f;

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

    /// <summary>How one slide gives way to the next.</summary>
    public enum Transition
    {
        Cut,
        Crossfade,
        WipeDown,
        WipeUp,
        WipeRight,
        WipeLeft,
    }

    /// <summary>
    /// ⭐ Both slides are already downloaded and resident — the next one is prefetched so a
    /// change never flashes the placeholder — so a transition costs a second texture lookup
    /// and nothing else.
    /// </summary>
    public Transition Change { get; set; } = Transition.Crossfade;

    /// <summary>
    /// How long the change takes.
    ///
    /// ⚠⚠ Derived from the wall clock like the slide index itself, never from a timer this
    /// client started. That is what keeps a room in step: everyone crosses over during the
    /// same real seconds, so nobody sees a fade the person beside them has already finished.
    /// </summary>
    public float ChangeSeconds { get; set; } = 0.8f;

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
    /// How far through a change we are, 0 to 1, for a rotation of <paramref name="slideCount"/>.
    /// 0 means settled on the current slide.
    ///
    /// ⭐ Position within the dwell comes from the clock, so this is identical on every client
    /// to the millisecond — the same property that lets slides agree without messages.
    /// </summary>
    public float ChangeProgressAt(DateTimeOffset now, int slideCount)
    {
        if (slideCount <= 1 || Change == Transition.Cut) return 0f;

        var dwell = MathF.Max(DwellSeconds, 1f);
        var fade = Math.Clamp(ChangeSeconds, 0f, dwell * 0.9f);
        if (fade <= 0.001f) return 0f;

        var seconds = now.ToUnixTimeMilliseconds() / 1000.0;
        var into = (float)(seconds % dwell);

        // The change happens at the end of a slide's turn, so the new one has arrived by the
        // time the index moves on.
        var start = dwell - fade;
        return into < start ? 0f : Math.Clamp((into - start) / fade, 0f, 1f);
    }

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
