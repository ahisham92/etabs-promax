# The window opens on Windows. Run on Windows, after publishing the assistant into a folder:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests/window.Tests.ps1 -Folder "publish/CSI ProMax" -Picture window.png
# It starts the assistant as a tester's PC does (dotnet CsiProMax.dll) on the demo model, with no CSI program, waits
# for its window, takes a picture of the screen, and closes it. A start that fails shows a message box and writes
# errors.txt instead of the window: both are told here. Nothing else checks the window on Windows before a release.
param([Parameter(Mandatory = $true)][string]$Folder, [string]$Picture)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { Write-Host 'Windows only: skipped.'; exit 0 }
$dll = Join-Path (Resolve-Path -LiteralPath $Folder).Path 'CsiProMax.dll'
if (-not (Test-Path -LiteralPath $dll)) { Write-Host "FAIL $dll is not there." -ForegroundColor Red; exit 1 }
$notes = Join-Path $env:LOCALAPPDATA 'CsiProMax\errors.txt'
if (Test-Path -LiteralPath $notes) { Remove-Item -LiteralPath $notes -Force }
$failed = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "ok   $what" } else { Write-Host "FAIL $what" -ForegroundColor Red; $script:failed++ }
}
function Errors { if (Test-Path -LiteralPath $notes) { (Get-Content -LiteralPath $notes | Select-Object -First 60) -join "`n" } else { '' } }

# A larger screen than the runner's default, so the whole window is in the picture. Not every Windows has this command.
try { Set-DisplayResolution -Width 1920 -Height 1080 -Force | Out-Null } catch { }

# One start: with the AI chat (as most testers have it), then without it (install-no-ai.ps1, or Turn the AI chat off).
function Open-Window([string]$Label, [string[]]$Extra, [string]$Shot) {
    if (Test-Path -LiteralPath $notes) { Remove-Item -LiteralPath $notes -Force }
    $process = Start-Process -FilePath 'dotnet' -ArgumentList (@(('"' + $dll + '"'), '--demo', '--this-version', '--no-update') + $Extra) -PassThru
    $title = ''
    try {
        $deadline = (Get-Date).AddSeconds(120)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
            $process.Refresh()
            if ($process.HasExited) { break }
            if ($process.MainWindowHandle -ne [IntPtr]::Zero -and $process.MainWindowTitle) { $title = $process.MainWindowTitle; break }
        }
        Check (-not $process.HasExited) "$Label the assistant is running$(if ($process.HasExited) { ' (it ended with code ' + $process.ExitCode + ')' })"
        Check ($title -like 'CSI ProMax*') "$Label its window is open: '$title'"
        # Time to draw, and for anything that fails just after the window shows.
        Start-Sleep -Seconds 12
        $process.Refresh()
        Check (-not $process.HasExited) "$Label it is still running twelve seconds later"
        $said = Errors
        Check ($said.Length -eq 0) "$Label it wrote no error while starting and drawing $said"

        if ($Shot) {
            try {
                Add-Type -AssemblyName System.Windows.Forms
                Add-Type -AssemblyName System.Drawing
                $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
                $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
                $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
                $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
                $bitmap.Save($Shot, [System.Drawing.Imaging.ImageFormat]::Png)
                $graphics.Dispose(); $bitmap.Dispose()
                Write-Host "     picture of the screen ($($bounds.Width) x $($bounds.Height)): $Shot"
            }
            catch { Write-Host "     no picture: $($_.Exception.Message)" }
        }
    }
    finally {
        if (-not $process.HasExited) {
            try { $null = $process.CloseMainWindow() } catch { }
            if (-not $process.WaitForExit(8000)) { try { $process.Kill() } catch { } }
        }
    }
}

Open-Window '[with the AI chat]' @() $Picture
$noAiPicture = if ($Picture) { [IO.Path]::ChangeExtension($Picture, $null).TrimEnd('.') + '-no-ai.png' } else { '' }
Open-Window '[without the AI chat]' @('--no-ai') $noAiPicture

if ($failed -gt 0) { Write-Host "`n$failed failed." -ForegroundColor Red; exit 1 }
Write-Host "`nThe window opens, with the AI chat and without it."
