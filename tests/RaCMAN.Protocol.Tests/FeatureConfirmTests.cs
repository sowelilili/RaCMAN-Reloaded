using System.Diagnostics;
using System.Text;
using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Feature flags bit6 CONFIRM and FEATURE_CONFIRM_TEXT (revision 1.21): the flag read off a
/// DESCRIBE row, what it covers, and the reply read as the text it is.
/// </summary>
public class FeatureConfirmFlagTests
{
    private static Feature Row(FeatureKind kind, FeatureFlags flags) =>
        new(21, kind, 5, 0, flags, 0xFF, 0, 0, "Reset trophies (freezes ~3 s)");

    [Fact]
    public void Bit6IsConfirmAndReadsOffTheRow()
    {
        Assert.Equal(0x40, (int)FeatureFlags.Confirm);

        // Beside the bits that were already there: an ACTION with only CONFIRM, and a TOGGLE that
        // is also auto-applied.
        var action = Feature.Parse(Row(FeatureKind.Action, FeatureFlags.Confirm).ToBytes());
        var toggle = Feature.Parse(Row(FeatureKind.Toggle, FeatureFlags.Confirm | FeatureFlags.Auto).ToBytes());

        Assert.True(action.Confirms);
        Assert.Equal(FeatureFlags.Confirm, action.Flags);
        Assert.True(toggle.Confirms);
        Assert.True(toggle.Auto);

        var raw = Row(FeatureKind.Action, FeatureFlags.None).ToBytes();
        Assert.False(Feature.Parse(raw).Confirms);
        raw[4] = 0x40;
        Assert.True(Feature.Parse(raw).Confirms);
    }

    [Fact]
    public void ItCoversTriggeringAnActionAndTurningAToggleOnAndNothingElse()
    {
        var action = Row(FeatureKind.Action, FeatureFlags.Confirm);
        var toggle = Row(FeatureKind.Toggle, FeatureFlags.Confirm);
        var plain = Row(FeatureKind.Toggle, FeatureFlags.Auto);

        Assert.True(action.ConfirmsTrigger);
        Assert.False(toggle.ConfirmsTrigger);

        Assert.True(toggle.ConfirmsSet(1));
        Assert.False(toggle.ConfirmsSet(0));
        Assert.True(toggle.ConfirmsAuto(true));
        Assert.False(toggle.ConfirmsAuto(false));

        Assert.False(plain.ConfirmsSet(1));
        Assert.False(plain.ConfirmsAuto(true));

        Assert.True(FeatureConfirmations.Needed(action, FeatureRequest.Trigger, 1));
        Assert.True(FeatureConfirmations.Needed(toggle, FeatureRequest.Set, 1));
        Assert.False(FeatureConfirmations.Needed(toggle, FeatureRequest.Set, 0));
        Assert.True(FeatureConfirmations.Needed(toggle, FeatureRequest.SetAuto, 1));
        Assert.False(FeatureConfirmations.Needed(toggle, FeatureRequest.SetAuto, 0));
        Assert.False(FeatureConfirmations.Needed(plain, FeatureRequest.Set, 1));
    }

    [Fact]
    public void TheReplyIsTheTextAndNothingElse()
    {
        Assert.Equal("Deletes the trophy record.", FeatureConfirmText.Parse(Encoding.UTF8.GetBytes("Deletes the trophy record.")));
        Assert.Equal("Löschen – sicher?", FeatureConfirmText.Parse(Encoding.UTF8.GetBytes("Löschen – sicher?")));
        Assert.Equal(string.Empty, FeatureConfirmText.Parse(ReadOnlySpan<byte>.Empty));

        // No terminator is sent, but one that is still ends the text.
        Assert.Equal("Sure?", FeatureConfirmText.Parse(Encoding.UTF8.GetBytes("Sure?\0\0junk")));
    }

    [Fact]
    public void FiveHundredAndTwelveBytesIsTheMost()
    {
        var most = Encoding.ASCII.GetBytes(new string('a', FeatureConfirmText.MaxBytes));
        Assert.Equal(512, FeatureConfirmText.Parse(most).Length);

        var over = new byte[FeatureConfirmText.MaxBytes + 1];
        Array.Fill(over, (byte)'a');
        Assert.Throws<ProtocolException>(() => FeatureConfirmText.Parse(over));

        // The fake's side cuts a long text on a character boundary, never inside one.
        var cut = FeatureConfirmText.Encode(new string('a', 511) + "é" + "tail");
        Assert.Equal(511, cut.Length);
        Assert.Equal(new string('a', 511), FeatureConfirmText.Parse(cut));
    }
}

