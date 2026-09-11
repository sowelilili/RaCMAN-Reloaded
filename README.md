# RaCMAN Reloaded

A modding tool and trainer for the original Ratchet & Clank quadrilogy, on PS3 and RPCS3, tailored for speedrunners. It's
- Standalone: Powered by the qwark server, which runs locally on your console for better performance, with an optional PC client.
- Seamless: connection, reconnection and switching games is seamless and happens automatically.
- Portable: supports Windows and Linux (macOS coming soon?), PS3 and RPCS3.
- Customizable: and it even has a dark mode :)

## Installation

Download the newest release from the [releases page](https://github.com/sowelilili/RaCMAN-Reloaded/releases). Each release contains the client, the console module `qwark.sprx` and the RPCS3 helper.

- **Windows, installer**: run `RaCMANReloaded-win-Setup.exe`. It installs for your user account only. It does not need administrator rights.
- **Windows, portable**: unzip `RaCMANReloaded-win-Portable.zip` and run `RaCMAN.App.exe`.
- **Linux**: download `RaCMANReloaded-linux.AppImage`, make it executable and run it.

## Connect to a PS3

1. Start the console and make sure webMAN runs on it.
2. Type the console's IP address into the client.
3. Press **Connect**.
4. Start one of the four games.

The client finds the module on the console, sends it if no plugin slot holds it, and connects. Each step shows a message. If a step fails, the message tells you which step failed.

The panels fill with the controls for the game that runs. If the connection stops, the client connects again by itself. The console keeps the state, so nothing is lost.

## Connection modes

The mode is on the Settings panel, in the **Connection** section.

**webMAN** is the default mode. The client asks webMAN which plugin slot holds the module. If no slot holds it, the client sends `qwark.sprx` to the console and loads it. Automatic reconnections do the same, so the client can recover a module that stopped.

**Standalone** never uses webMAN. The client only connects to the module's port. Use this mode if the console loads the module at startup.

Standalone mode adds an **Installation** part to the Connection panel. It makes the console load the module at startup. Press **Install to boot_plugins.txt**, then restart the console. The console reads that list only at startup. This one installation needs webMAN's FTP server. After it, the client does not need webMAN.

## Connect to RPCS3

1. In RPCS3, switch the IPC server on. It is in Manage, Network Services, IPC.
2. Start the game.
3. In the client, select **RPCS3** on the Connection panel.

The client starts `qwark-rpcs3.exe` beside it and connects to it on this PC. The panels behave as they do on a console, with one difference: RPCS3 recompiles the game code, so it cannot accept code patches. The client therefore switches off the mods, the save file manager and the patch cheats. Everything that writes data continues to work.

RPCS3 accepts one IPC client at a time. If another program holds the port, the client says so and waits for the port. RPCS3 support is for Windows at the moment.

## Updates

The client asks GitHub for a newer release once a day. If there is one, a bar appears at the top of the window. Press **Download**, then **Restart to update**. The client downloads nothing until you press the button.

The Settings panel holds the switch, a **Check now** button and the version you run. The installer and the portable build can update themselves. A folder that you built yourself cannot.

A new client can carry a newer console module. The console keeps the module it already loaded, so the header says that `qwark.sprx` is out of date. The Connection panel then tells you how to send the new one.

## Your files

Your settings, colour presets, watchlists, save files and mods are in the data folder. An update never touches it.

| | |
|---|---|
| Windows | `%APPDATA%\RaCMAN Reloaded` |
| Linux | `$XDG_CONFIG_HOME/racman-reloaded`, or `~/.config/racman-reloaded` |
| macOS | `~/Library/Application Support/RaCMAN Reloaded` |

The Settings panel has an **Open folder** button for it. To use a different folder, start the client with `--data-dir <path>`, or set the `RACMAN_DATA_DIR` variable. A copy on a memory stick needs one of the two.

You can put your own mods in `mods/<TITLEID>/` in that folder. A mod of yours replaces a shipped mod with the same folder name.

## Windows firewall

The console sends live data to the client over UDP. Windows blocks that data for a new, unsigned application. At the first start the client offers to add a rule for itself. If you refuse, the client uses TCP instead, which is slower, and the Connection panel keeps a button to add the rule later.

## Customising the Game page

The client, not the console, decides where each control is drawn. `data/gamelayout.json` holds that layout. You can move a feature to another section, and make a section into a sub-page or a tab. See [docs/gamelayout.md](docs/gamelayout.md).

## Building

```
dotnet build
dotnet test
dotnet run --project src/RaCMAN.App
```

`publish.ps1` makes `../build/RaCMAN-Reloaded/` and a zip of it. It takes `qwark.sprx` and `qwark-rpcs3.exe` from `../qwark/dist/`, which qwark commits, so a release needs no PS3 SDK. `-All` adds linux-x64 and osx-x64.

To work without a console, build the qwark simulator with `../qwark/build-host.sh`. Run `qwark-host.exe`, type `boot NPEA00385`, and connect the client to `127.0.0.1`. `dotnet run --project src/RaCMAN.App -- --help` lists the flags for headless runs.

## Releasing

1. Build `qwark.sprx` and `qwark-rpcs3.exe` in the qwark repository and commit them to its `dist/` folder. The release fails without them.
2. Tag a commit here `vX.Y.Z` and push the tag.

`.github/workflows/release.yml` then builds the client, packs it with [Velopack](https://docs.velopack.io) and uploads it. The Windows job makes the release. The Linux job adds its AppImage to it. The version comes from the tag.

A release carries `Setup.exe`, the portable zip, the AppImage, the `.nupkg` packages and the `releases.win.json` and `releases.linux.json` files. Do not delete those two files from a release. The client reads them to find updates.

There is no macOS job yet. macOS needs signing and notarisation first.

## Repository layout

- `src/RaCMAN.Protocol`: framing, opcodes, records and the client. No UI code.
- `src/RaCMAN.App`: the ImGui application and its panels.
- `tests/RaCMAN.Protocol.Tests`: xUnit tests, with a fake qwark server.
- `controllerskins/`: input display skins.
- `mods/`: the mod library each release ships.
- `packaging/`: the Windows firewall helper and the AppImage icon.
- `docs/`: the game layout reference.
