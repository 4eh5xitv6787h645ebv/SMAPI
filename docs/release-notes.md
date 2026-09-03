---
layout: default
title: Release notes
---

← [Documentation home](index.md)

# Release notes

## Linux fork 4.5.3 alpha 3 — release candidate, not public

The planned embedded version is
`4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.3`, reserved for annotated tag
`fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.3`. This is a source candidate only: alpha 3 is
not a published release until the reviewed change is merged, the exact merge commit and trusted
workload pass qualification, the annotated-tag workflow publishes its six assets, and a fresh
public download passes independent verification. Until then, alpha 2 below remains the current
public package.

* Replaced the generic graphical-installer terminal diagnostic with stable outcome-specific event
  codes and truthful severity for install/update/repair/uninstall/backup/rollback, interrupted
  recovery, and recovery-history cleanup. Fixed messages retain only the typed operation, durable
  state, safe next action, and applicable stable error code.
* Expanded the local diagnostic viewer with visible snapshot health and separate display-omission,
  private-raw-log-omission, and progress-coalescence counts. The product now explicitly distinguishes
  the sanitized snapshot from the current-user-only rotating JSONL log and states its one-MiB file,
  five-file/five-MiB aggregate, next-session rotation, no-auto-upload, and review-before-sharing
  boundaries without showing its path.
* Restored the missing fixed progress message for local-package import and added bounded rendering,
  typed terminal-matrix, production observer/session, privacy, and health-accounting coverage.
* Corrected the Linux manual-install documentation to distinguish the legacy console/headless
  install-or-uninstall path from the graphical Core transaction. The guide now gives exact
  normal-user commands and exit semantics, requires release verification before extraction, and
  states the missing plan, receipt, journal, recovery, history, and authenticated-rollback
  guarantees. Raw extraction is explicitly limited to a last-resort fresh install, with an explicit
  prohibition on recursive deletion.
* Made the interactive Linux console wrapper reject supplied options with status 2 instead of
  silently dropping them, and made prompt-free requests without `--game-path` fail before game
  discovery. Headless use still requires the exact direct-apphost command documented in the guide.
* Added a production-reachable safe cancellation checkpoint after each complete mutation and its
  durable `Applied` journal record. A cancellation observed there uses the existing non-cancellable
  full rollback path; individual renames, rollback, and commit publication are never interrupted,
  and a request which loses the final commit race is still reported as committed.

These changes are in source only until a replacement exact-commit prerelease is published and
qualified. The alpha 2 package below does not gain them retroactively. The corrected description of
its legacy console boundary distinguishes alpha 2's behavior from the new safeguards above; it does
not claim that package was changed after publication.

## Linux fork 4.5.3 alpha 2

