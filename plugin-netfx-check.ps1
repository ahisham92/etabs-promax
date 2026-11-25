# Read-only: does the SAP2000 plug-in bind to SAP2000 26's SAP2000v1.dll on .NET Framework (Windows PowerShell)?
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$api = 'C:\Program Files\Computers and Structures\SAP2000 26\SAP2000v1.dll'
try {
  Add-Type -Path $api
  $asm = [Reflection.Assembly]::LoadFrom((Join-Path $here 'CSI ProMax\CsiProMaxSapPlugin.dll'))
  $t = $asm.GetType('CsiProMaxSapPlugin.cPlugin', $true)
  "is cPluginContract on .NET Framework: " + [SAP2000v1.cPluginContract].IsAssignableFrom($t)
  $o = [Activator]::CreateInstance($t); $text = ''; [void]$o.Info([ref]$text); "Info: $text"
} catch { "FAIL: " + $_.Exception.GetBaseException().Message }
