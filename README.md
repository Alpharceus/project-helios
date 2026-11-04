# Helios — a temperature overlay that runs your RGB as a heat gauge

A featherweight always-on-top CPU/GPU temperature, load and fan-speed overlay
for Windows that can also drive your whole RGB setup as a four-level
temperature gauge through OpenRGB. One C# source file, compiled with the C#
compiler that already ships inside Windows — no .NET SDK, no runtime to
install, no Electron. Measured cost: ~0.1 % of total CPU and ~75 MB RAM.

## Why this shape

- **Monitoring apps are heavy.** The vendor suites (Armoury Crate, SignalRGB,
  iCUE…) burn hundreds of MB and real CPU to draw a number. Helios is a single
  borderless WinForms window polling sensors every 2 s on a low-priority
  background thread.
- **Zero toolchain.** It builds with
  `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` — present on every
  Windows install since 2012. Clone, drop in the sensor DLLs, run one command.
  (That compiler speaks C# 5, so the source deliberately sticks to it.)
- **RGB with no resident cost.** Instead of animating LEDs from a daemon,
  Helios spawns the OpenRGB CLI *only when the temperature crosses a band
  boundary* — blue → green → yellow → red, with 3 °C of hysteresis so a temp
  sitting on a boundary can't flap the color. Between changes: zero extra
  processes, zero RAM, zero CPU.

## Features

- Always-on-top overlay: never steals focus, hidden from Alt-Tab, drag it
  anywhere, position and chosen monitor are remembered
- CPU and GPU temperature **and load %**, a RAM row, and every real fan with
  rpm **and % of its observed maximum** (self-calibrating — maxes are learned
  and persisted)
- Temperature alerts: overlay text turns red at the threshold, plus an
  optional topmost popup with sound — fired once per over-temp episode
  (re-arms 5 °C below the limit), not spammed
- Four-level RGB temperature gauge via OpenRGB (tray-toggleable; turning it
  off hands the LEDs back to your saved OpenRGB profile)
- Start-with-Windows via an elevated Task Scheduler logon task — no UAC
  prompt per boot
- Self-diagnostic: everything the sensor library sees is rewritten to
  `%APPDATA%\TempMonitor\sensors.txt` every ~30 s — first place to look when a
  reading seems wrong

## Hardware (what I run it on)

Ryzen 7 9800X3D · RTX 5070 Ti · ASUS X870 MAX GAMING WIFI7 (Nuvoton NCT6701D
SuperIO) · triple monitors, overlay on a portrait side panel · 2× RGB DDR5
sticks and the board's Aura controller driven through OpenRGB.

Fan-header names are generic on Nuvoton chips; the mapping of header → label
(e.g. "Fan #7" → "AIO Pump" on my board) is one obvious function
(`FriendlyFanName`) at the top of the source — edit to taste.

## GNOME edition (Linux)

`gnome/` is a GNOME Shell 50 extension of the same idea, for a Fedora laptop (Dell G15 5515: Ryzen 7 5800H,
RTX 3050 Ti, three 1080p panels). Wayland lets no ordinary app float over other windows, so Helios lives in the shell:

```sh
gnome/install.sh          # first install: log out and back in, then it's in the top bar
gnome/install.sh --remove
```

- **Top bar**: CPU and GPU temperature, colored as the same four-level gauge (blue → green → yellow → red, heating
  reacts at once, cooling waits out 3 °C of hysteresis). This laptop has no RGB Linux can drive, so the gauge *is*
  the color; the OpenRGB hook is still there (`"rgb": true`) for machines that have devices.
- **Dropdown**: CPU/GPU/iGPU temperature + load (GPU power too), RAM, both fans as rpm and % of the real max,
  NVMe, SODIMM and ambient temps; toggles for the overlay and alerts.
- **Overlay**: optional click-through panel on any monitor corner, always above windows.
- **Lock screen**: the readout stays on the lock screen (overlay card over the Atlas maps, display-only top-bar item).
- **Alerts**: critical GNOME notification once per over-temperature episode (re-arms 5 °C below).
- **Config**: `~/.config/helios/config.json`, written with defaults on first run (laptop defaults: alerts at 95 °C CPU /
  87 °C GPU, bands `60,75,88` / `55,68,80`).

What Linux made simpler: every sensor is plain sysfs (`k10temp`, `amdgpu`, `alienware_wmi`/`dell_smm` fans with a real
`fanN_max`, `nvme`), so no kernel driver, no elevation, no fan-max learning, and none of the LibreHardwareMonitor quirks
below. Reads are async (Gio worker threads) because firmware-backed fan reads can take milliseconds. The one cost trap
is NVIDIA: a one-shot `nvidia-smi` burns ~40 ms of CPU and an open NVML handle keeps an Optimus dGPU awake, so Helios
streams `nvidia-smi -lms` only while the dGPU is already driving a display and shows "asleep" otherwise.

## Not in this repo (deliberately)

The `build\` folder is git-ignored: it holds the compiled exe and third-party
DLLs you should fetch from their own sources.

| You need | Where to get it |
|---|---|
| `LibreHardwareMonitorLib.dll` 0.9.6 + its deps (`HidSharp`, `RAMSPDToolkit-NDD`, `DiskInfoToolkit`, `BlackSharp.Core`, `System.Memory`, `System.Buffers`, `System.Numerics.Vectors`, `System.Runtime.CompilerServices.Unsafe`, `System.Threading.AccessControl`) | Easiest: grab the [LibreHardwareMonitor 0.9.6 release](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases) zip and copy those DLLs out of it into `build\`. Or pull the `LibreHardwareMonitorLib` NuGet package (net472) and its dependency tree. |
| **PawnIO driver** | [pawnio.eu](https://pawnio.eu) — signed kernel driver LibreHardwareMonitor 0.9.5+ uses instead of WinRing0. Without it, CPU temps and motherboard fans silently read zero. |
| **OpenRGB** (optional) | [openrgb.org](https://openrgb.org) — only needed for the RGB gauge; everything else works without it. Mark `OpenRGB.exe` to run as administrator or SMBus devices (RGB RAM) won't respond. |

## Build

```bat
cd helios
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
  /out:build\TempMonitor.exe /win32manifest:src\app.manifest ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:build\LibreHardwareMonitorLib.dll src\Program.cs
```

The exe must live next to the DLLs, and must run elevated (the manifest
demands it — kernel-driver access for CPU temperature sensors). The binary is
named `TempMonitor.exe`; Helios is the project, TempMonitor is the process.

Right-click the overlay or its tray icon for everything: monitor picker,
alert thresholds, popup toggle, RGB gauge toggle, start-with-Windows, exit.

## Hard-won quirks

Things the sensor stack does that cost me an afternoon each, encoded in the
source so they don't cost you one:

1. **The NCT6701D fan-read bug (LHM 0.9.6).** Any `Computer` instance reads
   fan RPMs correctly for only its first ~4 seconds, then every fan reads 0
   forever — temps keep working, so it looks like your fans died. Fresh
   instances always read fine. Workaround: motherboard fans are read by a
   throwaway motherboard-only instance recreated every 10 s.
2. **`Computer.Close()` is process-global.** Closing *any* instance tears
   down the library's kernel-driver singleton and breaks every other live
   instance's CPU reads. The throwaway instances above are abandoned to the
   GC, never Close()d. Counterintuitive but deliberate.
3. **Elevation is not optional** — and it applies to OpenRGB too: USB
   lighting controllers work unelevated, but SMBus devices (RGB RAM) need
   admin, which produces the confusing "everything changes color except the
   RAM" failure.
4. **ASUS addressable zones only commit on apply-all.** Setting a single
   addressable header's zone does nothing on this Aura controller; profile
   loads and whole-device applies (which is all Helios uses) work fine.
5. **Concurrent SMBus scans wedge ENE DRAM controllers.** Two OpenRGB
   detections running at once (in my case: two Helios instances that survived
   across logon sessions, each spawning OpenRGB on the same band change) can
   collide on the SMBus hard enough that a stick's RGB controller stops
   ACKing entirely — SPD still reads, slots still show occupied, but the
   stick vanishes from OpenRGB until it fully loses power. Recovery is a
   shutdown with the PSU switched off for ~30 s; a warm reboot won't do it
   (standby power keeps the controller alive). Helios now guards against the
   root cause with a `Global\` single-instance mutex — session-local
   (`Local\`) mutexes don't stop a stale instance from a previous logon.
6. **Config keys** live in `%APPDATA%\TempMonitor\config.txt` — thresholds
   (`cpumax`, `gpumax`), RGB band boundaries (`rgbcpubands=50,60,72`,
   `rgbgpubands=45,55,70`), OpenRGB path (`rgbexe`), fallback profile name
   (`rgbprofile`), and the learned per-fan maxima (`fanmax.*`).

## License

MIT for everything in this repo. LibreHardwareMonitorLib is MPL-2.0 (used
unmodified as a referenced assembly); OpenRGB is GPL-2.0 and is only invoked
as an external, separately-installed program — neither is distributed here.