This **unofficial experimental Linux x86_64 fork** was published on 2 September 2026 UTC as
[`fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2`](https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.2).
Its embedded version is `4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.2`, and its six public
assets were built from exact reviewed commit
[`052699e8`](https://github.com/4eh5xitv6787h645ebv/SMAPI/commit/052699e8ccba0d13f9d4f02e0bb199aa04cec605).
Download all six assets from that release; do not substitute a pull-request or Actions artifact.
The existing terminal launcher remains in the same ZIP as the graphical installer.

* Added a maintainable Avalonia Linux desktop frontend around the shared transactional installer
  Core and strict out-of-process protocol. The GUI never implements its own game-file mutation
  rules and never requires root for a normal user installation.
* Added reviewed public-release selection and a native local-package folder path. Both routes require
  the exact six release files and use the same checksum, metadata, manifest, release-identity, and
  GitHub-attestation verification before game discovery or mutation. Local paths are not displayed,
  logged, or retained for retry.
* Added validated game discovery and manual selection; read-only install, update, repair, uninstall,
  backup, rollback, and recovery planning; explicit destructive confirmations; transactional
  execution and cancellation; automatic interrupted-operation recovery; and authenticated recovery
  history cleanup.
* Added visible release/download/verification, inspection, execution, rollback, and recovery progress
  with typed path-free errors and exact durable-state outcomes. Modified, legacy, unknown, linked,
  special, and ambiguous launcher entries remain blocked unless the reviewed workflow exposes an
  exact permitted decision.
* Added keyboard-only operation, unique access keys, readable focus, safe Cancel defaults for
  destructive actions, screen-reader names/live status, vertical scrolling without horizontal page
  scrolling, and tested 420-DIP plus 100%, 125%, 150%, and 200% layouts. The supported first desktop
  path is X11 or XWayland; experimental native Wayland is not advertised as supported.
* Added one bounded, local-only private diagnostic session before graphical desktop or network
  startup, with fail-closed startup and pre-mutation readiness checks. **View diagnostic log** is
  available on every production screen and exposes only a bounded sanitized snapshot; no telemetry
  or automatic upload was added.
* Kept the legacy console/headless install-or-uninstall and last-resort raw-install paths documented.
  The graphical and console launchers ship from the same exact verified package, but the legacy
  console path is not transactionally equivalent to the GUI Core workflow.
* Added exact-package, protocol, filesystem, failure, cancellation, recovery, accessibility,
  privacy, and launcher qualification. The freshly downloaded six-asset public set passed exact
  inventory, checksum, metadata, manifest-authority, and two-subject local-bundle attestation
  verification; its exact ZIP then passed structural, packaged-GUI, and disposable
  install/update/uninstall/failure lifecycle checks. Authentic GNOME/KDE, X11/XWayland, AT-SPI,
  scaling, and production-workflow screenshots remain pending.

See the [graphical installer guide](technical/linux-gui-shell.md),
[Linux alpha guide](technical/linux-alpha-release.md), and
[screenshot evidence contract](technical/linux-gui-screenshot-evidence.md).

## Linux fork 4.5.3 alpha 1

This is an **unofficial experimental Linux x86_64 fork** based on official SMAPI 4.5.2. Its
embedded version is `4.5.3-unofficial.4eh5xitv6787h645ebv.linux.alpha.1`. It was published on
28 August 2026 under the immutable tag
[`fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1`](https://github.com/4eh5xitv6787h645ebv/SMAPI/releases/tag/fork-4eh5xitv6787h645ebv-linux-v4.5.3-alpha.1)
after exact-commit qualification, independent reviews, and an isolated trusted-workload smoke.
The downloaded public artifact then passed post-publication clean-room verification. See the
[sanitized validation record](technical/linux-alpha-release-validation.md).

* Added exact-commit Linux-only release automation with pinned source inputs, SHA-256 checksums,
  machine-readable build metadata, GitHub provenance, and tag-only prerelease publication.
* Added structural package and isolated install/update/uninstall/failure lifecycle gates.
* Made headless installer failures prompt-free and machine-detectable, fixed malformed
  `--game-path` handling, and prevented headless deletion failures from retrying forever.
* Preserved current SMAPI logs and local Mod Health Reports across install and uninstall.
* Documented verification, installation, upgrade, manual install, uninstall, official-4.5.2
  rollback, privacy boundaries, known limitations, and the release process in the
  [Linux alpha guide](technical/linux-alpha-release.md).
* Retained the controlled 4.5.2 comparison and its one-machine limitations; no private modpack or
  save data is included in the repository or release artifacts.

## Additional changes included in this fork snapshot
* Rebuilt the repository README and GitHub Pages site around an evidence-based comparison with
  official SMAPI, including clearly scoped whole-workload and microbenchmark results.
* Added a reproducible official-4.5.2-versus-fork Linux benchmark bundle with 20 sanitized raw
  captures, exact commit and runtime provenance, full update/allocation/GC/transition distributions,
  run-to-run variation, and paired diagnostic-overhead evidence. On the tested workstation and
  private workload with 132 code mods and 176 content packs, median-of-run mean update elapsed
  duration was 14.596 ms official and 7.228 ms fork, with lower fork values in all five fixed-order
  pairs. These are descriptive one-machine results, not universal FPS, CPU-use, power, or latency
  claims; the published evidence retains cache/order, software-rendering, runtime, higher
  selected-core busy-time, Gen1 GC, and noisy Farm-transition limitations.
* For players:
  * Added a Linux desktop `health` console workflow which creates private, local mod-health reports for troubleshooting load errors, log floods, failed callbacks, and slow update ticks.
  * Added a Linux desktop in-game viewer for Mod Health Reports. Enter `health view` to inspect the current session's sanitized report using mouse, keyboard, or controller. The report stays private and local: the viewer never uploads it or opens external apps. Missing viewer translations fall back to English, while schema-v1 finding text remains canonical English.
  * Improved performance.
  * Improved error message when a mod is blocked by Windows Smart App Control.
  * Improved translations. Thanks to To2morrow (updated Korean)!
  * Updated internal mod blacklist.

* For mod authors:
  * Added OS metrics to the [metrics API](technical/web.md#modsmetrics).

* For the web UI:
  * Added [mod stats page](https://smapi.io/stats) (see [announcement](https://www.patreon.com/pathoschild/posts/new-mod-dataset-161970558)).
  * Deprecated the [mod compatibility list](https://smapi.io/stats) (see [announcement](https://www.patreon.com/pathoschild/posts/162629586)).

* For tool maintainers:
  * Added the [open mod dataset](https://github.com/Pathoschild/StardewModDataset).

## 4.5.2
Released 14 March 2026 for Stardew Valley 1.6.14 or later. See [build attestation](https://github.com/Pathoschild/SMAPI/attestations/21366863).

* For players:
  * Improved performance a bit.
  * Fixed the Linux/macOS installer not saving the color scheme correctly in 4.5.0+.
  * Fixed typo in config UI text (thanks to QuentiumYT!).
  * Improved translations. Thanks to dekthaiinchina (updated Thai), dewanggatrustha (updated Indonesian), QuentiumYT (updated French), Timur13240
 (updated Russian), and vlcoo (updated Spanish)!

* For mod authors:
  * Fixed input API ignoring controller overrides when there's no controller plugged in (thanks to spacechase0!).
  * Fixed asset propagation for the farmhouse map not updating the farmhouse fridge position.

## 4.5.1
Released 25 January 2026 for Stardew Valley 1.6.14 or later. See [build attestation](https://github.com/Pathoschild/SMAPI/attestations/17385144).

* For players:
  * Fixed error installing SMAPI 4.5.0 on Linux/macOS.
  * Improved translations. Thanks to Maatsuki (updated Portuguese)!

## 4.5.0
Released 25 January 2026 for Stardew Valley 1.6.14 or later. See [release highlights](https://www.patreon.com/posts/149054246) and [build attestation](https://github.com/Pathoschild/SMAPI/attestations/17379361).

* For players:
  * Added in-game config UI for SMAPI via [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098).
  * SMAPI now uses [automated and attested builds](https://www.patreon.com/posts/automated-builds-148417912) (thanks to DecidedlyHuman)!  
    _This improves the security and transparency of SMAPI builds. Every step to build SMAPI from the public source code is now public and verifiable, with file signatures to let players and tools confirm the build hasn't been tampered with._
  * SMAPI can now detect known malicious loose files in the `Mods` folder.
  * Updated internal mod blacklist.

* For mod authors:
  * SMAPI no longer has a separate 'for developers' version.  
    _Instead, you can now use [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) to enable 'developer mode' in the console window options._

## 4.4.0
Released 10 January 2026 for Stardew Valley 1.6.14 or later. See [release highlights](https://www.patreon.com/posts/147916705).

* For players:
  * Added [`set_verbose` console command](https://stardewvalleywiki.com/Modding:Console_commands#set_verbose).
  * The SMAPI log now shows a friendly Windows name (like "Windows 11") instead of its internal identifier.
  * Fixed `player_add` and `list_items` console commands not including some newer juice items.
  * Fixed farmhouse map edits sometimes removing the spouse room (thanks to SinZ!).
  * Fixed installer error if Steam has an empty game path saved to the registry.

* For mod authors:
  * Added [input API to send button presses to the game](https://stardewvalleywiki.com/Modding:Modder_Guide/APIs/Input#Send_input) (thanks to martiandweller!).
  * Added transparency masks via `PatchMode.Mask` when editing images (thanks to PinkSerenity!).
  * Added support for map tilesheets referencing an asset outside `Content/Maps` using a relative `../` path (thanks to Spiderbuttons!).
  * Added asset propagation for spouse room map edits.
  * Improved performance when propagating localized assets in some cases (thanks to SinZ!).
  * Improved error-handling during asset propagation.
  * Updated dependencies, including...
    * [Newtonsoft.Json](https://www.newtonsoft.com/json) 13.0.3 → 13.0.4 (see [changes](https://github.com/JamesNK/Newtonsoft.Json/releases/tag/13.0.4));
    * [Pintail](https://github.com/Nanoray-pl/Pintail) 2.8.1 → 2.9.1 (see [changes](https://github.com/Nanoray-pl/Pintail/blob/master/docs/release-notes.md#291)).
  * Fixed asset propagation for farmer sprites before a save is loaded.
  * Removed `System.Management.dll`, which SMAPI no longer uses.

* For the web UI:
  * Improved mod compatibility list:
    * Added support for mod links in warnings.
  * Improved Content Patcher [JSON schema](technical/web.md#using-a-schema-file-directly):
    * Updated for Content Patcher 2.8.0 and 2.9.0.
    * Fixed schema requiring `AddNPCWarps` instead of `AddNpcWarps`.
    * Fixed validation error if a warp field contains tokens or consecutive spaces (thanks to irocendar!).
    * Fixed validation error if a `Target` contains multiple targets (thanks to irocendar!).
    * Fixed `FromFile` errors like "_matches a schema that is not allowed_" (thanks to irocendar!).

## 4.3.2
Released 14 July 2025 for Stardew Valley 1.6.14 or later. See [4.3 release highlights](https://www.patreon.com/posts/133992196).

* For players:
  * Added a friendly error message when the game fails to launch with a `NoSuitableGraphicsDeviceException`.
  * Fixed crash when SMAPI tries to update the mod blacklist if ReShade is installed.

## 4.3.1
Released 13 July 2025 for Stardew Valley 1.6.14 or later.

* For players:
  * Improved performance when mods edit maps (thanks to SinZ!).
* For mod authors:
  * Fixed new `helper.ModRegistry.GetFromNamespacedId` not handling prefix IDs correctly.

## 4.3.0
Released 12 July 2025 for Stardew Valley 1.6.14 or later. See [release highlights](https://www.patreon.com/posts/133992196).

* For players:
  * Added 'malicious mod' blacklist.  
    _Once a malicious mod has been reported, this lets us quickly block it for all players. This helps mitigate damage in case of future attacks. This feature can be disabled in the SMAPI settings if needed._
  * Improved content load performance for non-English players.
  * Fixed some community shortcuts breaking if a mod edited the map which contains them.

* For mod authors:
  * Added `helper.ModRegistry.GetFromNamespacedId` method to get a mod given a [standard namespaced ID](https://stardewvalleywiki.com/Modding:Common_data_field_types#Unique_string_ID) (e.g. an item ID).
  * You can now have an `en.json` translation file which overrides `default.json`.
  * Updated dependencies, including...
    * [Mono.Cecil](https://github.com/jbevain/cecil) 0.11.5 → 0.11.6 (see [changes](https://github.com/jbevain/cecil/compare/0.11.5...0.11.6));
    * [FluentHttpClient](https://github.com/Pathoschild/FluentHttpClient#readme) 4.4.1 → 4.4.2 (see [changes](https://github.com/Pathoschild/FluentHttpClient/blob/develop/RELEASE-NOTES.md#442));
    * [Pintail](https://github.com/Nanoray-pl/Pintail) 2.6.1 → 2.8.1 (see [changes](https://github.com/Nanoray-pl/Pintail/blob/master/docs/release-notes.md#260)).

* For the web UI:
  * Increased default upload expiry from 30 to 60 days, to help avoid expired SMAPI logs when mod authors check messages monthly.
  * Improved JSON validator:
    * You can now hover/click braces to highlight matching pairs.
    * You can now hover and click 'Copy' on the top-right to copy the full code to the clipboard.
    * Updated to newer syntax highlighting library.
    * Fixed CurseForge update keys not recognized (thanks to Dunc4nNT!).
    * Fixed some JSON files breaking page layout.
  * Improved log parser:
    * Mods which failed to load are now shown in the mod list (with 'failed to load' in the error column).
    * Added suggested fix if there's a newer SMAPI version available.
    * Reduced response times with a new analysis cache and client-side fetch.
    * Removed support for very old SMAPI logs.
    * You can now download a JSON representation of the parsed log (see the download link at the bottom of the log page).
    * Fixed server error if a JSON file contains nested comment syntax.
  * Improved [JSON schemas](technical/web.md#using-a-schema-file-directly):
    * The Content Patcher JSON schema now allows decimal values in local tokens (thanks to rikai!).
    * The `$schema` value is no longer validated.
    * Updated Content Patcher schema for Content Patcher 2.7.0.
    * Updated manifest schema for the new `%ProjectVersion%` value in `Version`.
  * Improved mod compatibility list:
    * Reduced response times with a new cache and client-side fetch.
    * Fixed sort order for mods with non-Latin characters in the name.
  * Third-party libraries are now served from `smapi.io` instead of external CDNs.

## 4.2.1
Released 25 March 2025 for Stardew Valley 1.6.14 or later.

* For players:
  * Fixed crash when some mods' custom tiles are on-screen.

* For mod authors:
  * Reverted the fix for the game's `Data/ChairTiles` logic not handling unique string IDs like `Maps/Author.ModName` correctly.  
    _The fix caused crashes loading map tiles in some cases. This will be fixed in the next game update instead._

## 4.2.0
Released 24 March 2025 for Stardew Valley 1.6.14 or later. See [release highlights](https://www.patreon.com/posts/125017679).

* For players:
  * Fixed `log_context` command not disabling the extra logs when run again.
  * Fixed update alerts when using an unofficial port of SMAPI with a four-part version number.
  * Fixed installer on Linux not always opening a terminal as intended (thanks to HoodedDeath!).
  * Updated compatibility list.

* For mod authors:
  * Mod events are now raised on the shipping menu (except when it's actually saving).
  * Added translation API methods to query translation keys (`ContainsKey` and `GetKeys`).
  * ~~Fixed the game's `Data/ChairTiles` logic not handling unique string IDs like `Maps/Author.ModName` correctly.~~  
    _Reverted in 4.2.1._
  * Fixed exception thrown if `modRegistry.GetApi<T>` can't proxy the API to the given interface. It now logs an error and returns null as intended.

* For external tools:
  * Added toolkit method to read the compatibility list from a local copy of its Git repo.

* For the web UI:
  * You can now link to a mod in the compatibility list by its unique ID, like [smapi.io/mods#Pathoschild.ContentPatcher](https://smapi.io/mods#Pathoschild.ContentPatcher).
  * Fixed search engines able to index uploaded logs and JSON files via the raw download option.
  * Improved Content Patcher JSON schema:
    * Updated for Content Patcher 2.5.0.
    * Added format validation for token names.
    * Fixed incorrect error when setting a config default to a boolean or number.

## 4.1.10
Released 18 December 2024 for Stardew Valley 1.6.14 or later.

* For players:
  * Updated for the upcoming Stardew Valley 1.6.15.
  * Fixed errors when cross-playing between PC and Android.

* For mod authors:
  * Improved [Content Patcher JSON schema](technical/web.md#using-a-schema-file-directly) to allow boolean and numeric values in dynamic tokens.


> [!IMPORTANT]  
> **For players on macOS:**  
> There are recent security changes in macOS. Make sure to follow the updated [install guide for
> macOS](https://stardewvalleywiki.com/Modding:Installing_SMAPI_on_Mac) when installing or updating SMAPI.
>
> Players on Linux or Windows can ignore this.

## 4.1.9
Released 08 December 2024 for Stardew Valley 1.6.14 or later.

* For players:
  * Fixed compatibility with new macOS security restrictions (again).
  * Fixed unable to override color schemes via `smapi-internal/config.user.json`.

## 4.1.8
Released 28 November 2024 for Stardew Valley 1.6.14 or later.

* For players:
  * Updated the mod compatibility blacklist.
  * Fixed compatibility with new macOS security restrictions.
  * Fixed crash with some rare combinations of mods involving Harmony and mod APIs.

* For mod authors:
  * Added `PathUtilities.CreateSlug` to get a safe Unicode string for use in special contexts like URLs and file paths.  
    _For example, `PathUtilities.CreateSlug("some 例子?!/\\~ text")` becomes `"some-例子-text"`._
  * `PathUtilities.IsSlug` now allows more Unicode characters.
  * Updated [Pintail](https://github.com/Nanoray-pl/Pintail) 2.6.0 → 2.6.1 (see [changes](https://github.com/Nanoray-pl/Pintail/blob/master/docs/release-notes.md#261)).

* For the web UI:
  * Fixed log parser not highlighting update alerts for mods which SMAPI couldn't load.
  * Fixed CurseForge links not shown for mods that have a CurseForge page.

* For external tools:
  * Revamped the mod compatibility list to simplify maintenance. It's now stored [in a Git repo](https://github.com/Pathoschild/SmapiCompatibilityList), which replaces the former [wiki page](https://stardewvalleywiki.com/Modding:Mod_compatibility).
  * Added toolkit method to get the URL from an update key's site and mod ID.

## 4.1.7
Released 12 November 2024 for Stardew Valley 1.6.14 or later.

* For players:
  * Updated for Stardew Valley 1.6.14.
  * Updated mod compatibility list.
  * Fixed crash if a mod has a missing or invalid DLL.

## 4.1.6
Released 07 November 2024 for Stardew Valley 1.6.10 or later.

* For players:
  * Revamped message shown after a game update to avoid confusion.
  * Added option to disable content integrity checks in `smapi-internal/config.json`. When disabled, SMAPI will log a warning for visibility when someone helps you troubleshoot game issues.

* For mod authors:
  * Fixed `translation.ApplyGenderSwitchBlocks(false)` not applied correctly.

## 4.1.5
Released 07 November 2024 for Stardew Valley 1.6.10 or later.

* For players:
  * Updated mod compatibility list.
  * Fixed translation issues in some mods with SMAPI 4.1._x_.

* For mod authors:
  * Fixed `translation.UsePlaceholder(false)` also disabling custom fallback text in recent builds, not just the "no translation" placeholder.

## 4.1.4
Released 05 November 2024 for Stardew Valley 1.6.10 or later.

* For players:
  * Fixed a wide variety of mod errors and crashes after SMAPI 4.1.0 in some specific cases (e.g. Content Patcher "unable to find constructor" errors).

* For mod authors:
  * Removed the new private assembly references feature. This may be revisited in a future update once the dust settles on 1.6.9.
  * Fixed error propagating edits to `Data/ChairTiles`.

## 4.1.3
Released 04 November 2024 for Stardew Valley 1.6.10 or later.

* For players:
  * Improved compatibility rewriters for Stardew Valley 1.6.9+.

## 4.1.2
Released 04 November 2024 for Stardew Valley 1.6.10 or later.

* For players:
  * Updated for Stardew Valley 1.6.10.
  * Fixed various issues with custom maps loaded from `.tmx` files in Stardew Valley 1.6.9.

## 4.1.1
Released 04 November 2024 for Stardew Valley 1.6.9 or later.

* For players:
  * Fixed crash when loading saves containing a custom spouse room loaded from a `.tmx` file.

## 4.1.0
Released 04 November 2024 for Stardew Valley 1.6.9 or later. See [release highlights](https://www.patreon.com/posts/115304143).

* For players:
  * Updated for Stardew Valley 1.6.9.
  * SMAPI now auto-detects missing or modified content files, and logs a warning if found.
  * SMAPI now uses iTerm2 on macOS if it's installed (thanks to yinxiangshi!).
  * SMAPI now enables GameMode on Linux if it's installed (thanks to noah1510!).
  * SMAPI now anonymizes paths containing your home path (thanks to AnotherPillow!).
  * Removed confusing "Found X mods with warnings:" log message.
  * The installer on Linux now tries to open a terminal if needed (thanks to HoodedDeath!).
  * Fixed installer not detecting Linux Flatpak install paths.
  * Fixed various content issues for non-English players (e.g. content packs not detecting the current festival correctly).
  * Fixed dependencies on obsolete redundant mods not ignored in some cases.
  * Fixed issues in Console Commands:
    * Fixed `list_items` & `player_add` not handling dried items, pickled forage, smoked fish, and specific bait correctly.
    * Fixed `list_items` & `player_add` listing some flooring & wallpaper items twice.
    * Fixed `show_data_files` & `show_game_files` no longer working correctly (thanks to jakerosado!).
  * Fixed some mod overlays mispositioned when your UI scale is non-100% and zoom level is 100%.
  * Fixed incorrect 'direct console access' warnings.
  * Updated mod compatibility list.

* For mod authors:
  * Added support for [private assembly references](https://stardewvalleywiki.com/Modding:Modder_Guide/APIs/Manifest#Private_assemblies) (thanks to Shockah!).
  * Added support for [i18n subfolders](https://stardewvalleywiki.com/Modding:Modder_Guide/APIs/Translation#i18n_folder) (thanks to spacechase0!).
  * Added asset propagation for `Data/ChairTiles`.
  * Added new C# API methods:
    * Added `DoesAssetExist` methods to `helper.GameContent` and `helper.ModContent` (thanks to KhloeLeclair!).
    * Added scroll wheel suppression via `helper.Input.SuppressScrollWheel()` (thanks to MercuriusXeno!).
    * Added `PathUtilities.AnonymizePathForDisplay` to anonymize home paths (thanks to AnotherPillow!).
  * Added parameter docs to event interfaces. This lets you fully document your event handlers like `/// <inheritdoc cref="IGameLoopEvents.SaveLoaded" />`.
  * Translations now support [gender switch blocks](https://stardewvalleywiki.com/Modding:Dialogue#Gender_switch).
  * Translations now support tokens in their placeholder text.
  * SMAPI no longer blocks map edits which change the tilesheet order, since that no longer causes crashes in Stardew Valley 1.6.9.
  * The SMAPI log now includes the assembly version of each loaded mod (thanks to spacechase0!).
  * Updated dependencies, including...
    * [FluentHttpClient](https://github.com/Pathoschild/FluentHttpClient#readme) 4.3.0 → 4.4.1 (see [changes](https://github.com/Pathoschild/FluentHttpClient/blob/develop/RELEASE-NOTES.md#441));
    * [Pintail](https://github.com/Nanoray-pl/Pintail) 2.3.0 → 2.6.0 (see [changes](https://github.com/Nanoray-pl/Pintail/blob/master/docs/release-notes.md#260)).
  * Fixed `content.Load` ignoring language override in recent versions.
  * Fixed player sprites and building paint masks not always propagated on change.
  * Fixed `.tmx` map tile sizes being premultiplied, which is inconsistent with the game's `.tbin` maps.
  * Fixed various edge cases when chaining methods on `Translation` instances.

* For the update check server:
  * Rewrote update checks for mods on CurseForge and ModDrop to use new export API endpoints.  
    _This should result in much faster update checks for those sites, and less chance of update-check errors when their servers are under heavy load._
  * Added workaround for CurseForge auto-syncing prerelease versions with an invalid version number.

* For the log parser:
  * Clicking a checkbox in the mod list now always only changes that checkbox, to allow hiding a single mod.
  * Fixed the wrong game folder path shown if the `Mods` folder path was customized.

* For the JSON validator:
  * Updated for Content Patcher 2.1.0 &ndash; 2.4.0, and fixed validation for `Priority` fields.
  * Fixed incorrect errors shown for..
    * some valid `Entries`, `Fields`, `MapProperties`, `MapTiles`, and `When` field values;
    * `CustomLocations` entries which use the new [unique string ID](https://stardewvalleywiki.com/Modding:Common_data_field_types#Unique_string_ID) format;
    * `AddWarps` warps when a location name contains a dot.

* For the web API:
  * The [anonymized metrics for update check requests](technical/web.md#modsmetrics) now counts requests by SMAPI and game version.

## 4.0.8
Released 21 April 2024 for Stardew Valley 1.6.4 or later.

* For players:
  * Added option to disable Harmony fix for players with certain crashes.
  * Fixed crash for non-English players in split-screen mode when mods translate some vanilla assets.
  * SMAPI no longer rewrites mods which use Harmony 1.x, to help reduce Harmony crashes.  
    _This should affect very few mods that still work otherwise, and any Harmony mod updated after July 2021 should be unaffected._
  * Updated mod compatibility list to prevent common crashes.

* For the update check server:
  * Rewrote update checks for mods on Nexus Mods to use a new Nexus API endpoint.  
    _This should result in much faster update checks for Nexus, and less chance of update-check errors when the Nexus servers are under heavy load._

## 4.0.7
Released 18 April 2024 for Stardew Valley 1.6.4 or later.

* For players:
  * Updated for Stardew Valley 1.6.4.
  * The installer now lists detected game folders with an incompatible version to simplify troubleshooting.
  * When the installer asks for a game folder path, entering an incorrect path to a file inside it will now still select the folder.
  * Fixed installer not detecting 1.6 compatibility branch.

* For the web UI:
  * Updated `manifest.json` JSON schema for the new `MinimumGameVersion` field (thanks to KhloeLeclair!).

* For external tool authors:
  * In the SMAPI toolkit, added a new `GetGameFoldersIncludingInvalid()` method to get all detected game folders and their validity type.

## 4.0.6
Released 07 April 2024 for Stardew Valley 1.6.0 or later.

* For players:
  * The SMAPI log file now includes installed mod IDs, to help with troubleshooting (thanks to DecidedlyHuman!).

* For mod authors:
  * Added optional [`MinimumGameVersion` manifest field](https://stardewvalleywiki.com/Modding:Modder_Guide/APIs/Manifest#Minimum_SMAPI_or_game_version).

## 4.0.5
Released 06 April 2024 for Stardew Valley 1.6.0 or later.

* For players:
  * The installer now deletes obsolete files from very old SMAPI versions again. (This was removed in SMAPI 4.0, but many players still had very old versions.)
  * The installer now deletes Error Handler automatically if it's at the default path.
  * Fixed mods sometimes not applying logic inside new buildings.
  * Minor optimizations.
  * Updated mod compatibility list.

* For mod authors:
  * Fixed world-changed events (e.g. `ObjectListChanged`) not working correctly inside freshly-constructed buildings.

## 4.0.4
Released 29 March 2024 for Stardew Valley 1.6.0 or later.

* For players:
  * Added `log_context` console command, which replaces `test_input` and logs more info like menu changes.
  * Added [`--prefer-terminal-name` command-line argument](technical/smapi.md#command-line-arguments) to override which terminal SMAPI is launched with (thanks to test482!).
  * Fixed some mods compiled for Stardew Valley 1.6.3+ not working in 1.6.0–1.6.2.
  * Fixed SMAPI's "_Found warnings with X mods_" message counting hidden warnings.
  * Improved translations. Thanks to RezaHidayatM (added Indonesian)!

* For the web UI:
  * Improved smapi.io colors for accessibility, converted PNG images to SVG, and updated Patreon logo (thanks to ishan!).
  * Fixed JSON schema validation:
    * Manifest `UpdateKeys` field now allows dots in the GitHub repo name.
    * Fixed Content Patcher's `FromMapFile` and `FromFile` patterns.

## 4.0.3
Released 27 March 2024 for Stardew Valley 1.6.0 or later.

* For players:
  * Updated compatibility rewrites for Stardew Valley 1.6.3.
  * Updated mod compatibility list.
  * Tweaked `player_add` console command's error messages for clarity.

## 4.0.2
Released 24 March 2024 for Stardew Valley 1.6.0 or later.

* For players:
  * Updated mod compatibility list.
  * Improved status for obsolete mods to be clearer that they can be removed.
  * Disabled Extra Map Layers mod.
    _Extra Map Layers mod caused visual issues like dark shadows in all locations with extra map layers, since the game now handles them automatically. SMAPI now disables Extra Map Layers and ignores dependencies on it._
  * When using a custom `Mods` folder path, SMAPI now logs the game folder path to simplify troubleshooting.

## 4.0.1
Released 20 March 2024 for Stardew Valley 1.6.0 or later.

* For players:
  * Fixed error in some cases when rewritten mod code removes items from an inventory.

* For the web UI:
  * Added CurseForge download link to main page for cases where Nexus is unavailable.

## 4.0.0
Released 19 March 2024 for Stardew Valley 1.6.0 or later. See [release highlights](https://www.patreon.com/posts/100388693).

* For players:
  * Updated for Stardew Valley 1.6.
  * Added support for overriding SMAPI configuration per `Mods` folder (thanks to Shockah!).
  * Improved performance.
  * Improved compatibility rewriting to handle more cases (thanks to SinZ for his contributions!).
  * Removed the bundled `ErrorHandler` mod, which is now integrated into Stardew Valley 1.6.
  * Removed obsolete console commands: `list_item_types` (no longer needed) and `player_setimmunity` (broke in 1.6 and rarely used).
  * Removed support for seamlessly updating from SMAPI 2.11.3 and earlier (released in 2019).  
    _If needed, you can update to SMAPI 3.18.0 first and then install the latest version._

* For mod authors:
  * Updated to .NET 6.
  * Added [`RenderingStep` and `RenderedStep` events](https://stardewvalleywiki.com/Modding:Modder_Guide/APIs/Events#Display.RenderingStep), which let you handle a specific step in the game's render cycle.
  * Added support for [custom update manifests](https://stardewvalleywiki.com/Modding:Modder_Guide/APIs/Update_checks#Custom_update_manifest) (thanks to Jamie Taylor!).
  * Removed all deprecated APIs.
  * SMAPI no longer intercepts output written to the console. Mods which directly access `Console` will be listed under mod warnings.
  * Calling `Monitor.VerboseLog` with an interpolated string no longer evaluates the string if verbose mode is disabled (thanks to atravita!). This only applies to mods compiled in SMAPI 4.0.0 or later.
  * Fixed redundant `TRACE` logs for a broken mod which references members with the wrong types.

* For the web UI:
  * Updated JSON validator for Content Patcher 2.0.0.
  * Added [anonymized metrics for update check requests](technical/web.md#modsmetrics).
  * Fixed uploaded log/JSON file expiry alway shown as renewed.
  * Fixed update check for mods with a prerelease version tag not recognized by the ModDrop API. SMAPI now parses the version itself if needed.

* For SMAPI developers:
  * Added `LogTechnicalDetailsForBrokenMods` option in `smapi-internal/config.json`, which adds more technical info to the SMAPI log when a mod is broken. This is mainly useful for creating compatibility rewriters.

## 3.18.6 and earlier
See [older release notes](release-notes-archived.md).
