# CSI ProMax: keeps "CSI ProMax" in the Tools menu of every ETABS, SAFE and SAP2000 of this Windows user.
#
# Why: a CSI program empties its own plug-in list now and then. ETABS 23 and SAP2000 26 started through their API (a
# test, a script, another tool) write their settings back with "NumberPlugIns=0" when they close, and a program closed
# by force or reset by its own update can do the same. CSI ProMax could put itself back only once it was opened, and
# with the Tools menu entry gone a tester had no way left to open it (2026-11-26). So this small script runs on its
# own, hidden, from the moment the Windows user signs in: once a minute it looks at each program's settings file and,
# when CSI ProMax is missing and that program is closed, adds it back under the install line's rule (no other plug-in
# in the list, a copy of the settings file kept beside it first). It never starts, closes or licenses a program and
# never touches a model. What it does goes in %LOCALAPPDATA%\CsiProMax\keep-plugins.log.
#
#   keep-plugins.ps1                 one look now, and say what was found
#   keep-plugins.ps1 -Watch          keep looking, once a minute (what the sign-in task runs; one copy at a time)
#   keep-plugins.ps1 -Register       start doing that at every sign-in, from now on (install.ps1 and CSI ProMax do this)
#   keep-plugins.ps1 -Unregister     stop it
#   keep-plugins.ps1 -Remove         stop it and take CSI ProMax out of every Tools menu (uninstall.ps1 does this)
#   -Folder <path>                   the CSI ProMax folder whose plug-in files the menus name (default Documents\CSI ProMax)
# To keep the menus as they are without uninstalling: put a file named keep-plugins-off.txt in %LOCALAPPDATA%\CsiProMax.
#
# This file is plain ASCII on purpose, and works in Windows PowerShell 5.1 and PowerShell 7.
param(
    [string]$Folder,
    [switch]$Watch,
    [switch]$Register,
    [switch]$Unregister,
    [switch]$Remove,
    [switch]$Quiet,
    [int]$Every = 60,
    # For tests: folders that stand in for %LOCALAPPDATA%\Computers and Structures and %LOCALAPPDATA%\CsiProMax.
    [string]$SettingsRoot,
    [string]$StateRoot,
    [switch]$NoProcessCheck
)

