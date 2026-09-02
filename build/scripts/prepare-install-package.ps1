#!/usr/bin/env pwsh

#
#
# Note: On Windows, this script *does not* set Linux permissions. The final changes are handled by the
# finalize-install-package.sh file in WSL.
#
#


##########
## Validate environment
##########
if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Error "This script needs PowerShell 7 or later. Run it using 'pwsh' instead." # needed for `$IsWindows`
    exit 1
}
$ErrorActionPreference = "Stop"


##########
## Read arguments
##########
$windowsOnly = $false # Windows-only build
$linuxOnly = $false # Linux-only build
$skipBundleDeletion = $false # skip bundle deletion (only applies when using WSL to finalize the build on Windows)
$gamePathOverride = $null # explicit reference-assembly path for reproducible builds
$githubCliDirectory = $null # staged pinned GitHub CLI binary and license for the Linux release package
foreach ($arg in $args) {
    if ($arg -eq "--windows-only" -and $IsWindows) {
        $windowsOnly = $true
    }
    elseif ($arg -eq "--linux-only") {
        $linuxOnly = $true
    }
    elseif ($arg -eq "--skip-bundle-deletion") {
        $skipBundleDeletion = $true
    }
    elseif ($arg.StartsWith("--game-path=")) {
        $gamePathOverride = $arg.Substring("--game-path=".Length)
    }
    elseif ($arg.StartsWith("--github-cli-directory=")) {
        $githubCliDirectory = $arg.Substring("--github-cli-directory=".Length)
    }
}
if ($windowsOnly -and $linuxOnly) {
    throw "The --windows-only and --linux-only options can't be combined."
}
if ($linuxOnly -and [string]::IsNullOrWhiteSpace($githubCliDirectory)) {
    throw "Linux-only release packages require --github-cli-directory with the staged pinned GitHub CLI binary and license."
}


##########
## Find the game folder
##########
if ($gamePathOverride) {
    $possibleGamePaths = @($gamePathOverride)
}
elseif ($IsWindows) {
    $possibleGamePaths=(
        # GOG
        "C:\Program Files\GalaxyClient\Games\Stardew Valley",
        "C:\Program Files\GOG Galaxy\Games\Stardew Valley",
        "C:\Program Files\GOG Games\Stardew Valley",
        "C:\Program Files (x86)\GalaxyClient\Games\Stardew Valley",
        "C:\Program Files (x86)\GOG Galaxy\Games\Stardew Valley",
        "C:\Program Files (x86)\GOG Games\Stardew Valley",

        # Steam
        "C:\Program Files\Steam\steamapps\common\Stardew Valley",
        "C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
    )
}
else {
    $possibleGamePaths=(
        # override
        "$HOME/StardewValley",

        # Linux
        "$HOME/GOG Games/Stardew Valley/game",
        "$HOME/.steam/steam/steamapps/common/Stardew Valley",
        "$HOME/.local/share/Steam/steamapps/common/Stardew Valley",
        "$HOME/.var/app/com.valvesoftware.Steam/data/Steam/steamapps/common/Stardew Valley",

        # macOS
        "/Applications/Stardew Valley.app/Contents/MacOS",
        "$HOME/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS"
    )
}

$gamePath = ""
foreach ($possibleGamePath in $possibleGamePaths) {
    if (Test-Path $possibleGamePath -PathType Container) {
        $gamePath = $possibleGamePath
        break
    }
}


##########
## Preset values
##########
# paths
$bundleModNames = "ConsoleCommands", "SaveBackup"

# build configuration
$buildConfig = "Release"
$framework = "net6.0"
if ($windowsOnly) {
    $folders = "windows"
    $runtimes = @{ windows = "win-x64" }
    $msBuildPlatformNames = @{ windows = "Windows_NT" }
}
elseif ($linuxOnly) {
    $folders = "linux"
    $runtimes = @{ linux = "linux-x64" }
    $msBuildPlatformNames = @{ linux = "Unix" }
}
else {
    $folders = "linux", "macOS", "windows"
    $runtimes = @{ linux = "linux-x64"; macOS = "osx-x64"; windows = "win-x64" }
    $msBuildPlatformNames = @{ linux = "Unix"; macOS = "OSX"; windows = "Windows_NT" }
}

