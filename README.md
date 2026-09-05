# VoicemeeterAgent

A Windows tray app that keeps the Windows master volume and a Voicemeeter Banana strip in sync,
and keeps Voicemeeter's output buses attached only to devices that actually exist.

## Features

**Two-way volume sync.** Moving the Windows volume slider moves the Voicemeeter fader, and moving
the fader moves the Windows slider. Echo filtering stops the two from fighting each other.

**Switchable volume curves.** How the slider maps to gain is a matter of taste and of how loud the
speakers are, so the curve is a tray menu choice rather than a hard-coded formula. See
[Volume curves](#volume-curves).

**Bus device watchdog.** A powered-off monitor or an unplugged speaker drops out of Voicemeeter's
device list, but Voicemeeter keeps re-probing the missing device every few seconds, which clips
audio on the buses that are still working. When the watchdog is on, the agent detaches a bound
device while it is missing and re-attaches it once it comes back, with no clipping in between.

**Runs at startup.** Optional, via the tray menu.

## Install

Download the latest zip from [Releases](https://github.com/DhakadG/voicemeeter-agent/releases),
unpack it anywhere, and run `VoicemeeterAgent.exe`. It lives in the tray with no window.

Requires Windows 10/11, [Voicemeeter Banana](https://vb-audio.com/Voicemeeter/banana.htm), and the
.NET 8 desktop runtime.

## Usage

Right-click the tray icon for:

| Item | What it does |
|---|---|
| Sync Now | Pushes the current Windows volume to Voicemeeter |
| Volume Curve | Picks the slider-to-gain curve and the gain floor |
| Bus Devices ▸ A1/A2/A3 | Binds a bus to an output device (or `(none)`) |
| Auto-reconnect bus devices | Turns the watchdog on or off |
| Start with Windows | Adds/removes the HKCU Run entry |

The status dot at the top is green when Voicemeeter is connected and red when it is not.

### Volume curves

Every curve reaches 0 dB at 100% and the floor at 0%. They differ in how the dB range is spread
across the slider, which is what decides how big each volume step feels.

| Curve (`Profile` value) | 25% | 50% | 75% | 90% | Feel |
|---|---|---|---|---|---|
| Knee (`Knee`) | −40 dB | −20 dB | −10 dB | −4 dB | Quiet range passes quickly, loud range gets twice the resolution of Linear dB |
| Linear dB (`LinearDb`) | −45 dB | −30 dB | −15 dB | −6 dB | Even 6 dB per 10% everywhere. The v1.0 curve |
| Squared (`Gamma2`) | −24 dB | −12 dB | −5 dB | −1.8 dB | Between the two below |
| Amplitude (`Amplitude`) | −12 dB | −6 dB | −2.5 dB | −0.9 dB | Slider position is the amplitude ratio. The v1.1 curve |

The floor (gain at 0%) is selectable at −40, −50 or −60 dB. Raising the floor spreads less dB over
the same slider, so every step gets smaller.

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
  "Profile": "Knee",
  "DeviceWatchdogEnabled": true,
  "DeviceWatchdogIntervalMs": 5000,
  "Buses": [
    { "Bus": 1, "Type": 3, "Name": "Monitor speakers" }
  ]
}
```

- `Profile` — `Knee`, `LinearDb`, `Gamma2` or `Amplitude`
- `MinGainDb` — clamped on load to −96..−24 dB; values outside that break the curve inverses
- `Bus` — 0 = A1, 1 = A2, 2 = A3
- `Type` — 1 = MME, 3 = WDM, 4 = KS, 5 = ASIO
- `Name` — exact device name as Voicemeeter reports it

## Build

```
dotnet build VoicemeeterAgent.sln -c Release
```

Output lands in `releases/VoicemeeterAgent-v<version>/` as a self-contained folder. The build closes
any running instance first, since a running agent locks the output exe.

`VoicemeeterAgent.exe --selftest` checks that every volume curve is anchored at both ends, is
monotonic, and round-trips; it exits 0 on success and runs in CI on every push.

Pushing a `v*` tag builds a Release zip and publishes it to GitHub Releases.

## Layout

```
src/VoicemeeterAgent/   application source
docs/                   Voicemeeter Remote API reference
archive/                superseded material from the original AudioRouter project
```