$ErrorActionPreference = 'Stop'
$menuText = 'CSI ProMax'
$taskName = 'CSI ProMax keeps its Tools menu'
$plugins = @(
    [pscustomobject]@{ Program = 'ETABS'; Name = 'CsiProMaxPlugin'; Dll = 'CsiProMaxPlugin.dll'; Oldest = 18 },
    [pscustomobject]@{ Program = 'SAFE'; Name = 'CsiProMaxSafePlugin'; Dll = 'CsiProMaxSafePlugin.dll'; Oldest = 20 },
    [pscustomobject]@{ Program = 'SAP2000'; Name = 'CsiProMaxSapPlugin'; Dll = 'CsiProMaxSapPlugin.dll'; Oldest = 20 }
)
$onWindows = ($PSVersionTable.PSEdition -eq 'Desktop') -or $IsWindows
$local = $env:LOCALAPPDATA
if (-not $local) { try { $local = [Environment]::GetFolderPath('LocalApplicationData') } catch { } }
if (-not $SettingsRoot -and $local) { $SettingsRoot = Join-Path $local 'Computers and Structures' }
if (-not $StateRoot -and $local) { $StateRoot = Join-Path $local 'CsiProMax' }
if (-not $Folder) { try { $Folder = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'CSI ProMax' } catch { } }
if ($Folder) { $Folder = $Folder.TrimEnd('\', '/') }
$logFile = if ($StateRoot) { Join-Path $StateRoot 'keep-plugins.log' } else { $null }

function Say([string]$text) { if (-not $Quiet -and -not $Watch) { Write-Host $text } }

function Note([string]$text) {
    if (-not $logFile) { return }
    try {
        if (-not (Test-Path -LiteralPath $StateRoot)) { New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null }
        $line = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + '  ' + $text
        $kept = @()
        if (Test-Path -LiteralPath $logFile) { $kept = @(Get-Content -LiteralPath $logFile | Select-Object -Last 499) }
        [IO.File]::WriteAllLines($logFile, [string[]]($kept + $line))
    } catch { }
}

# The settings file as text, with how to write it back the same way (encoding mark, and bytes kept one to one).
function Read-Settings([string]$ini) {
    $bytes = [IO.File]::ReadAllBytes($ini)
    $skip = 0; $plain = $false
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { $encoding = New-Object Text.UTF8Encoding($false); $skip = 3 }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) { $encoding = New-Object Text.UnicodeEncoding($false, $false); $skip = 2 }
    else { $encoding = [Text.Encoding]::GetEncoding(28591); $plain = $true }
    @{ Bytes = $bytes; Skip = $skip; Plain = $plain; Encoding = $encoding; Text = $encoding.GetString($bytes, $skip, $bytes.Length - $skip) }
}

function Write-Settings([string]$ini, $read, [string]$text) {
    $backup = $ini + '.before-csi-promax'
    if (-not (Test-Path -LiteralPath $backup)) { Copy-Item -LiteralPath $ini -Destination $backup }
    $body = $read.Encoding.GetBytes($text)
    $out = New-Object byte[] ($read.Skip + $body.Length)
    [Array]::Copy($read.Bytes, 0, $out, 0, $read.Skip)
    [Array]::Copy($body, 0, $out, $read.Skip, $body.Length)
    [IO.File]::WriteAllBytes($ini, $out)
}

# The [PlugIn] part of a settings file: where it starts and ends, the count line, and each PlugIn... line.
function Split-PlugIns([string]$text) {
    $newline = "`n"; if ($text.Contains("`r`n")) { $newline = "`r`n" }
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($text -split "`r?`n")) { $lines.Add($line) }
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s*\[PlugIn\]\s*$') { $start = $i; break } }
    if ($start -lt 0) { return $null }
    $end = $lines.Count
    for ($i = $start + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s*\[') { $end = $i; break } }
    $countAt = -1; $count = -1; $indent = ' '; $entries = @(); $paths = @()
    for ($i = $start + 1; $i -lt $end; $i++) {
        if ($lines[$i] -match '^(\s*)NumberPlugIns\s*=\s*(\d+)\s*$') {
            if ($countAt -ge 0) { return $null }
            $countAt = $i; $indent = $Matches[1]; $count = [int]$Matches[2]
        }
        elseif ($lines[$i] -match '^\s*PlugIn\w*\s*=') {
            $entries += $i
            if ($lines[$i] -match '^\s*PlugInPath\w*\s*=\s*(.*?)\s*$') { $paths += $Matches[1].Trim('"') }
        }
    }
    if ($countAt -lt 0) { return $null }
    @{ Lines = $lines; Newline = $newline; Start = $start; End = $end; CountAt = $countAt; Count = $count; Indent = $indent; Entries = $entries; Paths = $paths }
}

function Join-PlugIns($split, [string[]]$section) {
    $lines = $split.Lines
    $kept = @()
    for ($i = $split.Start + 1; $i -lt $split.End; $i++) { if ($i -ne $split.CountAt -and ($split.Entries -notcontains $i)) { $kept += $lines[$i] } }
    $all = @()
    if ($split.Start -gt 0) { $all += $lines.GetRange(0, $split.Start) }
    $all += @($lines[$split.Start]) + $section + $kept
    if ($split.End -lt $lines.Count) { $all += $lines.GetRange($split.End, $lines.Count - $split.End) }
    $all -join $split.Newline
}

function Leaf([string]$path) { ($path -split '[\\/]')[-1] }

# CSI ProMax in the list, naming $path once. The same rule as install.ps1 and the Tools menu repair in CSI ProMax:
# a list with another vendor's plug-in in it is never changed, since how several are written is not known.
function Edit-Add([string]$text, [string]$name, [string]$path) {
    $split = Split-PlugIns $text
    if (-not $split) { return @{ Status = 'unknown' } }
    $ours = @($split.Paths | Where-Object { (Leaf $_) -ieq (Leaf $path) })
    if ($ours.Count -gt 0) {
        if ($ours.Count -eq 1 -and $ours[0] -ieq $path) { return @{ Status = 'already' } }
        if ($split.Paths.Count -ne $ours.Count) { return @{ Status = 'others' } }
        $status = 'moved'
    }
    elseif ($split.Count -eq 0 -and $split.Entries.Count -eq 0) { $status = 'added' }
    else { return @{ Status = 'others' } }
    $i = $split.Indent
    @{ Status = $status; Text = (Join-PlugIns $split @(($i + 'NumberPlugIns=1'), ($i + 'PlugInName=' + $name), ($i + 'PlugInMenuText=' + $menuText), ($i + 'PlugInPath=' + $path))) }
}

# CSI ProMax out of the list. Only a list that holds nothing but CSI ProMax is changed (back to NumberPlugIns=0).
function Edit-Remove([string]$text) {
    $split = Split-PlugIns $text
    if (-not $split) { return @{ Status = 'unknown' } }
    $names = @($plugins | ForEach-Object { $_.Dll })
    $ours = @($split.Paths | Where-Object { $names -contains (Leaf $_) })
    if ($ours.Count -eq 0) { return @{ Status = 'absent' } }
    if ($ours.Count -ne $split.Paths.Count) { return @{ Status = 'others' } }
    @{ Status = 'removed'; Text = (Join-PlugIns $split @(($split.Indent + 'NumberPlugIns=0'))) }
}

# True when this version of the program is open, or may be: an open program writes its settings file when it closes,
# so that file is never changed under it. A process with no titled window that started more than ten minutes ago was
# left behind by a program that hung on closing, and does not count.
function Test-Open([string]$program, [int]$number) {
    if ($NoProcessCheck) { return $false }
    foreach ($process in @(Get-Process -Name $program -ErrorAction SilentlyContinue)) {
        $window = $false; $old = $false
        try { $window = ($process.MainWindowHandle -ne 0) -and [bool]$process.MainWindowTitle } catch { $window = $true }
        try { $old = ((Get-Date) - $process.StartTime).TotalMinutes -gt 10 } catch { $old = $false }
        if (-not $window -and $old) { continue }
        $file = $null
        try { $file = $process.Path } catch { }
        if (-not $file) { return $true }
        if ($file -match ('[\\/]' + [regex]::Escape($program) + ' ?(\d+)[\\/]')) { if ([int]$Matches[1] -eq $number) { return $true } }
        else { return $true }
    }
    $false
}

# Every settings file of every ETABS, SAFE and SAP2000 of this user, with its plug-in and label ("ETABS 23").
function Find-Settings {
    $found = @()
    if (-not $SettingsRoot -or -not (Test-Path -LiteralPath $SettingsRoot)) { return $found }
    foreach ($directory in @(Get-ChildItem -LiteralPath $SettingsRoot -Directory -ErrorAction SilentlyContinue)) {
        if ($directory.Name -notmatch '^(ETABS|SAFE|SAP2000) (\d+)$') { continue }
        $programName = $Matches[1]; $number = [int]$Matches[2]
        $plugin = $plugins | Where-Object { $_.Program -ieq $programName } | Select-Object -First 1
        if ($number -lt $plugin.Oldest -or $number -gt 99) { continue }
        foreach ($ini in @(Get-ChildItem -LiteralPath $directory.FullName -File -Filter "$($plugin.Program)*.ini" -ErrorAction SilentlyContinue)) {
            $found += [pscustomobject]@{ Label = "$($plugin.Program) $number"; Plugin = $plugin; Number = $number; Ini = $ini.FullName }
        }
    }
    @($found | Sort-Object { $_.Plugin.Program }, Number)
}

function Test-Installed { if (-not $Folder) { return $false }; foreach ($p in $plugins) { if (-not (Test-Path -LiteralPath (Join-Path $Folder $p.Dll))) { return $false } }; $true }

# Remembered per settings file between looks, so the log says once that a program is open and the list waits for it.
$script:waiting = @{}

# One look at every program: CSI ProMax added back where it is missing and the program is closed.
function Invoke-Look {
    if ($StateRoot -and (Test-Path -LiteralPath (Join-Path $StateRoot 'keep-plugins-off.txt'))) { Say 'keep-plugins-off.txt is there, so the Tools menus are left as they are.'; return @() }
    if (-not (Test-Installed)) { Say "CSI ProMax's plug-in files are not in $Folder, so no Tools menu is changed."; return @() }
    $results = @()
    foreach ($item in (Find-Settings)) {
        $path = Join-Path $Folder $item.Plugin.Dll
        $status = 'unknown'
        try {
            $read = Read-Settings $item.Ini
            if ($read.Plain -and $path -match '[^\x20-\x7E]') { $status = 'unknown' }
            else {
                $edit = Edit-Add $read.Text $item.Plugin.Name $path
                $status = $edit.Status
                if ($edit.Text) {
                    if (Test-Open $item.Plugin.Program $item.Number) { $status = 'open' }
                    else {
                        $emptied = (Get-Item -LiteralPath $item.Ini).LastWriteTime.ToString('yyyy-MM-dd HH:mm')
                        Write-Settings $item.Ini $read $edit.Text
                        if ($status -eq 'added') { Note "$($item.Label): CSI ProMax was missing from the Tools menu (the program last wrote its settings at $emptied) and is added back. $($item.Ini)" }
                        else { Note "$($item.Label): the Tools menu named another CSI ProMax file, or named it twice, and names $path now, once." }
                    }
                }
            }
        } catch { $status = 'unknown' }
        if ($status -eq 'open' -and -not $script:waiting[$item.Ini]) { Note "$($item.Label): CSI ProMax is missing from the Tools menu; the program is open, so it is added once the program is closed." }
        $script:waiting[$item.Ini] = ($status -eq 'open')
        $results += [pscustomobject]@{ Label = $item.Label; Ini = $item.Ini; Status = $status }
    }
    $results
}

function Get-Watchers {
    if (-not $onWindows) { return @() }
    try {
        @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'" -ErrorAction Stop |
          Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -and $_.CommandLine -like '*keep-plugins.ps1*' -and $_.CommandLine -like '*-Watch*' })
    } catch { @() }
}

