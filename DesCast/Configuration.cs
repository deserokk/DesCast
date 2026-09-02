using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Newtonsoft.Json;

namespace DesCast;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// Every screen the user has placed, across every house. Filtered down to the
    /// current location at draw time rather than stored per-location, so moving house
    /// is an edit rather than a migration.
    ///
    /// ⚠⚠ <c>ObjectCreationHandling.Replace</c> is not optional. Newtonsoft's default is
    /// Auto, which sees a property whose getter already returns a non-null collection and
    /// <b>appends</b> the JSON items to it instead of replacing — so any persisted list
    /// with an initialiser grows by its own length on every single load, silently and
    /// forever. This cost DeserokUtils 21 copies of every city before anyone noticed.
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<ScreenPlacement> Screens { get; set; } = new();

    /// <summary>Master switch. Nothing renders when this is off.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// ⚠ Legacy single-manifest field, superseded by <see cref="ManifestUrls"/>. Kept only
    /// so an existing subscription migrates instead of silently dropping.
    /// </summary>
    public string ManifestUrl { get; set; } = string.Empty;

    /// <summary>
    /// Shared room definitions to subscribe to. ⭐⭐ A list rather than one, because a house
    /// is not one shared space: the Free Company hall is published by officers, while each
    /// private room belongs to whoever lives in it. Subscribing to several and merging them
    /// lets both exist without anyone needing edit rights over the other.
    ///
    /// ⭐ Entries are scoped by house id, which includes the room number, so a manifest can
    /// only place screens in rooms its own entries name — the FC hall file cannot put
    /// anything in someone's bedroom.
    ///
    /// ⚠⚠ ObjectCreationHandling.Replace, or Newtonsoft appends to the initialiser on every
    /// load and the list grows by its own length forever.
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> ManifestUrls { get; set; } = new();

    /// <summary>
    /// Member room files kept for the company manifest builder.
    ///
    /// ⚠ Purely a convenience list for whoever assembles the company file — these are not
    /// subscribed to. Subscribing happens through the company file's own include list, so
    /// this is a notepad rather than a second source of truth.
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> BuilderEntries { get; set; } = new();

    /// <summary>
    /// Screen links found on the Free Company board, cached.
    ///
    /// ⭐ Kept separate from <see cref="ManifestUrls"/> rather than merged into it. The
    /// board's list belongs to the officers and is replaced wholesale whenever the board
    /// changes; the user's own list is theirs. Merging would mean a board edit silently
    /// deleting somebody's private-room subscription.
    ///
    /// ⚠ Cached deliberately, because the board is only readable after the Free Company
    /// window has been opened — so this must survive the session that read it.
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> CompanyBoardUrls { get; set; } = new();

    /// <summary>
    /// When the board was last read. Null means never — which is a different message to
    /// the user than "read it, and there was nothing on it".
    /// </summary>
    public DateTimeOffset? CompanyBoardSeenAt { get; set; }

    /// <summary>
    /// Fold a single-manifest config into the list. ⭐ A migration, not a default — a
    /// changed initialiser cannot reach a config that already exists.
    /// </summary>
    public bool MigrateManifestUrls()
    {
        if (ManifestUrls.Count > 0 || string.IsNullOrWhiteSpace(ManifestUrl)) return false;
        ManifestUrls.Add(ManifestUrl.Trim());
        ManifestUrl = string.Empty;
        return true;
    }

    /// <summary>
    /// ⚠ Whether the game's depth buffer uses reverse-Z (near = 1.0, far = 0.0).
    /// Modern D3D11 titles usually do, so that is the default — but getting it backwards
    /// produces an unmistakable symptom rather than a subtle one: the panel is either
    /// visible through every wall in the house, or invisible everywhere. Exposed as a
    /// toggle so the answer can be established in one click in-game instead of guessed
    /// at here.
    /// </summary>
    public bool ReverseDepth { get; set; } = true;

    /// <summary>
    /// Draw the panel with no depth test at all. Useful once, to separate "the geometry
    /// is wrong" from "the occlusion is wrong" — if a screen appears with this on and
    /// vanishes with it off, placement is fine and <see cref="ReverseDepth"/> is the
    /// suspect.
    /// </summary>
    public bool DisableOcclusion { get; set; } = false;

    /// <summary>
    /// Keep screens from drawing over the game's own interface.
    ///
    /// ⚠ On by default, because a screen covering your hotbars is worse than a screen with
    /// a rectangular bite out of it. A setting rather than a rule because the culling uses
    /// bounding boxes, so it over-covers slightly — someone framing a screenshot may prefer
    /// the picture intact and the interface hidden anyway.
    /// </summary>
    public bool AvoidGameUi { get; set; } = true;

    /// <summary>Show the placement editor on load. Off by default once things settle.</summary>
    /// <summary>
    /// Longest edge a picture is kept at, in pixels. 0 keeps whatever arrives.
    ///
    /// ⚠⚠ This, and nothing else, is what a room costs in video memory. The file format
    /// is irrelevant — a JPEG and a PNG of the same photograph decode to exactly the same
    /// width × height × 4 bytes, because compression is undone before the GPU sees it.
    ///
    /// ⭐ A phone photograph arrives at six megapixels and charges 24 MB for detail no wall
    /// panel can display. Capping the long edge is a straight division with nothing visible
    /// given up, which is why the default is not "off".
    /// </summary>
    public int MaxImageEdge { get; set; } = ImageDecode.DefaultMaxEdge;

    public bool OpenOnLoad { get; set; } = true;

    [NonSerialized] private IDalamudPluginInterface? pluginInterface;

    public void Initialise(IDalamudPluginInterface pi) => pluginInterface = pi;

    public void Save() => pluginInterface!.SavePluginConfig(this);
}
