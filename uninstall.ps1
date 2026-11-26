# CSI ProMax: uninstall for this Windows user. No administrator rights.
#
#   irm https://raw.githubusercontent.com/ahisham92/etabs-promax/main/uninstall.ps1 | iex
#
# What it does:
#   1. Stops the small sign-in task that keeps CSI ProMax in the Tools menus (keep-plugins.ps1), so nothing adds it back.
#   2. Takes CSI ProMax out of the Tools menu of each ETABS, SAFE and SAP2000 that is closed (a list that also holds
#      another vendor's plug-in is left alone and named). Run it again after closing a program that was open.
#   3. Deletes the "CSI ProMax Engine" shortcut, and the older Modeler and Viewer ones (desktop and Start menu).
# Your models, review packs and the "Made in chat" folder are never deleted.
#
# With options:
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/ahisham92/etabs-promax/main/uninstall.ps1))) -Files
#     -Files       also delete the program files: Documents\CSI ProMax (except Made in chat) and the downloaded versions
#     -Everything  -Files, and this PC's CSI ProMax settings, licence and chat history (%LOCALAPPDATA%\CsiProMax)
#     -Folder <p>  the CSI ProMax folder, when it is not Documents\CSI ProMax
#
# This file is plain ASCII on purpose, and works in Windows PowerShell 5.1 and PowerShell 7.
function Uninstall-CsiProMax {
    [CmdletBinding()]
    param(
        [string]$Folder,
        [switch]$Files,
        [switch]$Everything,
        # For tests: stand-ins for %LOCALAPPDATA%\Computers and Structures and %LOCALAPPDATA%\CsiProMax, no process check.
        [string]$SettingsRoot,
        [string]$StateRoot,
        [string]$KeeperFile,
        [switch]$NoProcessCheck,
        [switch]$NoShortcuts
    )
    $ErrorActionPreference = 'Stop'
    if ($Everything) { $Files = $true }
    $local = $env:LOCALAPPDATA
    if (-not $local) { try { $local = [Environment]::GetFolderPath('LocalApplicationData') } catch { } }
    if (-not $StateRoot -and $local) { $StateRoot = Join-Path $local 'CsiProMax' }
    if (-not $Folder) { $Folder = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'CSI ProMax' }

    Write-Host ''
    Write-Host 'CSI ProMax uninstall' -ForegroundColor Cyan
    try {
        # 1 and 2: keep-plugins.ps1 -Remove. The copy on this PC, else the one in this repository.
        $text = $null
        foreach ($candidate in @($KeeperFile, (Join-Path $StateRoot 'keep-plugins.ps1'), (Join-Path $Folder 'keep-plugins.ps1'))) {
            if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { $text = Get-Content -Raw -LiteralPath $candidate; break }
        }
        if (-not $text) { $text = [string](Invoke-WebRequest -UseBasicParsing -Uri 'https://raw.githubusercontent.com/ahisham92/etabs-promax/main/keep-plugins.ps1').Content }
        $keeper = [scriptblock]::Create(([string]$text).TrimStart([char]0xFEFF))
        $options = @{ Remove = $true; Folder = $Folder }
        if ($SettingsRoot) { $options.SettingsRoot = $SettingsRoot }
        if ($StateRoot) { $options.StateRoot = $StateRoot }
        if ($NoProcessCheck) { $options.NoProcessCheck = $true }
        & $keeper @options

        # 3. The shortcuts.
        if (-not $NoShortcuts) {
            $places = @()
            try { $places += [Environment]::GetFolderPath('Desktop') } catch { }
            try { $places += (Join-Path ([Environment]::GetFolderPath('Programs')) 'CSI ProMax') } catch { }
            foreach ($place in $places) {
                if (-not $place) { continue }
                foreach ($name in 'CSI ProMax Engine.lnk', 'CSI ProMax Modeler.lnk', 'CSI ProMax Viewer.lnk') {
                    $link = Join-Path $place $name
                    if (Test-Path -LiteralPath $link) { try { Remove-Item -LiteralPath $link -Force } catch { } }
                }
            }
            $menu = $places | Where-Object { $_ -like '*CSI ProMax' } | Select-Object -First 1
            if ($menu -and (Test-Path -LiteralPath $menu) -and -not (Get-ChildItem -LiteralPath $menu -Force)) { try { Remove-Item -LiteralPath $menu -Force } catch { } }
            if ($StateRoot) { $mark = Join-Path $StateRoot 'shortcuts.txt'; if (Test-Path -LiteralPath $mark) { try { Remove-Item -LiteralPath $mark -Force } catch { } } }
            Write-Host '  The Modeler and Viewer shortcuts are deleted.'
        }

        # 4. The files, only when asked.
        if ($Files) {
            $left = @()
            if (Test-Path -LiteralPath $Folder) {
                foreach ($item in @(Get-ChildItem -LiteralPath $Folder -Force)) {
                    if ($item.PSIsContainer -and $item.Name -ieq 'Made in chat') { continue }
                    try { Remove-Item -LiteralPath $item.FullName -Recurse -Force } catch { $left += $item.Name }
                }
                if (-not (Get-ChildItem -LiteralPath $Folder -Force)) { try { Remove-Item -LiteralPath $Folder -Force } catch { } }
            }
            if ($Everything) { $targets = @($StateRoot) } else { $targets = @((Join-Path $StateRoot 'versions'), (Join-Path $StateRoot 'updates')) }
            foreach ($target in $targets) { if ($target -and (Test-Path -LiteralPath $target)) { try { Remove-Item -LiteralPath $target -Recurse -Force } catch { $left += $target } } }
            if ($left.Count -gt 0) { Write-Host "  Some files are in use and were left ($($left -join ', ')). Close CSI ProMax and the CSI programs, then run this again." -ForegroundColor Yellow }
            else { Write-Host '  The program files are deleted.' }
        }
        else { Write-Host "  The program files are kept in $Folder. To delete them too, run this line with -Files (see the top of uninstall.ps1)." }
        Write-Host '  To install CSI ProMax again: irm https://raw.githubusercontent.com/ahisham92/etabs-promax/main/install.ps1 | iex'
        Write-Host ''
    }
    catch {
        Write-Host ''
        Write-Host "CSI ProMax was not uninstalled: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host ''
    }
}

Uninstall-CsiProMax @args
