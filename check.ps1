# CSI ProMax: why does Tools > CSI ProMax not open? One line that answers it. It changes nothing on the PC.
#
#   irm https://raw.githubusercontent.com/ahisham92/etabs-promax/main/check.ps1 | iex
#
# What it reads, and it only reads:
#   1. The plug-in list of each ETABS, SAFE and SAP2000 found for this Windows user.
#   2. The CSI ProMax file each list names: is it there, is it on this PC (not online-only in OneDrive), did Windows
#      mark it as downloaded (blocked), is it on a network drive.
#   3. The program's own API file (ETABSv1.dll, SAFEv1.dll, SAP2000v1.dll) and which .NET the program runs on.
#   4. Whether that kind of .NET can create the plug-in. It is tried for real, in a PowerShell of its own, the way the
#      program does it: load the file, find its class cPlugin by name, make one, find its Main and Info, and see that
#      Main takes the program's own model and callback. Info is called (it only gives a line of text); Main is not,
#      so CSI ProMax is not started. A CSI program before ETABS 22, SAFE 22 and SAP2000 26 runs on the .NET Framework,
#      which Windows PowerShell runs on too.
# The answer is printed and copied, ready to paste into a message. No CSI program is started, closed or changed, no
# model is touched, and no file is written.
#
# With options (all optional):
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/ahisham92/etabs-promax/main/check.ps1))) -NoClipboard
#     -NoClipboard    print only
#     -NoLoad         leave out step 4
#     -PassThru       give the findings back as objects (the installer and the tests use this)
#
# This file is plain ASCII on purpose, and works in Windows PowerShell 5.1 and PowerShell 7.

