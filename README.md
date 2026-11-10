<p align="center"><img src="assets/icon-256.png" width="128" alt="GlassBell icon"></p>

<h1 align="center">GlassBell</h1>

<p align="center">Notifications of every app as fast glass banners, in the style of macOS. For Windows 11.</p>

<p align="center"><b>English</b> · <a href="README.ru.md">Русский</a></p>

---

GlassBell runs in the background, picks up every notification Windows receives and shows it as a glass banner in the top-right corner of the screen: a lens that bends the background at its edges, a colored fringe, highlights along the outline and a soft shadow that adapts to what is behind it.

## Features

- **Every app.** GlassBell reads the notifications of all apps (Windows notification listener) and shows them as its own banners, up to four stacked at once.
- **Real glass, drawn on the GPU.** Refraction at the edges, dispersion, highlights, adaptive tint and shadow. Four quality levels, and an optional live background that follows what is behind the banner even while it moves.
- **Fast.** A single banner is on screen 30–60 ms after the notification arrives (measured on the developer's PC).
- **Click to open.** A click on a banner opens what the notification points to, the same as a click on the Windows one.
- **Sound.** Three chimes synthesized in code (no audio files); the volume can follow the system volume.
- **Settings with a live preview**, a tray icon, start with Windows.
- **20 languages.** The interface follows the Windows display language; other languages get English.
- **Light.** About 1% of one CPU core while idle and 120–150 MB of memory, also after hundreds of banners (measured on the developer's PC).

## Install

1. Download **GlassBell-Setup.exe** from [Releases](https://github.com/vibegineer/GlassBell/releases/latest) and run it. No administrator rights are needed.
   The installer is not code-signed, so Windows SmartScreen may say "Windows protected your PC": click **More info → Run anyway**.
2. Turn on **Do not disturb** (Settings → System → Notifications), so that the Windows banners do not show next to the GlassBell ones. Notifications still arrive while Do not disturb is on, and GlassBell still sees them.

The icon next to the clock opens the settings. To remove GlassBell: Settings → Apps → Installed apps → GlassBell → Uninstall.

**Requirements:** Windows 11, 64-bit. Everything else (.NET Framework 4.8, Direct3D) is part of Windows.

## Command line

GlassBell can also show banners for your own scripts:

```
GlassBell.exe --title "Build finished" --body "All 214 tests passed" --app "CI"
```

| Command | What it does |
|---|---|
| `GlassBell.exe` | starts GlassBell if needed and opens the settings |
| `--title T --body B [--app A] [--app-id AUMID] [--seconds S]` | shows a banner (`--app-id` takes the icon of an installed app) |
| `--test` | shows two sample banners |
| `--settings` / `--quit` | opens the settings / closes GlassBell |
| `--install` / `--uninstall` | turns start with Windows on / off (for a copy used without the installer) |
| `--stats` | writes the memory use of the running copy to the log |

## Files

| What | Where |
|---|---|
| Program | `%LOCALAPPDATA%\Programs\GlassBell` |
| Settings | `%APPDATA%\GlassBell\settings.ini` |
| Log | `%LOCALAPPDATA%\GlassBell\log.txt` |

`lang=de` (or any other code from `lang/`) in `settings.ini` picks the language by hand; `lang=auto` follows Windows.
A `portable.flag` file next to `GlassBell.exe` keeps the settings and the log in the program's folder instead.

## Build

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

This builds `bin\GlassBell.exe` and `dist\GlassBell-Setup.exe` with the C# compiler of the .NET Framework that comes with Windows: no SDK, no NuGet, no Visual Studio.

| Folder | Contents |
|---|---|
| `src/` | the program: banners, glass renderer, notification mirroring, settings, chimes |
| `setup/` | the installer and uninstaller |
| `lang/` | the interface texts, one file per language |
| `assets/` | the icon |
| `tools/` | test and benchmark scripts used during development, the icon generator |

## Translations

The translations were made with AI and checked only for completeness and fit, not by native speakers. Corrections are welcome: each language is one plain text file in [`lang/`](lang), and [`lang/en.txt`](lang/en.txt) explains every line.

## License

[MIT](LICENSE). macOS is a trademark of Apple Inc.; GlassBell is not affiliated with Apple.
