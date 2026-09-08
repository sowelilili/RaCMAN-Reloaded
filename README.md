# RaCMAN Reloaded

RaCMAN Reloaded is the PC client for [qwark](../qwark/), the PS3-side trainer for the Ratchet & Clank PS3 games. It is a thin client: every panel renders what the console reports, and every control sends one request to the console and shows the answer. Game logic and addresses live in qwark, not here.

Runs on Windows, Linux and macOS (.NET 8, Dear ImGui through ImGui.NET, OpenTK).

## Getting started

1. Load qwark on the console (the Connection panel can upload `qwark.sprx` and load it through webMAN; see the qwark README).
2. Enter the console's IP and connect. The status line shows the session state, the running game and the qwark version.
3. Start a supported game. The panels fill in from the console's own description of the game.

If the connection drops, the client reconnects on its own; there is nothing to restore because the console keeps all the state.

## Panels

- **Game**: the everyday controls for the running game: the player values as an editable table on top, then Cheats, Player and Savefile stacked below it, each toggle with an "on boot" (auto-apply) box. The less-used sections are sub-pages listed under Game in the side nav: Manips (category setup and manipulation helpers), Collectables, Cosmetics and Debug.
- **Positions and planets**: eight position slots per planet, planet loading with reset flags, die.
- **Unlocks** and **Level flags**: per-game tables and a hex view of the flag region.
- **Memory**: viewer, watches (live in telemetry), freezes, raw instruction patches, and the moby table.
- **Mods**: the local library in `mods/<TITLEID>/`, uploaded to the console the first time a mod is used or when it changes; auto-apply flags; ZIP install.
- **Combos**: controller combos executed on the console.
- **Input display**: the 21 controller skins from RaCMAN.
- **Save files**: a category library in `savefiles/<TITLEID>/<category>/`, moved through the game's savefile helper: the two ACTIONs the console flags SAVE_ASIDE and LOAD_ASIDE, plus the generic file ops.
- **Settings**: local PC preferences. Light (default) or dark theme, and a "show debug information" switch that reveals the protocol-level detail (tick and generation counters, opcode hints, table addresses) that is hidden otherwise. Also reloads `gamelayout.json` without a restart.

When the game reboots, the console keeps read-only watches and asks, through this client, whether to re-apply anything that writes memory (toggles, freezes, patches, mods). Nothing that writes is re-applied silently unless its auto flag is set.

## Customising the Game page

The Game page's layout is owned by the client, not the console. qwark's DESCRIBE groups are the default; `data/gamelayout.json` (shipped, and yours to edit) overrides it. `sideSections` names the sections that become sub-pages under Game in the side nav; everything else stacks on the Game page. The per-game entries are keyed by game (`rac1`, `rac2`, `rac3`, `rac4`), so the disc release BCES01503, which hosts three games under one title id, gets the right layout for whichever game is running; an entry keyed by a title id is honoured first if you want one. In each entry a `moves` table sends a feature (by its exact label) to a named section, creating it if needed, and `tabOrder` sets the order. VALUE features default to the editable table at the top, but a move can pull one into a section (that's how QE ends up under Debug). Changing the layout never needs a qwark rebuild; the app reads the file on start, and the Settings panel can reload it.

## Telemetry and the firewall (Windows)

The console streams live state (readouts, toggle state, the pad for combos) to the PC over UDP. Windows Firewall blocks unsolicited inbound UDP for a freshly unzipped, unsigned app, often without showing a prompt. On first run the client offers to add the rule; if you decline it keeps working by falling back to slower TCP polling, and the Connection panel then shows a one-click **Allow inbound UDP through Windows Firewall** button. Either way it asks for administrator approval and only adds an inbound rule for RaCMAN. You can also double-click **Allow through Firewall.cmd**, or remove the rules later with `windows-firewall.ps1 -Remove`.

## Building

```
dotnet build
dotnet test
dotnet run --project src/RaCMAN.App
```

`publish.ps1` produces `../build/RaCMAN-Reloaded/` and `../build/RaCMAN-Reloaded.zip` with the app, the skins, the moby layout data, the mod library and `qwark.sprx`. `-All` adds linux-x64 and osx-x64, each as its own complete folder and zip.

For development without a console, build the qwark host simulator (`../qwark/build-host.sh`), run `qwark-host.exe`, type `boot NPEA00385`, and connect the client to `127.0.0.1`. `dotnet run --project src/RaCMAN.App -- --help` lists the flags used for headless checks (`--fake-server`, `--fake-script`, `--connect`, `--panel`, `--exit-after`). `--fake-script "2:quit,3:xmb,4:boot"` drives the in-process fake console through a session change, which is how the "side panels never outlive the game" rule is checked without hardware.

## Layout

- `src/RaCMAN.Protocol`: framing, opcodes, records and the client; no UI dependency.
- `src/RaCMAN.App`: the ImGui application and its panels.
- `tests/RaCMAN.Protocol.Tests`: xUnit tests, including a fake qwark server.
- `controllerskins/`: input display skins.
