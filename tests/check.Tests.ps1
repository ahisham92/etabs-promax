# Tests for check.ps1 (why Tools > CSI ProMax does not open). Run after a Release build of the three plug-ins:
#   pwsh -NoProfile -File tests/check.Tests.ps1          (any system: the plug-ins are created on the newer .NET)
#   powershell -NoProfile -File tests/check.Tests.ps1    (Windows: the plug-ins are created on the .NET Framework too,
#                                                         which ETABS 18 to 21, SAFE 20 and 21, SAP2000 20 to 25 run on)
# They use the plug-ins as built, the stand-ins for the programs' API files, and made-up settings files in a temp
# folder: no CSI program and no download. The "Cannot create plugin!" of ETABS 20 is what they guard against.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repository = Split-Path -Parent $here
$text = Get-Content -Raw -LiteralPath (Join-Path $repository 'check.ps1')
# The file as the one line runs it, and its function alone (the file without its last line, which runs it).
$script = [scriptblock]::Create($text)
$last = 'Test-CsiProMax @args'
if (-not $text.TrimEnd().EndsWith($last)) { throw "check.ps1 no longer ends with '$last'." }
. ([scriptblock]::Create($text.TrimEnd().Substring(0, $text.TrimEnd().Length - $last.Length)))
$failed = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "ok   $what" } else { Write-Host "FAIL $what" -ForegroundColor Red; $script:failed++ }
}
function Built([string]$project, [string]$file) {
    $path = Join-Path $repository (Join-Path 'src' (Join-Path $project (Join-Path 'bin' (Join-Path 'Release' (Join-Path 'netstandard2.0' $file)))))
    if (-not (Test-Path -LiteralPath $path)) { throw "$file is not built: run dotnet build CsiProMax.sln -c Release first." }
    $path
}
$onWindows = ($env:OS -eq 'Windows_NT')
$root = Join-Path ([IO.Path]::GetTempPath()) ('cpm-check-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$settings = Join-Path $root 'Computers and Structures'
$programs = Join-Path $root 'Program Files'
$install = Join-Path $root 'CSI ProMax'
New-Item -ItemType Directory -Force -Path $install | Out-Null
Set-Content -LiteralPath (Join-Path $install 'CsiProMax.dll') -Value 'the assistant' -NoNewline
foreach ($item in @(@('CsiProMax.Plugin', 'CsiProMaxPlugin.dll'), @('CsiProMax.Plugin.Safe', 'CsiProMaxSafePlugin.dll'), @('CsiProMax.Plugin.Sap', 'CsiProMaxSapPlugin.dll'))) {
    Copy-Item -LiteralPath (Built $item[0] $item[1]) -Destination (Join-Path $install $item[1])
}

# A program: its folder with its API file (the stand-in, which has the real file's name, version and key), and its
# settings file with the plug-in list.
function Program([string]$label, [string]$apiProject, [string]$apiFile, [string]$list, [string]$apiAs) {
    $folder = New-Item -ItemType Directory -Force -Path (Join-Path $programs $label)
    if ($apiProject) {
        $target = $apiFile
        if ($apiAs) { $target = $apiAs }
        Copy-Item -LiteralPath (Built $apiProject $apiFile) -Destination (Join-Path $folder.FullName $target)
    }
    $directory = New-Item -ItemType Directory -Force -Path (Join-Path $settings $label)
    $name = ($label -split ' ')[0]
    [IO.File]::WriteAllBytes((Join-Path $directory.FullName "$name.ini"), [Text.Encoding]::GetEncoding(28591).GetBytes($list))
}
function Listed([string]$name, [string]$path) {
    "[Units]`r`n Force=kN`r`n[PlugIn]`r`n NumberPlugIns=1`r`n PlugInName=$name`r`n PlugInMenuText=CSI ProMax`r`n PlugInPath=$path`r`n`r`n[Graphics]`r`n Colour=1`r`n"
}
$etabs = Join-Path $install 'CsiProMaxPlugin.dll'
Program 'ETABS 20' 'EtabsApiStub' 'ETABSv1.dll' (Listed 'CsiProMaxPlugin' $etabs)
Program 'ETABS 23' 'EtabsApiStub' 'ETABSv1.dll' (Listed 'CsiProMaxPlugin' $etabs)
Program 'SAFE 21' 'SafeApiStub' 'SAFEv1.dll' (Listed 'CsiProMaxSafePlugin' (Join-Path $install 'CsiProMaxSafePlugin.dll'))
Program 'SAP2000 24' 'SapApiStub' 'SAP2000v1.dll' (Listed 'CsiProMaxSapPlugin' (Join-Path $install 'CsiProMaxSapPlugin.dll'))
# What goes wrong on a tester's PC.
Program 'ETABS 19' 'EtabsApiStub' 'ETABSv1.dll' (Listed 'CsiProMaxPlugin' (Join-Path $root (Join-Path 'OneDrive - Some Company' (Join-Path 'Documents' (Join-Path 'CSI ProMax' 'CsiProMaxPlugin.dll')))))
Program 'ETABS 21' 'EtabsApiStub' 'ETABSv1.dll' "[PlugIn]`r`n NumberPlugIns=0`r`n`r`n[Graphics]`r`n"
Program 'ETABS 22' 'EtabsApiStub' 'ETABSv1.dll' (Listed 'ProMax' $etabs)
# A program whose API file is another one than the plug-in was built for (here SAP2000's, under ETABS's file name).
Program 'ETABS 18' 'SapApiStub' 'SAP2000v1.dll' (Listed 'CsiProMaxPlugin' $etabs) 'ETABSv1.dll'
Program 'ETABS 17' '' '' (Listed 'CsiProMaxPlugin' $etabs)
Program 'CSiBridge 26' '' '' (Listed 'CsiProMaxPlugin' $etabs)

try {
    # On Windows the PC's own PowerShells are used: Windows PowerShell is the .NET Framework. Elsewhere this
    # PowerShell stands in for both kinds, so that each program's own kind is the one that decides.
    $arguments = @{ SettingsRoot = $settings; ProgramsRoot = $programs; PassThru = $true }
    if (-not $onWindows) {
        $self = (Get-Process -Id $PID).Path
        $command = Get-Command pwsh -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command) { $self = $command.Source }
        $arguments.LoadWith = @(".NET Framework=$self", ".NET=$self")
    }
    $answer = Test-CsiProMax @arguments
    $by = @{}
    foreach ($program in $answer.Programs) { $by[$program.Program] = $program }
    if ($env:CPM_CHECK_DUMP -eq '1') { Write-Host $answer.Text }

    Check ($answer.Programs.Count -eq 8) "the eight ETABS, SAFE and SAP2000 found are looked at, and nothing else ($($answer.Programs.Count))"
    Check ($answer.Text -match 'Too old for CSI ProMax[^\r\n]*ETABS 17') 'a program older than CSI ProMax works with is named as too old'

    # The plug-ins as built can be created by the kind of .NET each program runs on.
    foreach ($label in 'ETABS 20', 'ETABS 23', 'SAFE 21', 'SAP2000 24') {
        Check ($by[$label].Loads -eq $true -and $by[$label].Reasons.Count -eq 0) "$label can create the plug-in ($($by[$label].Runtime)): $($by[$label].Reasons -join ' | ')"
    }
    Check ($by['ETABS 20'].Runtime -eq '.NET Framework' -and $by['ETABS 23'].Runtime -eq '.NET') 'ETABS 20 runs on the .NET Framework and ETABS 23 on the newer .NET'
    Check ($by['SAFE 21'].Runtime -eq '.NET Framework' -and $by['SAP2000 24'].Runtime -eq '.NET Framework') 'so do SAFE 21 and SAP2000 24'
    Check ($by['ETABS 20'].Api -match '^ETABSv1, Version=1\.0\.0\.0, Culture=neutral, PublicKeyToken=453d728ef24c6f5e$') "the API file's name, version and key are in the answer: $($by['ETABS 20'].Api)"
    Check ($answer.Text -match "ETABS 20(.|\r|\n)*?Tried on \.NET Framework[^\r\n]*: the plug-in was created, against the program's own API file") 'the report says on which .NET it was tried'

    # What goes wrong.
    Check ($by['ETABS 19'].Loads -eq $false -and ($by['ETABS 19'].Reasons -join ' ') -match 'is not there' -and $by['ETABS 19'].Place -eq 'OneDrive') 'a file that is not there, in a OneDrive folder, is said so'
    Check (-not $by['ETABS 21'].Listed -and ($by['ETABS 21'].Reasons -join ' ') -match 'not in this list') 'a program without CSI ProMax in its list is said so'
    Check (($by['ETABS 22'].Reasons -join ' ') -match "must be exactly CsiProMaxPlugin") 'a wrong plug-in name in the list is said so'
    Check ($by['ETABS 18'].Loads -eq $false -and ($by['ETABS 18'].Reasons -join ' ') -match 'built for the API file') "a program with another API file than the plug-in was built for cannot create it: $($by['ETABS 18'].Reasons -join ' | ')"
    Check ($answer.Text -match 'Result: [^\r\n]*ETABS 18[^\r\n]*cannot open CSI ProMax' -and $answer.Text -match [regex]::Escape('-Folder "$env:LOCALAPPDATA\CSI ProMax"')) 'the report ends with what to do: the install line, into a folder on this PC'

    # The bug itself: the plug-in file of version 0.4.4 (tests/fixtures, taken from that release). Its class says
    # ": cPluginContract", which only the API files of the .NET 8 programs have, so ETABS 20 answered "Cannot create
    # plugin!" and listed it as "Not Found". The API file here has none either, like the one of ETABS 18 to 21: the
    # old file must not be creatable, and the report must say why. The same program creates today's plug-in above.
    $old = Join-Path $root 'CSI ProMax 0.4.4'
    New-Item -ItemType Directory -Force -Path $old | Out-Null
    Copy-Item -LiteralPath (Join-Path $here (Join-Path 'fixtures' 'CsiProMaxPlugin-0.4.4.dll')) -Destination (Join-Path $old 'CsiProMaxPlugin.dll')
    Set-Content -LiteralPath (Join-Path $old 'CsiProMax.dll') -Value 'the assistant' -NoNewline
    Program 'ETABS 20' 'EtabsApiStub' 'ETABSv1.dll' (Listed 'CsiProMaxPlugin' (Join-Path $old 'CsiProMaxPlugin.dll'))
    $before = Test-CsiProMax @arguments
    $twenty = $before.Programs | Where-Object { $_.Program -eq 'ETABS 20' }
    Check ($twenty.Loads -eq $false) "the plug-in of 0.4.4 cannot be created by ETABS 20: $($twenty.Reasons -join ' | ')"
    Check (($twenty.Reasons -join ' ') -match 'names cPluginContract' -and ($twenty.Reasons -join ' ') -match 'install the newest CSI ProMax') 'and the report says why, and what to do'
    Check ($before.Text -match 'ETABS 20(.|\r|\n)*?Tried on \.NET Framework[^\r\n]*: the plug-in could NOT be created') 'the report shows the try that failed'

    # Only the plug-in file, without CSI ProMax beside it.
    $alone = Join-Path $root 'alone'
    New-Item -ItemType Directory -Force -Path $alone | Out-Null
    Copy-Item -LiteralPath $etabs -Destination (Join-Path $alone 'CsiProMaxPlugin.dll')
    Program 'ETABS 20' 'EtabsApiStub' 'ETABSv1.dll' (Listed 'CsiProMaxPlugin' (Join-Path $alone 'CsiProMaxPlugin.dll'))
    $arguments.NoLoad = $true
    $second = Test-CsiProMax @arguments
    $twenty = $second.Programs | Where-Object { $_.Program -eq 'ETABS 20' }
    Check (($twenty.Reasons -join ' ') -match 'CsiProMax\.dll is not beside') 'the plug-in file alone, without the rest of CSI ProMax, is said so'
    Check ($null -eq ($second.Programs | Where-Object { $_.Program -eq 'ETABS 23' }).Loads) 'without the try, a program with nothing wrong is left undecided, not passed'

    # Printed for a person: nothing comes back down the pipeline (irm | iex shows only the text).
    $printed = @(& $script -SettingsRoot $settings -ProgramsRoot $programs -NoLoad -NoClipboard 6> $null)
    Check ($printed.Count -eq 0) 'run as the one line, it prints and gives nothing back'

    # Every version CSI ProMax works with, one by one, from the oldest to the newest of each program: each creates its
    # plug-in, on its own kind of .NET. (The API file is the stand-in in all of them: the real one has the same name,
    # version and key in every version, and the stand-in holds only what the oldest one has.)
    $settings = Join-Path $root (Join-Path 'every version' 'Computers and Structures')
    $programs = Join-Path $root (Join-Path 'every version' 'Program Files')
    $every = @(
        @{ Program = 'ETABS';   From = 18; Net8From = 22; To = 23; Stub = 'EtabsApiStub'; Api = 'ETABSv1.dll';   Plugin = 'CsiProMaxPlugin' },
        @{ Program = 'SAFE';    From = 20; Net8From = 22; To = 23; Stub = 'SafeApiStub';  Api = 'SAFEv1.dll';    Plugin = 'CsiProMaxSafePlugin' },
        @{ Program = 'SAP2000'; From = 20; Net8From = 26; To = 26; Stub = 'SapApiStub';   Api = 'SAP2000v1.dll'; Plugin = 'CsiProMaxSapPlugin' }
    )
    $expected = @{}
    foreach ($row in $every) {
        foreach ($number in $row.From..$row.To) {
            $label = "$($row.Program) $number"
            Program $label $row.Stub $row.Api (Listed $row.Plugin (Join-Path $install "$($row.Plugin).dll"))
            if ($number -lt $row.Net8From) { $expected[$label] = '.NET Framework' } else { $expected[$label] = '.NET' }
        }
    }
    $arguments.SettingsRoot = $settings
    $arguments.ProgramsRoot = $programs
    $arguments.Remove('NoLoad')
    $all = Test-CsiProMax @arguments
    Check ($all.Programs.Count -eq $expected.Count) "all $($expected.Count) versions are looked at ($($all.Programs.Count))"
    foreach ($row in $every) {
        $wrong = @()
        foreach ($number in $row.From..$row.To) {
            $label = "$($row.Program) $number"
            $one = $all.Programs | Where-Object { $_.Program -eq $label }
            if (-not $one -or $one.Loads -ne $true -or $one.Reasons.Count -ne 0 -or $one.Runtime -ne $expected[$label]) { $wrong += "$label on '$($one.Runtime)': $($one.Reasons -join ' | ')" }
        }
        $older = "$($row.From) to $($row.Net8From - 1) on the .NET Framework"
        $newer = "$($row.Net8From) to $($row.To) on the newer .NET"
        if ($row.Net8From -eq $row.To) { $newer = "$($row.To) on the newer .NET" }
        Check ($wrong.Count -eq 0) "$($row.Program) $older and ${newer}: each creates the plug-in $($wrong -join '; ')"
    }
    Check ($all.Text -match 'Result: nothing wrong was found\.') 'and the report says that nothing is wrong'

    # The file is plain ASCII, so that it reads the same in every PowerShell.
    $bytes = [IO.File]::ReadAllBytes((Join-Path $repository 'check.ps1'))
    Check (@($bytes | Where-Object { $_ -gt 126 }).Count -eq 0) 'check.ps1 is plain ASCII'
}
finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }

if ($failed -gt 0) { Write-Host "`n$failed failed." -ForegroundColor Red; exit 1 }
Write-Host "`nAll passed."
