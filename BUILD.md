# RaCMAN Reloaded: developer guide

The [README](README.md) is the quickstart and the [user guide](USER-GUIDE.md) has the details for users. This guide covers building, releasing and the repository's layout.

## Building

```
dotnet build
dotnet test
dotnet run --project src/RaCMAN.App
```

`publish.ps1` makes `../build/RaCMAN-Reloaded/` and a zip of it. It takes `qwark.sprx`, `qwark.build`, `ui.pak` and `qwark-rpcs3.exe` from `../qwark/dist/`, which qwark commits, so a release needs no PS3 SDK. From a test dist it also takes the game modules, `rac1.sprx` to `rac4.sprx` and `tod.sprx`, each named for its game (qwark's `docs/PROTOCOL.md` 9.11), through `packaging/stage-game-modules.ps1`. That script stages them only when `qwark.build` says `dev 1`. A dist with none stages none and does not fail. A release dist's leftover modules are never staged. It puts `qwark-installer.pkg` from there beside the zip. `-All` adds linux-x64 and osx-x64.

### Test builds and release builds of qwark

Every qwark build between two releases reports the same build number, so the module also says whether it is a test build and which one (qwark's `docs/PROTOCOL.md` 3.1.1). `make` writes `qwark.build` beside `qwark.sprx`: the build number, `dev 1` or `dev 0`, and the build ID, a CRC of qwark's sources. The client reads the `qwark.build` beside the `qwark.sprx` it sends. When the console runs a test module whose ID is not the shipped one's, or a release module of the same number while the client ships a test build, the client treats it as out of date and replaces it as it replaces an older build: by itself in standalone mode, with the out-of-date warning and the Load button in webMAN mode. When the IDs match, the module is current and nothing is sent again. Release modules of the same build are judged by the number alone, as before. So is `qwark-rpcs3.exe`, which the client starts from beside itself. Without a `qwark.build`, the number alone decides.

- **A test dist:** in qwark, `make` (or `_Make.bat`), `./build-host.sh`, then `make dist`. This is the default: a plain `make` is a test build. `make dist` says which kind it copied.
- **A release dist:** in qwark, `./build-host.sh`, then `make release` (or `_Make.bat release`). This builds the module without the test flag (`QWARK_DEV=0`) and runs `make dist`. Commit `dist/`. A release links its games in, so its `dist/` has no game modules (`rac1.sprx` to `rac4.sprx`, `tod.sprx`); delete any a test build left there.

To work without a console, build the qwark simulator with `../qwark/build-host.sh`. Run `qwark-host.exe`, type `boot NPEA00385`, and connect the client to `127.0.0.1`. `dotnet run --project src/RaCMAN.App -- --help` lists the flags for headless runs.

## Releasing

1. Build a release dist in the qwark repository (`make release`, above) and commit `dist/`: `qwark.sprx`, `qwark.build`, `ui.pak`, `qwark-installer.pkg` and `qwark-rpcs3.exe`. The release fails without them, and it refuses a `dist/` whose `qwark.build` says `dev 1`, or that holds any game module (`rac1.sprx` to `rac4.sprx`, `tod.sprx`) or an older test build's `game-*.sprx` (`packaging/check-qwark-build.ps1`).
2. Run `.\release.ps1 X.Y.Z`. It checks both repositories, rebuilds the release module to compare it with `dist/`, runs the tests, then tags both repositories and pushes the tags. Or tag a commit here `vX.Y.Z` and push the tag yourself.

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
- `docs/`: the game layout reference, the firewall notes, the standalone module update, the OBS page and the features in test builds only.
