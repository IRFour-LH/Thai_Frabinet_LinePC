<#
.SYNOPSIS
  Removes the AOI Line Server Windows service, its firewall rule and desktop shortcut.
  The install folder (settings, history, logs) is kept unless -RemoveFiles is given.
  Run in an Administrator PowerShell.
#>
param(
    [string]$InstallDir = "C:\AoiLine\Server",
    [switch]$RemoveFiles,
    [string]$ShortcutFolder = [Environment]::GetFolderPath("CommonDesktopDirectory")
)
$ErrorActionPreference = "Stop"
$ServiceName = "AoiLineServer"

$svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne "Stopped") { Stop-Service $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Service $ServiceName removed."
}
Get-NetFirewallRule -DisplayName "AOI Line Server*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Remove-Item (Join-Path $ShortcutFolder "AOI - All lines.url") -ErrorAction SilentlyContinue
if ($RemoveFiles -and (Test-Path $InstallDir)) {
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "Removed $InstallDir"
} else {
    Write-Host "Kept $InstallDir (settings, history, logs). Use -RemoveFiles to delete it."
}
