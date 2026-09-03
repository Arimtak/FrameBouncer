# FrameBouncer

**English | [Deutsch](README.de.md)**

A small Windows tool that **limits FPS in games** (e.g. 60, 120 or 144), so your PC runs
quieter, cooler and uses less power. Settings are saved per game and applied automatically
the next time the game starts.

**Free for everyone** – MIT license (see [LICENSE](LICENSE)).

## Screenshot

![FrameBouncer UI](docs/screenshot.png)

## Installation

1. Download the latest version from **GitHub Releases**:
   `FrameBouncer-vX.Y.Z-win-x64.zip`
2. Extract the ZIP to any folder (e.g. `D:\Tools\FrameBouncer`) – it is a **single portable
   EXE** (`FrameBouncer.exe`); you can copy the file anywhere (Desktop, USB stick).
3. Install and start **RTSS** (RivaTuner Statistics Server, free at guru3d.com) – without
   RTSS, FrameBouncer cannot set an FPS limit.
4. Start `FrameBouncer.exe` – done, no installation required.

> On the very first **Apply**, a Windows security prompt (UAC) may appear once.
> After that, FrameBouncer never asks again.

## Usage

1. **Select a game:** Choose your running game from the list at the top (or click
   **Pick window** and then click the game).
2. **Set FPS:** Type a value or use the presets (30 / 60 / 120 / 144).
3. **Press Apply** – the game is now limited.

The profile is saved automatically: the next time the game starts, the limit applies on its
own. When you close FrameBouncer, all limits are removed again.

| Icon / Button | Meaning |
|---|---|
| 📌 **Pin** | Keep the window always on top |
| **Autostart** | Start automatically with Windows |
| 🔔 **Tray** | Keeps running in the notification area after ✕ |
| ⭳ **Backup** / ⭱ **Restore** | Save / restore profiles |
| ⟳ **Updates** | Check for a new version |

At the bottom, the program shows live FPS, frame time, 1% / 0.1% lows and (if MSI
Afterburner is running) GPU / CPU temperature.

## Language

Switch between **English** and **Deutsch** in the header of the app. The selection is saved
and restored on the next start. Settings and profiles are not affected by switching.

## Uninstall

Simply delete the EXE. Your settings and profiles are stored under
`Documents\FrameBouncer` (`settings.json`, `Backups\`, `Updates\`) – you can delete that
folder too. The EXE itself stays portable: all data always lives in your documents, never
next to the EXE.

## License

[MIT](LICENSE) – you may freely use, copy, modify and redistribute the program.
Developer details: [docs/architecture.md](docs/architecture.md).