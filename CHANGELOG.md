# Changelog

## 1.1.0 — 2026-11-13

- The banners are drawn by the graphics card from start to finish: it takes the screen behind them and draws the glass, the shadow and the text in one go. The glass keeps up with the screen at its own refresh rate (60, 144, 240 Hz or more) while the processor stays nearly idle. The renderer of 1.0 is still in the settings (Rendering: Classic) and takes over by itself when the graphics card cannot do this.
- A round × over the corner of the banner under the pointer closes it without opening it.
- With two or more banners, a Close all button shows under them while the pointer is on them. Close all is also in the tray menu, and `GlassBell.exe --close-all` does it from the command line.
- The frame rate of the live background is now a setting of the classic renderer only: with the graphics card the glass follows the screen at its own rate.

## 1.0.0 — 2026-11-11

The first release.

- Shows the notifications of every app as glass banners in the top-right corner of the screen: a lens that bends the background at its edges, a colored fringe, highlights along the outline and a soft shadow, drawn on the GPU. Four quality levels and an optional live background that follows what is behind a banner 10 to 60 times a second.
- A click on a banner opens what the notification points to.
- Three chimes synthesized in code; their volume can follow the system volume.
- Settings with a live preview, a tray icon, start with Windows.
- The interface in 20 languages: it follows the Windows display language, or the language picked in the settings.
- A per-user installer and uninstaller, no administrator rights needed. Windows 11, 64-bit.
