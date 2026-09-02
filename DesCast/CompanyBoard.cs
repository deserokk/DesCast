using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace DesCast;

/// <summary>
/// Reads the Free Company board and takes any screen links out of it.
///
/// ⭐⭐ The board is the ideal place to publish where a company's screens live, and the
/// reason is that the game already solves every hard part: it is **server-persisted** by
/// Square, **readable by every member** with no distribution on our side, and
/// **editable only by ranks with the permission**, enforced by the game rather than by us.
/// No account, no token, no service, no config to hand around.
///
/// ⚠⚠ It is only readable once the Free Company window has been opened in this session.
/// Measured, not assumed: cold it is empty, <c>InfoProxyFreeCompany.RequestData()</c>
/// returns false, and the whole company dataset — roster, counts, board — arrives together
/// the moment the window opens. Forcing that would mean reverse-engineering native code to
/// find what gates the request.
///
/// ⭐ Which is not worth doing, because <b>the board is a pointer and pointers do not
/// change.</b> It says where the manifest lives; the manifest itself is refetched every
/// few minutes over HTTP with no game involvement. So this needs reading roughly once,
/// ever — we cache what we find and tell the user to open the window once if we have
/// nothing.
/// </summary>
internal sealed unsafe class CompanyBoard
{
    /// <summary>
    /// The tag looked for in the board text.
    ///
    /// ⭐ Deliberately bland. Chris' reasoning: the board is read by every member including
    /// people on a vanilla client, and it lives on Square's servers — so the line should
    /// look like a note somebody left, not like machine-readable configuration announcing
    /// third-party software. "Screens" and a short token admit nothing.
    /// </summary>
    private const string Tag = "screens:";

    private readonly Configuration config;
    private string lastSeenRaw = string.Empty;

    public CompanyBoard(Configuration config) => this.config = config;

    /// <summary>
    /// Called every frame. ⚠ Cheap on purpose — a null check and a length compare — because
    /// this sits on the draw path. The board is only re-parsed when its text actually
    /// changes, which is approximately never.
    /// </summary>
    public void Tick()
    {
        var agent = AgentFreeCompany.Instance();
        if (agent is null) return;

        var raw = agent->Board.ToString();
        if (string.IsNullOrEmpty(raw) || raw == lastSeenRaw) return;

        lastSeenRaw = raw;

        var found = Parse(raw);
        if (found.Count == 0)
        {
            // ⚠ Seen but with nothing in it is a real state, distinct from never seen — it
            // is the difference between "open your FC window" and "your officers have not
            // put a link on the board yet", which are different problems for the user.
            config.CompanyBoardSeenAt = DateTimeOffset.UtcNow;
            config.Save();
            return;
        }

        if (SameAsStored(found)) return;

        config.CompanyBoardUrls = found;
        config.CompanyBoardSeenAt = DateTimeOffset.UtcNow;
        config.Save();

        Plugin.Log.Information(
            $"Company board: picked up {found.Count} screen link(s).");
    }

    /// <summary>
    /// Pull the tokens following the tag out of free-form board text.
    ///
    /// ⚠ The board is a human notice that happens to contain a link, not a config file, so
    /// this is deliberately forgiving: the tag can be anywhere, in any case, and the rest
    /// of the board is ignored. Collection stops at the next word ending in a colon, so
    /// "Screens: 0GzA4vpc Discord: ZqGhQxfNah" takes the paste id and leaves the Discord
    /// invite alone.
    /// </summary>
    internal static List<string> Parse(string board)
    {
        var result = new List<string>();

        var at = board.IndexOf(Tag, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return result;

        var rest = board[(at + Tag.Length)..];
        foreach (var token in rest.Split(
                     new[] { ' ', '\t', '\r', '\n', ',', ';' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.EndsWith(':')) break;      // the next label — stop
            result.Add(token);
        }

        return result;
    }

    private bool SameAsStored(List<string> found)
    {
        var stored = config.CompanyBoardUrls;
        if (stored.Count != found.Count) return false;
        for (var i = 0; i < found.Count; i++)
            if (!string.Equals(stored[i], found[i], StringComparison.Ordinal)) return false;
        return true;
    }
}
