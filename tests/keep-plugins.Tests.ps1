# Tests for keep-plugins.ps1 (CSI ProMax kept in the Tools menus) and uninstall.ps1.
# Run: pwsh -NoProfile -File tests/keep-plugins.Tests.ps1 (also Windows PowerShell 5.1). Made-up settings files only:
# no CSI program, no scheduled task, no download.
#
# The bug these hold (2026-11-26): on a tester's PC CSI ProMax was gone from the Tools menu of the CSI programs. A
# program started through its API writes its settings back with NumberPlugIns=0, and CSI ProMax put itself back only
# once it was opened, which, with the menu entry gone, it no longer could be.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = Split-Path -Parent $here
$keeperFile = Join-Path $repo 'keep-plugins.ps1'
$keeper = [scriptblock]::Create((Get-Content -Raw -LiteralPath $keeperFile))
$uninstall = [scriptblock]::Create((Get-Content -Raw -LiteralPath (Join-Path $repo 'uninstall.ps1')))
$failed = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "ok   $what" } else { Write-Host "FAIL $what" -ForegroundColor Red; $script:failed++ }
}
function Text([string]$file) { [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($file)) }
function Settings([string]$folder, [string]$name, [string]$text) {
    $directory = New-Item -ItemType Directory -Force -Path (Join-Path $settings $folder)
    $file = Join-Path $directory.FullName $name
    [IO.File]::WriteAllBytes($file, [Text.Encoding]::GetEncoding(28591).GetBytes($text))
    $file
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('cpm-keep-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$settings = Join-Path $root 'Computers and Structures'
$state = Join-Path $root 'CsiProMax'
$folder = Join-Path (Join-Path $root 'Documents') 'CSI ProMax'
New-Item -ItemType Directory -Force -Path $folder | Out-Null
foreach ($name in 'CsiProMax.dll', 'CsiProMaxPlugin.dll', 'CsiProMaxSafePlugin.dll', 'CsiProMaxSapPlugin.dll') { Set-Content -LiteralPath (Join-Path $folder $name) -Value $name -NoNewline }
New-Item -ItemType Directory -Force -Path (Join-Path $folder 'Made in chat') | Out-Null
Set-Content -LiteralPath (Join-Path (Join-Path $folder 'Made in chat') 'pack.epmx') -Value 'kept' -NoNewline
$common = @{ Folder = $folder; SettingsRoot = $settings; StateRoot = $state; NoProcessCheck = $true; Quiet = $true }

try {
    # What a program wrote after it was started through its API: the list emptied (the tester's case).
    $emptied = "[Units]`r`n Force=kN`r`n[PlugIn]`r`n NumberPlugIns=0`r`n`r`n[Graphics]`r`n Colour=1`r`n"
    $etabs = Settings 'ETABS 23' 'ETABS.ini' $emptied
    $safe = Settings 'SAFE 22' 'SAFE.ini' $emptied
    $sap = Settings 'SAP2000 26' 'SAP2000v26.ini' $emptied
    $other = "[PlugIn]`r`n NumberPlugIns=1`r`n PlugInName=Someone.Else`r`n PlugInMenuText=Else`r`n PlugInPath=C:\Else\Else.dll`r`n"
    $else = Settings 'ETABS 21' 'ETABS.ini' $other
    $old = Settings 'ETABS 17' 'ETABS.ini' $emptied

    & $keeper @common
    foreach ($pair in @(@($etabs, 'CsiProMaxPlugin'), @($safe, 'CsiProMaxSafePlugin'), @($sap, 'CsiProMaxSapPlugin'))) {
        $t = Text $pair[0]
        Check ($t -match 'NumberPlugIns=1' -and $t -match ('PlugInName=' + $pair[1] + "`r`n") -and $t.Contains('PlugInPath=' + (Join-Path $folder ($pair[1] + '.dll')))) "an emptied list gets CSI ProMax back: $(Split-Path -Leaf (Split-Path -Parent $pair[0]))"
        Check ($t.Contains("[Graphics]`r`n Colour=1") -and $t.StartsWith("[Units]`r`n Force=kN`r`n[PlugIn]`r`n")) 'the rest of the settings file is kept as it was'
        Check (Test-Path -LiteralPath ($pair[0] + '.before-csi-promax')) 'a copy of the settings file is kept first'
    }
    Check ((Text $else) -eq $other) "a list with another vendor's plug-in is never changed"
    Check ((Text $old) -eq $emptied) 'a program older than the plug-ins support is left alone'
    $log = Get-Content -Raw -LiteralPath (Join-Path $state 'keep-plugins.log')
    Check ($log -match 'ETABS 23: CSI ProMax was missing from the Tools menu \(the program last wrote its settings at ') 'the log says what was added back, and when the program had emptied it'

    # A second look changes nothing (no rewrite every minute).
    $before = (Get-Item -LiteralPath $etabs).LastWriteTimeUtc
    Start-Sleep -Milliseconds 1100
    & $keeper @common
    Check ((Get-Item -LiteralPath $etabs).LastWriteTimeUtc -eq $before) 'a list that has CSI ProMax is not written again'

    # Emptied again (the program was started through its API once more): put back again.
    [IO.File]::WriteAllBytes($sap, [Text.Encoding]::GetEncoding(28591).GetBytes($emptied))
    & $keeper @common
    Check ((Text $sap) -match 'PlugInName=CsiProMaxSapPlugin') 'emptied a second time, it is put back a second time'

    # A program that is open is never written under: its file is rewritten when it closes. Here "open" means a
    # process of the program's name with a window; none exists in the test, so it is checked by reading the rule.
    $source = Get-Content -Raw -LiteralPath $keeperFile
    Check ($source -match "if \(Test-Open \`$item.Plugin.Program \`$item.Number\) \{ \`$status = 'open' \}") 'an open program is left until it is closed'

    # The off switch.
    [IO.File]::WriteAllBytes($etabs, [Text.Encoding]::GetEncoding(28591).GetBytes($emptied))
    Set-Content -LiteralPath (Join-Path $state 'keep-plugins-off.txt') -Value 'off'
    & $keeper @common
    Check ((Text $etabs) -eq $emptied) 'keep-plugins-off.txt leaves the menus as they are'
    Remove-Item -LiteralPath (Join-Path $state 'keep-plugins-off.txt')

    # No plug-in files in the folder (CSI ProMax deleted by hand): nothing is added that would name missing files.
    $gone = Join-Path $root 'Nowhere'
    & $keeper -Folder $gone -SettingsRoot $settings -StateRoot $state -NoProcessCheck -Quiet
    Check ((Text $etabs) -eq $emptied) 'with no plug-in files there, no Tools menu is changed'

    # A list naming an old copy elsewhere is made to name this folder, once.
    $moved = Settings 'ETABS 22' 'ETABS.ini' "[PlugIn]`r`n NumberPlugIns=1`r`n PlugInName=CsiProMaxPlugin`r`n PlugInMenuText=CSI ProMax`r`n PlugInPath=C:\Old place\CsiProMaxPlugin.dll`r`n"
    & $keeper @common
    $t = Text $moved
    Check ($t.Contains('PlugInPath=' + (Join-Path $folder 'CsiProMaxPlugin.dll')) -and -not $t.Contains('Old place')) 'an entry naming another copy names this install now'

    # UTF-16 settings keep their encoding.
    $utf16 = Join-Path (New-Item -ItemType Directory -Force -Path (Join-Path $settings 'SAFE 23')).FullName 'SAFE.ini'
    [IO.File]::WriteAllBytes($utf16, [byte[]](0xFF, 0xFE) + [Text.Encoding]::Unicode.GetBytes($emptied))
    & $keeper @common
    $bytes = [IO.File]::ReadAllBytes($utf16)
    Check ($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE -and ([Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length - 2) -match 'PlugInName=CsiProMaxSafePlugin')) 'a UTF-16 settings file keeps its encoding'

    # Uninstall: CSI ProMax taken out of every list that holds only it; other vendors' lists left alone; files kept.
    & $uninstall -Folder $folder -SettingsRoot $settings -StateRoot $state -KeeperFile $keeperFile -NoProcessCheck -NoShortcuts *> $null
    foreach ($file in $etabs, $safe, $sap, $moved, $utf16) {
        if ($file -eq $utf16) { $b = [IO.File]::ReadAllBytes($file); $t = [Text.Encoding]::Unicode.GetString($b, 2, $b.Length - 2) } else { $t = Text $file }
        Check ($t -match 'NumberPlugIns=0' -and $t -notmatch 'CsiProMax') "uninstall takes CSI ProMax out: $(Split-Path -Leaf (Split-Path -Parent $file))"
    }
    Check ((Text $etabs) -eq $emptied) 'uninstall leaves the settings file as the program had it before CSI ProMax'
    Check ((Text $else) -eq $other) "uninstall leaves another vendor's list alone"
    Check (Test-Path -LiteralPath (Join-Path $folder 'CsiProMaxPlugin.dll')) 'uninstall without -Files keeps the program files'

    # Uninstall with -Files: the program files go, Made in chat stays.
    & $uninstall -Folder $folder -SettingsRoot $settings -StateRoot $state -KeeperFile $keeperFile -NoProcessCheck -NoShortcuts -Files *> $null
    Check (-not (Test-Path -LiteralPath (Join-Path $folder 'CsiProMaxPlugin.dll'))) 'uninstall -Files deletes the program files'
    Check (Test-Path -LiteralPath (Join-Path (Join-Path $folder 'Made in chat') 'pack.epmx')) 'uninstall -Files never deletes Made in chat'

    # install.ps1 sets the keeper up, and the release ships both scripts.
    $install = Get-Content -Raw -LiteralPath (Join-Path $repo 'install.ps1')
    Check ($install.Contains('-File $keeper -Register -Folder $Folder')) 'install.ps1 registers the keeper'
    $release = Get-Content -Raw -LiteralPath (Join-Path (Join-Path (Join-Path $repo '.github') 'workflows') 'release.yml')
    Check ($release.Contains('cp check.ps1 keep-plugins.ps1 package/dist/') -and $release.Contains('uninstall.ps1)')) 'the release ships keep-plugins.ps1 in the package and uninstall.ps1 beside it'
    foreach ($name in 'keep-plugins.ps1', 'uninstall.ps1') {
        $b = [IO.File]::ReadAllBytes((Join-Path $repo $name))
        Check (-not ($b | Where-Object { $_ -gt 0x7E -or ($_ -lt 0x20 -and $_ -ne 9 -and $_ -ne 10 -and $_ -ne 13) })) "$name is plain ASCII (Windows PowerShell 5.1 reads it right)"
    }
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
if ($failed -gt 0) { Write-Host "$failed failed" -ForegroundColor Red; exit 1 }
Write-Host 'keep-plugins: all passed'