function Test-CsiProMax {
    [CmdletBinding()]
    param(
        # For tests: a folder that stands in for %LOCALAPPDATA%\Computers and Structures.
        [string]$SettingsRoot,
        # For tests: folders that stand in for Program Files\Computers and Structures.
        [string[]]$ProgramsRoot,
        # For tests: the PowerShell programs that try to create the plug-in, as "label=path".
        [string[]]$LoadWith,
        [switch]$NoClipboard,
        [switch]$NoLoad,
        [switch]$PassThru
    )

    $ErrorActionPreference = 'Stop'
    $onWindows = ($env:OS -eq 'Windows_NT')
    $installLine = '& ([scriptblock]::Create((irm https://raw.githubusercontent.com/ahisham92/etabs-promax/main/install.ps1))) -Folder "$env:LOCALAPPDATA\CSI ProMax"'
    # Net8From: the first version of the program that runs on .NET 8. The ones before run on the .NET Framework.
    $plugins = @(
        @{ Program = 'ETABS';   Name = 'CsiProMaxPlugin';     Dll = 'CsiProMaxPlugin.dll';     Api = 'ETABSv1';   Oldest = 18; Net8From = 22 },
        @{ Program = 'SAFE';    Name = 'CsiProMaxSafePlugin'; Dll = 'CsiProMaxSafePlugin.dll'; Api = 'SAFEv1';    Oldest = 20; Net8From = 22 },
        @{ Program = 'SAP2000'; Name = 'CsiProMaxSapPlugin';  Dll = 'CsiProMaxSapPlugin.dll';  Api = 'SAP2000v1'; Oldest = 20; Net8From = 26 }
    )
    $report = New-Object System.Collections.Generic.List[string]
    $findings = New-Object System.Collections.Generic.List[object]

    function Read-Text([string]$file) {
        $bytes = [IO.File]::ReadAllBytes($file)
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { return (New-Object Text.UTF8Encoding($false)).GetString($bytes, 3, $bytes.Length - 3) }
        if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) { return [Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length - 2) }
        return [Text.Encoding]::GetEncoding(28591).GetString($bytes)
    }

    # The lines of the [PlugIn] part of a settings file, or nothing when it has none.
    function Get-PlugInLines([string]$text) {
        $lines = [regex]::Split($text, "\r?\n")
        $start = -1
        for ($i = 0; $i -lt $lines.Length; $i++) { if ($lines[$i] -match '^\s*\[PlugIn\]\s*$') { $start = $i; break } }
        if ($start -lt 0) { return $null }
        $kept = @($lines[$start])
        for ($i = $start + 1; $i -lt $lines.Length; $i++) {
            if ($lines[$i] -match '^\s*\[') { break }
            if ($lines[$i].Trim().Length -gt 0) { $kept += $lines[$i] }
        }
        return ,$kept
    }

    # The plug-ins a list names: each PlugInName starts one, and the menu text and path that follow belong to it.
    function Get-PlugInEntries([string[]]$lines) {
        $entries = @()
        $current = $null
        foreach ($line in $lines) {
            if ($line -notmatch '^\s*PlugIn(Name|MenuText|Path)\w*\s*=\s*(.*?)\s*$') { continue }
            $key = $Matches[1]; $value = $Matches[2].Trim('"')
            if ($key -eq 'Name' -or $null -eq $current) {
                $current = [pscustomobject]@{ Name = ''; Menu = ''; Path = '' }
                $entries += $current
            }
            if ($key -eq 'Name') { $current.Name = $value } elseif ($key -eq 'MenuText') { $current.Menu = $value } else { $current.Path = $value }
        }
        return $entries
    }

    function Get-Place([string]$path) {
        if ($path -match '^\\\\') { return 'a network folder' }
        foreach ($name in 'OneDrive', 'OneDriveCommercial', 'OneDriveConsumer') {
            $root = [Environment]::GetEnvironmentVariable($name)
            if ($root -and $path.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { return 'OneDrive' }
        }
        if ($path -match '[\\/]OneDrive( - [^\\/]+)?[\\/]') { return 'OneDrive' }
        if ($onWindows -and $path -match '^([A-Za-z]):\\') {
            try { if ((New-Object IO.DriveInfo($Matches[1])).DriveType -eq [IO.DriveType]::Network) { return 'a network drive' } } catch { }
        }
        return 'this PC'
    }

    function Get-FileFacts([string]$path) {
        $facts = @{ Exists = $false; Size = 0; OnlineOnly = $false; Blocked = $false; Attributes = ''; Error = '' }
        try {
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $facts }
            $item = Get-Item -LiteralPath $path -Force
            $facts.Exists = $true
            $facts.Size = $item.Length
            $facts.Attributes = [string]$item.Attributes
            # Offline (0x1000), recall on open (0x40000), recall on data access (0x400000): the content is in the cloud.
            $raw = [int]$item.Attributes
            if (($raw -band 0x1000) -or ($raw -band 0x40000) -or ($raw -band 0x400000)) { $facts.OnlineOnly = $true }
            if ($onWindows) {
                try { if (Get-Item -LiteralPath $path -Stream 'Zone.Identifier' -ErrorAction Stop) { $facts.Blocked = $true } } catch { }
            }
        }
        catch { $facts.Error = $_.Exception.Message }
        return $facts
    }

    function Find-ProgramFolder([string]$label) {
        $roots = @()
        if ($ProgramsRoot) { $roots += $ProgramsRoot }
        else {
            foreach ($name in 'ProgramW6432', 'ProgramFiles', 'ProgramFiles(x86)') {
                $value = [Environment]::GetEnvironmentVariable($name)
                if ($value) { $roots += (Join-Path $value 'Computers and Structures') }
            }
        }
        foreach ($root in ($roots | Select-Object -Unique)) {
            $folder = Join-Path $root $label
            if (Test-Path -LiteralPath $folder -PathType Container) { return $folder }
        }
        return $null
    }

    function Get-Identity([string]$file) {
        try { return [Reflection.AssemblyName]::GetAssemblyName($file).FullName }
        catch { return 'not read: ' + $_.Exception.Message }
    }

    # The PowerShell programs that try to create the plug-in. Windows PowerShell runs on the .NET Framework, as the
    # older CSI programs do; PowerShell 7, when the PC has it, runs on the newer .NET, as the newer ones do.
    function Get-Loaders {
        $loaders = @()
        if ($LoadWith) {
            foreach ($item in $LoadWith) {
                $at = $item.IndexOf('=')
                if ($at -gt 0) { $loaders += [pscustomobject]@{ Label = $item.Substring(0, $at); Path = $item.Substring($at + 1) } }
            }
            return ,$loaders
        }
        if (-not $onWindows) { return ,$loaders }
        $old = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        if (Test-Path -LiteralPath $old) { $loaders += [pscustomobject]@{ Label = '.NET Framework'; Path = $old } }
        $new = Get-Command pwsh.exe -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($new) { $loaders += [pscustomobject]@{ Label = '.NET'; Path = $new.Source } }
        return ,$loaders
    }

    # Tries it: a PowerShell of its own loads the program's API file and the plug-in, and makes the plug-in's class.
    # Its own process, so that this window does not keep the plug-in file open afterwards. The text below has no
    # double quote in it, so it passes to the other PowerShell as it is.
    $tryText = @'
