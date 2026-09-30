using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// LEVELFLAGS_GET for one planet. The flags are a bitfield, so the view is one byte per row with a
/// checkbox per bit (7 down to 0) and the hex value beside them; ticking a bit sends the whole byte
/// with LEVELFLAGS_SET. The bytes are the game's flag regions concatenated, so an offset here means
/// nothing to this client beyond "byte n". The table re-reads itself on the Settings panel's table
/// refresh interval: a flag flips as the game runs, and a stale table is worse than a re-read a
/// second. An interval of zero leaves the Refresh button as the only thing that reads.
///
/// "Check all" is the bit checkboxes over the whole table: LEVELFLAGS_SET of FF for every byte
/// LEVELFLAGS_GET reported, one request per byte, the way the Unlocks panel's bulk buttons loop
/// UNLOCK_SET. The client knows nothing about which bits the game uses, so it sets them all.
/// </summary>
public static class LevelFlagsPanel
{
    /// <summary>What "Check all" writes to every byte: all eight bits set.</summary>
    public const byte AllBits = 0xFF;

    /// <summary>Which of the row's two confirmations is showing. One at most, so there is one field.</summary>
    private enum Armed
    {
        None,
        Reset,
        CheckAll,
    }

    private static byte[] _flags = Array.Empty<byte>();
    private static int _loadedPlanet = -1;
    private static int _planet = -1;
    private static float _sinceRefresh;
    private static Armed _armed;

    /// <summary>The red of a confirmation button: the second click that writes game memory.</summary>
    private static readonly Vector4 ConfirmColour = new(0.6f, 0.2f, 0.2f, 1f);

    /// <summary>How many flag bytes the last LEVELFLAGS_GET returned, for the smoke-run summary.</summary>
    public static int LoadedByteCount => _flags.Length;

    public static void Reset()
    {
        ClearData();
        _planet = -1;
    }

    /// <summary>
    /// Drops the flag bytes themselves. They are a copy of the running process's memory, so they
    /// must not survive the session that produced them; the planet the user picked does.
    /// </summary>
    public static void ClearData()
    {
        _flags = Array.Empty<byte>();
        _loadedPlanet = -1;
        _sinceRefresh = 0;
        _armed = Armed.None;
    }

    public static void Draw(AppState state)
    {
        Ui.Heading("Level flags");

        var session = state.Session;
        var planets = state.Planets;

        // Default to the planet the player is on, and follow it until the user picks another.
        if (_planet < 0) _planet = session.CurrentPlanet;

        ImGui.BeginDisabled(!state.Connected);

        ImGui.SetNextItemWidth(240);
        if (planets.Length > 0)
        {
            // The filler names in the console list ("(none)", "(unused)", "(infinite loop)") are
            // not offered; _planet stays the real index the console expects, and one left on a
            // hidden id comes back as the first planet the game really has.
            var choices = PlanetChoices.For(planets);
            int pick = choices.PositionFor(_planet);
            _planet = choices.PlanetAt(pick);
            if (ImGui.Combo("Planet", ref pick, choices.Labels, choices.Count))
            {
                _planet = choices.PlanetAt(pick);
                Load(state);
            }
        }
        else
        {
            // Enter reads the planet that was typed, which is what picking one from the combo above
            // does; without a list there is no picking, so the box is the only way to ask.
            if (Ui.SubmitInt("Planet index", ref _planet, 0, 255)) Load(state);
        }

        // Only where nothing else reads the table: with an interval set on the Settings panel the
        // flags are never more than that many seconds old, and the button had nothing to add.
        if (!state.Settings.AutoRefreshesTables)
        {
            ImGui.SameLine();
            if (ImGui.Button("Refresh")) Load(state);
        }

        // Resetting or checking a planet's flags writes game memory, so unlike the reads above it
        // needs a game to write to. The rest of the row keeps working between sessions. Each asks
        // for a second click, and arming one puts the other back, so there is only ever one
        // confirmation on the row to click.
        ImGui.BeginDisabled(!state.Ingame);
        ImGui.SameLine();
        if (_armed == Armed.Reset)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, ConfirmColour);
            if (ImGui.Button("Confirm reset"))
            {
                _armed = Armed.None;
                byte planet = (byte)_planet;
                state.Run(async () =>
                {
                    await state.Client.LevelFlagsResetAsync(planet).ConfigureAwait(false);
                    state.Post(() => Load(state));
                }, $"Level flags reset on planet {planet}");
            }

