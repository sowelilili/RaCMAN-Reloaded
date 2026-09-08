using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// LEVELFLAGS_GET for one planet. The flags are a bitfield, so the default view is one byte per
/// row with a checkbox per bit (7 down to 0); a Hex view keeps the compact 16-bytes-a-row grid
/// for scanning. Either way an edit sends the whole byte with LEVELFLAGS_SET. The bytes are the
/// game's flag regions concatenated, so an offset here means nothing to this client beyond
/// "byte n".
/// </summary>
public static class LevelFlagsPanel
{
    private const int BytesPerRow = 16;
    private const float AutoRefreshSeconds = 1f;

    private static byte[] _flags = Array.Empty<byte>();
    private static int _loadedPlanet = -1;
    private static int _planet = -1;
    private static bool _autoRefresh;
    private static bool _bitView = true;
    private static float _sinceRefresh;
    private static bool _resetArmed;

    private static int _editing = -1;
    private static string _editText = string.Empty;
    private static bool _focusEdit;

    /// <summary>How many flag bytes the last LEVELFLAGS_GET returned, for the smoke-run summary.</summary>
    public static int LoadedByteCount => _flags.Length;

    public static void Reset()
    {
        ClearData();
        _planet = -1;
        _autoRefresh = false;
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
        _resetArmed = false;
        _editing = -1;
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
            int index = Math.Clamp(_planet, 0, planets.Length - 1);
            if (ImGui.Combo("Planet", ref index, planets, planets.Length))
            {
                _planet = index;
                _editing = -1;
                Load(state);
            }
        }
        else
        {
            int index = _planet;
            if (ImGui.InputInt("Planet index", ref index))
            {
                _planet = Math.Clamp(index, 0, 255);
                _editing = -1;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh")) Load(state);

        ImGui.SameLine();
        ImGui.Checkbox("Auto-refresh (1 Hz)", ref _autoRefresh);

        ImGui.SameLine();
        if (ImGui.Checkbox("Bit view", ref _bitView)) _editing = -1;

        ImGui.SameLine();
        if (_resetArmed)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f));
            if (ImGui.Button("Confirm reset"))
            {
                _resetArmed = false;
                byte planet = (byte)_planet;
                state.Run(async () =>
                {
                    await state.Client.LevelFlagsResetAsync(planet).ConfigureAwait(false);
                    state.Post(() => Load(state));
                }, $"LEVELFLAGS_RESET on planet {planet}");
            }

            ImGui.PopStyleColor();
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) _resetArmed = false;
        }
        else if (ImGui.Button("Reset flags..."))
        {
            _resetArmed = true;
        }

        ImGui.EndDisabled();

        // Quiet outside INGAME: the automatic first read between sessions is not worth a toast.
        if (state.Connected && _loadedPlanet != _planet) Load(state, quiet: !state.Ingame);

        if (_autoRefresh && state.Connected)
        {
            _sinceRefresh += ImGui.GetIO().DeltaTime;
            if (_sinceRefresh >= AutoRefreshSeconds)
            {
                _sinceRefresh = 0;
                Load(state, quiet: true);
            }
        }

        if (session.CurrentPlanet != _planet && state.Ingame)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Go to current ({session.CurrentPlanet})"))
            {
                _planet = session.CurrentPlanet;
                _editing = -1;
            }
        }

        ImGui.Spacing();

        if (_flags.Length == 0)
        {
            Ui.Hint(!state.Connected ? "Connect to read level flags."
                : !state.Ingame ? $"Level flags are read out of game memory; the session is {session.State}, not INGAME."
                : "No flag bytes for this planet. LEVELFLAGS_GET answers Unsupported when the game has no flag region.");
            return;
        }

        Ui.Hint(_bitView
            ? $"{_flags.Length} bytes, planet {_loadedPlanet}. One byte per row, bit 7 to bit 0; ticking a bit sends the byte with LEVELFLAGS_SET."
            : $"{_flags.Length} bytes, planet {_loadedPlanet}. Click a cell to edit it; Enter sends LEVELFLAGS_SET.");
        ImGui.Spacing();

        if (_bitView) DrawBits(state);
        else DrawGrid(state);
    }

    // ------------------------------------------------------------- bit view

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

            ImGui.TableNextColumn();
            ImGui.TextColored(Ui.Grey, $"0x{index:X4}");

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
            if (value != 0) ImGui.TextColored(Ui.Green, $"{value:X2}");
            else ImGui.TextUnformatted($"{value:X2}");

            ImGui.PopID();
        }

        ImGui.EndDisabled();
        ImGui.EndTable();
    }

    // ------------------------------------------------------------- hex view

    private static void DrawGrid(AppState state)
    {
        if (!ImGui.BeginChild("##flags", new Vector2(-1, -1), ImGuiChildFlags.Borders,
                ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.EndChild();
            return;
        }

        bool enabled = state.Ingame;
        float cell = ImGui.CalcTextSize("FF").X + ImGui.GetStyle().FramePadding.X * 2 + 6;

        for (int offset = 0; offset < _flags.Length; offset += BytesPerRow)
        {
            ImGui.TextColored(Ui.Grey, $"{offset:X4}");

            for (int i = 0; i < BytesPerRow && offset + i < _flags.Length; i++)
            {
                int index = offset + i;
                ImGui.SameLine(0, i == 8 ? 12 : 4);
                ImGui.PushID(index);

                if (_editing == index)
                {
                    ImGui.SetNextItemWidth(cell + 12);
                    if (_focusEdit)
                    {
                        ImGui.SetKeyboardFocusHere();
                        _focusEdit = false;
                    }

                    if (ImGui.InputText("##edit", ref _editText, 3,
                            ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.EnterReturnsTrue))
                    {
                        Commit(state, index);
                    }
                    else if (!ImGui.IsItemActive() && !ImGui.IsItemHovered())
                    {
                        _editing = -1;
                    }
                }
                else
                {
                    byte value = _flags[index];
                    if (value != 0) ImGui.PushStyleColor(ImGuiCol.Text, Ui.Green);
                    bool clicked = ImGui.Button($"{value:X2}", new Vector2(cell, 0));
                    if (value != 0) ImGui.PopStyleColor();

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip($"offset 0x{index:X4} ({index})\nvalue 0x{value:X2} = {Convert.ToString(value, 2).PadLeft(8, '0')}");
                    }

                    if (clicked && enabled)
                    {
                        _editing = index;
                        _editText = value.ToString("X2");
                        _focusEdit = true;
                    }
                    else if (clicked)
                    {
                        state.AddToast($"Level flag edits need INGAME (state is {state.Session.State})", ToastKind.Error);
                    }
                }

                ImGui.PopID();
            }
        }

        ImGui.EndChild();
    }

    private static void Commit(AppState state, int index)
    {
        _editing = -1;
        if (!byte.TryParse(_editText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
        {
            state.AddToast($"'{_editText}' is not a hex byte", ToastKind.Error);
            return;
        }

        SendByte(state, index, value);
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
