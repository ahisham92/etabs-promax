# Measures where ETABS 23 spends the time when a review pack reads its tables (2026-11-27, thread "Make the results
# pack take minutes"). Starts its OWN hidden ETABS on a COPY of a field-test scratch model, never the user's window.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File measure-etabs-tables.ps1 [-Model <path to .EDB or .e2k>]
param([string]$Model = "", [string]$Out = "")
$ErrorActionPreference = "Stop"
$etabsDir = Get-ChildItem "C:\Program Files\Computers and Structures" -Directory -Filter "ETABS 2*" | Sort-Object Name -Descending | Select-Object -First 1
if (-not $etabsDir) { throw "ETABS not found" }
Add-Type -Path (Join-Path $etabsDir.FullName "ETABSv1.dll")
if (-not $Model) {
  $roots = @("$env:LOCALAPPDATA\CsiProMax\Field test", "$env:LOCALAPPDATA\Packages\AnthropicPBC.Claude_fnn82j28hfe8t\LocalCache\Local\CsiProMax\Field test")
  $Model = ($roots | Where-Object { Test-Path $_ } | ForEach-Object { Get-ChildItem $_ -Recurse -Include *.EDB,*.e2k -File } | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if (-not $Model) { throw "no model found" }
$work = Join-Path $env:TEMP ("cpm-pack-speed-" + (Get-Date -Format "HHmmss"))
New-Item -ItemType Directory $work | Out-Null
$copy = Join-Path $work (Split-Path $Model -Leaf)
Copy-Item $Model $copy
if (-not $Out) { $Out = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "CSI ProMax\pack-speed-etabs.txt" }
New-Item -ItemType Directory (Split-Path $Out) -Force | Out-Null
"model $Model -> $copy  ETABS $($etabsDir.Name)  $(Get-Date)" | Set-Content $Out
function Say($t) { $t | Tee-Object -FilePath $Out -Append }
function Time($label, [scriptblock]$b) { $w = [Diagnostics.Stopwatch]::StartNew(); try { $r = & $b } catch { $r = "ERROR " + $_.Exception.Message }; Say ("{0,8:N0} ms  {1}  -> {2}" -f $w.ElapsedMilliseconds, $label, $r) }

# What the API offers for reading many tables at once.
Say ("cDatabaseTables: " + (([ETABSv1.cDatabaseTables].GetMethods() | ForEach-Object Name | Sort-Object -Unique) -join ", "))
Say ("cFile: " + (([ETABSv1.cFile].GetMethods() | ForEach-Object Name | Sort-Object -Unique) -join ", "))
try { Say ("eFileTypeIO: " + ([enum]::GetNames([ETABSv1.eFileTypeIO]) -join ", ")) } catch { Say "no eFileTypeIO" }
foreach ($mm in [ETABSv1.cDatabaseTables].GetMethods() | Where-Object { $_.Name -match "CSV|XML|Export|Excel|Access" }) { Say ("  " + $mm.ToString()) }
foreach ($mm in [ETABSv1.cFile].GetMethods() | Where-Object { $_.Name -match "Export" }) { Say ("  " + $mm.ToString()) }
$helper = New-Object ETABSv1.Helper
$app = $helper.CreateObjectProgID("CSI.ETABS.API.ETABSObject")
$app.ApplicationStart() | Out-Null
try { $app.Hide() | Out-Null } catch {}
$m = $app.SapModel
try {
  Time "open model" { $m.File.OpenFile($copy) }
  Time "run analysis" { $m.Analyze.RunAnalysis() }
  [string[]]$cases = $null; $n = 0
  $m.LoadCases.GetNameList([ref]$n, [ref]$cases, [Enum]::ToObject([ETABSv1.eLoadCaseType], 0)) | Out-Null
  Time "SetLoadCasesSelectedForDisplay ($n)" { $m.DatabaseTables.SetLoadCasesSelectedForDisplay([ref]$cases) }
  1..5 | ForEach-Object { Time "LoadPatterns.GetNameList (baseline call)" { $k = 0; [string[]]$x = $null; $m.LoadPatterns.GetNameList([ref]$k, [ref]$x) } }
  Time "GetAllTables" { $k=0; [string[]]$a=$null; [string[]]$b=$null; [int[]]$c=$null; [bool[]]$d=$null; $m.DatabaseTables.GetAllTables([ref]$k,[ref]$a,[ref]$b,[ref]$c,[ref]$d) }
  Time "GetAvailableTables" { $k=0; [string[]]$a=$null; [string[]]$b=$null; [int[]]$c=$null; $m.DatabaseTables.GetAvailableTables([ref]$k,[ref]$a,[ref]$b,[ref]$c) }
  $tables = @("Load Pattern Definitions", "Story Definitions", "Base Reactions", "Story Drifts", "Joint Displacements", "Element Forces - Beams", "Element Forces - Area Shells", "Joint Assignments - Springs")
  foreach ($t in $tables) {
    foreach ($i in 1..3) {
      Time "Array    '$t' #$i" { [string[]]$f=$null; [string[]]$fl=$null; [string[]]$d=$null; $v=0; $r=0; $ret = $m.DatabaseTables.GetTableForDisplayArray($t, [ref]$fl, "", [ref]$v, [ref]$f, [ref]$r, [ref]$d); "ret $ret rows $r" }
    }
    Time "CSVString '$t'" { [string[]]$fl=$null; $v=0; $s=""; $ret = $m.DatabaseTables.GetTableForDisplayCSVString($t, [ref]$fl, "", [ref]$v, [ref]$s, ","); "ret $ret chars $($s.Length)" }
    $csv = Join-Path $work (($t -replace '[^A-Za-z0-9]', '_') + ".csv")
    Time "CSVFile   '$t'" { [string[]]$fl=$null; $v=0; $ret = $m.DatabaseTables.GetTableForDisplayCSVFile($t, [ref]$fl, "", [ref]$v, $csv, ","); "ret $ret" }
  }
  # Results through cAnalysisResults (one call for every object), for comparison with the tables.
  $m.Results.Setup.DeselectAllCasesAndCombosForOutput() | Out-Null
  foreach ($c in $cases) { $m.Results.Setup.SetCaseSelectedForOutput($c) | Out-Null }
  Time "Results.JointDispl All" { $k=0; [string[]]$o=$null;[string[]]$e=$null;[string[]]$lc=$null;[string[]]$st=$null;[double[]]$sn=$null;[double[]]$u1=$null;[double[]]$u2=$null;[double[]]$u3=$null;[double[]]$r1=$null;[double[]]$r2=$null;[double[]]$r3=$null
    $ret = $m.Results.JointDispl("All", [ETABSv1.eItemTypeElm]::GroupElm, [ref]$k,[ref]$o,[ref]$e,[ref]$lc,[ref]$st,[ref]$sn,[ref]$u1,[ref]$u2,[ref]$u3,[ref]$r1,[ref]$r2,[ref]$r3); "ret $ret n $k" }
  Time "Results.FrameForce All" { $k=0; [string[]]$o=$null;[double[]]$os=$null;[string[]]$e=$null;[double[]]$es=$null;[string[]]$lc=$null;[string[]]$st=$null;[double[]]$sn=$null;[double[]]$p=$null;[double[]]$v2=$null;[double[]]$v3=$null;[double[]]$t=$null;[double[]]$m2=$null;[double[]]$m3=$null
    $ret = $m.Results.FrameForce("All", [ETABSv1.eItemTypeElm]::GroupElm, [ref]$k,[ref]$o,[ref]$os,[ref]$e,[ref]$es,[ref]$lc,[ref]$st,[ref]$sn,[ref]$p,[ref]$v2,[ref]$v3,[ref]$t,[ref]$m2,[ref]$m3); "ret $ret n $k" }
  # The same table again after a pause: does the floor come from a timer in ETABS's message loop?
  Start-Sleep -Seconds 3
  Time "Array 'Load Pattern Definitions' after 3 s idle" { [string[]]$f=$null; [string[]]$fl=$null; [string[]]$d=$null; $v=0; $r=0; $m.DatabaseTables.GetTableForDisplayArray("Load Pattern Definitions", [ref]$fl, "", [ref]$v, [ref]$f, [ref]$r, [ref]$d) }
  # Every table in one call, if the program can export them to a file.
  foreach ($kind in @("DBTablesXML", "DBTablesAccess", "DBTablesExcel", "DBTablesText", "TextFile")) {
    try { $ft = [ETABSv1.eFileTypeIO]$kind } catch { continue }
    $ext = @{ DBTablesXML = ".xml"; DBTablesAccess = ".mdb"; DBTablesExcel = ".xlsx"; DBTablesText = ".txt"; TextFile = ".e2k" }[$kind]
    $file = Join-Path $work ("all" + $ext)
    Time "File.ExportFile $kind" { $ret = $m.File.ExportFile($file, $ft); "ret $ret bytes " + $(if (Test-Path $file) { (Get-Item $file).Length } else { "no file" }) }
  }
  # Only some fields of the springs table.
  Time "GetAllFieldsInTable Springs" { $v=0;$k=0;[string[]]$a=$null;[string[]]$b=$null;[string[]]$c=$null;[string[]]$d=$null;[string[]]$e=$null;[bool[]]$g=$null; $m.DatabaseTables.GetAllFieldsInTable("Joint Assignments - Springs",[ref]$v,[ref]$k,[ref]$a,[ref]$b,[ref]$c,[ref]$d,[ref]$e,[ref]$g) }
} catch { Say ("ERROR: " + $_.Exception.Message + " at " + $_.InvocationInfo.PositionMessage) }
finally {
  try { $app.ApplicationExit($false) | Out-Null } catch {}
  Say "done $(Get-Date)"
}