# version number
$version = $args[0]
if (!$version) {
    $version = Read-Host "SMAPI release version (like '4.0.0')"
}


##########
## Move to SMAPI root
##########
Set-Location "$PSScriptRoot/../.."

function Get-UnixHardLinkCount {
    param(
        [Parameter(Mandatory = $true)] [string] $Path
    )

    $count = if ($IsMacOS) {
        & stat -f '%l' -- $Path
    }
    else {
        & stat -c '%h' -- $Path
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Failed reading the hard-link count for '$Path'."
    }
    return $count
}

function Assert-PinnedGitHubCliFile {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [long] $ExpectedSize,
        [Parameter(Mandatory = $true)] [string] $ExpectedSha256
    )

    $item = Get-Item -LiteralPath $Path -Force
    if ($item -isnot [System.IO.FileInfo] -or ![string]::IsNullOrEmpty($item.LinkType)) {
        throw "The pinned GitHub CLI input '$Path' must be an ordinary non-link file."
    }
    if (!$IsWindows) {
        $hardLinkCount = Get-UnixHardLinkCount $item.FullName
        if ($hardLinkCount -ne "1") {
            throw "The pinned GitHub CLI input '$Path' must have exactly one hard link."
        }
    }
    if ($item.Length -ne $ExpectedSize) {
        throw "The pinned GitHub CLI input '$Path' has unexpected size $($item.Length)."
    }
    $actualSha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $ExpectedSha256) {
        throw "The pinned GitHub CLI input '$Path' has an unexpected SHA-256 digest."
    }
}

function Assert-LinuxGuiPublishOutput {
    param(
        [Parameter(Mandatory = $true)] [string] $Path
    )

    $directory = Get-Item -LiteralPath $Path -Force
    if ($directory -isnot [System.IO.DirectoryInfo] -or ![string]::IsNullOrEmpty($directory.LinkType)) {
        throw "The Linux graphical-installer publish output must be an ordinary directory."
    }
    $entries = @(Get-ChildItem -LiteralPath $directory.FullName -Force)
    if (
        $entries.Count -ne 1 -or
        $entries[0].Name -ne "SMAPI.Installer.Gui" -or
        $entries[0] -isnot [System.IO.FileInfo] -or
        ![string]::IsNullOrEmpty($entries[0].LinkType) -or
        $entries[0].Length -le 0
    ) {
        throw "The Linux graphical-installer publish output must contain exactly one nonempty ordinary 'SMAPI.Installer.Gui' apphost."
    }
    if (!$IsWindows) {
        $hardLinkCount = Get-UnixHardLinkCount $entries[0].FullName
        if ($hardLinkCount -ne "1") {
            throw "The Linux graphical-installer apphost must have exactly one hard link."
        }
    }
}

if ($githubCliDirectory) {
    $githubCliDirectory = [System.IO.Path]::GetFullPath($githubCliDirectory, (Get-Location).Path)
    $githubCliDirectoryItem = Get-Item -LiteralPath $githubCliDirectory -Force
    if ($githubCliDirectoryItem -isnot [System.IO.DirectoryInfo] -or ![string]::IsNullOrEmpty($githubCliDirectoryItem.LinkType)) {
        throw "The pinned GitHub CLI path must be an ordinary directory."
    }
    $githubCliEntries = @(Get-ChildItem -LiteralPath $githubCliDirectory -Force)
    $githubCliEntryNames = @($githubCliEntries.Name | Sort-Object) -join "`n"
    if ($githubCliEntries.Count -ne 2 -or $githubCliEntryNames -ne "gh`ngh-LICENSE.txt") {
        throw "The pinned GitHub CLI directory must contain exactly 'gh' and 'gh-LICENSE.txt'."
    }
    Assert-PinnedGitHubCliFile "$githubCliDirectory/gh" 39805090 "b58e487e37c00c114aa07f14987ce12f5e5abf12b9da8a38937b65ef218f6772"
    Assert-PinnedGitHubCliFile "$githubCliDirectory/gh-LICENSE.txt" 1068 "6da4adc42392c8485e40b4251c7e332fc3352df1947c9ffade71dd60b14a7a4f"
}


