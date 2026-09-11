namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Reading webMAN's VSH plugin page. The fixtures below are webMAN MOD's own HTML: one table row
/// per slot, a full slot carrying the plugin's name, its path and an unload button, an empty one
/// carrying a box to type a path into, and a hidden datalist of every .sprx on the console's
/// drives so that box can autocomplete. That datalist is why a plain search of the page for
/// "qwark.sprx" reported a module as loaded when nothing had loaded it.
/// </summary>
public class VshPluginPageTests
{
    private const string Sprx = "qwark.sprx";

    /// <summary>A row for a slot with a plugin in it, as ps3mapi_vshplugin prints one.</summary>
    private static string FullSlot(int slot, string name, string path) =>
        $"<tr><td width=\"75\" style=\"text-align:left; float:left;\">{slot}</td>"
        + $"<td width=\"120\" style=\"text-align:left; float:left;\">{name}</td>"
        + $"<td width=\"500\" style=\"text-align:left; float:left;\">{path}</td>"
        + "<td width=\"100\" style=\"text-align:right; float:right;\">"
        + "<form action=\"/vshplugin.ps3mapi\" method=\"get\" enctype=\"application/x-www-form-urlencoded\" target=\"_self\">"
        + $"<input name=\"unload_slot\" type=\"hidden\" value=\"{slot}\">"
        + "<input type=\"submit\" value=\" Unload \"/></form></td></tr>";

    /// <summary>And a row for an empty one, with the path box and its Load button.</summary>
    private static string EmptySlot(int slot) =>
        $"<tr><td width=\"75\" style=\"text-align:left; float:left;\">{slot}</td>"
        + "<td width=\"120\" style=\"text-align:left; float:left;\">NULL</td>"
        + "<form action=\"/vshplugin.ps3mapi\" method=\"get\" enctype=\"application/x-www-form-urlencoded\" target=\"_self\">"
        + "<td width=\"500\" style=\"text-align:left; float:left;\">"
        + "<input name=\"prx\" style=\"width:555px\" list=\"plugins\" type=\"text\" size=\"128\" value=\"\">"
        + $"<input name=\"load_slot\" type=\"hidden\" value=\"{slot}\"></td>"
        + "<td width=\"100\" style=\"text-align:right; float:right;\"><input type=\"submit\" value=\" Load \"/></td>"
        + "</form></tr>";

    /// <summary>The hidden list of every .sprx the console has, which is not a list of loaded ones.</summary>
    private static string PluginFileList(params string[] paths) =>
        "<div style=\"display:none\"><datalist id=\"plugins\">"
        + string.Concat(paths.Select(p => $"<option>{p}</option>"))
        + "</datalist></div>";

    private static string Page(string rows, string tail = "") =>
        "<html><head><title>PS3 Manager API</title></head><body><table>" + rows + "</table>" + tail + "</body></html>";

    [Fact]
    public void AFileTheConsoleMerelyHasIsNotAFileThatIsLoaded()
    {
        // The bug this parser exists for: qwark.sprx sits in /dev_hdd0/plugins because a boot
        // install put it there, so webMAN offers it in the datalist. No slot holds it.
        var page = Page(
            FullSlot(0, "webftp_server", "/dev_hdd0/tmp/wm_lang/webftp_server.sprx")
            + EmptySlot(1) + EmptySlot(2) + EmptySlot(3),
            PluginFileList("/dev_hdd0/plugins/qwark.sprx", "/dev_hdd0/tmp/qwark.sprx"));

        var status = VshPluginStatus.Parse(page, Sprx);

        Assert.Equal(VshPluginState.NotLoaded, status.State);
        Assert.False(status.IsLoaded);
        Assert.True(status.IsKnown);
    }

