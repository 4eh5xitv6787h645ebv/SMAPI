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

On Linux, close the game and run "install on Linux (graphical).sh" for the graphical installer.
It runs as your normal desktop user and never needs sudo or root. The graphical launcher uses a
private temporary runtime-extraction directory and removes it when the installer closes.

The existing "install on Linux.sh" console installer remains available as the non-graphical
fallback. Headless and scripted users can also run "internal/linux/SMAPI.Installer" directly.

For official SMAPI and general mod help, see:
https://stardewvalleywiki.com/Modding:Player_Guide


Manual install
--------------------------------
THIS IS NOT RECOMMENDED FOR MOST PLAYERS. See the instructions above instead.
If you really want to install SMAPI manually, here's how.

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
    and "unix-launcher.sh" to "StardewValley". Keep "StardewModdingAPI" beside it. Mark the launcher
    and runtime hosts executable as described in the release guide, then launch the game as usual.

When installing on Linux or macOS:
- To configure the color scheme, edit the `smapi-internal/config.json` file and see instructions
  there for the 'ColorScheme' setting.