##########
## Clear old build files
##########
Write-Output "Clearing old builds..."
Write-Output "-------------------------------------------------"

foreach ($path in (Get-ChildItem -Recurse -Include ('bin', 'obj'))) {
    Write-Output "$path"
    Remove-Item -Recurse -Force "$path"
}
Write-Output ""


##########
## Compile files
##########
. "$PSScriptRoot/set-smapi-version.ps1" "$version"
foreach ($folder in $folders) {
    $runtime = $runtimes[$folder]
    $msbuildPlatformName = $msBuildPlatformNames[$folder]

    Write-Output "Compiling SMAPI for $folder..."
    Write-Output "-------------------------------------------------"
    if ($folder -eq "linux") {
        dotnet publish src/SMAPI --configuration $buildConfig -v minimal --runtime "$runtime" --framework "$framework" -p:OS="$msbuildPlatformName" -p:TargetFrameworks="$framework" -p:GamePath="$gamePath" -p:CopyToGameFolder="false" -p:RollForward="LatestMajor" -p:EnableUnsafeBinaryFormatterSerialization="false" -p:SelfContained="false" -p:UseAppHost="false"
    }
    else {
        dotnet publish src/SMAPI --configuration $buildConfig -v minimal --runtime "$runtime" --framework "$framework" -p:OS="$msbuildPlatformName" -p:TargetFrameworks="$framework" -p:GamePath="$gamePath" -p:CopyToGameFolder="false" --self-contained true
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Failed publishing SMAPI for $folder (exit code $LASTEXITCODE)."
    }
    Write-Output ""
    Write-Output ""

    if ($folder -eq "linux") {
        Write-Output "Compiling private .NET 10 host for $folder..."
        Write-Output "-------------------------------------------------"
        dotnet publish build/SMAPI.Host --configuration $buildConfig -v minimal --runtime "$runtime" --self-contained false
        if ($LASTEXITCODE -ne 0) {
            throw "Failed publishing the private .NET 10 host for $folder (exit code $LASTEXITCODE)."
        }
        Write-Output ""
        Write-Output ""

        Write-Output "Compiling game-runtime .NET 6 fallback host for $folder..."
        Write-Output "-------------------------------------------------"
        dotnet publish build/SMAPI.Host.Net6 --configuration $buildConfig -v minimal --runtime "$runtime" --self-contained true
        if ($LASTEXITCODE -ne 0) {
            throw "Failed publishing the game-runtime .NET 6 fallback host for $folder (exit code $LASTEXITCODE)."
        }
        Write-Output ""
        Write-Output ""
    }

    Write-Output "Compiling installer for $folder..."
    Write-Output "-------------------------------------------------"
    dotnet publish src/SMAPI.Installer --configuration $buildConfig -v minimal --runtime "$runtime" --framework "$framework" -p:OS="$msbuildPlatformName" -p:TargetFrameworks="$framework" -p:GamePath="$gamePath" -p:CopyToGameFolder="false" --self-contained true
    Write-Output ""
    Write-Output ""

    if ($LASTEXITCODE -ne 0) {
        throw "Failed publishing the installer for $folder (exit code $LASTEXITCODE)."
    }

    if ($folder -eq "linux") {
        $linuxGuiPublishPath = "src/SMAPI.Installer.Gui/bin/$buildConfig/net10.0/linux-x64/package-publish"
        Write-Output "Compiling graphical installer for $folder..."
        Write-Output "-------------------------------------------------"
        dotnet publish src/SMAPI.Installer.Gui --configuration $buildConfig -v minimal --runtime "$runtime" --framework "net10.0" --output "$linuxGuiPublishPath" -p:OS="$msbuildPlatformName" -p:CopyToGameFolder="false" --self-contained true -p:PublishSingleFile="true" -p:IncludeNativeLibrariesForSelfExtract="true" -p:PublishTrimmed="false" -p:PublishAot="false" -p:DebugSymbols="false" -p:DebugType="None"
        if ($LASTEXITCODE -ne 0) {
            throw "Failed publishing the graphical installer for $folder (exit code $LASTEXITCODE)."
        }
        Assert-LinuxGuiPublishOutput $linuxGuiPublishPath
        Write-Output ""
        Write-Output ""
    }

    foreach ($modName in $bundleModNames) {
        Write-Output "Compiling $modName for $folder..."
        Write-Output "-------------------------------------------------"
        dotnet publish src/SMAPI.Mods.$modName --configuration $buildConfig -v minimal --runtime "$runtime" --framework "$framework" -p:OS="$msbuildPlatformName" -p:TargetFrameworks="$framework" -p:GamePath="$gamePath" -p:CopyToGameFolder="false" --self-contained false
        Write-Output ""
        Write-Output ""
        if ($LASTEXITCODE -ne 0) {
            throw "Failed publishing $modName for $folder (exit code $LASTEXITCODE)."
        }
    }
}


