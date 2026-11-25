# CSI ProMax: one-line install for Windows. No .exe of ours and no administrator rights.
#
#   irm https://raw.githubusercontent.com/ahisham92/etabs-promax/main/install.ps1 | iex
#
# What it does, in this order:
#   1. Downloads the newest published CSI ProMax (a zip of DLLs) from this repository's releases.
#   2. Puts it in Documents\CSI ProMax. Files are added or replaced; nothing is deleted. A file that is in use stops
#      the install before anything is changed.
#   3. Looks for Microsoft's .NET 8 Desktop Runtime, which the CSI ProMax window needs. ETABS 23 and SAP2000 26 bring
#      it. When the PC has none, Microsoft's own install script puts it in your user folder (no administrator rights).
#   4. Adds "CSI ProMax" to the Tools menu of each ETABS, SAFE and SAP2000 found for this Windows user, when that
#      program is closed and has no other plug-in. A copy of the program's settings file is kept beside it first.
# It never starts, closes, licenses or changes a CSI program or a model. Running it again is safe.
#
# With options (all optional):
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/ahisham92/etabs-promax/main/install.ps1))) -Tag v0.4.0
#     -Tag v0.4.0     that version instead of the newest
#     -Folder <path>  another folder than Documents\CSI ProMax
#     -NoPlugins      leave the programs' Tools menus alone
#     -NoRuntime      do not look for or install the .NET 8 Desktop Runtime
#     -Preview        say what would be added to the Tools menus, and change none of them
#     -Zip <file>     a CsiProMax-<version>.zip you already have, instead of downloading
#
# This file is plain ASCII on purpose, and works in Windows PowerShell 5.1 and PowerShell 7.