            ImGui.PopStyleColor();
            ImGui.SameLine();
            if (ImGui.Button("Cancel##reset")) _armed = Armed.None;
        }
        else if (ImGui.Button("Reset flags..."))
        {
            _armed = Armed.Reset;
        }

        // Check all also needs the bytes themselves: their count is how many it writes.
        ImGui.SameLine();
        ImGui.BeginDisabled(_flags.Length == 0);
        if (_armed == Armed.CheckAll)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, ConfirmColour);
            if (ImGui.Button("Confirm check all"))
            {
                _armed = Armed.None;
                CheckAll(state);
            }

            ImGui.PopStyleColor();
            ImGui.SameLine();
            if (ImGui.Button("Cancel##checkall")) _armed = Armed.None;
        }
        else
        {
            if (ImGui.Button("Check all...")) _armed = Armed.CheckAll;
            Ui.Tooltip("Sets every flag bit of this planet: each byte in the table is written as FF.");
        }

        ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGui.EndDisabled();

        // Quiet outside INGAME: the automatic first read between sessions is not worth a toast.
        // Nothing at all while the console is busy with a launch (section 1.1): _loadedPlanet is
        // left alone, so the next frame after INGAME is the one that reads.
        if (state.Connected && !state.ConsoleBusy && _loadedPlanet != _planet) Load(state, quiet: !state.Ingame);

        // The interval is read every frame, so a change on the Settings panel takes effect at once.
        float period = state.Settings.TableRefreshSeconds;
        if (period > 0 && state.Connected && !state.ConsoleBusy)
        {
            _sinceRefresh += ImGui.GetIO().DeltaTime;
            if (_sinceRefresh >= period)
            {
                _sinceRefresh = 0;
                Load(state, quiet: true);
            }
        }
        else
        {
            _sinceRefresh = 0;
        }

        if (session.CurrentPlanet != _planet && state.Ingame)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Go to current ({session.CurrentPlanet})"))
            {
                _planet = session.CurrentPlanet;
            }
        }

        ImGui.Spacing();

        if (_flags.Length == 0)
        {
            Ui.Hint(!state.Connected ? "Connect to read level flags."
                : !state.Ingame ? $"Reading level flags needs INGAME (state is {session.State.DisplayName()})."
                : "This game has no level flags.");
            return;
        }

        DrawBits(state);
    }

    /// <summary>One byte per row: offset, eight bit checkboxes (7 down to 0), and the hex value.</summary>
    private static void DrawBits(AppState state)
    {
        const int columns = 10;   // offset + 8 bits + hex
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit
                    | ImGuiTableFlags.ScrollY;

        // A scrolling table needs an explicit height, but sized to the panel it draws its column
        // borders down through the empty space below a short region. Size it to the rows instead,
        // and only let it fill (and scroll, with the header frozen) when the rows outgrow the panel.
        var style = ImGui.GetStyle();
        float headerHeight = ImGui.GetTextLineHeight() + style.CellPadding.Y * 2;
        float rowHeight = ImGui.GetFrameHeight() + style.CellPadding.Y * 2;
        float needed = headerHeight + _flags.Length * rowHeight + style.ScrollbarSize / 2;
        float height = Math.Min(needed, ImGui.GetContentRegionAvail().Y);

        if (!ImGui.BeginTable("flag-bits", columns, flags, new Vector2(-1, height))) return;

        ImGui.TableSetupScrollFreeze(0, 1);   // keep the bit numbers visible while scrolling
        ImGui.TableSetupColumn("Offset");
        for (int bit = 7; bit >= 0; bit--) ImGui.TableSetupColumn(bit.ToString());
        ImGui.TableSetupColumn("Hex");
        ImGui.TableHeadersRow();

        bool enabled = state.Ingame;
        ImGui.BeginDisabled(!enabled);

        for (int index = 0; index < _flags.Length; index++)
        {
            byte value = _flags[index];

            ImGui.TableNextRow();
            ImGui.PushID(index);

            // A row is a checkbox high, so the offset and the hex value sit on the checkboxes' line.
            ImGui.TableNextColumn();
            Ui.TableLabel(Ui.Grey, $"0x{index:X4}");

            for (int bit = 7; bit >= 0; bit--)
            {
                ImGui.TableNextColumn();
                bool set = ((value >> bit) & 1) != 0;
                if (ImGui.Checkbox($"##b{bit}", ref set))
                {
                    byte updated = set ? (byte)(value | (1 << bit)) : (byte)(value & ~(1 << bit));
                    SendByte(state, index, updated);
                }
            }

            ImGui.TableNextColumn();
            if (value != 0) Ui.TableLabel(Ui.Green, $"{value:X2}");
            else Ui.TableLabel($"{value:X2}");

            ImGui.PopID();
        }

        ImGui.EndDisabled();
        ImGui.EndTable();
    }

    /// <summary>Writes one byte with LEVELFLAGS_SET, optimistically updating the local copy, then re-reads.</summary>
    private static void SendByte(AppState state, int index, byte value)
    {
        byte planet = (byte)_loadedPlanet;
        ushort offset = (ushort)index;
        _flags[index] = value;

        state.Run(async () =>
        {
            await state.Client.LevelFlagsSetAsync(planet, offset, value).ConfigureAwait(false);
            state.Post(() => Load(state, quiet: true));
        });
    }

    /// <summary>
    /// The confirmed "Check all": every byte of the table as last read goes to FF on screen at once,
    /// as a single tick does, and <see cref="RunCheckAll"/> writes them to the planet they were read from.
    /// </summary>
    private static void CheckAll(AppState state)
    {
        byte planet = (byte)_loadedPlanet;
        int length = _flags.Length;
        Array.Fill(_flags, AllBits);

        RunCheckAll(state, planet, length, () => Load(state, quiet: true));
    }

    /// <summary>
    /// <see cref="CheckAllAsync"/> from one task, with one toast when every byte is written and the
    /// refusal's status when one is not. <paramref name="reread"/> is posted either way: after a
    /// refusal the bytes before it are set and the rest are not, and only a read says which.
    /// </summary>
    public static void RunCheckAll(AppState state, byte planet, int length, Action reread)
    {
        state.Run(async () =>
        {
            try
            {
                await CheckAllAsync(state.Client, planet, length).ConfigureAwait(false);
            }
            finally
            {
                state.Post(reread);
            }
        }, CheckAllToast(planet));
    }

    /// <summary>What the toast says when "Check all" has written every byte.</summary>
    public static string CheckAllToast(byte planet) => $"Level flags all set on planet {planet}";

    /// <summary>
    /// LEVELFLAGS_SET of <see cref="AllBits"/> at every offset below <paramref name="length"/>, one
    /// request per byte, from offset 0 up, each waiting for the answer to the one before. The length
    /// is what LEVELFLAGS_GET reported for the planet. A refusal is thrown as it arrives, so nothing
    /// is written past it.
    /// </summary>
    public static async Task CheckAllAsync(QwarkClient client, byte planet, int length, CancellationToken cancellationToken = default)
    {
        for (int offset = 0; offset < length; offset++)
        {
            await client.LevelFlagsSetAsync(planet, (ushort)offset, AllBits, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Load(AppState state, bool quiet = false)
    {
        if (!state.Connected || _planet < 0) return;

        byte planet = (byte)_planet;
        _loadedPlanet = planet;

        state.Run(async () =>
        {
            try
            {
                var bytes = await state.Client.LevelFlagsGetAsync(planet).ConfigureAwait(false);
                state.Post(() =>
                {
                    if (_loadedPlanet == planet) _flags = bytes;
                });
            }
            catch (QwarkStatusException ex) when (quiet && ex.Status is Status.NotIngame or Status.Unsupported)
            {
                // An auto-refresh between states says nothing; the manual path still toasts.
            }
            catch (QwarkStatusException)
            {
                state.Post(() =>
                {
                    if (_loadedPlanet == planet) _flags = Array.Empty<byte>();
                });
                throw;
            }
        });
    }
}
