# RaCMAN Reloaded

Thin cross-platform PC client for qwark, the PS3-side trainer. Read `../DESIGN.md` (section 4) and `../qwark/docs/PROTOCOL.md` (the wire contract) before changing anything.

## Build and run

```
dotnet build
dotnet test
dotnet run --project src/RaCMAN.App
```

.NET 8. `src/RaCMAN.Protocol` is the framing, opcode and telemetry library with no UI dependency; `src/RaCMAN.App` is the Dear ImGui application on ImGui.NET 1.91.6 + OpenTK 4.9.4 (GLFW window, OpenGL 3.3 backend written in-repo); `tests/RaCMAN.Protocol.Tests` is xUnit.

For end-to-end runs use the simulator from the sibling repo: build `../qwark` with `make host`, run `qwark-host.exe`, type `boot NPEA00385` on its stdin, and connect the client to 127.0.0.1.

## Rules

- No game logic and no address tables in this repo. Panels render from the last telemetry packet and the DESCRIBE reply, nothing else.
- Every control sends one request and shows the status code; never swallow a non-OK status.
- There is no client-side session state to restore on reconnect: HELLO, SUBSCRIBE, DESCRIBE, the list ops, done.
- Never touch ImGui from a thread other than the render thread; the network client hands results over through a queue.
- Do not commit or add attribution trailers unless asked.