##########
## Prepare install package
##########
Write-Output "Preparing install package..."
Write-Output "----------------------------"

# init paths
$installAssets = "src/SMAPI.Installer/assets"
$packageTitle = if ($linuxOnly) { "SMAPI $version Linux installer" } else { "SMAPI $version installer" }
$packagePath = "bin/$packageTitle"

# init structure
Write-Host "Setting up structure..."
foreach ($folder in $folders) {
    $folderPath = "$packagePath/internal/$folder/bundle/smapi-internal"

    if ($IsWindows) {
        # On Windows, mkdir creates parent directories automatically and the --parents argument isn't recognized.
        mkdir "$folderPath" > $null
    }
    else
    {
        mkdir "$folderPath" --parents
    }
}

# copy base installer files
foreach ($name in @("install on Linux.sh", "install on Linux (graphical).sh", "install on macOS.command", "install on Windows.bat", "README.txt")) {
    if ($windowsOnly -and ($name -eq "install on Linux.sh" -or $name -eq "install on Linux (graphical).sh" -or $name -eq "install on macOS.command")) {
        continue;
    }
    if ($linuxOnly -and ($name -eq "install on macOS.command" -or $name -eq "install on Windows.bat")) {
        continue;
    }

    Copy-Item "$installAssets/$name" "$packagePath"
}