/// <summary>FEATURE_CONFIRM_TEXT over the wire, against the fake console moved to UYA.</summary>
public class FeatureConfirmTextOpTests
{
    private static async Task<(FakeQwarkServer Server, QwarkClient Client)> UyaAsync()
    {
        var server = new FakeQwarkServer();
        server.Describe = FakeQwarkServer.WithManipHeaderFeatures(server.Describe with { Game = GameId.Rac3 }, GameId.Rac3);
        server.Session = server.Session with { Game = GameId.Rac3, TitleId = "NPEA00387" };
        server.Start();

        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return (server, client);
    }

    [Fact]
    public async Task UyasTrophyFeaturesAreDescribedConfirmAndHaveTheirText()
    {
        var (server, client) = await UyaAsync();
        using (server)
        using (client)
        {
            var describe = await client.DescribeAsync();
            var reset = describe.Features.Single(f => f.Id == FakeQwarkServer.TrophyResetId);
            var auto = describe.Features.Single(f => f.Id == FakeQwarkServer.TrophyAutoResetId);

            Assert.Equal(FakeQwarkServer.TrophyResetLabel, reset.Label);
            Assert.Equal(FakeQwarkServer.TrophyAutoResetLabel, auto.Label);
            Assert.True(reset.ConfirmsTrigger);
            Assert.True(auto.ConfirmsSet(1));
            Assert.Equal(2, describe.Features.Count(f => f.Confirms));

            string text = await client.FeatureConfirmTextAsync(reset.Id);
            Assert.Equal(server.ConfirmTexts[FakeQwarkServer.TrophyResetLabel], text);

            // One byte, the id, under opcode 0x0026.
            Assert.Equal(0x0026, (int)Opcode.FeatureConfirmText);
            Assert.Equal(new byte[] { 21 }, server.LastPayload(Opcode.FeatureConfirmText));
        }
    }

    [Fact]
    public async Task AFeatureWithoutTheFlagIsBadArgAndOutsideAGameItIsUnsupported()
    {
        var (server, client) = await UyaAsync();
        using (server)
        using (client)
        {
            var plain = await Assert.ThrowsAsync<QwarkStatusException>(() => client.FeatureConfirmTextAsync(0));
            Assert.Equal(Status.BadArg, plain.Status);

            server.Session = server.Session with { State = SessionState.Xmb, Game = GameId.None, TitleId = string.Empty };
            var xmb = await Assert.ThrowsAsync<QwarkStatusException>(() => client.FeatureConfirmTextAsync(FakeQwarkServer.TrophyResetId));
            Assert.Equal(Status.Unsupported, xmb.Status);
        }
    }
}

/// <summary>
/// The question in front of a CONFIRM feature, as <see cref="AppState"/> drives it against the fake
/// console moved to UYA: nothing goes out before Continue, nothing at all on Cancel, turning a
/// toggle off is never asked about, and the "on boot" box is asked about like the toggle itself.
/// </summary>
public class FeatureConfirmGateTests
{
    private const ulong ResetToggleBit = 1UL << FakeQwarkServer.TrophyAutoResetId;

    private static async Task<(FakeQwarkServer Server, AppState State)> UyaStateAsync()
    {
        var server = new FakeQwarkServer();
        server.Describe = FakeQwarkServer.WithManipHeaderFeatures(server.Describe with { Game = GameId.Rac3 }, GameId.Rac3);
        server.Session = server.Session with { Game = GameId.Rac3, TitleId = "NPEA00387" };
        server.Start();

        var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        Assert.True(await PumpAsync(state, () => state.Describe.Features.Any(f => f.Confirms)));
        return (server, state);
    }

    private static async Task<bool> PumpAsync(AppState state, Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            state.Tick(1f / 60f);
            if (condition()) return true;
            await Task.Delay(16);
        }

