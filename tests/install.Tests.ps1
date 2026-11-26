# Tests for install.ps1 (the one-line install). Run: pwsh -NoProfile -File tests/install.Tests.ps1
# They use a made-up package and made-up settings files in a temp folder: no download, no CSI program.
$ErrorActionPreference = 'Stop'
$script = [scriptblock]::Create((Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..' 'install.ps1')))
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

$root = Join-Path ([IO.Path]::GetTempPath()) ('cpm-install-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$package = New-Item -ItemType Directory -Force -Path (Join-Path $root 'package')
$dist = New-Item -ItemType Directory -Force -Path (Join-Path $package.FullName 'dist' 'web')
foreach ($name in 'CsiProMax.dll', 'CsiProMaxPlugin.dll', 'CsiProMaxSafePlugin.dll', 'CsiProMaxSapPlugin.dll') {
    Set-Content -LiteralPath (Join-Path $package.FullName 'dist' $name) -Value "first $name" -NoNewline
}
Set-Content -LiteralPath (Join-Path $dist.FullName 'viewer.html') -Value '<html></html>' -NoNewline
Set-Content -LiteralPath (Join-Path $package.FullName 'INSTALL.txt') -Value 'notes' -NoNewline
# The check that the installer runs when it is done, as the release packs it.
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..' 'check.ps1') -Destination (Join-Path $package.FullName 'dist' 'check.ps1')
$zip = Join-Path $root 'CsiProMax-9.8.7.zip'
Compress-Archive -Path (Join-Path $package.FullName '*') -DestinationPath $zip

$settings = Join-Path $root 'Computers and Structures'
$fresh = "[Units]`r`n Force=kN`r`n[PlugIn]`r`n NumberPlugIns=0`r`n`r`n[Graphics]`r`n Colour=1`r`n"
$etabs23 = Settings 'ETABS 23' 'ETABS.ini' $fresh
$safe22 = Settings 'SAFE 22' 'SAFE.ini' $fresh
$sap26 = Settings 'SAP2000 26' 'SAP2000v26.ini' ($fresh -replace '\[Graphics\]', '[ToolStrips]')
$other = "[PlugIn]`r`n NumberPlugIns=1`r`n PlugInName=Someone.Else`r`n PlugInMenuText=Someone else`r`n PlugInPath=C:\Else\Else.dll`r`n`r`n[Graphics]`r`n"
$sap24 = Settings 'SAP2000 24' 'SAP2000v24.ini' $other
$moved = "[PlugIn]`r`n NumberPlugIns=1`r`n PlugInName=CsiProMaxPlugin`r`n PlugInMenuText=CSI ProMax`r`n PlugInPath=C:\Old place\CsiProMaxPlugin.dll`r`n`r`n[Graphics]`r`n"
$etabs21 = Settings 'ETABS 21' 'ETABS.ini' $moved
$unix = Settings 'ETABS 20' 'ETABS.ini' "[PlugIn]`n NumberPlugIns=0`n`n[Graphics]`n"
$safe16 = Settings 'SAFE 16' 'SAFE.ini' $fresh
$bridge = Settings 'CSiBridge 26' 'CSiBridge.ini' $fresh
$none = Settings 'ETABS 19' 'ETABS.ini' "[Graphics]`r`n Colour=1`r`n"
# The ETABS 20 tester's list: CSI ProMax added by hand a second time, from another folder and under another menu text.
$twice = Settings 'ETABS 18' 'ETABS.ini' "[PlugIn]`r`n NumberPlugIns=2`r`n PlugInName=CsiProMaxPlugin`r`n PlugInMenuText=CSI ProMax`r`n PlugInPath=C:\Users\x\OneDrive - Company\Documents\CSI ProMax\CsiProMaxPlugin.dll`r`n PlugInName=CsiProMaxPlugin`r`n PlugInMenuText=myCsiProMaxPlugin`r`n PlugInPath=C:\Users\x\Documents\CSI ProMax\CsiProMaxPlugin.dll`r`n`r`n[Graphics]`r`n"
# Ours beside someone else's: never changed.
$mixed = Settings 'SAP2000 23' 'SAP2000v23.ini' "[PlugIn]`r`n NumberPlugIns=2`r`n PlugInName=Someone.Else`r`n PlugInMenuText=Else`r`n PlugInPath=C:\Else\Else.dll`r`n PlugInName=CsiProMaxSapPlugin`r`n PlugInMenuText=CSI ProMax`r`n PlugInPath=C:\Old\CsiProMaxSapPlugin.dll`r`n"
$mixedWas = Text $mixed
$utf16 = Join-Path (New-Item -ItemType Directory -Force -Path (Join-Path $settings 'ETABS 22')).FullName 'ETABS.ini'
[IO.File]::WriteAllBytes($utf16, [byte[]](0xFF, 0xFE) + [Text.Encoding]::Unicode.GetBytes($fresh))

$folder = Join-Path $root 'Documents' 'CSI ProMax'
$plugin = Join-Path $folder 'CsiProMaxPlugin.dll'

# A preview changes no settings file.
& $script -Zip $zip -Folder $folder -SettingsRoot $settings -NoRuntime -Preview *> $null
Check ((Text $etabs23) -eq $fresh) 'preview: the settings file is not changed'
Check (-not (Test-Path -LiteralPath ($etabs23 + '.before-csi-promax'))) 'preview: no copy is made'
Check ((Get-Content -Raw -LiteralPath $plugin) -eq 'first CsiProMaxPlugin.dll') 'the package is copied into the folder'
Check (Test-Path -LiteralPath (Join-Path $folder 'web' 'viewer.html')) 'folders inside the package are copied'
Check (Test-Path -LiteralPath (Join-Path $folder 'INSTALL.txt')) 'the notes are copied'

# The real run.
$mine = Join-Path $folder 'Bugs'
New-Item -ItemType Directory -Force -Path $mine | Out-Null
Set-Content -LiteralPath (Join-Path $mine 'kept.txt') -Value 'mine'
$output = (& $script -Zip $zip -Folder $folder -SettingsRoot $settings -NoRuntime *>&1 | Out-String)
$expected = "[Units]`r`n Force=kN`r`n[PlugIn]`r`n NumberPlugIns=1`r`n PlugInName=CsiProMaxPlugin`r`n PlugInMenuText=CSI ProMax`r`n PlugInPath=$plugin`r`n`r`n[Graphics]`r`n Colour=1`r`n"
Check ((Text $etabs23) -eq $expected) 'ETABS: the plug-in is listed, and every other byte is as it was'
Check ((Text ($etabs23 + '.before-csi-promax')) -eq $fresh) 'a copy of the settings file is kept first'
Check ((Text $safe22).Contains(" PlugInName=CsiProMaxSafePlugin`r`n PlugInMenuText=CSI ProMax`r`n PlugInPath=$(Join-Path $folder 'CsiProMaxSafePlugin.dll')`r`n")) 'SAFE gets its own plug-in'
Check ((Text $sap26).Contains(" NumberPlugIns=1`r`n PlugInName=CsiProMaxSapPlugin`r`n") -and (Text $sap26).Contains("`r`n`r`n[ToolStrips]")) 'SAP2000 gets its own plug-in'
Check ((Text $sap24) -eq $other) 'a list with another plug-in is left alone'
Check ($output -match 'SAP2000 24\s+has other plug-ins') 'and the person is told to add it by hand'
Check ((Text $etabs21).Contains(" NumberPlugIns=1`r`n PlugInName=CsiProMaxPlugin`r`n PlugInMenuText=CSI ProMax`r`n PlugInPath=$plugin`r`n`r`n[Graphics]")) 'our own entry from another folder is pointed at this one'
Check ((Text $unix) -eq "[PlugIn]`n NumberPlugIns=1`n PlugInName=CsiProMaxPlugin`n PlugInMenuText=CSI ProMax`n PlugInPath=$plugin`n`n[Graphics]`n") 'line ends are kept as they were'
Check ((Text $safe16) -eq $fresh -and (Text $bridge) -eq $fresh) 'an old SAFE and another program are not touched'
Check ((Text $twice) -eq "[PlugIn]`r`n NumberPlugIns=1`r`n PlugInName=CsiProMaxPlugin`r`n PlugInMenuText=CSI ProMax`r`n PlugInPath=$plugin`r`n`r`n[Graphics]`r`n") 'CSI ProMax listed twice by hand becomes one entry, for this folder'
Check ((Text $mixed) -eq $mixedWas) 'CSI ProMax beside another plug-in is left alone'
# Ahmed, 2026-11-26: only real problems are shown; a self-test that could not run here says nothing.
$checked = [regex]::Match($output, 'Checked: can each program open CSI ProMax\?(?<rest>[\s\S]*?)(\r?\n\r?\n|$)')
Check (-not ($output -match 'could not be tried|not tried') -and (-not $checked.Success -or $checked.Groups['rest'].Value -match '\s(NO|not yet):')) 'when it is done, the installer checks that each program can open CSI ProMax, and shows only real problems'
Check ((Text $none) -eq "[Graphics]`r`n Colour=1`r`n" -and $output -match 'ETABS 19\s+Its settings file was not understood') 'a file with no plug-in list is left alone'
$bytes = [IO.File]::ReadAllBytes($utf16)
Check ($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE -and [Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length - 2).Contains(" PlugInPath=$plugin`r`n")) 'a UTF-16 file stays UTF-16'
Check ((Get-Content -Raw -LiteralPath (Join-Path $mine 'kept.txt')).Trim() -eq 'mine') 'files already in the folder are kept'
Check ($output -match 'was already in' -and $output -notmatch 'not installed') 'a second run replaces nothing'

# Again: nothing changes, and the first copy is not overwritten.
$before = Text $etabs23
$output = (& $script -Zip $zip -Folder $folder -SettingsRoot $settings -NoRuntime *>&1 | Out-String)
Check ((Text $etabs23) -eq $before -and $output -match 'ETABS 23\s+CSI ProMax is already in the Tools menu') 'running it again changes nothing'
Check ((Text ($etabs23 + '.before-csi-promax')) -eq $fresh) 'the first copy of the settings file is still the first'

# -NoPlugins leaves the settings alone.
[IO.File]::WriteAllBytes($safe22, [Text.Encoding]::GetEncoding(28591).GetBytes($fresh))
& $script -Zip $zip -Folder $folder -SettingsRoot $settings -NoRuntime -NoPlugins *> $null
Check ((Text $safe22) -eq $fresh) '-NoPlugins: no settings file is changed'

# A newer package while a file is in use: nothing is replaced.
Set-Content -LiteralPath (Join-Path $package.FullName 'dist' 'CsiProMax.dll') -Value 'second CsiProMax.dll' -NoNewline
Set-Content -LiteralPath (Join-Path $package.FullName 'dist' 'CsiProMaxPlugin.dll') -Value 'second CsiProMaxPlugin.dll' -NoNewline
$zip2 = Join-Path $root 'CsiProMax-9.8.8.zip'
Compress-Archive -Path (Join-Path $package.FullName '*') -DestinationPath $zip2
$held = [IO.File]::Open($plugin, 'Open', 'Read', 'None')
try { $output = (& $script -Zip $zip2 -Folder $folder -SettingsRoot $settings -NoRuntime *>&1 | Out-String) } finally { $held.Close() }
Check ($output -match 'is in use' -and $output -match 'Nothing was changed') 'a file in use stops the install with a plain message'
Check ((Get-Content -Raw -LiteralPath (Join-Path $folder 'CsiProMax.dll')) -eq 'first CsiProMax.dll') 'and no other file was replaced'
$output = (& $script -Zip $zip2 -Folder $folder -SettingsRoot $settings -NoRuntime *>&1 | Out-String)
Check ((Get-Content -Raw -LiteralPath $plugin) -eq 'second CsiProMaxPlugin.dll' -and $output -match '2 files added or replaced') 'once it is free, only the changed files are replaced'

# With the AI chat or without it: -NoAI writes "off", a plain run keeps the choice, -WithAI turns it on again.
$aiFile = Join-Path $root 'state' 'ai.txt'
$output = (& $script -Zip $zip2 -Folder $folder -SettingsRoot $settings -NoRuntime -NoPlugins -AiFile $aiFile *>&1 | Out-String)
Check (-not (Test-Path -LiteralPath $aiFile) -and $output -match 'The AI chat is on') 'a first install has the AI chat on, and writes no choice'
$output = (& $script -Zip $zip2 -Folder $folder -SettingsRoot $settings -NoRuntime -NoPlugins -AiFile $aiFile -NoAI *>&1 | Out-String)
Check ((Get-Content -Raw -LiteralPath $aiFile) -eq 'off' -and $output -match 'The AI chat is off') '-NoAI: the choice "off" is kept for every version to read'
Check ($output -match 'Without the AI chat' -and $output -notmatch 'Claude Code on your own Claude plan') '-NoAI: the next steps say nothing of Claude Code'
Check ((Get-Content -Raw -LiteralPath $plugin) -eq 'second CsiProMaxPlugin.dll' -and $output -notmatch 'not installed') '-NoAI: the same package is installed'
$output = (& $script -Zip $zip2 -Folder $folder -SettingsRoot $settings -NoRuntime -NoPlugins -AiFile $aiFile *>&1 | Out-String)
Check ((Get-Content -Raw -LiteralPath $aiFile) -eq 'off' -and $output -match 'The AI chat is off') 'a plain run again keeps the choice (an update does not turn the chat back on)'
$output = (& $script -Zip $zip2 -Folder $folder -SettingsRoot $settings -NoRuntime -NoPlugins -AiFile $aiFile -WithAI *>&1 | Out-String)
Check ((Get-Content -Raw -LiteralPath $aiFile) -eq 'on' -and $output -match 'The AI chat is on') '-WithAI turns the chat on again'
$output = (& $script -Zip $zip2 -Folder $folder -SettingsRoot $settings -NoRuntime -NoPlugins -AiFile $aiFile -WithAI -NoAI *>&1 | Out-String)
Check ($output -match 'not installed' -and $output -match 'not both') '-NoAI and -WithAI together are refused'
# install-no-ai.ps1 is the same install with -NoAI: it downloads install.ps1, so here only its text is checked.
$noAi = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..' 'install-no-ai.ps1')
Check ($noAi -match 'raw\.githubusercontent\.com/ahisham92/etabs-promax/main/install\.ps1' -and $noAi -match '-NoAI' -and $null -ne [scriptblock]::Create($noAi)) 'install-no-ai.ps1 runs install.ps1 with -NoAI'
Check (-not ($noAi -match '[^\x09\x0A\x0D\x20-\x7E]')) 'install-no-ai.ps1 is plain ASCII'

# What is not a package says so.
$wrong = Join-Path $root 'CsiProMax-0.0.0.zip'
Compress-Archive -Path (Join-Path $package.FullName 'INSTALL.txt') -DestinationPath $wrong
$output = (& $script -Zip $wrong -Folder (Join-Path $root 'nowhere') -SettingsRoot $settings -NoRuntime *>&1 | Out-String)
Check ($output -match 'not installed' -and -not (Test-Path -LiteralPath (Join-Path $root 'nowhere'))) 'a zip that is not CSI ProMax is refused'

Check (-not ((Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..' 'install.ps1')) -match '[^\x09\x0A\x0D\x20-\x7E]')) 'install.ps1 is plain ASCII'
Check (@(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory -Filter 'CsiProMax-install-*').Count -eq 0) 'the working folder is removed'

Remove-Item -LiteralPath $root -Recurse -Force
if ($failed -gt 0) { Write-Host "$failed failed" -ForegroundColor Red; exit 1 }
Write-Host 'All install tests passed.'
