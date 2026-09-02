     ___           ___           ___           ___        ___     
    /  /\         /__/\         /  /\         /  /\      /  /\    
   /  /:/_       |  |::\       /  /::\       /  /::\    /  /:/    
  /  /:/ /\      |  |:|:\     /  /:/\:\     /  /:/\:\  /  /:/     
 /  /:/ /::\   __|__|:|\:\   /  /:/~/::\   /  /:/~/:/ /  /::\ ___ 
/__/:/ /:/\:\ /__/::::| \:\ /__/:/ /:/\:\ /__/:/ /:/ /__/:/\:\  /\
\  \:\/:/~/:/ \  \:\~~\__\/ \  \:\/:/__\/ \  \:\/:/  \__\/  \:\/:/
 \  \::/ /:/   \  \:\        \  \::/       \  \::/        \__\::/ 
  \__\/ /:/     \  \:\        \  \:\        \  \:\        /  /:/  
    /__/:/       \  \:\        \  \:\        \  \:\      /__/:/   
    \__\/         \__\/         \__\/         \__\/      \__\/    


SMAPI lets you run Stardew Valley with mods. Don't forget to download mods separately.


Automated install
--------------------------------
This package may be an unofficial fork. Check the release page and embedded version before running
it. For the experimental Linux fork, verify the checksum and GitHub attestation first, then see:
https://4eh5xitv6787h645ebv.github.io/SMAPI/technical/linux-alpha-release.html

On Linux in an X11 or XWayland desktop session, close the game and run
"install on Linux (graphical).sh". It runs as your normal desktop user and never needs sudo or root.
The graphical launcher uses a private temporary runtime-extraction directory and normally removes
it after an ordinary exit or successfully settled HUP, INT, or TERM signal. If its bounded child-
settlement deadline expires, it retains those private runtime files to avoid unsafe deletion; after
confirming no installer process remains, you may remove the leftover directory manually. A power
loss or SIGKILL can also leave that private temporary directory behind.

The existing "install on Linux.sh" console installer remains available as the non-graphical
fallback. Use it for a terminal, headless or native-Wayland-only session, or if the graphical
launcher fails. This is a legacy direct install/uninstall path, not a text version of the graphical
transactional workflow. It does not verify the release itself. Verify the complete release set and
outer ZIP before extracting or running anything, close the game, back up saves and Mods, and never
use sudo.

The interactive wrapper accepts no command-line options and rejects them with status 2. For a
prompt-free Linux action, open a terminal in this extracted installer directory and use exactly one
of these commands with an absolute game path:

  ./internal/linux/SMAPI.Installer --no-prompt --install --game-path "/absolute/path/to/Stardew Valley"
  ./internal/linux/SMAPI.Installer --no-prompt --uninstall --game-path "/absolute/path/to/Stardew Valley"

Do not run --no-prompt without the complete action and game-path arguments. Unknown arguments are
ignored. Exit 0 means the requested legacy action returned success; exit 2 covers known validation
failure paths; exit 1 covers an unexpected exception. Shell or signal exits can use
other statuses. A failure or signal can happen after files changed, and no exit status proves a
rollback. The console can print game paths and full exception text, so review it before sharing.

The legacy path supports only install and uninstall. Running Install again first removes known SMAPI
files and is not an authenticated Update or Repair. It has no graphical/Core plan, receipt, journal,
automatic rollback, interrupted recovery, recovery history, backup operation, or authenticated
rollback. The --linux-protocol-v1-jsonl flag is private to the graphical frontend and is not a
supported manual command.

For official SMAPI and general mod help, see:
https://stardewvalleywiki.com/Modding:Player_Guide


Manual install
--------------------------------
THIS IS NOT RECOMMENDED FOR MOST PLAYERS. See the instructions above instead.
If you really want to install SMAPI manually, here's how.

On Linux, raw extraction is a last resort for a fresh install only. First verify all release files
and the outer installer ZIP using the release guide above, and make a separate full backup. Do not
use these steps for an update, repair, uninstall, or rollback. They do not detect file conflicts,
create an authenticated receipt or journal, roll back a partial copy, or recover an interruption.
If any SMAPI host, smapi-internal folder, unix-launcher.sh, or StardewValley-original already exists
in the game folder, stop instead of overwriting it.

1. Unzip "internal/windows/install.dat" (Windows), "internal/linux/install.dat" (Linux), or
   "internal/macOS/install.dat" (macOS). You can change '.dat' to '.zip'; it is a normal ZIP with
   another extension to prevent confusion.

2. Copy the files from the folder you just unzipped into your game folder. The
   `StardewModdingAPI.exe` file should be right next to the game's executable.

3. Copy `Stardew Valley.deps.json` in the game folder. On Linux name the copy
   `StardewModdingAPI-net6.deps.json`; on Windows/macOS name it `StardewModdingAPI.deps.json`.

4.
  - Windows only: if you use Steam, see the install guide above to enable achievements and
    overlay. Otherwise, just run StardewModdingAPI.exe in your game folder to play with mods.

  - Linux/macOS only: rename the "StardewValley" file (no extension) to "StardewValley-original",
    without overwriting an existing backup, and "unix-launcher.sh" to "StardewValley". Keep
    "StardewModdingAPI" beside it. Mark the launcher and runtime hosts executable as described in the
    release guide, then launch the game as usual.

Never recursively delete the game folder, Mods, saves, ErrorLogs, HealthReports, .smapi-installer,
or other user data. Never use a wildcard or broad recursive delete to imitate uninstallation. On
Linux, see the version-specific release guide for the exact fresh-install collision and permission
steps.

When installing on Linux or macOS:
- To configure the color scheme, edit the `smapi-internal/config.json` file and see instructions
  there for the 'ColorScheme' setting.