        state.Tick(1f / 60f);
        return condition();
    }

    private static async Task PumpForAsync(AppState state, int milliseconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            state.Tick(1f / 60f);
            await Task.Delay(16);
        }
    }

    private static Feature ById(AppState state, byte id) => state.Describe.Features.Single(f => f.Id == id);

    /// <summary>Asks, and waits until the dialog has the console's answer to show.</summary>
    private static async Task<PendingConfirm> AskedAsync(AppState state)
    {
        Assert.True(await PumpAsync(state, () => state.Confirms.Pending is { Loading: false }));
        return state.Confirms.Pending!;
    }

    private static int Count(FakeQwarkServer server, Opcode opcode) => server.RequestLog().Count(op => op == opcode);

    [Fact]
    public async Task TheResetButtonSendsNothingUntilContinue()
    {
        var (server, state) = await UyaStateAsync();
        using (server)
        using (state)
        {
            server.ClearRequestLog();
            var reset = ById(state, FakeQwarkServer.TrophyResetId);

            state.Confirms.Trigger(reset);
            var question = await AskedAsync(state);

            Assert.Same(reset, question.Feature);
            Assert.Equal(FeatureRequest.Trigger, question.Request);
            Assert.Equal(server.ConfirmTexts[FakeQwarkServer.TrophyResetLabel], question.Body);
            Assert.Null(question.Problem);

            // The question is all that went out, however long it stays on screen.
            await PumpForAsync(state, 300);
            Assert.Equal(1, Count(server, Opcode.FeatureConfirmText));
            Assert.Equal(0, Count(server, Opcode.FeatureTrigger));

            state.Confirms.Continue(question);
            Assert.Null(state.Confirms.Pending);
            Assert.True(await PumpAsync(state, () => server.Triggered.Contains(FakeQwarkServer.TrophyResetId)));
            Assert.Equal(new byte[] { FakeQwarkServer.TrophyResetId }, server.LastPayload(Opcode.FeatureTrigger));
            Assert.Equal(1, Count(server, Opcode.FeatureTrigger));
            Assert.Equal("1/1/0", state.Confirms.Summary);
        }
    }

    [Fact]
    public async Task CancelSendsNothingAndTheToggleStaysAsItWas()
    {
        var (server, state) = await UyaStateAsync();
        using (server)
        using (state)
        {
            server.ClearRequestLog();
            var reset = ById(state, FakeQwarkServer.TrophyResetId);
            var toggle = ById(state, FakeQwarkServer.TrophyAutoResetId);

            state.Confirms.Trigger(reset);
            state.Confirms.Cancel(await AskedAsync(state));

            state.Confirms.Set(toggle, 1);
            var question = await AskedAsync(state);
            Assert.Equal(FeatureRequest.Set, question.Request);
            state.Confirms.Cancel(question);

            await PumpForAsync(state, 300);

            Assert.Null(state.Confirms.Pending);
            Assert.Equal(0, Count(server, Opcode.FeatureTrigger));
            Assert.Equal(0, Count(server, Opcode.FeatureSet));
            Assert.Empty(server.Triggered);
            Assert.Equal(0UL, server.Session.ToggleState & ResetToggleBit);
            Assert.Equal(0UL, state.Session.ToggleState & ResetToggleBit);
            Assert.Equal("2/0/2", state.Confirms.Summary);

            // An answer to a question that is gone sends nothing either.
            state.Confirms.Continue(question);
            await PumpForAsync(state, 100);
            Assert.Equal(0, Count(server, Opcode.FeatureSet));
        }
    }

    [Fact]
    public async Task TheToggleIsAskedAboutOnTheWayOnAndNotOnTheWayOff()
    {
        var (server, state) = await UyaStateAsync();
        using (server)
        using (state)
        {
            server.ClearRequestLog();
            var toggle = ById(state, FakeQwarkServer.TrophyAutoResetId);

            state.Confirms.Set(toggle, 1);
            var question = await AskedAsync(state);
            Assert.Equal(0, Count(server, Opcode.FeatureSet));

            state.Confirms.Continue(question);
            Assert.True(await PumpAsync(state, () => (state.Session.ToggleState & ResetToggleBit) != 0));
            Assert.Equal(new byte[] { 39, 0, 0, 0, 1 }, server.LastPayload(Opcode.FeatureSet));

            // Off goes straight out: no question, no text asked for.
            server.ClearRequestLog();
            state.Confirms.Set(toggle, 0);
            Assert.Null(state.Confirms.Pending);
            Assert.True(await PumpAsync(state, () => (state.Session.ToggleState & ResetToggleBit) == 0));
            Assert.Equal(new byte[] { 39, 0, 0, 0, 0 }, server.LastPayload(Opcode.FeatureSet));
            Assert.Equal(0, Count(server, Opcode.FeatureConfirmText));
            Assert.Equal(1, state.Confirms.Asked);
        }
    }

    [Fact]
    public async Task TheOnBootBoxIsAskedAboutWhenTickedAndNotWhenCleared()
    {
        var (server, state) = await UyaStateAsync();
        using (server)
        using (state)
        {
            server.ClearRequestLog();
            var toggle = ById(state, FakeQwarkServer.TrophyAutoResetId);

            state.Confirms.SetAuto(toggle, true);
            var question = await AskedAsync(state);
            Assert.Equal(FeatureRequest.SetAuto, question.Request);

            // Cancelled, nothing is sent and the box stays clear.
            state.Confirms.Cancel(question);
            await PumpForAsync(state, 200);
            Assert.Equal(0, Count(server, Opcode.FeatureSetAuto));
            Assert.Equal(0UL, state.Session.ToggleAuto & ResetToggleBit);

            // Continued, it is.
            state.Confirms.SetAuto(toggle, true);
            state.Confirms.Continue(await AskedAsync(state));
            Assert.True(await PumpAsync(state, () => (state.Session.ToggleAuto & ResetToggleBit) != 0));
            Assert.Equal(new byte[] { 39, 1 }, server.LastPayload(Opcode.FeatureSetAuto));

            // Clearing it is never asked about.
            state.Confirms.SetAuto(toggle, false);
            Assert.Null(state.Confirms.Pending);
            Assert.True(await PumpAsync(state, () => (state.Session.ToggleAuto & ResetToggleBit) == 0));
            Assert.Equal(2, state.Confirms.Asked);
        }
    }

    [Fact]
    public async Task AFeatureWithoutTheFlagIsSentAtOnce()
    {
        var (server, state) = await UyaStateAsync();
        using (server)
        using (state)
        {
            server.ClearRequestLog();
            var ammo = state.Describe.Features.Single(f => f.Label == "Infinite ammo");
            var die = state.Describe.Features.Single(f => f.Kind == FeatureKind.Action && f.Label == "Die");

            state.Confirms.Set(ammo, 1);
            state.Confirms.SetAuto(ammo, true);
            state.Confirms.Trigger(die);

            Assert.Null(state.Confirms.Pending);
            Assert.True(await PumpAsync(state, () => (state.Session.ToggleState & 1UL) != 0
                                                     && (state.Session.ToggleAuto & 1UL) != 0
                                                     && server.Triggered.Contains(die.Id)));
            Assert.Equal(0, Count(server, Opcode.FeatureConfirmText));
            Assert.Equal(0, state.Confirms.Asked);
        }
    }

    [Fact]
    public async Task AQuestionDoesNotOutliveItsGame()
    {
        var (server, state) = await UyaStateAsync();
        using (server)
        using (state)
        {
            server.ClearRequestLog();
            state.Confirms.Trigger(ById(state, FakeQwarkServer.TrophyResetId));
            var question = await AskedAsync(state);

            // A reboot of the same title: the question was about the process that went.
            server.Session = server.Session with { Generation = server.Session.Generation + 1 };
            Assert.True(await PumpAsync(state, () => state.Confirms.Pending is null));

            state.Confirms.Continue(question);
            await PumpForAsync(state, 200);

            Assert.Equal(0, Count(server, Opcode.FeatureTrigger));
            Assert.Contains(state.Toasts, t => t.Text.Contains("was not sent", StringComparison.Ordinal));
            Assert.Equal("1/0/1", state.Confirms.Summary);
        }
    }

    [Fact]
    public async Task WithoutTheConsolesWordsTheQuestionIsTheGenericOne()
    {
        var (server, state) = await UyaStateAsync();
        using (server)
        using (state)
        {
            // A module that does not know the op, as one older than the revision answers.
            server.RefusedOps[Opcode.FeatureConfirmText] = Status.UnknownOp;
            var reset = ById(state, FakeQwarkServer.TrophyResetId);

            state.Confirms.Trigger(reset);
            var question = await AskedAsync(state);

            Assert.Null(question.Text);
            Assert.Equal("Reset trophies (freezes ~3 s): this can't be undone from RaCMAN. Continue?", question.Body);
            Assert.Equal(FeatureConfirmations.Fallback(reset.Label), question.Body);
            Assert.Equal(nameof(Status.UnknownOp), question.Problem);

            // The dialog says it, so nothing else does.
            Assert.DoesNotContain(state.Toasts, t => t.Kind == ToastKind.Error);

            // And the question still works.
            state.Confirms.Continue(question);
            Assert.True(await PumpAsync(state, () => server.Triggered.Contains(reset.Id)));
        }
    }

    [Fact]
    public async Task AnEmptyAnswerIsNoWordingEither()
    {
        var (server, state) = await UyaStateAsync();
        using (server)
        using (state)
        {
            server.ConfirmTexts[FakeQwarkServer.TrophyAutoResetLabel] = "   ";

            state.Confirms.Set(ById(state, FakeQwarkServer.TrophyAutoResetId), 1);
            var question = await AskedAsync(state);

            Assert.Null(question.Problem);
            Assert.Equal(FeatureConfirmations.Fallback(FakeQwarkServer.TrophyAutoResetLabel), question.Body);
            state.Confirms.Cancel(question);
        }
    }
}

