# RaCMAN Reloaded: developer guide

The [README](README.md) is the quickstart and the [user guide](USER-GUIDE.md) has the details for users. This guide covers building, releasing and the repository's layout.

## Building

```
dotnet build
dotnet test
dotnet run --project src/RaCMAN.App
```

`publish.ps1` makes `../build/RaCMAN-Reloaded/` and a zip of it. It takes `qwark.sprx`, `ui.pak` and `qwark-rpcs3.exe` from `../qwark/dist/`, which qwark commits, so a release needs no PS3 SDK. It puts `qwark-installer.pkg` from there beside the zip. `-All` adds linux-x64 and osx-x64.

To work without a console, build the qwark simulator with `../qwark/build-host.sh`. Run `qwark-host.exe`, type `boot NPEA00385`, and connect the client to `127.0.0.1`. `dotnet run --project src/RaCMAN.App -- --help` lists the flags for headless runs.

## Releasing

1. Build `qwark.sprx`, `ui.pak`, `qwark-installer.pkg` and `qwark-rpcs3.exe` in the qwark repository and commit them to its `dist/` folder. The release fails without them.
2. Tag a commit here `vX.Y.Z` and push the tag.

`.github/workflows/release.yml` then builds the client, packs it with [Velopack](https://docs.velopack.io) and uploads it. The Windows job makes the release. The Linux job adds its AppImage to it. The version comes from the tag.

A release carries `Setup.exe`, the portable zip, the AppImage, `qwark-installer.pkg`, the `.nupkg` packages and the `releases.win.json` and `releases.linux.json` files. Do not delete those two files from a release. The client reads them to find updates.

There is no macOS job yet. macOS needs signing and notarisation first.

## Repository layout

- `src/RaCMAN.Protocol`: framing, opcodes, records and the client. No UI code.
- `src/RaCMAN.App`: the ImGui application and its panels.
- `tests/RaCMAN.Protocol.Tests`: xUnit tests, with a fake qwark server.
- `controllerskins/`: input display skins.
- `mods/`: the mod library each release ships.
- `packaging/`: the Windows firewall helper and the AppImage icon.
- `docs/`: the game layout reference, the firewall notes, the standalone module update and the OBS page.
