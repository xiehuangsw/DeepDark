<div align="center">

# DeepDark

**A tiny Windows dimmer that goes darker than your monitor's minimum brightness**

Single file · No installer · No admin rights · ~5 MB RAM · ~0.04% CPU · 0% GPU

[![Download latest](https://img.shields.io/badge/Download-DeepDark.exe-blue?style=for-the-badge&logo=windows)](https://github.com/xiehuangsw/DeepDark/releases/latest)

[![build](https://github.com/xiehuangsw/DeepDark/actions/workflows/build.yml/badge.svg)](https://github.com/xiehuangsw/DeepDark/actions/workflows/build.yml)
[![release](https://img.shields.io/github/v/release/xiehuangsw/DeepDark?include_prereleases)](https://github.com/xiehuangsw/DeepDark/releases)
[![license](https://img.shields.io/github/license/xiehuangsw/DeepDark)](LICENSE)

English · [简体中文](README.md)

</div>

---

Even at the lowest backlight setting your screen can still be too bright at night.
DeepDark adds another layer of dimming **below** the monitor's own minimum.

![Settings panel](assets/screenshot-panel.png)

## Why not just draw a black overlay?

Most dimmers cover the screen with a translucent black window. As a result, system UI such as
**Task View, virtual desktop switching and the Start menu** is composited *above* that window —
it flashes back to full brightness, which is exactly what you don't want at night.

DeepDark instead rewrites the display's **gamma lookup table**, which is applied by the GPU
*before scan-out*. Every surface — Task View, desktop switching, Start menu, UAC — stays at the
same dim level, with **no flicker and no leakage**.

If the system ignores the gamma table (for example when **HDR** is enabled), DeepDark
automatically falls back to a black overlay window and tells you so in the panel.

## Features

- **Driver-level dimming**: 0–95%, applied instantly, covers every system surface
- **Compatibility mode**: automatic black-overlay fallback when gamma is unavailable
- **5 global hotkeys, all customizable**, with automatic fallback if a combination is taken
- **Multi-monitor**, rebuilt automatically on plug/unplug or resolution change
- **Tray resident**: minimize to tray, right-click menu for quick adjustments
- **Optional auto-start**
- **Crash-safe**: after a hard kill it restores the original gamma before dimming again
- **One-click rescue** script to restore the screen if anything goes wrong

## Download

Grab `DeepDark.exe` from [Releases](https://github.com/xiehuangsw/DeepDark/releases), put it in a normal
folder (Desktop, `D:\DeepDark\`, …) and double-click. No installation, no admin rights.

> ⚠️ The binary is unsigned, so Windows SmartScreen may warn about an unknown publisher —
> choose "Run anyway", or build it yourself from source.

## Usage

### Settings panel

| Control | Meaning |
| --- | --- |
| Dim level slider | 0–95%, higher = darker, applied live |
| Click-through overlay | Only relevant in black-overlay mode; unchecking it makes the overlay capture clicks |
| Use black overlay | Force the compatibility (overlay) mechanism |
| Start with Windows | Adds a per-user Run entry |
| Minimize to tray | Minimize hides the window, keeps dimming active |
| Hide mask / Hotkeys… / Exit | Temporarily restore / customize hotkeys / exit and restore |

The window is resizable and scrollable with the mouse wheel.

### Hotkeys (customizable)

| Action | Default |
| --- | --- |
| Darker 5% | `Ctrl+Alt+Down` |
| Lighter 5% | `Ctrl+Alt+Up` |
| Show / hide mask | `Ctrl+Alt+M` |
| Show settings panel | `Ctrl+Alt+S` |
| Exit and restore | `Ctrl+Alt+Q` |

If a default combination is already taken, DeepDark falls back automatically and shows the key
that actually works. Click **"修改快捷键…" (Edit hotkeys)** to pick your own.

![Hotkey editor](assets/screenshot-hotkeys.png)

### Command line

```
DeepDark.exe                 reuse the last dim level
DeepDark.exe --level=70      set dim level 0-95
DeepDark.exe --restore       reset the gamma table to normal
```

Launching `DeepDark.exe` again brings up the panel of the running instance (single instance).

## Compatibility

| | |
| --- | --- |
| Windows 11 | ✅ tested (2560×1600 @150%) |
| Windows 10 | ✅ only APIs available since Vista are used (1809+ expected to work) |
| Laptop panels / external monitors / multi-monitor | ✅ handled per display |
| GPUs | ✅ Intel / AMD / NVIDIA / Qualcomm — plain Win32 APIs, no vendor SDK |
| Hybrid graphics laptops | ✅ the display output path is patched, not the renderer |
| High DPI | ✅ layout measured from the actual font, fine at 150% / 200% |
| **HDR / advanced color** | ⚠️ gamma is ignored by the OS → automatic overlay fallback |
| Windows Night Light | ⚠️ shares the same gamma table; DeepDark overrides its tint |
| Exclusive fullscreen games | ⚠️ may reset gamma; DeepDark re-applies within 10 s (use borderless) |
| Lock screen / UAC / Start menu / desktop switch | ✅ dimmed as well |

## Build from source

Requires nothing but the C# compiler bundled with Windows:

```powershell
git clone https://github.com/xiehuangsw/DeepDark.git
cd DeepDark
powershell -ExecutionPolicy Bypass -File build.ps1
# -> dist\DeepDark.exe
```

## License

[MIT](LICENSE)

Issues and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).
