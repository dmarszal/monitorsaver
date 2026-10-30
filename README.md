# Monitor Saver

A small Windows tray app that turns off individual monitors in a multi-monitor setup when they are not being used, and turns them back on when you return to them.

It is useful for reducing power use, heat and distraction, and for protecting OLED panels from burn-in caused by static content on screens you are not looking at.

---

## Contents

- [Features](#features)
- [Requirements](#requirements)
- [Building](#building)
- [Running](#running)
- [How it works](#how-it-works)
  - [Activity detection](#activity-detection)
  - [Screen content detection](#screen-content-detection)
  - [Turning monitors off](#turning-monitors-off)
  - [Waking monitors](#waking-monitors)
  - [Lock, sleep and display changes](#lock-sleep-and-display-changes)
- [Tray menu](#tray-menu)
- [Configuration](#configuration)
  - [Settings file](#settings-file)
  - [Global settings](#global-settings)
  - [Per-monitor settings](#per-monitor-settings)
  - [Monitor keys](#monitor-keys)
- [Choosing a power-off method](#choosing-a-power-off-method)
- [Troubleshooting](#troubleshooting)
- [Known limitations](#known-limitations)
- [Project structure](#project-structure)
- [Code overview](#code-overview)
- [Uninstalling](#uninstalling)

---

## Features

- Detects per monitor whether it is in use, based on:
  - mouse movement on that monitor,
  - keyboard / click input while the foreground window is on that monitor,
  - visible changes in the monitor's screen content (video, scrolling, windows opening...).
- Turns idle monitors off after a configurable time:
  - **DDC/CI power off** – sends a real standby command to the monitor over the video cable, or
  - **Black overlay** – covers the monitor with a black, click-through window (for monitors without DDC/CI support).
- Wakes a monitor when the mouse moves onto it, or optionally when its content changes.
- Every setting can be configured **per monitor** or left on the global default.
- Settings are tied to the physical monitor and port, so they survive `DISPLAY1`/`DISPLAY2` renumbering.
- Tray icon only — no main window, minimal CPU use.
- Optional start with Windows.
- Single executable, no installer, no runtime to install.

## Requirements

| Requirement | Notes |
|---|---|
| Windows 10 1703 or later / Windows 11 | Per-monitor DPI awareness (v2) is used for correct coordinates on mixed-DPI setups. |
| Windows 10 2004 or later (recommended) | Needed for the black overlay to be excluded from screen capture, so content changes can still be detected underneath it. |
| .NET Framework 4.x | Pre-installed on all supported Windows versions. |
| Monitors with DDC/CI enabled (optional) | Needed for real power-off. Without it, use the black overlay method. |

No .NET SDK or Visual Studio is required to build.

## Building

The app is a single C# file compiled with the C# compiler that ships with Windows (`csc.exe` from .NET Framework 4.x).

```bat
build.cmd
```

This produces `MonitorSaver.exe` next to the source file. Under the hood it runs:

```bat
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize /target:winexe ^
    /out:MonitorSaver.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll MonitorSaver.cs
```

> The built-in compiler supports C# 5 only, so the source avoids newer syntax such as string interpolation (`$"..."`), `?.`, `nameof` and `out var`.

If `MonitorSaver.exe` is running, exit it from the tray menu before rebuilding, otherwise the file is locked.

## Running

Double-click `MonitorSaver.exe`, or from a terminal:

```bat
MonitorSaver.exe
```

A monitor icon appears in the notification area (check the `^` overflow if it is hidden). Right-click it to open the menu. Only one instance can run at a time; starting it again does nothing.

To start it automatically at logon, enable **Start with Windows** in the tray menu.

**First run – recommended check:** for each monitor, open its submenu and use **Turn off now**, then move the mouse onto it to confirm it wakes up. If the monitor does not turn off, or does not come back, switch that monitor to **Method: black overlay**. See [Choosing a power-off method](#choosing-a-power-off-method).

---

## How it works

The app runs a timer every **500 ms**. Each monitor has a *last active* timestamp; any detected activity resets it. When a monitor's timestamp is older than its idle timeout, the monitor is turned off.

### Activity detection

**Mouse (every 0.5 s)**
The cursor position is compared with the previous tick. If the cursor moved, the monitor under it is marked active (and woken if it was off). A cursor that is parked and not moving does *not* keep a monitor active.

**Keyboard and clicks (every 0.5 s)**
Windows' "last input" counter (`GetLastInputInfo`) is compared with the previous tick. If it changed but the cursor did not move, the input was keyboard, a click or the scroll wheel. That activity is credited to the monitor showing the **foreground window**. So typing in a window on monitor B keeps B on, even if the cursor sits on monitor A.

### Screen content detection

Every **2 seconds**, for each monitor:

1. **Capture a thumbnail.** The monitor's region of the desktop is scaled down to **160 × 90** pixels using GDI `StretchBlt` in `HALFTONE` mode, which averages blocks of pixels. On a 2560 × 1440 screen each thumbnail pixel represents a 16 × 16 block, so tiny changes (blinking text cursor, clock digits) mostly average away.
2. **Compare with the previous thumbnail.** A thumbnail pixel counts as changed if any of its red, green or blue channels differ by more than **24** (out of 255). This ignores colour noise and minor shading.
3. **Apply the sensitivity threshold.** If the percentage of changed pixels (out of 14 400) is at or above the monitor's sensitivity setting, the monitor is marked active.

| Sensitivity | Threshold | Changed thumbnail pixels needed |
|---|---|---|
| High | 0.2 % | ~29 |
| Normal (default) | 0.5 % | ~72 |
| Low | 2 % | ~288 |
| Very low | 10 % | ~1 440 |

Typical results at *Normal*:

| Change | Detected as activity? |
|---|---|
| Playing video, scrolling, moving/opening windows | Yes |
| Toast notification appearing | Usually yes |
| Taskbar clock, blinking text cursor, small spinner | Usually no |

The mouse pointer is not included in captures, so it never counts as a content change.

### Turning monitors off

When a monitor has been idle for longer than its **Turn off after** setting and is not marked **Never turn off**, it is turned off with its configured method:

- **DDC/CI** – the app sends VCP feature `0xD6` (Power Mode) with value `4` (off / standby) to every physical monitor behind that display, using the Windows Monitor Configuration API (`dxva2.dll`). The panel and backlight actually turn off, but the display stays attached to Windows, so window layout is unaffected (see [Troubleshooting](#troubleshooting) for exceptions).
- **Black overlay** – a borderless black window covers the monitor. It is:
  - topmost, so it covers everything,
  - click-through and non-activating, so it never steals focus or input,
  - excluded from screen capture (`WDA_EXCLUDEFROMCAPTURE`), so the content detector still sees what is underneath.

If the DDC/CI command fails (monitor does not support it), the app automatically falls back to the black overlay for that monitor.

### Waking monitors

An off monitor is woken when:

- the mouse moves while the cursor is on it,
- keyboard / click input happens while the foreground window is on it,
- its content changes above the sensitivity threshold — **only if** *Wake when content changes* is on for that monitor,
- you choose **Wake now** or **Wake all monitors** in the tray menu,
- you uncheck **Enabled**, or exit the app.

Waking sends VCP `0xD6` value `1` (on) for DDC/CI, or closes the overlay. Most monitors take one to three seconds to light up after a DDC/CI wake.

### Lock, sleep and display changes

| Event | Behaviour |
|---|---|
| Session locked | Automatic turning off is paused. |
| Session unlocked | Pausing ends and every monitor's idle timer restarts. |
| System resumes from sleep | Monitor list is refreshed and idle timers restart. |
| Display configuration changes (monitor connected/disconnected, resolution or layout change) | Monitor list is refreshed; existing monitors keep their state and overlays are moved to the new bounds. |

---

## Tray menu

```
Monitor Saver
────────────────────────────────────────────
DELL U2720Q (DISPLAY1) [primary]  -  idle 0:12   ▸
LG ULTRAGEAR (DISPLAY2)  -  off                  ▸
HP E24 (DISPLAY3)  -  always on                  ▸
────────────────────────────────────────────
✓ Enabled
  Defaults for all monitors                      ▸
  Start with Windows
────────────────────────────────────────────
  Wake all monitors
  Exit
```

Each monitor line shows its name, Windows display number, `[primary]` for the primary display, and its state:

- `idle m:ss` – time since the monitor was last active,
- `off` – currently turned off by the app,
- `always on` – *Never turn off* is set.

### Monitor submenu

| Item | Description |
|---|---|
| Turn off now | Turns the monitor off immediately (useful for testing the method). |
| Wake now | Turns it back on. |
| Never turn off | Excludes this monitor from automatic turn-off. Activity is still tracked. |
| Method: DDC/CI power off | Use a real DDC/CI standby command (default). |
| Method: black overlay | Use a black covering window instead. |
| Turn off after ▸ | Idle time for this monitor: *Use default*, 1, 2, 5, 10, 15, 30 or 60 min. |
| Wake when content changes ▸ | *Use default*, On or Off. |
| Content change sensitivity ▸ | *Use default*, High, Normal, Low or Very low. |

Submenu titles show the current value and `(default)` when the monitor follows the global default.

### Global items

| Item | Description |
|---|---|
| Enabled | Master switch. Unchecking it wakes all monitors and stops automatic turn-off. |
| Defaults for all monitors ▸ | Default idle time, content waking and sensitivity, used by every monitor whose setting is *Use default*. |
| Start with Windows | Adds/removes `MonitorSaver.exe` in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. |
| Wake all monitors | Turns all monitors back on and restarts their idle timers. |
| Exit | Wakes all monitors and closes the app. |

The tray icon tooltip shows how many monitors are currently off, e.g. `Monitor Saver - 1 of 3 off`.

---

## Configuration

All settings are changed from the tray menu and saved immediately. You normally never need to edit the file by hand.

### Settings file

```
%APPDATA%\MonitorSaver\settings.ini
```

Plain `key=value` lines. Keys that are missing use their defaults. If you edit the file by hand, exit the app first, since it rewrites the whole file whenever a setting changes from the menu.

Example:

```ini
Enabled=1
TimeoutSec=300
WakeOnContent=1
ChangePct=0.5
DELD06F_5_10c0ae1_0_UID4629.TimeoutSec=600
DELD06F_5_10c0ae1_0_UID4629.Method=ddc
GSM77BD_5_10c0ae1_0_UID4357.Method=overlay
GSM77BD_5_10c0ae1_0_UID4357.ChangePct=2
HWP3095_5_10c0ae1_0_UID4353.Exclude=1
```

### Global settings

| Key | Type | Default | Description |
|---|---|---|---|
| `Enabled` | `0` / `1` | `1` | Master switch for automatic turn-off. |
| `TimeoutSec` | seconds | `300` | Default idle time before a monitor is turned off. |
| `WakeOnContent` | `0` / `1` | `1` | Default for whether content changes wake an off monitor. |
| `ChangePct` | percent | `0.5` | Default percentage of changed thumbnail pixels that counts as activity. |

### Per-monitor settings

Prefixed with the monitor's key (see below). Omitted keys fall back to the global value.

| Key | Type | Default | Description |
|---|---|---|---|
| `<key>.Exclude` | `0` / `1` | `0` | Never turn this monitor off. |
| `<key>.Method` | `ddc` / `overlay` | `ddc` | Power-off method. `ddc` falls back to the overlay if the command fails. |
| `<key>.TimeoutSec` | seconds | global `TimeoutSec` | Idle time for this monitor. |
| `<key>.WakeOnContent` | `0` / `1` | global `WakeOnContent` | Whether content changes wake this monitor. |
| `<key>.ChangePct` | percent | global `ChangePct` | Sensitivity for this monitor. |

The menu offers fixed values, but the file accepts any number, e.g. `TimeoutSec=90` or `ChangePct=1.25`. A non-standard value is shown as-is in the menu.

### Monitor keys

A monitor key is built from the monitor's device interface path, combining the monitor model code with the port instance it is connected to:

```
\\?\DISPLAY#DELD06F#5&10c0ae1&0&UID4629#{e6f07b5f-...}
            ───┬─── ─────────┬─────────
               │             └ port / connection instance
               └ EDID manufacturer + product code
→ DELD06F_5_10c0ae1_0_UID4629
```

This means:

- settings stay with the same monitor even when Windows renumbers `DISPLAY1`, `DISPLAY2`...,
- two identical monitors on different ports get different keys,
- moving a monitor to a different port or GPU output gives it a new key (configure it again),
- if the path cannot be read, the key falls back to the display name, e.g. `DISPLAY1`.

---

## Choosing a power-off method

| | DDC/CI power off | Black overlay |
|---|---|---|
| Power saving | Full (backlight / panel off) | OLED: near full. LCD: small (backlight stays on). |
| Burn-in protection | Yes | Yes (OLED pixels are off when black) |
| Monitor support needed | DDC/CI must be supported and enabled | None |
| Wake speed | 1–3 s (monitor dependent) | Instant |
| Risk | Some monitors won't wake via DDC/CI, or drop off the bus (see below) | None |

**Recommendation:** try DDC/CI first with **Turn off now** for every monitor. Keep it where it works reliably; use the black overlay elsewhere. On OLED monitors the overlay is often the better choice anyway — it is instant and the pixels are effectively off.

### Enabling DDC/CI

DDC/CI is usually a setting in the monitor's on-screen menu, often under *System*, *Setup* or *Others*, named "DDC/CI". It is typically on by default but some models ship with it off. Docks, KVM switches and some adapters (especially USB-C/DisplayPort to HDMI) may not pass DDC/CI through.

---

## Troubleshooting

**The monitor does not turn off with DDC/CI.**
DDC/CI is unsupported, disabled in the monitor's OSD, or blocked by a dock/adapter. The app normally falls back to the overlay automatically when the command fails, but some monitors accept the command and ignore it. Switch that monitor to **Method: black overlay**.

**The monitor turns off but immediately turns back on.**
Some monitors treat the standby command as "go to sleep until a signal arrives", and the GPU is still sending a signal. Use the black overlay for that monitor.

**The monitor turns off but does not wake up.**
Some monitors stop listening to DDC/CI while in standby. Press the monitor's power button, then switch that monitor to the black overlay method.

**Windows jump to another screen when a monitor turns off.**
Some DisplayPort monitors signal a disconnect (hot-plug) when entering standby, so Windows removes the display and rearranges windows. Disable the monitor's "DP deep sleep" / "Auto power off" / similar OSD option, or use the black overlay for that monitor.

**A monitor never turns off.**
Something on it keeps changing: an animated wallpaper, live chart, video ad, or a game/app in the background. Lower that monitor's **Content change sensitivity**, or set **Wake when content changes** to *Off* (content changes will then still keep an *on* monitor active, but cannot wake an *off* one). Also check it is not set to **Never turn off**, and that **Enabled** is checked.

**A monitor turns off while I'm watching video on it.**
The video may be DRM-protected and appear black to screen capture (e.g. some streaming services in browsers with hardware DRM), so it looks static. Increase that monitor's sensitivity, set a longer idle time for it, or mark it **Never turn off** while watching.

**Notifications keep waking a monitor.**
Set that monitor's sensitivity to *Low* or *Very low*, or turn off *Wake when content changes* for it.

**A monitor is still off after the app exited or crashed.**
If the app is killed (e.g. through Task Manager), it cannot send the wake command. Overlays disappear automatically with the process, but a monitor powered off with DDC/CI stays off. Press its power button — a newly started instance does not know which monitors the previous one turned off, so **Wake all monitors** and mouse movement will not wake it.

**The overlay is in the wrong place or the wrong size.**
This can happen right after a resolution or scaling change. Use **Wake now** and let it turn off again; the overlay is recreated with the current bounds.

**Start with Windows stopped working after I moved the app.**
The registry entry stores the full path to the exe. Uncheck and recheck **Start with Windows** from the new location.

---

## Known limitations

- The mouse pointer is not part of captures; pointer movement is only detected through the mouse check.
- DRM-protected or exclusive-fullscreen content may capture as black and therefore look static.
- Content that changes constantly in small ways (animated wallpapers, dashboards) can keep a monitor on indefinitely unless sensitivity is lowered.
- Keyboard activity is attributed to the foreground window's monitor; a window spanning two monitors counts for the one containing most of it.
- DDC/CI behaviour varies widely between monitor models; the app cannot verify that a monitor really turned off.
- Settings are per monitor *and port*; moving a monitor to another output requires configuring it again.
- On Windows versions before 10 2004, the overlay is captured as black, so content changes under an overlay cannot wake the monitor.

---

## Project structure

```
MonitorSaver.cs    – entire application source
build.cmd          – builds MonitorSaver.exe with the Windows built-in C# compiler
MonitorSaver.exe   – built application (output of build.cmd)
README.md          – this file
```

## Code overview

All code lives in `MonitorSaver.cs`:

| Type | Responsibility |
|---|---|
| `Native` | P/Invoke declarations for `user32`, `gdi32` and `dxva2` (monitor enumeration, cursor and input state, screen capture, DDC/CI, window styling). |
| `Ddc` | `SetPower(hMonitor, on)` – sends VCP `0xD6` to all physical monitors behind an `HMONITOR`. |
| `Overlay` | The black, topmost, click-through, capture-excluded form used by the overlay method. Ignores `WM_DPICHANGED` to keep exact physical bounds. |
| `Mon` | Runtime state of one monitor: handle, device name, key, bounds, last activity, off state, overlay, last thumbnail. |
| `Settings` | Loads/saves `settings.ini`; global defaults and per-monitor lookups with fallback (`TimeoutFor`, `WakeOnContentFor`, `ChangePctFor`, `Excluded`, `Method`). |
| `App` | Tray icon, menu, timer loop, activity and content detection, turn-off/wake logic, system event handling, autostart. Contains `Main`. |

Main loop (`App.Tick`, every 500 ms):

1. Refresh the monitor list if the display configuration changed.
2. Mouse moved → mark the monitor under the cursor active.
3. Other input without movement → mark the foreground window's monitor active.
4. Every 4th tick → capture thumbnails and mark monitors with enough change active.
5. Turn off monitors idle longer than their timeout (unless disabled, paused or excluded).
6. Update the tray tooltip.

Tunable constants in `App`:

| Constant | Value | Meaning |
|---|---|---|
| `TickMs` | 500 | Main loop interval (ms). |
| `ContentEveryTicks` | 4 | Content check every N ticks (2 s). |
| `ThumbW`, `ThumbH` | 160, 90 | Thumbnail size used for content comparison. |
| `PixelDiffThreshold` | 24 | Per-channel difference for a thumbnail pixel to count as changed. |

## Uninstalling

1. Uncheck **Start with Windows** (or delete the `MonitorSaver` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`).
2. Choose **Exit** from the tray menu.
3. Delete the program folder and `%APPDATA%\MonitorSaver`.
