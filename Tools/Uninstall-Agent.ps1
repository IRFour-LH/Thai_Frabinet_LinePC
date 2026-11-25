<#
.SYNOPSIS
  Removes the AOI Line Agent Windows service and its desktop shortcuts.
  The install folder (agent.json, forwarded lists, logs) is kept unless -RemoveFiles is given.
  AOI result files are never touched. Run in an Administrator PowerShell.
#>
param(
    [string]$InstallDir = "C:\AoiLine\Agent",
    [switch]$RemoveFiles,
    [string]$ShortcutFolder = [Environment]::GetFolderPath("CommonDesktopDirectory")
)
$ErrorActionPreference = "Stop"
$ServiceName = "AoiLineAgent"

$svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne "Stopped") { Stop-Service $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Service $ServiceName removed."
}
Get-ChildItem $ShortcutFolder -Filter "AOI Line *.url" -ErrorAction SilentlyContinue | Remove-Item
Remove-Item (Join-Path $ShortcutFolder "AOI Agent.url") -ErrorAction SilentlyContinue
if ($RemoveFiles -and (Test-Path $InstallDir)) {
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "Removed $InstallDir"
} else {
    Write-Host "Kept $InstallDir. Use -RemoveFiles to delete it."
}