# copy per-platform files
foreach ($folder in $folders) {
    $runtime = $runtimes[$folder]

    # get paths
    $smapiBin = "src/SMAPI/bin/$buildConfig/$runtime/publish"
    $internalPath = "$packagePath/internal/$folder"
    $bundlePath = "$internalPath/bundle"
    $linuxHostBin = "build/SMAPI.Host/bin/$buildConfig/net10.0/linux-x64/publish"
    $linuxNet6HostBin = "build/SMAPI.Host.Net6/bin/$buildConfig/net6.0/linux-x64/publish"

    # installer files
    Copy-Item "src/SMAPI.Installer/bin/$buildConfig/$runtime/publish/*" "$internalPath" -Recurse
    Remove-Item -Recurse -Force "$internalPath/assets"
    if ($folder -eq "linux") {
        $linuxGuiPublishPath = "src/SMAPI.Installer.Gui/bin/$buildConfig/net10.0/linux-x64/package-publish"
        Assert-LinuxGuiPublishOutput $linuxGuiPublishPath
        Copy-Item -LiteralPath "$linuxGuiPublishPath/SMAPI.Installer.Gui" -Destination "$internalPath/SMAPI.Installer.Gui"
        $packagedGui = Get-Item -LiteralPath "$internalPath/SMAPI.Installer.Gui" -Force
        if (
            $packagedGui -isnot [System.IO.FileInfo] -or
            ![string]::IsNullOrEmpty($packagedGui.LinkType) -or
            $packagedGui.Length -le 0 -or
            (Get-FileHash -LiteralPath $packagedGui.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath "$linuxGuiPublishPath/SMAPI.Installer.Gui" -Algorithm SHA256).Hash
        ) {
            throw "The packaged Linux graphical-installer apphost isn't the exact ordinary published file."
        }
        if (!$IsWindows) {
            $hardLinkCount = Get-UnixHardLinkCount $packagedGui.FullName
            if ($hardLinkCount -ne "1") {
                throw "The packaged Linux graphical-installer apphost must have exactly one hard link."
            }
        }
    }
    if ($folder -eq "linux" -and $githubCliDirectory) {
        Copy-Item -LiteralPath "$githubCliDirectory/gh" -Destination "$internalPath/gh"
        Copy-Item -LiteralPath "$githubCliDirectory/gh-LICENSE.txt" -Destination "$internalPath/gh-LICENSE.txt"
        Assert-PinnedGitHubCliFile "$internalPath/gh" 39805090 "b58e487e37c00c114aa07f14987ce12f5e5abf12b9da8a38937b65ef218f6772"
        Assert-PinnedGitHubCliFile "$internalPath/gh-LICENSE.txt" 1068 "6da4adc42392c8485e40b4251c7e332fc3352df1947c9ffade71dd60b14a7a4f"
    }

    # runtime config for SMAPI
    if ($folder -eq "linux") {
        Copy-Item "$installAssets/runtimeconfig-linux.json" "$bundlePath/StardewModdingAPI.runtimeconfig.json"
        Copy-Item "$installAssets/runtimeconfig-linux-net6.json" "$bundlePath/StardewModdingAPI-net6.runtimeconfig.json"
    }
    else {
        # This is identical to the one generated by the build, except that the min runtime version is
        # set to 6.0.0 (instead of whatever version it was built with) and rollForward is set to latestMinor instead of
        # minor.
        Copy-Item "$installAssets/runtimeconfig.json" "$bundlePath/StardewModdingAPI.runtimeconfig.json"
    }

    # installer DLL config
    if ($folder -eq "windows") {
        Copy-Item "$installAssets/windows-exe-config.xml" "$packagePath/internal/windows/install.exe.config"
    }

    # bundle root files
    if ($folder -eq "linux") {
        Copy-Item "$installAssets/smapi-runtime-dispatcher.sh" "$bundlePath/StardewModdingAPI"
        Copy-Item "$linuxHostBin/StardewModdingAPI-net10" "$bundlePath/StardewModdingAPI-net10"
        Copy-Item "$linuxNet6HostBin/StardewModdingAPI-net6" "$bundlePath/StardewModdingAPI-net6"
        Copy-Item "$smapiBin/StardewModdingAPI.dll" "$bundlePath"
        Copy-Item "$smapiBin/StardewModdingAPI.dll" "$bundlePath/StardewModdingAPI-net6.dll"
        Copy-Item "$smapiBin/StardewModdingAPI.dll" "$bundlePath/StardewModdingAPI-net10.dll"
        Copy-Item "$smapiBin/StardewModdingAPI.deps.json" "$bundlePath"
        $depsJson = Get-Content "$smapiBin/StardewModdingAPI.deps.json" -Raw
        $genericRuntimeAsset = '"StardewModdingAPI.dll": {}'
        $aliasedRuntimeAsset = '"StardewModdingAPI-net10.dll": {}'
        if ([regex]::Matches($depsJson, [regex]::Escape($genericRuntimeAsset)).Count -ne 1) {
            throw "The generated SMAPI deps file doesn't contain exactly one '$genericRuntimeAsset' runtime asset."
        }
        $depsJson = $depsJson.Replace($genericRuntimeAsset, $aliasedRuntimeAsset)
        [System.IO.File]::WriteAllText(
            "$bundlePath/StardewModdingAPI-net10.deps.json",
            $depsJson,
            [System.Text.UTF8Encoding]::new($false)
        )
        Copy-Item "$installAssets/runtimeconfig-linux.json" "$bundlePath/StardewModdingAPI-net10.runtimeconfig.json"
        Copy-Item "$linuxHostBin/dotnet" "$bundlePath/smapi-internal" -Recurse
        Copy-Item "$smapiBin/StardewModdingAPI.xml" "$bundlePath"
        Copy-Item "$smapiBin/steam_appid.txt" "$bundlePath"
    }
    else {
        foreach ($name in @("StardewModdingAPI", "StardewModdingAPI.dll", "StardewModdingAPI.xml", "steam_appid.txt")) {
            if ($name -eq "StardewModdingAPI" -and $folder -eq "windows") {
                $name = "$name.exe"
            }

            Copy-Item "$smapiBin/$name" "$bundlePath"
        }
    }

    # bundle i18n
    Copy-Item -Recurse "$smapiBin/i18n" "$bundlePath/smapi-internal"

    # bundle smapi-internal
    $internalFiles = @("0Harmony.dll", "0Harmony.pdb", "0Harmony.xml", "Markdig.dll", "Mono.Cecil.dll", "Mono.Cecil.Mdb.dll", "Mono.Cecil.Pdb.dll", "MonoMod.Backports.dll", "MonoMod.Core.dll", "MonoMod.Iced.dll", "MonoMod.ILHelpers.dll", "MonoMod.Utils.dll", "Newtonsoft.Json.dll", "Pathoschild.Http.Client.dll", "Pintail.dll", "TMXTile.dll", "SMAPI.Toolkit.dll", "SMAPI.Toolkit.xml", "SMAPI.Toolkit.CoreInterfaces.dll", "SMAPI.Toolkit.CoreInterfaces.xml", "System.Net.Http.Formatting.dll")
    if ($folder -eq "linux") {
        $internalFiles += @("HtmlAgilityPack.dll", "Mono.Cecil.Rocks.dll", "Newtonsoft.Json.Bson.dll")
    }
    foreach ($name in $internalFiles) {
        Copy-Item "$smapiBin/$name" "$bundlePath/smapi-internal"
    }

    if ($folder -eq "windows") {
        Copy-Item "$smapiBin/VdfConverter.dll" "$bundlePath/smapi-internal"
    }

    Copy-Item "$smapiBin/SMAPI.blacklist.json" "$bundlePath/smapi-internal/blacklist.json"
    Copy-Item "$smapiBin/SMAPI.config.json" "$bundlePath/smapi-internal/config.json"
    Copy-Item "$smapiBin/SMAPI.metadata.json" "$bundlePath/smapi-internal/metadata.json"
    if ($folder -eq "linux" -or $folder -eq "macOS") {
        Copy-Item "$installAssets/unix-launcher.sh" "$bundlePath"
    }
    else {
        Copy-Item "$installAssets/windows-exe-config.xml" "$bundlePath/StardewModdingAPI.exe.config"
    }

    # copy bundled mods
    foreach ($modName in $bundleModNames) {
        $fromPath = "src/SMAPI.Mods.$modName/bin/$buildConfig/$runtime/publish"
        $targetPath = "$bundlePath/Mods/$modName"

        if ($IsWindows) {
            # On Windows, mkdir creates parent directories automatically and the --parents argument isn't recognized.
            mkdir "$targetPath" > $null
        }
        else
        {
            mkdir "$targetPath" --parents
        }

        Copy-Item "$fromPath/$modName.dll" "$targetPath"
        Copy-Item "$fromPath/manifest.json" "$targetPath"
        if (Test-Path "$fromPath/i18n" -PathType Container) {
            Copy-Item -Recurse "$fromPath/i18n" "$targetPath"
        }
    }
}