    [Fact]
    public void ASlotThatHoldsItSaysWhichOneAndWhereItCameFrom()
    {
        var page = Page(
            FullSlot(0, "webftp_server", "/dev_hdd0/tmp/wm_lang/webftp_server.sprx")
            + EmptySlot(1)
            + FullSlot(5, "qwark", "/dev_hdd0/plugins/qwark.sprx")
            + EmptySlot(6),
            PluginFileList("/dev_hdd0/plugins/qwark.sprx"));

        var status = VshPluginStatus.Parse(page, Sprx);

        Assert.Equal(VshPluginState.Loaded, status.State);
        Assert.True(status.IsLoaded);
        Assert.Equal(5, status.Slot);
        Assert.Equal("5", status.SlotText);
        Assert.Equal("/dev_hdd0/plugins/qwark.sprx", status.Path);
    }

    [Fact]
    public void TheSlotAWebManLoadPutsItInIsReadTheSameWay()
    {
        // A runtime load leaves it under /dev_hdd0/tmp instead; both are the module being loaded.
        var page = Page(EmptySlot(1) + FullSlot(5, "qwark", "/dev_hdd0/tmp/qwark.sprx"));

        var status = VshPluginStatus.Parse(page, Sprx);

        Assert.True(status.IsLoaded);
        Assert.Equal("/dev_hdd0/tmp/qwark.sprx", status.Path);
    }

    [Fact]
    public void EverySlotFullAndNoneOfThemOursIsAnAnswerToo()
    {
        var rows = string.Concat(Enumerable.Range(0, 7).Select(s => FullSlot(s, $"other{s}", $"/dev_hdd0/plugins/other{s}.sprx")));

        var status = VshPluginStatus.Parse(Page(rows), Sprx);

        Assert.Equal(VshPluginState.NotLoaded, status.State);
    }

    [Fact]
    public void APageWithNoSlotsOnItIsNotAnAnswer()
    {
        // An error page, a login page, or a webMAN whose plugin page has another shape: nothing
        // here may be read as "not loaded", because the caller would upload over a running module.
        Assert.Equal(VshPluginState.Unknown, VshPluginStatus.Parse("<html><body>Not found</body></html>", Sprx).State);
        Assert.Equal(VshPluginState.Unknown, VshPluginStatus.Parse(string.Empty, Sprx).State);
        Assert.Equal(VshPluginState.Unknown, VshPluginStatus.Parse(null, Sprx).State);

        // Not even a page that names the file, if it does not say which slot holds anything.
        Assert.Equal(VshPluginState.Unknown,
            VshPluginStatus.Parse("<html><body>/dev_hdd0/plugins/qwark.sprx</body></html>", Sprx).State);
    }

    [Fact]
    public void AnOlderPageThatOffersItsFilesInASelectIsReadTheSameWay()
    {
        var page = Page(
            EmptySlot(1) + EmptySlot(2),
            "<select name=\"prx\"><option>/dev_hdd0/plugins/qwark.sprx</option></select>");

        Assert.Equal(VshPluginState.NotLoaded, VshPluginStatus.Parse(page, Sprx).State);
    }

    [Fact]
    public void TheFileNameIsMatchedWhateverCaseThePageUses()
    {
        var page = Page(EmptySlot(1) + FullSlot(4, "qwark", "/dev_hdd0/plugins/QWARK.SPRX"));

        var status = VshPluginStatus.Parse(page, Sprx);

        Assert.True(status.IsLoaded);
        Assert.Equal(4, status.Slot);
    }

    [Fact]
    public void TheReservedSlotZeroCountsLikeAnyOther()
    {
        // Slot 0 is webMAN's own and its button is disabled, but the row is shaped the same, so a
        // module found there is still a module that is loaded.
        var page = Page(FullSlot(0, "qwark", "/dev_hdd0/plugins/qwark.sprx") + EmptySlot(1));

        var status = VshPluginStatus.Parse(page, Sprx);

        Assert.True(status.IsLoaded);
        Assert.Equal(0, status.Slot);
    }

    [Fact]
    public void AnUnknownAnswerKnowsNothingAndSaysSo()
    {
        Assert.False(VshPluginStatus.Unknown.IsKnown);
        Assert.False(VshPluginStatus.Unknown.IsLoaded);
        Assert.Equal("?", VshPluginStatus.Unknown.SlotText);

        Assert.True(VshPluginStatus.NotLoaded.IsKnown);
        Assert.False(VshPluginStatus.NotLoaded.IsLoaded);
    }
}
