# Remote Pitwall

```
SimHub plugin --WebSocket--> Go relay --SSE--> browser
```

~8 MB image, ~2 MB RAM, one HTML file, no framework.
Fun side project: a remote pitwall for whoever's watching me drive.

## Stack

- **Plugin** (`plugin/`): C# SimHub plugin (.NET Framework 4.8). Reads live data at 20 Hz, tracks the laps of the current session and pushes everything over one WebSocket. Reconnects on its own.
- **Relay** (`server/`): Go, stdlib plus [coder/websocket](https://github.com/coder/websocket). Accepts the plugin (token auth), fans frames out to viewers via Server-Sent Events and keeps the latest state in memory so late joiners see the whole session.
- **Frontend** (`server/index.html`): a single page embedded in the relay binary. Vanilla JS, a canvas for the input trace.
- **Hosting**: Docker container behind Caddy on a 1 vCPU / 1 GB VPS.

## What the page shows

- Gear, speed, RPM, throttle/brake/clutch
- Position, lap, current/last/best lap, live delta to session best
- Fuel, estimated laps left, air and track temperature, flags, pit lane
- Tyre temperature, pressure and wear
- Throttle/brake/speed trace of the last 20 seconds
- Lap table for the session (time, sectors, fuel used)

## SimHub setup

### 1. Build the plugin

You need SimHub and the [.NET SDK](https://dotnet.microsoft.com/download) 8 or newer (`winget install Microsoft.DotNet.SDK.8`).

```powershell
git clone https://github.com/stephanrutter/remote-pitwall
cd remote-pitwall\plugin
dotnet build -c Release
```

The build references the DLLs from your SimHub install. If SimHub is not in `C:\Program Files (x86)\SimHub`:

```powershell
dotnet build -c Release -p:SimHubDir="D:\Games\SimHub"
```

### 2. Install it

1. Close SimHub.
2. Copy `plugin\bin\Release\net48\RemotePitwall.dll` into the SimHub folder (next to `SimHubWPF.exe`).
3. Start SimHub. It detects the new plugin and asks whether to enable **Remote Pitwall**; say yes.
   If it does not ask, enable it under Settings > Plugins.

### 3. Connect it

1. Open **Remote Pitwall** in SimHub's left menu.
2. **Server URL**: the address of your relay, e.g. `https://pitwall.example.com`.
   The plugin connects to `wss://<host>/pub` (`ws://` for `http://` URLs).
3. **Token**: the relay's `TOKEN`.
4. Click outside the field to apply; the plugin reconnects with the new values.

The status line tells you what is going on:

| Status | Meaning |
|---|---|
| Connected, waiting for a game | Connected to the relay, no game running yet |
| Streaming | Data is going out, the page should show LIVE |
| Error: ... 401 ... | Token does not match the relay |
| Error: (name or connection error) | Wrong URL or the relay is not reachable |

### Missing values

The plugin reads SimHub properties by name. If a value stays "–" on the page, the game does not provide it or the property name differs.
Look up the right name in SimHub under **Available properties** and fix it in `TickFields` at the top of `plugin/RemotePitwallPlugin.cs`.
To add a new field, add a line there and an element in `server/index.html` that `onTick` fills.

## Protocol

- Plugin: WebSocket to `/pub` with `Authorization: Bearer <TOKEN>`, text messages `tick {json}` (20 Hz) and `state {json}` (session info and lap list, on change and after every reconnect).
- Viewers: `GET /events` (SSE, events `tick` and `state`).