# mark scripts executable
Write-Host "Setting file permissions..."
if ($IsWindows) {
    Write-Warning "Can't set Unix file permissions on Windows. This may cause issues for Linux/macOS players."
}
else {
    ForEach ($path in @("install on Linux.sh", "install on Linux (graphical).sh", "install on macOS.command", "internal/linux/SMAPI.Installer.Gui", "internal/linux/bundle/unix-launcher.sh", "internal/linux/bundle/StardewModdingAPI", "internal/linux/bundle/StardewModdingAPI-net6", "internal/linux/bundle/StardewModdingAPI-net10", "internal/macOS/bundle/unix-launcher.sh")) {
        if (Test-Path "$packagePath/$path" -PathType Leaf) {
            chmod 755 "$packagePath/$path"
            if ($LASTEXITCODE -ne 0) {
                throw "Failed setting executable permissions for '$packagePath/$path' (exit code $LASTEXITCODE)."
            }
        }
        else {
            Write-Host "Couldn't set permissions for '$packagePath/$path': file does not exist."
        }
    }

    if (@($folders) -contains "linux") {
        foreach ($path in @("install on Linux.sh", "install on Linux (graphical).sh", "internal/linux/SMAPI.Installer", "internal/linux/SMAPI.Installer.Gui")) {
            $mode = [int][System.IO.File]::GetUnixFileMode("$packagePath/$path")
            if ($mode -ne 493) {
                throw "The packaged Linux executable '$path' must have exact mode 755."
            }
        }
    }

    if (Test-Path "$packagePath/internal/linux/gh" -PathType Leaf) {
        foreach ($permission in @(@("555", "gh"), @("444", "gh-LICENSE.txt"))) {
            chmod $permission[0] "$packagePath/internal/linux/$($permission[1])"
            if ($LASTEXITCODE -ne 0) {
                throw "Failed setting pinned GitHub CLI package permissions for '$($permission[1])' (exit code $LASTEXITCODE)."
            }
        }
    }

    $createdumpPaths = @(Get-ChildItem "$packagePath/internal/linux/bundle/smapi-internal/dotnet" -Filter "createdump" -File -Recurse)
    if ($createdumpPaths.Count -eq 0) {
        throw "The packaged private .NET runtime is missing its createdump executable."
    }
    $createdumpPaths | ForEach-Object {
        chmod 755 $_.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "Failed setting executable permissions for '$($_.FullName)' (exit code $LASTEXITCODE)."
        }
    }
}

