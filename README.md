# VoicemeeterAgent

A Windows tray app that keeps the Windows master volume and a Voicemeeter Banana strip in sync,
and keeps Voicemeeter's output buses attached only to devices that actually exist.

## Features

**Two-way volume sync.** Moving the Windows volume slider moves the Voicemeeter fader, and moving
the fader moves the Windows slider. Echo filtering stops the two from fighting each other.

**Amplitude-linear volume curve.** Windows volume scalar maps to gain as `20·log10(scalar)`:
100% = 0 dB, 50% = −6 dB, 25% = −12 dB. Halving the slider halves the amplitude, the same as any
other volume control. The floor is configurable (`MinGainDb`, default −60 dB).

**Bus device watchdog.** A powered-off monitor or an unplugged speaker drops out of Voicemeeter's
device list, but Voicemeeter keeps re-probing the missing device every few seconds, which clips
audio on the buses that are still working. When the watchdog is on, the agent detaches a bound
device while it is missing and re-attaches it once it comes back, with no clipping in between.

**Runs at startup.** Optional, via the tray menu.

## Requirements

- Windows 10/11
- [Voicemeeter Banana](https://vb-audio.com/Voicemeeter/banana.htm)
- .NET 8 desktop runtime (to build: .NET 8 SDK)

## Build

```
dotnet build VoicemeeterAgent.sln -c Release
```

Release builds copy a self-contained folder to `releases/VoicemeeterAgent-v<version>/`.
The build closes any running instance first, since a running agent locks the output exe.

## Usage

Run `VoicemeeterAgent.exe`. It lives in the tray with no window.

Right-click the tray icon for:

| Item | What it does |
|---|---|
| Sync Now | Pushes the current Windows volume to Voicemeeter |
| Bus Devices ▸ A1/A2/A3 | Binds a bus to an output device (or `(none)`) |
| Auto-reconnect bus devices | Turns the watchdog on or off |
| Start with Windows | Adds/removes the HKCU Run entry |

### Watchdog setup

1. Assign your devices in Voicemeeter itself as usual.
2. In the tray: **Bus Devices → A2 → \<your monitor speakers\>**, matching what Voicemeeter shows.
3. Tick **Auto-reconnect bus devices**.

Power the monitor off and the bus detaches within ~5 s. Power it back on and the bus re-attaches
after the device has been visible for two consecutive checks.

## Configuration

`%APPDATA%\VoicemeeterAgent\config.json`

```json
{
  "MinGainDb": -60,
  "DeviceWatchdogEnabled": true,
  "DeviceWatchdogIntervalMs": 5000,
  "Buses": [
    { "Bus": 1, "Type": 3, "Name": "Monitor speakers" }
  ]
}
```

- `Bus` — 0 = A1, 1 = A2, 2 = A3
- `Type` — 1 = MME, 3 = WDM, 4 = KS, 5 = ASIO
- `Name` — exact device name as Voicemeeter reports it

## Layout

```
src/VoicemeeterAgent/   application source
docs/                   Voicemeeter Remote API reference
archive/                superseded material from the original AudioRouter project
releases/               built, self-contained release folders
```
