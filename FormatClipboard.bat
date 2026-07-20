@echo off
REM FormatClipboard.bat — wrapper that bypasses PowerShell execution policy
REM Place this file next to FormatClipboard.ps1 and TsqlFormatter.exe
REM In SSMS External Tools: Command = full path to this .bat file, Arguments = empty

powershell.exe -ExecutionPolicy Bypass -NonInteractive -File "%~dp0FormatClipboard.ps1"
