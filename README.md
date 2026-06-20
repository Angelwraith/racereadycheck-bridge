# RaceReadyCheck Bridge

A tiny, optional companion for [RaceReadyCheck](https://racereadycheck.com). It does two things, and **only** these:

1. **Global hotkeys** — press a key (default **F8**) to toggle your *ready* status in your lobby even while Forza is fullscreen, and **F9** (host) to start the countdown.
2. **Optional telemetry relay** — if you turn it on, it reads Forza's "Data Out" UDP stream on your PC and serves it to the RaceReadyCheck tab in your browser **over localhost only**.

The website works fully without this app. The bridge is just convenience for in-game hotkeys and (optionally) a live telemetry readout.

## Why you can trust it

This is the whole point of the app being open source and minimal:

- **Hotkeys use `RegisterHotKey`, not a keyboard hook.** The app asks Windows to notify it about *only* the specific keys you configure (F8/F9). It cannot see, log, or transmit any other keystroke. (See `Modules/HotkeyModule.cs` — it's ~150 lines.) This is deliberately *not* a `keyboard`-hook/keylogger design.
- **No admin rights.** The manifest runs `asInvoker`. If anything ever asks you to "run as administrator," that's not this app.
- **Telemetry never leaves your PC.** When enabled, telemetry is served from `http://127.0.0.1:<port>` and read by your own browser tab. It is **never** sent to the RaceReadyCheck server. (See `Modules/TelemetryModule.cs`.)
- **The only thing sent to the server** is a hotkey *action* string (e.g. `ready_toggle`) plus your bridge token. (See `RelayClient.cs`.) The token is per-device, scoped to ready-toggle + room state, and you can revoke it on the website at any time.
- **No NuGet packages, no network downloads at build or run time** beyond calling the RaceReadyCheck API you already use. Framework code only (WinForms tray + `System.Net`).

You are encouraged to read the source before running it. It's short on purpose.

## Architecture (and how to extend it)

The app is a tray host that runs a list of **modules**. Each module implements `IBridgeModule` (`Start`/`Stop`/`Status`). Today there are two — `HotkeyModule` and `TelemetryModule`. Adding a new capability is just: implement the interface and add one line in `BridgeContext`.

```
Program.cs            entry point + tray icon + module registration
Config.cs             bridge.config.json (token, hotkeys, telemetry settings)
RelayClient.cs        the ONLY thing that talks to the RaceReadyCheck server
Modules/
  IBridgeModule.cs    the extension contract
  HotkeyModule.cs     RegisterHotKey -> POST /api/bridge/hotkey.php
  TelemetryModule.cs  Forza UDP -> parse -> localhost SSE for the browser
```

## Setup

1. **Get your token:** on racereadycheck.com, open your account menu → **Connect in-game bridge** → **Generate device token** (shown once).
2. **Run the exe.** On first launch a small **paste-token** window appears — paste the token and click **Save**. (You can reopen it any time from the tray icon → **Set bridge token…**.)
3. **Go.** Join a lobby on the website, then press **F8** in-game to ready up. (Power users can still hand-edit `bridge.config.json` via tray → Edit config.)

### Hotkeys
Rebind from the tray menu (**Hotkeys…**) or edit the `Hotkeys` map in the config. Keys can be `F1`–`F24`, letters, or digits, with optional `Ctrl`/`Alt`/`Shift`/`Win`, e.g. `"Ctrl+F9": "host_start"`. Actions:
`ready_toggle`, `ready_on`, `ready_off`, `host_start` (leader), `host_reset` (leader), `host_roll` (leader: random race), and `telemetry_record` (local: start/stop recording a session).

### Telemetry (optional)
1. In Forza: **Settings → HUD/Gameplay → Data Out → ON**, IP `127.0.0.1`, Port `5300` (or match `UdpPort`).
2. In the tray menu, click **Enable telemetry**.
3. The browser tab reads `http://127.0.0.1:5390/telemetry` — a full decode of the 324-byte FH6 "Car Dash" packet (all 89 fields; see `../telemetry/parser.py`). Telemetry stays on localhost and is never sent to the server.

**Recording:** press **F6** (or tray → **Record telemetry**) to capture a session to `%APPDATA%\RaceReadyCheck\telemetry\YYYYMMDD-HHMMSS.flog`. The file format is byte-identical to the original RaceReadyCheck app (`../telemetry/logger.py`): magic `FH6LOG\x00\x01` + `uint32` version, then `uint64` ns-timestamp + `uint16` length + raw payload per packet — so existing analysis tools read it unchanged.

## Build

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download). No other dependencies.

```bash
# run from source
dotnet run

# produce a single self-contained exe (no .NET install needed to run it)
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
# output: bin/Release/net8.0-windows/win-x64/publish/RaceReadyCheckBridge.exe
```

## Status

- Hotkey relay: **working** (v0.1).
- Telemetry relay: **functional, experimental** — parses the FH6 324-byte "Car Dash" packet (same format as the original RaceReadyCheck app) and streams a compact JSON snapshot to localhost. The website-side live view will consume it in a later release.

Not affiliated with Microsoft or Playground Games. "Forza" is a trademark of Microsoft.
