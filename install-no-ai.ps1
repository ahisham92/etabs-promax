# CSI ProMax without the AI chat: one-line install for Windows. No .exe of ours and no administrator rights.
#
#   irm https://raw.githubusercontent.com/ahisham92/etabs-promax/main/install-no-ai.ps1 | iex
#
# The same install as install.ps1 (the same files, the same Tools menu entry in ETABS, SAFE and SAP2000), with the AI
# chat off: the Modeler, modelling by hand in the 3D view, review packs and the Viewer, with no Claude Code used or
# installed. The chat can be turned on later in the CSI ProMax window (CSI ProMax menu, Turn the AI chat on), or by
# running the full install line with -WithAI. Updates come in the same way, without closing the CSI program.
#
# This file is plain ASCII on purpose, and works in Windows PowerShell 5.1 and PowerShell 7.

& {
    $ProgressPreference = 'SilentlyContinue'
    $text = $null
    foreach ($address in @(
            'https://raw.githubusercontent.com/ahisham92/etabs-promax/main/install.ps1',
            'https://github.com/ahisham92/etabs-promax/releases/latest/download/install.ps1')) {
        try {
            $content = (Invoke-WebRequest -UseBasicParsing -Uri $address).Content
            if ($content -is [byte[]]) { $content = [Text.Encoding]::UTF8.GetString($content) }
            $text = ([string]$content).TrimStart([char]0xFEFF)
            if ($text -match 'function Install-CsiProMax') { break }
            $text = $null
        }
        catch { $text = $null }
    }
    if (-not $text) {
        Write-Host ''
        Write-Host 'CSI ProMax was not installed: the install script could not be downloaded from GitHub.' -ForegroundColor Red
        Write-Host ''
        return
    }
    & ([scriptblock]::Create($text)) -NoAI @args
} @args
