# FormatClipboard.ps1
# Formats the text currently in the clipboard using TsqlFormatter.
#
# Usage in SSMS External Tools:
#   Option A (bat wrapper — recommended):
#     Command:   C:\Tools\TsqlFormatter\FormatClipboard.bat
#     Arguments: (empty)
#
#   Option B (direct):
#     Command:   powershell.exe
#     Arguments: -ExecutionPolicy Bypass -NonInteractive -File "C:\Tools\TsqlFormatter\FormatClipboard.ps1"

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = "Stop"

# Path to the formatter — same folder as this script
$dir       = Split-Path -Parent $MyInvocation.MyCommand.Definition
$formatter = Join-Path $dir "TsqlFormatter.exe"

# Fallback: look for published single-file exe
if (-not (Test-Path $formatter))
{
    $formatter = Join-Path $dir "publish\TsqlFormatter.exe"
}

if (-not (Test-Path $formatter))
{
    [System.Windows.Forms.MessageBox]::Show(
        "TsqlFormatter.exe not found.`nExpected: $formatter",
        "TsqlFormatter", "OK", "Error") | Out-Null
    exit 1
}

Add-Type -AssemblyName System.Windows.Forms

$text = [System.Windows.Forms.Clipboard]::GetText()

if ([string]::IsNullOrWhiteSpace($text))
{
    Write-Host "Clipboard is empty — copy your SQL selection first."
    exit 0
}

$formatted = $text | & $formatter --stdin 2>&1

if ($LASTEXITCODE -eq 0)
{
    [System.Windows.Forms.Clipboard]::SetText($formatted)
    Write-Host "Done — paste the result with Ctrl+V"
}
else
{
    Write-Error "Formatter error (exit code $LASTEXITCODE):`n$formatted"
}
