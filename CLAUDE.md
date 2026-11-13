# RaCMAN Reloaded

Thin cross-platform PC client for qwark, the PS3-side trainer. Read `../docs/DESIGN.md` (section 4) and `../qwark/docs/PROTOCOL.md` (the wire contract) before changing anything.

## Build and run

```
dotnet build
dotnet test
dotnet run --project src/RaCMAN.App
```

.NET 8. `src/RaCMAN.Protocol` is the framing, opcode and telemetry library with no UI dependency; `src/RaCMAN.App` is the Dear ImGui application on ImGui.NET 1.91.6 + OpenTK 4.9.4 (GLFW window, OpenGL 3.3 backend written in-repo); `tests/RaCMAN.Protocol.Tests` is xUnit.

The release carries qwark's committed `dist/` (BUILD.md). Since qwark build 57 a qwark release ships everything its test builds had (the game modules `rac1.sprx` .. `rac4.sprx` and `tod.sprx`, `ui.pak` with Tools of Destruction's Lua helper, per-user settings, USB mods); the DEV bit is the only difference, so gate nothing in the client on it or on test-ness: panels follow the module's capability bits and op answers. `packaging/check-qwark-build.ps1` refuses a release dist without every module or with a pak lacking ToD's entries, and `packaging/stage-game-modules.ps1` stages the modules for a release as for a test build.

For end-to-end runs use the simulator from the sibling repo: build `../qwark` with `make host`, run `qwark-host.exe`, type `boot NPEA00385` on its stdin, and connect the client to 127.0.0.1.

## Rules

- **Never edit `README.md` without the user's explicit permission.** It is the user's quickstart, written by them to be readable for humans. User-facing detail goes in `USER-GUIDE.md` (or `docs/<topic>.md`); building, releasing and the repository layout go in `BUILD.md`.
- No game logic and no address tables in this repo. Panels render from the last telemetry packet and the DESCRIBE reply, nothing else. One approved exception (2026-11-13): `SaveFilePlanet.cs` knows where each game's savefile stores its current level, so the save file manager can sort by planet. Keep any further savefile-format knowledge there, and ask before adding other game knowledge.
- Every control sends one request and shows the status code; never swallow a non-OK status.
- There is no client-side session state to restore on reconnect: HELLO, SUBSCRIBE, DESCRIBE, the list ops, done.
- Never touch ImGui from a thread other than the render thread; the network client hands results over through a queue.
- Nothing the user makes goes beside the executable. `AppPaths.Root` is the data folder and holds all of it; the application folder is what the release ships and the updater replaces it whole. New files belong on one side or the other, deliberately.
- No test and no headless run may reach the network. The update check is behind `UpdateService.Allowed`, which `--exit-after`, the fake-server flags and `--no-update-check` turn off, and the test run has its own data folder (`TestDataFolder`).
- Do not commit or add attribution trailers unless asked.