/// <summary>
/// The combos and the CONFIRM flag. A combo is one of five console actions and never names a
/// feature, so UYA's trophy reset cannot be put on the pad at all; the one combo that does what a
/// described ACTION does, "Load set-aside file", is refused if a game ever flags that action
/// CONFIRM, because the console fires a combo with nobody there to ask.
/// </summary>
public class FeatureConfirmComboTests
{
    private static DescribeResult WithLoadAside(DescribeResult describe, FeatureFlags extra) =>
        describe with
        {
            Features = describe.Features
                .Select(f => f.LoadsAside ? f with { Flags = f.Flags | extra } : f)
                .ToArray(),
        };

    /// <summary>The fake console's own feature table, without leaving its listener open.</summary>
    private static DescribeResult FakeTable()
    {
        using var server = new FakeQwarkServer();
        return server.Describe;
    }

    [Fact]
    public void NoComboStandsForUyasTrophyFeatures()
    {
        var uya = FakeQwarkServer.WithManipHeaderFeatures(FakeTable() with { Game = GameId.Rac3 }, GameId.Rac3);
        Assert.Equal(2, uya.Features.Count(f => f.Confirms));

        // Five actions, chosen by number; none of them is a feature, and none is refused.
        Assert.Equal(5, Enum.GetValues<ComboAction>().Length);
        Assert.All(Enum.GetValues<ComboAction>(), action => Assert.Null(CombosPanel.ConfirmFeatureFor(action, uya)));
    }

