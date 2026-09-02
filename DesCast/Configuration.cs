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
    /// URL of the shared room definition — a JSON file listing screens and what they show.
    /// ⭐ This is what makes screens exist for other people: everyone pointed at the same
    /// URL sees the same room, with no player needing to be online for it to be there.
    /// Empty means local screens only.
    /// </summary>
    public string ManifestUrl { get; set; } = string.Empty;

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

    /// <summary>Show the placement editor on load. Off by default once things settle.</summary>
    public bool OpenOnLoad { get; set; } = true;

    [NonSerialized] private IDalamudPluginInterface? pluginInterface;

    public void Initialise(IDalamudPluginInterface pi) => pluginInterface = pi;

    public void Save() => pluginInterface!.SavePluginConfig(this);
}
