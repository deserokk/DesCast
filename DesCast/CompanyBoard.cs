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
/// ⭐⭐⭐ <b>The game prints the board into chat at login, so usually there is nothing to
/// do at all.</b> Spotted by Chris 2026-09-01 in a login screenshot, after an afternoon of
/// working around the agent. Listening costs nothing, sends nothing, and needs no window
/// opened — it simply arrives.
///
/// ⚠ The agent path is kept as a fallback for anyone who loads the plugin mid-session and
/// has therefore already missed the login message. It only works after the Free Company
/// window has been opened: measured, not assumed — cold the agent is empty,
/// <c>InfoProxyFreeCompany.RequestData()</c> returns false, and the whole company dataset
/// arrives together the moment that window opens.
///
/// ⭐ Either way the result is cached permanently, because <b>the board is a pointer and
/// pointers do not change.</b> It says where the manifest lives; the manifest itself is
/// refetched every few minutes over HTTP with no game involvement.
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

    public CompanyBoard(Configuration config)
    {
        this.config = config;
        Plugin.Chat.ChatMessage += this.OnChatMessage;
    }

    public void Dispose() => Plugin.Chat.ChatMessage -= this.OnChatMessage;

    /// <summary>
    /// The board as the game announces it at login.
    ///
    /// ⚠⚠ <b>Sender must be empty.</b> This is the whole security of the thing: the login
    /// announcement is a system message with no sender, while anything a player types
    /// always has one. Without that check, someone standing next to you saying
    /// "Company Board: Screens: &lt;their link&gt;" in open chat would put their pictures on
    /// your walls. Matching on the text alone is not enough.
    /// </summary>
    private void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage chat)
    {
        try
        {
            if (chat.Sender.TextValue.Length != 0) return;

            var text = chat.Message.TextValue;
            if (text.IndexOf("company board", StringComparison.OrdinalIgnoreCase) < 0) return;

            Plugin.Log.Information($"Company board seen in chat (kind {chat.LogKind}).");
            Accept(text);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Company board chat read failed: {ex.Message}");
        }
    }

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

        Accept(raw);
    }

    /// <summary>Take board text from wherever it came and store anything useful in it.</summary>
    private void Accept(string raw)
    {
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