function Get-StartupLink {
    try { $startup = [Environment]::GetFolderPath('Startup') } catch { return $null }
    if (-not $startup) { return $null }
    Join-Path $startup 'CSI ProMax Tools menu.lnk'
}

function Invoke-Unregister {
    if (-not $onWindows) { return }
    foreach ($watcher in (Get-Watchers)) { try { Stop-Process -Id $watcher.ProcessId -Force -ErrorAction Stop } catch { } }
    if (Get-Command Unregister-ScheduledTask -ErrorAction SilentlyContinue) {
        try { if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop } } catch { }
    }
    $link = Get-StartupLink
    if ($link -and (Test-Path -LiteralPath $link)) { try { Remove-Item -LiteralPath $link -Force } catch { } }
    if ($StateRoot) { $mark = Join-Path $StateRoot 'keep-plugins.txt'; if (Test-Path -LiteralPath $mark) { try { Remove-Item -LiteralPath $mark -Force } catch { } } }
}

function Invoke-Register {
    if (-not $onWindows) { Say 'The sign-in task is for Windows.'; return }
    if (-not (Test-Path -LiteralPath $StateRoot)) { New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null }
    # The task runs a copy kept in %LOCALAPPDATA%\CsiProMax, which every version of CSI ProMax keeps up to date.
    $target = Join-Path $StateRoot 'keep-plugins.ps1'
    if ($PSCommandPath -and ($PSCommandPath -ine $target)) { Copy-Item -LiteralPath $PSCommandPath -Destination $target -Force }
    if (-not (Test-Path -LiteralPath $target)) { throw "There is no $target to run." }
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $powershell)) { $powershell = 'powershell.exe' }
    $arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$target`" -Watch -Folder `"$Folder`""
    foreach ($watcher in (Get-Watchers)) { try { Stop-Process -Id $watcher.ProcessId -Force -ErrorAction Stop } catch { } }
    $how = $null
    if (Get-Command Register-ScheduledTask -ErrorAction SilentlyContinue) {
        try {
            $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
            $action = New-ScheduledTaskAction -Execute $powershell -Argument $arguments
            # At sign-in, and every hour after: a look that is already running is left to run (IgnoreNew), so the
            # hourly start only matters when the one from sign-in was stopped.
            $triggers = @(New-ScheduledTaskTrigger -AtLogOn -User $user)
            try { $triggers += New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(1)) -RepetitionInterval (New-TimeSpan -Hours 1) } catch { }
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -Hidden
            $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
            Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $triggers -Settings $settings -Principal $principal `
                -Description 'Adds CSI ProMax back to the Tools menu of ETABS, SAFE and SAP2000 when a program has emptied its plug-in list. Never starts or changes a program or a model.' -Force -ErrorAction Stop | Out-Null
            Start-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            $how = 'task'
            $link = Get-StartupLink
            if ($link -and (Test-Path -LiteralPath $link)) { try { Remove-Item -LiteralPath $link -Force } catch { } }
        } catch { $how = $null }
    }
    if (-not $how) {
        # A PC that does not let its users make scheduled tasks: a shortcut in the user's Startup folder instead.
        $link = Get-StartupLink
        if (-not $link) { throw 'Neither a scheduled task nor a Startup shortcut could be made.' }
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($link)
        $shortcut.TargetPath = $powershell
        $shortcut.Arguments = $arguments
        $shortcut.WindowStyle = 7
        $shortcut.Description = 'CSI ProMax: keeps CSI ProMax in the Tools menu of ETABS, SAFE and SAP2000'
        $shortcut.Save()
        Start-Process -FilePath $powershell -ArgumentList $arguments -WindowStyle Hidden
        $how = 'startup'
    }
    [IO.File]::WriteAllText((Join-Path $StateRoot 'keep-plugins.txt'), "$how|$Folder")
    Note "Keeping the Tools menus from now on ($how), for $Folder."
    $how
}

# The review packs kept up to date after every analysis and design (CSI ProMax's watcher, dotnet CsiProMax.dll --packs):
# started from here at sign-in when CSI ProMax turned it on (packs.txt in %LOCALAPPDATA%\CsiProMax: on|dotnet|folder),
# none runs yet (its mutex Local\CsiProMax.Packs), and packs-off.txt is not there. It only reads the programs.
function Invoke-Packs {
    if (-not $onWindows -or -not $StateRoot) { return }
    $on = Join-Path $StateRoot 'packs.txt'
    if (-not (Test-Path -LiteralPath $on) -or (Test-Path -LiteralPath (Join-Path $StateRoot 'packs-off.txt'))) { return }
    $parts = @(([IO.File]::ReadAllText($on)).Trim() -split '\|')
    if ($parts.Count -lt 3 -or $parts[0] -ne 'on') { return }
    $dotnet = $parts[1]; $folder = $parts[2]; $dll = Join-Path $folder 'CsiProMax.dll'
    if (-not (Test-Path -LiteralPath $dotnet) -or -not (Test-Path -LiteralPath $dll)) { return }
    $existing = $null
    try { if ([Threading.Mutex]::TryOpenExisting('Local\CsiProMax.Packs', [ref]$existing)) { $existing.Dispose(); return } } catch { return }
    try {
        Start-Process -FilePath $dotnet -ArgumentList ('"' + $dll + '" --packs') -WorkingDirectory $folder -WindowStyle Hidden
        Note 'The review pack watcher is started (packs beside each model after every run and design).'
    } catch { Note "The review pack watcher could not start: $($_.Exception.Message)" }
}

function Invoke-Watch {
    $mutex = $null
    if ($onWindows) {
        $mutex = New-Object Threading.Mutex($false, 'Local\CsiProMax.KeepPlugins')
        try { if (-not $mutex.WaitOne(0)) { return } } catch [Threading.AbandonedMutexException] { }
    }
    $me = $PSCommandPath
    $stamp = if ($me -and (Test-Path -LiteralPath $me)) { (Get-Item -LiteralPath $me).LastWriteTimeUtc } else { $null }
    try {
        while ($true) {
            try { Invoke-Packs } catch { }
            try { Invoke-Look | Out-Null } catch { }
            Start-Sleep -Seconds ([Math]::Max(5, $Every))
            # A newer CSI ProMax replaced this script: the new one takes over.
            if ($stamp -and (Test-Path -LiteralPath $me) -and (Get-Item -LiteralPath $me).LastWriteTimeUtc -ne $stamp) {
                if ($mutex) { $mutex.ReleaseMutex(); $mutex.Dispose(); $mutex = $null }
                $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
                Start-Process -FilePath $powershell -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$me`" -Watch -Folder `"$Folder`"" -WindowStyle Hidden
                return
            }
        }
    }
    finally { if ($mutex) { try { $mutex.ReleaseMutex() } catch { }; $mutex.Dispose() } }
}

function Invoke-Remove {
    Invoke-Unregister
    $results = @()
    foreach ($item in (Find-Settings)) {
        $status = 'unknown'
        try {
            $read = Read-Settings $item.Ini
            $edit = Edit-Remove $read.Text
            $status = $edit.Status
            if ($edit.Text) {
                if (Test-Open $item.Plugin.Program $item.Number) { $status = 'open' }
                else { Write-Settings $item.Ini $read $edit.Text; Note "$($item.Label): CSI ProMax is taken out of the Tools menu (uninstall)." }
            }
        } catch { $status = 'unknown' }
        $results += [pscustomobject]@{ Label = $item.Label; Ini = $item.Ini; Status = $status }
    }
    $results
}

function Show($results, [bool]$removing) {
    foreach ($r in $results) {
        $label = $r.Label.PadRight(12)
        switch ($r.Status) {
            'added'   { Say "  $label CSI ProMax was missing from the Tools menu and is added back." }
            'moved'   { Say "  $label CSI ProMax in the Tools menu now names $Folder (once)." }
            'already' { Say "  $label CSI ProMax is in the Tools menu." }
            'removed' { Say "  $label CSI ProMax is taken out of the Tools menu." }
            'absent'  { Say "  $label CSI ProMax is not in the Tools menu." }
            'open'    { if ($removing) { Say "  $label is open: close it and run this again to take CSI ProMax out of its Tools menu." } else { Say "  $label is open: CSI ProMax is added to its Tools menu once it is closed." } }
            'others'  { Say "  $label has other plug-ins, so its list is left alone: Tools > Add/Show Plugins to change it by hand." }
            default   { Say "  $label Its settings file was not understood, so it is left alone." }
        }
    }
}

if ($Remove) { $r = @(Invoke-Remove); Show $r $true; return }
if ($Unregister) { Invoke-Unregister; Say 'CSI ProMax no longer keeps the Tools menus.'; return }
if ($Register) {
    $how = Invoke-Register
    if ($how -eq 'task') { Say "  CSI ProMax keeps itself in the Tools menus from now on (Windows task '$taskName')." }
    elseif ($how) { Say '  CSI ProMax keeps itself in the Tools menus from now on (a shortcut in your Startup folder).' }
    return
}
if ($Watch) { Invoke-Watch; return }
$r = @(Invoke-Look); Show $r $false
