namespace RaCMAN.Protocol.Testing;

/// <summary>
/// DEBUG_TEXT (revision 1.29, PROTOCOL.md 5.16): a game's read-only debug pages. Like qwark, only a
/// ToD session answers, with its two level-cache pages, "Now" and the SLOW "Cache"; the text is a
/// stand-in with the console's shape, not the game's data.
/// </summary>
public sealed partial class FakeQwarkServer
{
    /// <summary>The pages a ToD session lists, in order.</summary>
    public List<DebugPage> DebugPages { get; } = new()
    {
        new DebugPage(0, false, "Now"),
        new DebugPage(1, true, "Cache"),
    };

    /// <summary>Each page's text, by its index; a page missing here answers an empty text.</summary>
    public Dictionary<byte, string> DebugTexts { get; } = new()
    {
        [0] = "Game state 4 (gameplay), level 2 (Stratus City)\n\nWarmer: on (level gate 1, movie gate 1, pending 0)\n"
            + "  file 1 of 4: /built/levels/stratus city/streaming_sound.dat\n",
        [1] = "1792 of 1792 blocks in use (1.0 MiB each), 0 free\n",
    };

    /// <summary>Every page DEBUG_TEXT was asked for, 0xFF for the list, oldest first.</summary>
    public List<byte> DebugTextRequests { get; } = new();

    /// <summary>The op answers UNKNOWN_OP, as a module from before revision 1.29 does.</summary>
    public bool DebugTextUnknown { get; set; }

    private (Status, byte[]?) HandleDebugText(byte[] payload)
    {
        if (DebugTextUnknown) return (Status.UnknownOp, null);
        if (payload.Length < 1) return (Status.BadArg, null);
        if (_session.State != SessionState.Ingame || _session.Game != GameId.Tod) return (Status.Unsupported, null);

        byte page = payload[0];
        DebugTextRequests.Add(page);
        if (page == DebugText.ListRequest) return (Status.Ok, DebugText.ListBytes(DebugPages));
        if (page >= DebugPages.Count) return (Status.BadArg, null);
        return (Status.Ok, DebugText.TextBytes(DebugTexts.TryGetValue(page, out var text) ? text : string.Empty));
    }
}
