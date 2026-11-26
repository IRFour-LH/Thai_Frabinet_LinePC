<#
.SYNOPSIS
  Removes the Line PC Agent Windows service and its desktop shortcuts.
  The install folder (agent.json, forwarded lists, logs) is kept unless -RemoveFiles is given.
  AOI result files are never touched. Run in an Administrator PowerShell.
#>
param(
    [string]$InstallDir = "C:\LinePC\Agent",
    [switch]$RemoveFiles,
    [string]$ShortcutFolder = [Environment]::GetFolderPath("CommonDesktopDirectory")
)
$ErrorActionPreference = "Stop"
$ServiceName = "LinePCAgent"

$svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne "Stopped") { Stop-Service $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Service $ServiceName removed."
}
Get-ChildItem $ShortcutFolder -Filter "Line PC *.url" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne "Line PC - All lines.url" } | Remove-Item   # keep the server shortcut on the server PC
Remove-Item (Join-Path $ShortcutFolder "Line PC Agent.url") -ErrorAction SilentlyContinue
if ($RemoveFiles -and (Test-Path $InstallDir)) {
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "Removed $InstallDir"
} else {
    Write-Host "Kept $InstallDir. Use -RemoveFiles to delete it."
}