$ErrorActionPreference = 'Stop'
$api = '<<API>>'
$plugin = '<<PLUGIN>>'
$class = '<<CLASS>>'
function Say([string]$key, [string]$value) { [Console]::Out.WriteLine('CPM|' + $key + '|' + ($value -replace '\s*\r?\n\s*', ' ')) }
try {
    Say 'runtime' ([Environment]::Version.ToString())
    Say 'mode' ([string]$ExecutionContext.SessionState.LanguageMode)
    $apiAssembly = $null
    if ($api) {
        $apiAssembly = [Reflection.Assembly]::LoadFrom($api)
        Say 'api' $apiAssembly.FullName
        # The program has its API file loaded already. Here it is handed over when the plug-in asks for it, by the
        # rule of this kind of .NET: the .NET Framework wants the very same name, version and key; the newer .NET
        # goes by the name alone.
        $apiName = $apiAssembly.GetName().Name
        $exact = ($PSVersionTable.PSEdition -ne 'Core')
        $handler = [ResolveEventHandler] {
            param($source, $asked)
            if ($asked.Name -eq $apiAssembly.FullName) { return $apiAssembly }
            if (-not $exact -and (New-Object Reflection.AssemblyName($asked.Name)).Name -eq $apiName) { return $apiAssembly }
            return $null
        }
        [AppDomain]::CurrentDomain.add_AssemblyResolve($handler)
    }
    $assembly = [Reflection.Assembly]::LoadFrom($plugin)
    Say 'plugin' $assembly.FullName
    foreach ($reference in $assembly.GetReferencedAssemblies()) { Say 'needs' $reference.FullName }
    # As the program does: the class by its name, one of it, and its two methods by their names.
    $found = $assembly.GetType($class, $true)
    $made = [Activator]::CreateInstance($found)
    Say 'made' $found.FullName
    $main = $found.GetMethod('Main')
    if (-not $main) { Say 'fail' 'The class has no public method named Main.'; return }
    $info = $found.GetMethod('Info')
    if (-not $info) { Say 'fail' 'The class has no public method named Info.'; return }
    if ($apiAssembly) {
        # Asking for what Main takes is what asks for the API file of the program.
        $given = @($main.GetParameters())
        if ($given.Count -ne 2) { Say 'fail' ('Main takes ' + $given.Count + ' things; the program gives it two, its model and its callback.'); return }
        foreach ($parameter in $given) {
            $kind = $parameter.ParameterType
            if ($kind.IsByRef) { $kind = $kind.GetElementType() }
            Say 'takes' $kind.FullName
            if ($kind.Assembly -ne $apiAssembly) { Say 'fail' ('Main takes ' + $kind.FullName + ' from another file than the API file of the program: ' + $kind.Assembly.FullName); return }
        }
        $words = New-Object object[] 1
        $words[0] = ''
        $null = $info.Invoke($made, $words)
        Say 'info' ([string]$words[0])
    }
    Say 'ok' 'created'
}
catch {
    $e = $_.Exception
    while ($e) {
        Say 'error' ($e.GetType().FullName + ': ' + $e.Message + ' (0x' + $e.HResult.ToString('X8') + ')')
        # The name of a type that is not there, as the exception holds it: the message is in the PC's language.
        if ($e -is [TypeLoadException] -and $e.TypeName) { Say 'missing' $e.TypeName }
        if ($e -is [Reflection.ReflectionTypeLoadException]) {
            foreach ($inner in $e.LoaderExceptions) {
                if (-not $inner) { continue }
                Say 'error' $inner.Message
                if ($inner -is [TypeLoadException] -and $inner.TypeName) { Say 'missing' $inner.TypeName }
            }
        }
        $e = $e.InnerException
    }
}
'@

    function Test-Load($loader, [string]$api, [string]$plugin, [string]$class) {
        $ErrorActionPreference = 'Continue'   # here only: a line on the other PowerShell's error stream is an answer too
        $result = @{ Label = $loader.Label; Ok = $false; Made = $false; Runtime = ''; Mode = ''; Api = ''; Plugin = ''; Info = ''; Needs = @(); Takes = @(); Missing = @(); Errors = @() }
        $text = $tryText.Replace('<<API>>', $api.Replace("'", "''")).Replace('<<PLUGIN>>', $plugin.Replace("'", "''")).Replace('<<CLASS>>', $class)
        $output = @()
        try { $output = @(& $loader.Path -NoProfile -NonInteractive -Command $text 2>&1 | ForEach-Object { [string]$_ }) }
        catch { $result.Errors += ('It could not be started: ' + $_.Exception.Message) }
        foreach ($line in $output) {
            if ($line -notmatch '^CPM\|(\w+)\|(.*)$') { if ($line.Trim().Length -gt 0) { $result.Errors += $line.Trim() }; continue }
            $value = $Matches[2]
            switch ($Matches[1]) {
                'runtime' { $result.Runtime = $value }
                'mode'    { $result.Mode = $value }
                'api'     { $result.Api = $value }
                'plugin'  { $result.Plugin = $value }
                'needs'   { $result.Needs += $value }
                'takes'   { $result.Takes += $value }
                'missing' { $result.Missing += $value }
                'info'    { $result.Info = $value }
                'made'    { $result.Made = $true }
                'ok'      { $result.Ok = $true }
                default   { $result.Errors += $value }
            }
        }
        if (-not $result.Ok -and $result.Errors.Count -eq 0) { $result.Errors += 'It gave no answer.' }
        return $result
    }

    try {
        # The PC.
        $report.Add('CSI ProMax check, ' + (Get-Date).ToString('yyyy-MM-dd HH:mm'))
        $report.Add('')
        $report.Add('This PC')
        $system = [Environment]::OSVersion.VersionString
        $report.Add("  Windows: $system, PowerShell $($PSVersionTable.PSVersion) ($($ExecutionContext.SessionState.LanguageMode))")
        if ($onWindows) {
            $release = $null
            try { $release = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction Stop).Release } catch { }
            if ($release) {
                $name = 'older than 4.7.2'
                if ($release -ge 533320) { $name = '4.8.1' } elseif ($release -ge 528040) { $name = '4.8' } elseif ($release -ge 461808) { $name = '4.7.2' }
                $report.Add("  .NET Framework: $name (release $release)")
                if ($release -lt 461808) { $report.Add('  NOTE: the plug-in needs .NET Framework 4.7.2 or newer in the CSI programs that run on the .NET Framework.') }
            }
            else { $report.Add('  .NET Framework: not found in the registry') }
            $desktop = @()
            foreach ($root in @($env:DOTNET_ROOT, $(if ($env:ProgramW6432) { Join-Path $env:ProgramW6432 'dotnet' }), $(if ($env:ProgramFiles) { Join-Path $env:ProgramFiles 'dotnet' }),
                                $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet' }), $(if ($env:USERPROFILE) { Join-Path $env:USERPROFILE '.dotnet' }))) {
                if (-not $root) { continue }
                $shared = Join-Path $root 'shared\Microsoft.WindowsDesktop.App'
                if (Test-Path -LiteralPath $shared) { foreach ($version in (Get-ChildItem -LiteralPath $shared -Directory -Filter '8.*')) { $desktop += "$($version.Name) in $root" } }
            }
            if ($desktop.Count -gt 0) { $report.Add('  .NET 8 Desktop Runtime (the CSI ProMax window needs it): ' + (($desktop | Select-Object -Unique) -join '; ')) }
            else { $report.Add('  .NET 8 Desktop Runtime (the CSI ProMax window needs it): NOT FOUND') }
        }
        if ([string]$ExecutionContext.SessionState.LanguageMode -ne 'FullLanguage') {
            $report.Add('  NOTE: this PC allows only a restricted PowerShell. A security rule that does this usually also stops programs from loading files from user folders, CSI ProMax among them. Ask IT to allow the CSI ProMax folder.')
        }

        # The programs and their plug-in lists.
        $root = $SettingsRoot
        if (-not $root -and $env:LOCALAPPDATA) { $root = Join-Path $env:LOCALAPPDATA 'Computers and Structures' }
        $programs = @()
        $tooOld = @()
        if ($root -and (Test-Path -LiteralPath $root)) {
            foreach ($directory in (Get-ChildItem -LiteralPath $root -Directory | Sort-Object Name)) {
                if ($directory.Name -notmatch '^(ETABS|SAFE|SAP2000) (\d+)$') { continue }
                $programName = $Matches[1]
                $number = [int]$Matches[2]
                $plugin = $plugins | Where-Object { $_.Program -ieq $programName } | Select-Object -First 1
                if ($number -lt $plugin.Oldest -or $number -gt 99) { $tooOld += $directory.Name; continue }
                foreach ($ini in (Get-ChildItem -LiteralPath $directory.FullName -File -Filter "$($plugin.Program)*.ini")) {
                    $programs += [pscustomobject]@{ Label = $directory.Name; Plugin = $plugin; Number = $number; Ini = $ini.FullName }
                }
            }
        }
        $loaders = @()
        if (-not $NoLoad) { $loaders = Get-Loaders }
        if ($programs.Count -eq 0) {
            $report.Add('')
            $report.Add("No ETABS, SAFE or SAP2000 settings were found for this Windows user ($root). Open the program once, close it, and run the install line.")
        }
        if ($tooOld.Count -gt 0) {
            $report.Add('')
            $report.Add('Too old for CSI ProMax (it works from ETABS 18, SAFE 20 and SAP2000 20): ' + ($tooOld -join ', '))
        }

        foreach ($program in $programs) {
            $plugin = $program.Plugin
            $finding = [pscustomobject]@{
                Program = $program.Label; Ini = $program.Ini; Listed = $false; Name = ''; Path = ''; Exists = $false; OnlineOnly = $false; Blocked = $false
                Place = ''; Runtime = ''; Api = ''; ApiFile = ''; Loads = $null; Reasons = @()
            }
            $findings.Add($finding)
            $report.Add('')
            $report.Add($program.Label)

            # The program itself.
            $folder = Find-ProgramFolder $program.Label
            $apiFile = $null
            $onFramework = ($program.Number -lt $plugin.Net8From)
            if ($folder) {
                if (Test-Path -LiteralPath (Join-Path $folder "$($plugin.Program).runtimeconfig.json")) { $onFramework = $false }
                $version = ''
                try { $version = (Get-Item -LiteralPath (Join-Path $folder "$($plugin.Program).exe")).VersionInfo.FileVersion } catch { }
                $candidate = Join-Path $folder "$($plugin.Api).dll"
                if (Test-Path -LiteralPath $candidate) { $apiFile = $candidate; $finding.ApiFile = $candidate; $finding.Api = Get-Identity $candidate }
                $report.Add("  Program: $folder" + $(if ($version) { ", version $version" } else { '' }))
                if ($apiFile) { $report.Add("  Its API file: $($finding.Api)") } else { $report.Add("  Its API file $($plugin.Api).dll was not found in that folder.") }
            }
            else { $report.Add('  Program: its folder was not found in Program Files, so its API file could not be read.') }
            $finding.Runtime = $(if ($onFramework) { '.NET Framework' } else { '.NET' })
            $report.Add("  It runs on: $($finding.Runtime)" + $(if ($onFramework) { ' (the plug-in must load there; .NET 8 is only for the CSI ProMax window)' } else { '' }))

            # Its plug-in list.
            $lines = $null
            try { $lines = Get-PlugInLines (Read-Text $program.Ini) } catch { $report.Add("  Settings file: could not be read ($($_.Exception.Message))") }
            $report.Add("  Settings file: $($program.Ini)")
            if ($null -eq $lines) {
                $finding.Reasons += 'The settings file has no plug-in list. Open the program once, close it, and run the install line.'
                $report.Add('  CANNOT OPEN: ' + $finding.Reasons[0])
                continue
            }
            foreach ($line in $lines) { $report.Add('    ' + $line.TrimEnd()) }
            $ours = @(@(Get-PlugInEntries $lines) | Where-Object { ($_.Path -split '[\\/]')[-1] -ieq $plugin.Dll -or $_.Name -ieq $plugin.Name })
            if ($ours.Count -eq 0) {
                $finding.Reasons += 'CSI ProMax is not in this list. Close the program and run the install line, or add it by hand in Tools > Add/Show Plugins.'
                $report.Add('  NOT IN THE TOOLS MENU: ' + $finding.Reasons[0])
                continue
            }
            $entry = $ours[0]
            $finding.Listed = $true; $finding.Name = $entry.Name; $finding.Path = $entry.Path
            if ($entry.Name -cne $plugin.Name) { $finding.Reasons += "The plug-in name in the list is '$($entry.Name)'. It must be exactly $($plugin.Name), or the program cannot find the class in the file." }

            # The file the list names.
            $facts = Get-FileFacts $entry.Path
            $finding.Exists = $facts.Exists; $finding.OnlineOnly = $facts.OnlineOnly; $finding.Blocked = $facts.Blocked
            $finding.Place = Get-Place $entry.Path
            if ($facts.Error) { $finding.Reasons += "The file could not be looked at: $($facts.Error)" }
            elseif (-not $facts.Exists) { $finding.Reasons += "The file the list names is not there: $($entry.Path)" }
            else {
                $report.Add("  File: $($entry.Path)")
                $report.Add("    $($facts.Size) bytes, on $($finding.Place), attributes: $($facts.Attributes)")
                $beside = Join-Path (Split-Path -Parent $entry.Path) 'CsiProMax.dll'
                if (Test-Path -LiteralPath $beside) {
                    $version = ''
                    try { $version = (Get-Item -LiteralPath $beside).VersionInfo.ProductVersion } catch { }
                    $report.Add("    CSI ProMax beside it: CsiProMax.dll $version")
                }
                else { $finding.Reasons += 'CsiProMax.dll is not beside the plug-in file: the whole CSI ProMax folder is needed, not only the plug-in file.' }
                if ($facts.OnlineOnly) { $finding.Reasons += 'The file is online-only in OneDrive: its content is not on this PC, so the program cannot load it.' }
                if ($facts.Blocked -and $onFramework) { $finding.Reasons += 'Windows marked the file as downloaded from the internet (blocked). A CSI program on the .NET Framework refuses such a file.' }
                elseif ($facts.Blocked) { $report.Add('    Note: Windows marked the file as downloaded from the internet (blocked). This program does not mind; an older one would.') }
                $remote = ($finding.Place -ne 'this PC' -and $finding.Place -ne 'OneDrive')
                if ($remote -and $onFramework) { $finding.Reasons += "The file is on $($finding.Place). A CSI program on the .NET Framework refuses to load a plug-in from a network place." }
                elseif ($remote) { $report.Add("    Note: the file is on $($finding.Place), so CSI ProMax opens only while that place can be reached.") }
                if ($finding.Place -eq 'OneDrive') { $report.Add('    Note: the file is in a OneDrive folder. OneDrive can make it online-only later, and then the program cannot load it.') }
            }

            # The real try.
            if ($facts.Exists -and -not $facts.OnlineOnly -and $loaders.Count -gt 0) {
                $decided = $false
                foreach ($loader in $loaders) {
                    $api = ''
                    if ($apiFile) { $api = $apiFile }
                    $attempt = Test-Load $loader $api $entry.Path "$($plugin.Name).cPlugin"
                    # Only the program's own kind of .NET decides; the other one is printed for the reader.
                    $same = (($loader.Label -eq '.NET Framework') -eq $onFramework)
                    $where = "$($loader.Label) $($attempt.Runtime)".Trim()
                    if ($attempt.Ok -and $apiFile) { $report.Add("  Tried on ${where}: the plug-in was created, against the program's own API file. Its Main takes " + ($attempt.Takes -join ' and ') + '.') }
                    elseif ($attempt.Ok) { $report.Add("  Tried on ${where}: the plug-in file loads. The program's API file was not found, so making its class could not be tried.") }
                    else {
                        $report.Add("  Tried on ${where}: the plug-in could NOT be created.")
                        foreach ($message in $attempt.Errors) { $report.Add("    $message") }
                        foreach ($need in $attempt.Needs) { $report.Add("    the plug-in asks for: $need") }
                    }
                    if (-not $same) { continue }
                    $decided = $true
                    if ($attempt.Ok -and $apiFile) { $finding.Loads = $true }
                    elseif (-not $attempt.Ok) {
                        $built = @($attempt.Needs | Where-Object { $_ -match ('^' + [regex]::Escape($plugin.Api) + ',') }) | Select-Object -First 1
                        if (@($attempt.Missing | Where-Object { $_ -match 'cPluginContract$' }).Count -gt 0 -or ($attempt.Errors -join ' ') -match "'[^']*cPluginContract'") {
                            $finding.Reasons += "This CSI ProMax plug-in names cPluginContract, which the API file of $($program.Label) does not have (only the programs on .NET 8 have it). CSI ProMax 0.4.4 and older did that: install the newest CSI ProMax."
                        }
                        elseif ($onFramework -and $built -and $attempt.Api -and $built -ne $attempt.Api) {
                            $finding.Reasons += "The plug-in was built for the API file '$built', and this program has '$($attempt.Api)'. On the .NET Framework the two must be the same."
                        }
                        else { $finding.Reasons += "$($loader.Label) could not create the plug-in: " + ($attempt.Errors | Select-Object -First 1) }
                    }
                }
                if (-not $decided) { $report.Add("  Not tried on $($finding.Runtime): this PC has no PowerShell that runs on it.") }
            }
            if ($finding.Reasons.Count -gt 0) { $finding.Loads = $false }

            if ($finding.Loads -eq $true) { $report.Add('  OK: this program can open CSI ProMax from its Tools menu.') }
            elseif ($finding.Reasons.Count -gt 0) {
                $report.Add('  CANNOT OPEN:')
                foreach ($reason in $finding.Reasons) { $report.Add("    - $reason") }
            }
            else { $report.Add('  NOT DECIDED: the file is there and nothing is wrong with where it is, but creating it could not be tried here.') }
        }

        # What to do.
        $broken = @($findings | Where-Object { $_.Reasons.Count -gt 0 })
        $report.Add('')
        if ($findings.Count -gt 0 -and $broken.Count -eq 0) { $report.Add('Result: nothing wrong was found.') }
        elseif ($broken.Count -gt 0) {
            $report.Add('Result: ' + (($broken | ForEach-Object { $_.Program }) -join ', ') + ' cannot open CSI ProMax as things are.')
            $report.Add('What usually puts it right: close the CSI programs, then run this one line in PowerShell. It puts CSI ProMax in a')
            $report.Add('folder on this PC that OneDrive and network drives do not reach, unblocks the files, and points the Tools menu at it:')
            $report.Add('  ' + $installLine)
        }
        $report.Add('Please send this whole text to the person who gave you CSI ProMax.')
    }
    catch {
        $report.Add('')
        $report.Add('The check stopped: ' + $_.Exception.Message)
    }

    $text = ($report -join [Environment]::NewLine)
    if ($PassThru) { return [pscustomobject]@{ Text = $text; Programs = $findings.ToArray() } }
    Write-Host ''
    Write-Host $text
    Write-Host ''
    if (-not $NoClipboard) {
        try { Set-Clipboard -Value $text; Write-Host 'This text is copied: paste it into a message (Ctrl+V).' -ForegroundColor Cyan }
        catch { Write-Host 'Select the text above and copy it into a message.' -ForegroundColor Cyan }
        Write-Host ''
    }
}

Test-CsiProMax @args