# convert bundle folder into final 'install.dat' files
Write-Host "Tucking SMAPI bundle into install.dat..."
foreach ($folder in $folders) {
    $path = "$packagePath/internal/$folder"

    if ($IsWindows) {
        Compress-Archive -Path "$path/bundle/*" -CompressionLevel Optimal -DestinationPath "$path/install.dat"
    }
    else {
        # Compress-Archive doesn't keep Unix permissions, so use zip directly on Linux/macOS
        pushd "$path/bundle" > /dev/null
        zip "install.dat" * --recurse-paths --quiet
        if ($LASTEXITCODE -ne 0) {
            throw "Failed creating $folder/install.dat (exit code $LASTEXITCODE)."
        }
        popd > /dev/null
        mv "$path/bundle/install.dat" "$path/install.dat"
    }

    if (!$skipBundleDeletion) {
        Remove-Item -Recurse -Force "$path/bundle"
    }
}


###########
### Create release zips
###########
Write-Host "Creating release zip..."
$archiveName = if ($linuxOnly) { "SMAPI-$version-linux-x64-installer.zip" } else { "SMAPI-$version-installer.zip" }

if ($IsWindows) {
    Compress-Archive -Path "$packagePath" -DestinationPath "bin/$archiveName" -CompressionLevel Optimal
}
else {
    # Compress-Archive doesn't keep Unix permissions, so use zip directly on Linux/macOS
    pushd bin > /dev/null
    zip -9 "$archiveName" "$packageTitle" --recurse-paths --quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Failed creating the release ZIP (exit code $LASTEXITCODE)."
    }
    popd > /dev/null
}

Write-Output ""
Write-Output "Done! Package created in ${pwd.Path}/bin."
