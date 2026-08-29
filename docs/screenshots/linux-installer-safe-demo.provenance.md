# Linux installer safe-demo screenshot provenance

This record covers `linux-installer-safe-demo.png`, an illustrative capture of the disconnected graphical shell. It is not evidence that the final packaged graphical installer, download flow, or release workflow is complete.

## Exact source and image

- Source commit: `73bea44c7ba2e135f7afa537087330b8786f93f4`
- Source tree: `1f1456d2e4ebebd66549b0c83202c02c816ba134`
- Avalonia packages: `12.1.1`, as pinned by that tree
- PNG SHA-256: `22c939938bc72d29666b0061677c37ca1a17ba1cbf894bd5b6d1889a1b288dea`
- PNG dimensions: 1621 × 1187 pixels
- Capture time: 2026-08-29T12:52:14+08:00

## Capture environment and method

- Manjaro Linux, x86_64
- .NET SDK 10.0.108 and host 10.0.8
- Wayland desktop session using the supported XWayland path (`DISPLAY=:1`)
- ImageMagick 7.1.2-23 and xdotool 4.20260303.1

The exact source commit was built and launched from the repository root:

```sh
dotnet build src/SMAPI.Installer.Gui/SMAPI.Installer.Gui.csproj --configuration Release --no-restore
dotnet src/SMAPI.Installer.Gui/bin/Release/net10.0/SMAPI.Installer.Gui.dll --demo
```

The SMAPI window was activated, its vertical page was returned to the top with upward wheel events, and ImageMagick captured only that X11 window:

```sh
xdotool search --name 'SMAPI Linux Installer'
xdotool windowactivate 50331663
xdotool mousemove --window 50331663 800 400 click --repeat 20 --delay 20 4
import -window 50331663 docs/screenshots/linux-installer-safe-demo.png
sha256sum docs/screenshots/linux-installer-safe-demo.png
```

The numeric window ID is session-specific; repeat captures must use the ID returned by `xdotool search`.

## Privacy and accuracy review

The final PNG was inspected at original resolution. It contains only the application window and fixed synthetic demo data. It contains no desktop background, username, real home/game/Mods/save path, mod list, save name, package identity, token, notification, or private fixture data. The visible state accurately shows the backend as disconnected, execution disabled, release/folder values synthetic, and the demo log memory-only. The warning is scoped to installer-controlled game/package behavior and app downloads; it does not claim that Avalonia or the desktop stack performs no normal runtime access.
