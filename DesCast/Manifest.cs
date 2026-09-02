using System;
using System.Collections.Generic;
using System.Numerics;
using Newtonsoft.Json;

namespace DesCast;

/// <summary>
/// A shared room definition, fetched from a URL. ⭐⭐ This is the thing that turns a
/// personal plugin into the FC's noticeboard: placement stops living in one person's
/// config, so a screen exists for everyone who walks in rather than only for whoever
/// placed it.
///
/// ⚠⚠ Deliberately <b>not</b> tied to any player being online. Xiv Media Player derives
/// screen positions from the host's presence, so when the host leaves there are no
/// screens. The manifest is the room; nobody owns it.
/// </summary>
public sealed class Manifest
{
    [JsonProperty("version")] public int Version { get; set; } = 1;

    [JsonProperty("screens", ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<ManifestScreen> Screens { get; set; } = new();
}

public sealed class ManifestScreen
{
    /// <summary>Stable identifier for the entry. Only used in messages about it.</summary>
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;

    [JsonProperty("name")] public string Name { get; set; } = "Screen";

    /// <summary>
    /// The game's house id, as a <b>string</b>.
    ///
    /// ⚠ A string on purpose: this is a 64-bit number, and anything editing JSON with a
    /// JavaScript engine behind it — which is most web tooling — silently mangles integers
    /// past 2^53. A house id that quietly loses its last digits points at no house at all,
    /// and the symptom is "my screens just don't appear anywhere", with nothing to see in
    /// the file. Quoting it removes the whole class of problem.
    /// </summary>
    [JsonProperty("house")] public string House { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable location, written by the exporter and ignored on read. Purely so
    /// somebody editing the file can tell which entry is which without decoding a number.
    /// </summary>
    [JsonProperty("where")] public string Where { get; set; } = string.Empty;

    [JsonProperty("pos")] public float[] Pos { get; set; } = new float[3];
    [JsonProperty("rot")] public float[] Rot { get; set; } = new float[3];

    [JsonProperty("width")] public float Width { get; set; } = 3f;
    [JsonProperty("height")] public float Height { get; set; } = 1.6875f;

    /// <summary>Height follows the picture's shape; see <see cref="ScreenPlacement.FitToImage"/>.</summary>
    [JsonProperty("fit")] public bool Fit { get; set; } = true;

    [JsonProperty("opacity")] public float Opacity { get; set; } = 1f;

    /// <summary>Colour multiplier; 1 leaves the picture alone.</summary>
    [JsonProperty("brightness")] public float Brightness { get; set; } = 1f;
    [JsonProperty("contrast")] public float Contrast { get; set; } = 1f;
    [JsonProperty("saturation")] public float Saturation { get; set; } = 1f;
    [JsonProperty("edge")] public float EdgeSoftness { get; set; }

    /// <summary>Colour multiplier as [r, g, b]; omit for none.</summary>
    [JsonProperty("tint")] public float[]? Tint { get; set; }

    /// <summary>"fill", "letterbox" or "stretch".</summary>
    [JsonProperty("fitting")] public string Fitting { get; set; } = "fill";

    [JsonProperty("dwell")] public float Dwell { get; set; } = 15f;

    [JsonProperty("sources", ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> Sources { get; set; } = new();

    /// <summary>
    /// Convert to the same placement type local screens use, so the renderer and every
    /// draw-time decision stay identical whether a screen came from config or a manifest.
    /// Returns null when the entry names no valid house — ⚠ a screen that cannot say where
    /// it belongs renders nowhere rather than everywhere.
    /// </summary>
    public ScreenPlacement? ToPlacement()
    {
        if (!ulong.TryParse(House, out var houseId) || houseId == 0) return null;

        return new ScreenPlacement
        {
            Name = string.IsNullOrWhiteSpace(Name) ? "Screen" : Name,
            HouseId = houseId,
            Position = new Vector3(At(Pos, 0), At(Pos, 1), At(Pos, 2)),
            RotationDegrees = new Vector3(At(Rot, 0), At(Rot, 1), At(Rot, 2)),
            Width = Width,
            Height = Height,
            FitToImage = Fit,
            Opacity = Opacity,
            Brightness = Brightness,
            Contrast = Contrast,
            Saturation = Saturation,
            EdgeSoftness = EdgeSoftness,
            Tint = Tint is { Length: >= 3 } t ? new Vector3(t[0], t[1], t[2]) : Vector3.One,
            Fit = Fitting?.ToLowerInvariant() switch
            {
                "stretch" => ScreenPlacement.Fitting.Stretch,
                "letterbox" => ScreenPlacement.Fitting.Letterbox,
                _ => ScreenPlacement.Fitting.Fill,
            },
            DwellSeconds = Dwell,
            Sources = Sources ?? new List<string>(),
            Enabled = true,
        };
    }

    private static float At(float[]? a, int i) => a is not null && a.Length > i ? a[i] : 0f;

    /// <summary>Build an entry from a placed screen, for the export button.</summary>
    public static ManifestScreen From(ScreenPlacement s, string whereLabel) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..8],
        Name = s.Name,
        House = s.HouseId.ToString(),
        Where = whereLabel,
        Pos = new[] { s.Position.X, s.Position.Y, s.Position.Z },
        Rot = new[] { s.RotationDegrees.X, s.RotationDegrees.Y, s.RotationDegrees.Z },
        Width = s.Width,
        Height = s.Height,
        Fit = s.FitToImage,
        Opacity = s.Opacity,
        Brightness = s.Brightness,
        Contrast = s.Contrast,
        Saturation = s.Saturation,
        EdgeSoftness = s.EdgeSoftness,
        Tint = s.Tint == Vector3.One ? null : new[] { s.Tint.X, s.Tint.Y, s.Tint.Z },
        Fitting = s.Fit.ToString().ToLowerInvariant(),
        Dwell = s.DwellSeconds,
        Sources = new List<string>(s.Sources),
    };
}