function Install-CsiProMax {
    [CmdletBinding()]
    param(
        [string]$Tag,
        [string]$Folder,
        [string]$Zip,
        # For tests: a folder that stands in for %LOCALAPPDATA%\Computers and Structures. The files in it are copies,
        # so whether a program is open is not looked at.
        [string]$SettingsRoot,
        [switch]$NoPlugins,
        [switch]$NoRuntime,
        [switch]$Preview
    )

    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # the progress bar makes downloads many times slower in PowerShell 5.1
    $repository = 'ahisham92/etabs-promax'
    $menuText = 'CSI ProMax'
    $onWindows = ($env:OS -eq 'Windows_NT')
    $plugins = @(
        @{ Program = 'ETABS';   Name = 'CsiProMaxPlugin';     Dll = 'CsiProMaxPlugin.dll';     Oldest = 18 },
        @{ Program = 'SAFE';    Name = 'CsiProMaxSafePlugin'; Dll = 'CsiProMaxSafePlugin.dll'; Oldest = 20 },
        @{ Program = 'SAP2000'; Name = 'CsiProMaxSapPlugin';  Dll = 'CsiProMaxSapPlugin.dll';  Oldest = 20 }
    )

    function Say([string]$text, [string]$color) {
        if ($color) { Write-Host $text -ForegroundColor $color } else { Write-Host $text }
    }

    # The newest full release: github.com/<repository>/releases/latest redirects to its tag. GitHub's API is the
    # fallback only, because an office network shares its 60 anonymous requests an hour.
    function Get-LatestTag {
        $final = $null
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Method Head -Uri "https://github.com/$repository/releases/latest"
            $base = $response.BaseResponse
            if ($base.ResponseUri) { $final = [string]$base.ResponseUri }
            elseif ($base.RequestMessage -and $base.RequestMessage.RequestUri) { $final = [string]$base.RequestMessage.RequestUri }
        } catch { $final = $null }
        if ($final -and $final -match '/releases/tag/([^/?#]+)') { return [uri]::UnescapeDataString($Matches[1]) }
        try {
            $latest = Invoke-RestMethod -UseBasicParsing -Uri "https://api.github.com/repos/$repository/releases/latest"
            if ($latest.tag_name) { return [string]$latest.tag_name }
        } catch { }
        return $null
    }

    # Where a .NET 8 Desktop Runtime is, looked for in the same places and order as the plug-in looks (Launcher.cs).
    function Find-DesktopRuntime {
        $roots = @()
        if ($env:DOTNET_ROOT) { $roots += $env:DOTNET_ROOT }
        foreach ($programFiles in @($env:ProgramW6432, $env:ProgramFiles)) { if ($programFiles) { $roots += (Join-Path $programFiles 'dotnet') } }
        if ($env:LOCALAPPDATA) { $roots += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet') }
        if ($env:USERPROFILE) { $roots += (Join-Path $env:USERPROFILE '.dotnet') }
        foreach ($root in $roots) {
            $shared = Join-Path $root 'shared\Microsoft.WindowsDesktop.App'
            if (-not (Test-Path -LiteralPath (Join-Path $root 'dotnet.exe'))) { continue }
            if (-not (Test-Path -LiteralPath $shared)) { continue }
            $version = Get-ChildItem -LiteralPath $shared -Directory -Filter '8.*' | Sort-Object Name | Select-Object -Last 1
            if ($version) { return [pscustomobject]@{ Root = $root; Version = $version.Name } }
        }
        return $null
    }

    # Microsoft's dotnet-install script, run in a job of its own so that nothing it does can end this window.
    # It unpacks Microsoft's zips into %LOCALAPPDATA%\Microsoft\dotnet: no installer, no administrator rights.
    function Install-DesktopRuntime {
        $target = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
        $content = (Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1').Content
        if ($content -is [byte[]]) { $content = [Text.Encoding]::UTF8.GetString($content) }
        $content = ([string]$content).TrimStart([char]0xFEFF)
        $job = Start-Job -ArgumentList $content, $target -ScriptBlock {
            param($text, $dir)
            $ProgressPreference = 'SilentlyContinue'
            $install = [scriptblock]::Create($text)
            & $install -Channel 8.0 -Runtime dotnet -InstallDir $dir -NoPath
            & $install -Channel 8.0 -Runtime windowsdesktop -InstallDir $dir -NoPath
        }
        try { $null = Wait-Job -Job $job -Timeout 900; $null = Receive-Job -Job $job -ErrorAction SilentlyContinue 2>&1 }
        finally { Remove-Job -Job $job -Force -ErrorAction SilentlyContinue }
    }

    # The [PlugIn] part of a CSI settings file, with CSI ProMax in it. Pure text in, text out.
    # A program with no plug-in has "[PlugIn]" and " NumberPlugIns=0". With one plug-in it has NumberPlugIns=1 and
    # the lines PlugInName, PlugInMenuText and PlugInPath. How several plug-ins are written is not known here, so a
    # list that holds another plug-in is never changed.
    # A list that holds only CSI ProMax, from another folder or more than once (a tester added it by hand a second
    # time), is put right as a whole: one entry, for this folder.
    # Status: added, already, moved (ours was listed from another folder, or twice), others, unknown.
    function Edit-PlugInSection([string]$text, [string]$name, [string]$path) {
        $newline = "`n"
        if ($text.Contains("`r`n")) { $newline = "`r`n" }
        $lines = New-Object System.Collections.Generic.List[string]
        $lines.AddRange([string[]][regex]::Split($text, "\r?\n"))

        $start = -1
        for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s*\[PlugIn\]\s*$') { $start = $i; break } }
        if ($start -lt 0) { return @{ Status = 'unknown' } }
        $end = $lines.Count
        for ($i = $start + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s*\[') { $end = $i; break } }

        $countAt = -1; $count = -1; $indent = ' '; $entries = @(); $oursAt = -1; $oursPath = ''; $paths = 0; $ours = 0
        for ($i = $start + 1; $i -lt $end; $i++) {
            if ($lines[$i] -match '^(\s*)NumberPlugIns\s*=\s*(\d+)\s*$') {
                if ($countAt -ge 0) { return @{ Status = 'unknown' } }
                $countAt = $i; $indent = $Matches[1]; $count = [int]$Matches[2]
            }
            elseif ($lines[$i] -match '^\s*PlugIn\w*\s*=') {
                $entries += $i
                if ($lines[$i] -match '^\s*PlugInPath\w*\s*=\s*(.*?)\s*$') {
                    $value = $Matches[1].Trim('"')
                    $paths++
                    if ((($value -split '[\\/]')[-1]) -ieq (($path -split '[\\/]')[-1])) { $oursAt = $i; $oursPath = $value; $ours++ }
                }
            }
        }
        if ($countAt -lt 0) { return @{ Status = 'unknown' } }

        $status = $null
        if ($oursAt -ge 0) {
            if ($ours -eq 1 -and $oursPath -ieq $path) { return @{ Status = 'already' } }
            if ($paths -eq $ours) { $status = 'moved' } else { return @{ Status = 'others' } }
        }
        elseif ($count -eq 0 -and $entries.Count -eq 0) { $status = 'added' }
        else { return @{ Status = 'others' } }

        $kept = @()
        for ($i = $start + 1; $i -lt $end; $i++) { if ($i -ne $countAt -and ($entries -notcontains $i)) { $kept += $lines[$i] } }
        $section = @($lines[$start], ($indent + 'NumberPlugIns=1'), ($indent + 'PlugInName=' + $name),
                     ($indent + 'PlugInMenuText=' + $menuText), ($indent + 'PlugInPath=' + $path)) + $kept
        $all = @()
        if ($start -gt 0) { $all += $lines.GetRange(0, $start) }
        $all += $section
        if ($end -lt $lines.Count) { $all += $lines.GetRange($end, $lines.Count - $end) }
        return @{ Status = $status; Text = ($all -join $newline) }
    }

    # Adds the plug-in to one settings file, keeping its encoding and line ends. Returns the status.
    function Register-PlugIn([string]$ini, [string]$name, [string]$path) {
        $bytes = [IO.File]::ReadAllBytes($ini)
        $skip = 0
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { $encoding = New-Object Text.UTF8Encoding($false); $skip = 3 }
        elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) { $encoding = New-Object Text.UnicodeEncoding($false, $false); $skip = 2 }
        else {
            # No mark: every byte is kept as it is (Latin-1 maps bytes one to one). A folder name with letters
            # outside plain ASCII cannot be written safely that way, so it is left to the person.
            $encoding = [Text.Encoding]::GetEncoding(28591)
            if ($path -match '[^\x20-\x7E]') { return 'unknown' }
        }
        $edit = Edit-PlugInSection ($encoding.GetString($bytes, $skip, $bytes.Length - $skip)) $name $path
        if (-not $edit.Text) { return $edit.Status }
        if ($Preview) { return 'preview' }

        $backup = $ini + '.before-csi-promax'
        if (-not (Test-Path -LiteralPath $backup)) { Copy-Item -LiteralPath $ini -Destination $backup }
        $body = $encoding.GetBytes([string]$edit.Text)
        $out = New-Object byte[] ($skip + $body.Length)
        [Array]::Copy($bytes, 0, $out, 0, $skip)
        [Array]::Copy($body, 0, $out, $skip, $body.Length)
        [IO.File]::WriteAllBytes($ini, $out)
        return $edit.Status
    }

    $work = $null
    try {
        if (-not $Folder) {
            if (-not $onWindows) { throw 'CSI ProMax installs on Windows.' }
            $Folder = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'CSI ProMax'
        }
        try {
            $protocol = [Net.ServicePointManager]::SecurityProtocol
            if ([int]$protocol -ne 0 -and (([int]$protocol -band [int][Net.SecurityProtocolType]::Tls12) -eq 0)) {
                [Net.ServicePointManager]::SecurityProtocol = $protocol -bor [Net.SecurityProtocolType]::Tls12
            }
        } catch { }

        Say ''
        Say 'CSI ProMax install' 'Cyan'

        # 1. The package.
        $work = Join-Path ([IO.Path]::GetTempPath()) ('CsiProMax-install-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Path $work -Force | Out-Null
        if ($Zip) {
            if (-not (Test-Path -LiteralPath $Zip -PathType Leaf)) { throw "There is no file $Zip." }
            $package = (Resolve-Path -LiteralPath $Zip).Path
            $versionText = ([IO.Path]::GetFileNameWithoutExtension($package)) -replace '^CsiProMax-', ''
        }
        else {
            if (-not $Tag) {
                Say '  Looking for the newest version...'
                $Tag = Get-LatestTag
                if (-not $Tag) { throw "No published version was found at https://github.com/$repository/releases (or GitHub could not be reached)." }
            }
            $versionText = $Tag -replace '^[vV]', ''
            $package = Join-Path $work "CsiProMax-$versionText.zip"
            $address = "https://github.com/$repository/releases/download/$Tag/CsiProMax-$versionText.zip"
            Say "  Downloading CSI ProMax $versionText..."
            try { Invoke-WebRequest -UseBasicParsing -Uri $address -OutFile $package }
            catch { throw "The download failed ($address): $($_.Exception.Message)" }
        }
        $unpacked = Join-Path $work 'unpacked'
        Expand-Archive -LiteralPath $package -DestinationPath $unpacked -Force
        $dist = Join-Path $unpacked 'dist'
        if (-not (Test-Path -LiteralPath (Join-Path $dist 'CsiProMax.dll'))) { throw 'The package is not a CSI ProMax package (dist\CsiProMax.dll is missing).' }

        # 2. Into the folder: only files that differ, and nothing at all when one of them is in use.
        $distRoot = (Resolve-Path -LiteralPath $dist).Path.TrimEnd('\', '/')
        $changes = @()
        foreach ($file in (Get-ChildItem -LiteralPath $distRoot -Recurse -File)) {
            $relative = $file.FullName.Substring($distRoot.Length).TrimStart('\', '/')
            $target = Join-Path $Folder $relative
            if (Test-Path -LiteralPath $target -PathType Leaf) {
                $same = ((Get-Item -LiteralPath $target).Length -eq $file.Length) -and
                        ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)
                if ($same) { continue }
            }
            $changes += [pscustomobject]@{ Source = $file.FullName; Target = $target; Relative = $relative }
        }
        $inUse = @()
        foreach ($change in $changes) {
            if (-not (Test-Path -LiteralPath $change.Target -PathType Leaf)) { continue }
            try { $stream = [IO.File]::Open($change.Target, 'Open', 'ReadWrite', 'None'); $stream.Close() }
            catch { $inUse += $change.Relative }
        }
        if ($inUse.Count -gt 0) {
            throw ("CSI ProMax in $Folder is in use ($($inUse.Count) files, such as $($inUse[0])). Nothing was changed. " +
                   'Close the CSI ProMax window and the ETABS, SAFE or SAP2000 it was opened from, then run this line again.')
        }
        if (-not (Test-Path -LiteralPath $Folder)) { New-Item -ItemType Directory -Path $Folder -Force | Out-Null }
        $canUnblock = [bool](Get-Command Unblock-File -ErrorAction SilentlyContinue)
        foreach ($change in $changes) {
            $parent = Split-Path -Parent $change.Target
            if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
            Copy-Item -LiteralPath $change.Source -Destination $change.Target -Force
            if ($canUnblock) { try { Unblock-File -LiteralPath $change.Target } catch { } }
        }
        $notes = Join-Path $unpacked 'INSTALL.txt'
        if (Test-Path -LiteralPath $notes) { try { Copy-Item -LiteralPath $notes -Destination (Join-Path $Folder 'INSTALL.txt') -Force } catch { } }
        $Folder = (Resolve-Path -LiteralPath $Folder).Path
        if ($changes.Count -gt 0) { Say "  CSI ProMax $versionText is in $Folder ($($changes.Count) files added or replaced)." 'Green' }
        else { Say "  CSI ProMax $versionText was already in $Folder. Nothing needed replacing." 'Green' }

        # 3. The .NET 8 Desktop Runtime.
        if ($onWindows -and -not $NoRuntime) {
            $runtime = Find-DesktopRuntime
            if ($runtime) { Say "  .NET 8 Desktop Runtime $($runtime.Version) is on this PC ($($runtime.Root))." }
            else {
                Say '  This PC has no .NET 8 Desktop Runtime, which the CSI ProMax window needs.'
                Say '  Getting it from Microsoft into your user folder (no administrator rights). This can take a few minutes...'
                try { Install-DesktopRuntime } catch { Say "  Microsoft's script did not finish: $($_.Exception.Message)" 'Yellow' }
                $runtime = Find-DesktopRuntime
                if ($runtime) { Say "  .NET 8 Desktop Runtime $($runtime.Version) is now in $($runtime.Root)." 'Green' }
                else {
                    Say '  It could not be installed from here. Install ".NET Desktop Runtime 8" (Windows x64) once from' 'Yellow'
                    Say '  https://dotnet.microsoft.com/download/dotnet/8.0 and CSI ProMax will find it.' 'Yellow'
                }
            }
        }

        # 4. The Tools menu of each program.
        $byHand = $false
        if (-not $NoPlugins) {
            $root = $SettingsRoot
            if (-not $root -and $env:LOCALAPPDATA) { $root = Join-Path $env:LOCALAPPDATA 'Computers and Structures' }
            $found = @()
            if ($root -and (Test-Path -LiteralPath $root)) {
                foreach ($directory in (Get-ChildItem -LiteralPath $root -Directory)) {
                    if ($directory.Name -notmatch '^(ETABS|SAFE|SAP2000) (\d+)$') { continue }
                    $programName = $Matches[1]
                    $number = [int]$Matches[2]
                    $plugin = $plugins | Where-Object { $_.Program -ieq $programName } | Select-Object -First 1
                    if ($number -lt $plugin.Oldest -or $number -gt 99) { continue }
                    foreach ($ini in (Get-ChildItem -LiteralPath $directory.FullName -File -Filter "$($plugin.Program)*.ini")) {
                        $found += [pscustomobject]@{ Label = "$($plugin.Program) $number"; Plugin = $plugin; Number = $number; Ini = $ini.FullName }
                    }
                }
            }
            Say ''
            if ($found.Count -eq 0) {
                $byHand = $true
                Say '  No ETABS, SAFE or SAP2000 settings were found for this Windows user, so no Tools menu was changed.' 'Yellow'
                Say '  Open the program once, close it and run this line again, or add CSI ProMax by hand (below).' 'Yellow'
            }
            foreach ($item in ($found | Sort-Object { $_.Plugin.Program }, Number)) {
                $program = $item.Plugin.Program
                $status = 'unknown'
                # A process left behind with no titled window for more than ten minutes (the program hung on closing; SAP2000 keeps a hidden helper window) is not the program being open.
                if (-not $SettingsRoot -and (Get-Process -Name $program -ErrorAction SilentlyContinue | Where-Object { ($_.MainWindowHandle -ne 0 -and $_.MainWindowTitle) -or -not $_.StartTime -or ((Get-Date) - $_.StartTime).TotalMinutes -le 10 })) { $status = 'open' }
                else {
                    try { $status = Register-PlugIn $item.Ini $item.Plugin.Name (Join-Path $Folder $item.Plugin.Dll) }
                    catch { $status = 'unknown' }
                }
                $label = $item.Label.PadRight(12)
                switch ($status) {
                    'added'   { Say "  $label CSI ProMax is added to the Tools menu." 'Green' }
                    'moved'   { Say "  $label CSI ProMax in the Tools menu now points to this folder (once)." 'Green' }
                    'already' { Say "  $label CSI ProMax is already in the Tools menu." 'Green' }
                    'preview' { Say "  $label CSI ProMax would be added to the Tools menu (preview: nothing was changed)." }
                    'open'    { $byHand = $true; Say "  $label $program is open, so its plug-in list was left alone. Close it and run this line again, or add CSI ProMax by hand (below)." 'Yellow' }
                    'others'  { $byHand = $true; Say "  $label has other plug-ins, so its list was left alone. Add CSI ProMax by hand (below)." 'Yellow' }
                    default   { $byHand = $true; Say "  $label Its settings file was not understood, so it was left alone. Add CSI ProMax by hand (below)." 'Yellow' }
                }
            }
        }

        # The entry being there is not enough: the program must be able to create the plug-in when it is picked.
        # check.ps1 (installed with the rest) tries that as the program does it, for every program found.
        $checkFile = Join-Path $Folder 'check.ps1'
        if (-not $Preview -and -not $NoPlugins -and (Test-Path -LiteralPath $checkFile)) {
            try {
                $check = [scriptblock]::Create((Get-Content -Raw -LiteralPath $checkFile))
                if ($SettingsRoot) { $answer = & $check -PassThru -NoClipboard -SettingsRoot $SettingsRoot } else { $answer = & $check -PassThru -NoClipboard }
                $tried = @($answer.Programs)
                if ($tried.Count -gt 0) {
                    Say ''
                    Say 'Checked: can each program open CSI ProMax?' 'Cyan'
                    $cannot = $false
                    foreach ($one in $tried) {
                        $label = ([string]$one.Program).PadRight(12)
                        if (@($one.Reasons).Count -gt 0 -and $one.Listed) { $cannot = $true; Say "  $label NO: $(@($one.Reasons) -join ' ')" 'Red' }
                        elseif (@($one.Reasons).Count -gt 0) { Say "  $label not yet: $(@($one.Reasons) -join ' ')" 'Yellow' }
                        elseif ($one.Loads -eq $true) { Say "  $label yes: its plug-in was created the way $($one.Program) does it." 'Green' }
                        else { Say "  $label could not be tried on this PC." }
                    }
                    if ($cannot) {
                        Say '  For the full findings, run this line and send its text to the person who gave you CSI ProMax:' 'Yellow'
                        Say '    irm https://raw.githubusercontent.com/ahisham92/etabs-promax/main/check.ps1 | iex' 'Yellow'
                    }
                }
            }
            catch { Say "  (The check after installing could not run: $($_.Exception.Message))" }
        }

        Say ''
        Say 'Next' 'Cyan'
        Say '  1. Open ETABS, SAFE or SAP2000 and a model, then Tools > CSI ProMax. The window opens beside the program.'
        Say '  2. The chat runs on Claude Code on your own Claude plan. The window offers to install it or sign in.'
        Say "  3. On a PC other than the owner's, the Licence window shows this PC's code (SPM-....). Send it to the owner"
        Say '     and paste the key you get back.'
        Say ''
        if ($byHand -or $NoPlugins) { Say 'To add CSI ProMax by hand: Tools > Add/Show Plugins in the program, then' 'Cyan' }
        else { Say 'If Tools does not show CSI ProMax, add it by hand: Tools > Add/Show Plugins in the program, then' }
        foreach ($plugin in $plugins) {
            Say ("  {0,-8} plug-in name {1,-20} menu text {2}   file {3}" -f $plugin.Program, $plugin.Name, $menuText, (Join-Path $Folder $plugin.Dll))
        }
        Say ''
    }
    catch {
        Say ''
        Say "CSI ProMax was not installed: $($_.Exception.Message)" 'Red'
        Say ''
    }
    finally {
        # Only the working folder this run made in the temp folder, and nothing else.
        if ($work -and ($work -like '*CsiProMax-install-*') -and (Test-Path -LiteralPath $work)) {
            try { Remove-Item -LiteralPath $work -Recurse -Force } catch { }
        }
    }
}

Install-CsiProMax @args
