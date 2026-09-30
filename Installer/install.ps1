<#
    Lumen installer.

    Finds Nivalis Nights, installs BepInEx if it is not already there, and drops Lumen
    into the plugins folder.

    Nothing here touches anything outside the game folder, and it never removes a BepInEx
    install it did not create - other mods may be relying on it.

    Run it by double-clicking "Install Lumen.bat" next to this file.
#>

[CmdletBinding()]
param(
    # Skip auto-detection and use this folder. Also how the installer gets tested
    # without touching a real install.
    [string] $GamePath,

    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is far slower with it on

# Pinned rather than "latest". A bleeding-edge build can break on any given day, and this
# is the build Lumen is tested against.
$BepInExUrl = 'https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip'
$ReleaseApi = 'https://api.github.com/repos/jfraygit/Neonworks/releases/latest'

function Write-Step { param($Text) Write-Host "  $Text" -ForegroundColor Cyan }
function Write-Ok   { param($Text) Write-Host "  $Text" -ForegroundColor Green }
function Write-Warn { param($Text) Write-Host "  $Text" -ForegroundColor Yellow }
function Write-Bad  { param($Text) Write-Host "  $Text" -ForegroundColor Red }

function Find-Game {
    # An explicit path wins, so a non-Steam or relocated copy is never a dead end.
    if ($GamePath) {
        if (Test-Path (Join-Path $GamePath 'Nivalis Nights.exe')) { return $GamePath }
        throw "No 'Nivalis Nights.exe' in: $GamePath"
    }

    $steam = $null
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
        try {
            $value = (Get-ItemProperty -Path $key -ErrorAction Stop)
            if ($value.SteamPath)     { $steam = $value.SteamPath;     break }
            if ($value.InstallPath)   { $steam = $value.InstallPath;   break }
        } catch { }
    }

    $roots = @()
    if ($steam) {
        $roots += $steam

        # Steam spreads games across library folders on other drives; each one is listed
        # in libraryfolders.vdf as a "path" entry.
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($line in Get-Content $vdf) {
                if ($line -match '"path"\s+"(.+?)"') {
                    $roots += $Matches[1].Replace('\\', '\')
                }
            }
        }
    }

    foreach ($root in $roots) {
        $candidate = Join-Path $root 'steamapps\common\Nivalis Nights'
        if (Test-Path (Join-Path $candidate 'Nivalis Nights.exe')) { return $candidate }
    }

    return $null
}

function Assert-GameClosed {
    $running = Get-Process -Name 'Nivalis Nights' -ErrorAction SilentlyContinue
    if (-not $running) { return }

    Write-Bad 'Nivalis Nights is running. Close it and run this again.'
    throw 'Game is running'
}

function Install-BepInEx {
    param($Game)

    if (Test-Path (Join-Path $Game 'BepInEx\core')) {
        Write-Ok 'BepInEx is already installed, leaving it alone.'
        return
    }

    Write-Step 'Downloading BepInEx (about 34 MB)...'

    $zip = Join-Path $env:TEMP 'lumen-bepinex.zip'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $BepInExUrl -OutFile $zip -UseBasicParsing

    Write-Step 'Installing BepInEx...'
    Expand-Archive -Path $zip -DestinationPath $Game -Force
    Remove-Item $zip -Force -ErrorAction SilentlyContinue

    Write-Ok 'BepInEx installed.'
}

function Get-Lumen {
    # A DLL sitting next to the installer wins, so an offline or manual copy works.
    $local = Join-Path $PSScriptRoot 'Lumen.dll'
    if (Test-Path $local) {
        Write-Ok 'Using the Lumen.dll next to this installer.'
        return $local
    }

    Write-Step 'Downloading the latest Lumen...'

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $release = Invoke-RestMethod -Uri $ReleaseApi -UseBasicParsing -Headers @{
        'User-Agent' = 'Lumen-Installer'
    }

    $asset = $release.assets | Where-Object { $_.name -eq 'Lumen.dll' } | Select-Object -First 1
    if (-not $asset) { throw 'That release has no Lumen.dll attached.' }

    $target = Join-Path $env:TEMP 'Lumen.dll'
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $target -UseBasicParsing

    Write-Ok "Downloaded Lumen $($release.tag_name)."
    return $target
}

function Do-Install {
    param($Game)

    Assert-GameClosed
    Install-BepInEx -Game $Game

    $source = Get-Lumen
    $folder = Join-Path $Game 'BepInEx\plugins\Lumen'

    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    Copy-Item -Path $source -Destination (Join-Path $folder 'Lumen.dll') -Force

    Write-Ok 'Lumen installed.'
    Write-Host ''
    Write-Host '  Done. Start the game and press F10 for settings.' -ForegroundColor White
}

function Do-Uninstall {
    param($Game)

    Assert-GameClosed

    $folder = Join-Path $Game 'BepInEx\plugins\Lumen'
    if (Test-Path $folder) {
        Remove-Item $folder -Recurse -Force
        Write-Ok 'Lumen removed.'
    } else {
        Write-Warn 'Lumen was not installed.'
    }

    # BepInEx stays. Other mods may depend on it, and this installer has no way of knowing
    # whether it put it there in the first place.
    Write-Host ''
    Write-Host '  BepInEx was left in place in case other mods use it.' -ForegroundColor Gray
    Write-Host '  To remove it too, delete the BepInEx folder and winhttp.dll' -ForegroundColor Gray
    Write-Host '  from your Nivalis Nights folder.' -ForegroundColor Gray
}

# -------------------------------------------------------------------------------------

Write-Host ''
Write-Host '  LUMEN' -ForegroundColor Magenta
Write-Host '  Performance mod for Nivalis Nights' -ForegroundColor DarkGray
Write-Host ''

try {
    $game = Find-Game

    if (-not $game) {
        Write-Warn 'Could not find Nivalis Nights automatically.'
        Write-Host ''
        $typed = Read-Host '  Paste your Nivalis Nights folder path and press Enter'
        $typed = $typed.Trim('"').Trim()

        if (-not (Test-Path (Join-Path $typed 'Nivalis Nights.exe'))) {
            throw "No 'Nivalis Nights.exe' in: $typed"
        }
        $game = $typed
    }

    Write-Ok "Found the game: $game"
    Write-Host ''

    if ($Uninstall) { Do-Uninstall -Game $game } else { Do-Install -Game $game }
}
catch {
    Write-Host ''
    Write-Bad "Failed: $($_.Exception.Message)"
    Write-Host ''
    Write-Host '  If this keeps happening, install manually:' -ForegroundColor Gray
    Write-Host '  https://github.com/jfraygit/Neonworks' -ForegroundColor Gray
    exit 1
}