    [Fact]
    public void TheSetAsideComboIsRefusedOnlyWhenItsActionAsksFirst()
    {
        var plain = FakeTable();
        var asking = WithLoadAside(plain, FeatureFlags.Confirm);

        Assert.All(Enum.GetValues<ComboAction>(), action => Assert.Null(CombosPanel.ConfirmFeatureFor(action, plain)));

        var refused = CombosPanel.ConfirmFeatureFor(ComboAction.LoadSetAsideFile, asking);
        Assert.NotNull(refused);
        Assert.True(refused!.LoadsAside);
        Assert.All(Enum.GetValues<ComboAction>().Where(a => a != ComboAction.LoadSetAsideFile),
            action => Assert.Null(CombosPanel.ConfirmFeatureFor(action, asking)));

        Assert.Contains("cannot be put on a combo", CombosPanel.ConfirmRefusal(ComboAction.LoadSetAsideFile, refused));
    }

    [Fact]
    public async Task ACaptureForARefusedComboNeverStarts()
    {
        using var server = new FakeQwarkServer();
        server.Describe = WithLoadAside(server.Describe, FeatureFlags.Confirm);
        server.Start();

        using var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        var sw = Stopwatch.StartNew();
        while (state.Describe.LoadAsideAction is not { Confirms: true } && sw.ElapsedMilliseconds < 5000)
        {
            state.Tick(1f / 60f);
            await Task.Delay(16);
        }

        Assert.True(state.Describe.LoadAsideAction?.Confirms);

        server.ClearRequestLog();
        CombosPanel.BeginCapture(state, ComboAction.LoadSetAsideFile);
        await Task.Delay(200);

        // No hold was asked for and no capture runs on that row. Other test classes capture other
        // rows on the same static panel, so only this row is looked at.
        Assert.NotEqual(ComboAction.LoadSetAsideFile, CombosPanel.Capturing);
        Assert.DoesNotContain(Opcode.ComboSuspend, server.RequestLog());
        Assert.DoesNotContain(Opcode.ComboSet, server.RequestLog());
    }
}
