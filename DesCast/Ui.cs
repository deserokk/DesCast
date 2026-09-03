using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DesCast;

/// <summary>Small drawing helpers shared by both windows.</summary>
internal static class Ui
{
    /// <summary>
    /// Coloured text that wraps. ⚠ Plain TextColored runs off the window edge, and the
    /// messages most worth reading are the long ones — an error clipped mid-sentence is
    /// barely better than no error at all.
    /// </summary>
    public static void ErrorText(string text, float indent = 0f)
    {
        if (indent > 0f) ImGui.Indent(indent);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.5f, 0.45f, 1f));
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
        if (indent > 0f) ImGui.Unindent(indent);
    }

    /// <summary>
    /// "1 screen", "2 screens". ⚠ Not "screen(s)" — that reads as a placeholder somebody
    /// forgot to finish, and it is one of the cheapest things that makes software feel unfinished.
    /// </summary>
    public static string Count(int n, string noun)
        => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    /// <summary>Trim a link for display; the middle of a paste URL carries no information.</summary>
    public static string Shorten(string url)
        => url.Length <= 46 ? url : url[..28] + "..." + url[^12..];

    public static void OpenLink(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Could not open {url}: {ex.Message}");
        }
    }
}
