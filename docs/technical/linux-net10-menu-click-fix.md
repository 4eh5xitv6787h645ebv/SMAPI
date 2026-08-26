# Linux .NET 10 menu-click compatibility fix

This change addresses two independent Linux desktop behaviors exposed by the optional .NET 10 host. It does not change Android/mobile or Windows input paths.

## 1. Shared generic Harmony detours

CoreCLR shares native code between many reference-type generic instantiations. Harmony creates its replacement signature from the one constructed method a mod requested. Under .NET 10, JIT specialization of that closed signature can reinterpret arguments from another instantiation as the requested type.

SMAPI now patches Harmony wrapper creation on Linux .NET 10 so parameters and returns derived from generic substitutions use the canonical `object` ABI. Fixed signature types and value types are unchanged. This restores the behavior observed on the bundled .NET 6 host without disabling JIT inlining or other optimizations process-wide.

This does not make constructed reference-type generic detours isolated. A patch to one shared instantiation can still run for the others, as documented by Harmony and MonoMod. The fix prevents .NET 10 from making that existing limitation more type-unsafe than it is on .NET 6.

## 2. Mouse pulses between input polls

MonoGame provides mouse buttons as a current-state snapshot. During a long update frame, SDL can receive both the down and up event for a short click before the next snapshot. The final snapshot is released, so the click previously disappeared before either the game or SMAPI could handle it.

On Linux .NET 10, SMAPI now registers a passive SDL event watcher. For each mouse button, it:

1. records native down/up edges without changing SDL delivery;
2. checks whether either adjacent MonoGame snapshot represented the press;
3. queues only a complete pulse which was absent from both snapshots; and
4. exposes a synthetic press for one update followed by a release update.

Normally observed clicks are not replayed. Multiple missed pulses are separated by release ticks. If the expected SDL library or event-watch entry point is unavailable, SMAPI keeps the original polling behavior.

## Regression tooling

The disposable probe under `docs/technical/tools/linux-menu-click-probe` issues physical X11 clicks through `xdotool`. It separately counts:

- completed click processes;
- SMAPI press and release events;
- menu `receiveLeftClick` activations; and
- update gaps and GC collections.

The normal mode starts after a save is loaded. `StartAtTitle` provides a fast input-boundary smoke test with an otherwise empty isolated mod directory.

## Validation results

| Workload | Host | Result |
| --- | --- | --- |
| Generic type and method detours across `string`, `object`, and two custom reference types | .NET 10.0.11 | Arguments and reference returns match .NET 6 semantics |
| Title-screen control, 200 physical clicks held for 1 ms | .NET 6.0.32, buffer intentionally disabled | 0 presses / 0 releases / 0 activations |
| Title-screen control, 200 physical clicks held for 1 ms | .NET 10.0.11, buffer enabled | 200 presses / 200 releases / 200 activations |
| Trusted PR #158 modpack plus fresh Blossom save, 120 physical clicks held for 40 ms | .NET 10.0.11 before buffering | 116, then 109 activations in repeated runs |
| Same trusted full workload after buffering | .NET 10.0.11 | 120 presses / 120 releases / 120 activations, with 12 update gaps of at least 50 ms |

The final full SMAPI Release test run passed 1,680 tests with 3 platform skips and no failures.
