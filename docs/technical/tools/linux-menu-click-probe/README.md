# Linux menu click probe

This disposable desktop-only SMAPI mod checks whether short physical X11 clicks survive the game update loop. It opens a fixed test menu after `SaveLoaded`, issues clicks through `xdotool`, and compares four independently observable counts:

- click processes started and completed;
- SMAPI `ButtonPressed` events;
- SMAPI `ButtonReleased` events; and
- menu `receiveLeftClick` activations.

It also logs update gaps and process-wide GC collection-count changes for correlation. It does not synthesize input through SMAPI and does not retry missed clicks, since either would hide the defect or risk double activation.

The probe requires an X11 session and `xdotool` installed at `/usr/bin/xdotool`.

Build it against the exact isolated game under test:

```sh
dotnet build LinuxMenuClickProbe.csproj -c Release -p:GamePath=/path/to/game
```

Copy `manifest.json`, `config.json`, and `bin/Release/net6.0/LinuxMenuClickProbe.dll` into an isolated mod folder named `LinuxMenuClickProbe`. Run the same folder and configuration against each bundled Linux host. A valid comparison keeps the game, complete mod set, save, virtual display, click duration, and click interval identical; only the runtime/GC policy changes.

For a fast native-input smoke test which doesn't load a save, set `StartAtTitle` to `true`. That mode replaces the title menu after the configured warmup and is intended for an otherwise empty isolated `Mods` directory.

The terminal line is machine-readable:

```text
clickprobe-complete result=pass attempts=120 processes=120 process_failures=0 press_events=120 release_events=120 activations=120 frame_gaps=... max_frame_gap_ms=... gc=.../.../...
```

Any count mismatch is a failure. Retain the full log because a missed press with a completed `xdotool` process identifies polling/update loss, while a press event without a menu activation points to later menu handling.
