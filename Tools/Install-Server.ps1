<#
.SYNOPSIS
  Installs (or updates) AOI Line Server on this PC as a Windows service.
  Run in an Administrator PowerShell from the Server package folder.

  - Copies the program to the install folder (settings, history and logs there are kept on update)
  - Creates the Windows service "AoiLineServer" (starts with Windows, restarts if it crashes)
  - Opens the port in Windows Firewall so the line PCs can connect
  - Puts an "AOI - All lines" shortcut on the desktop

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Install-Server.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Install-Server.ps1 -Port 5000 -InstallDir "C:\AoiLine\Server"
#>
param(
    [string]$InstallDir = "C:\AoiLine\Server",
    [int]$Port = 5000,
    # Copy files and write settings only - no service, no firewall (for testing without admin rights).
    [switch]$NoService,
    [string]$ShortcutFolder = [Environment]::GetFolderPath("CommonDesktopDirectory")
)
$ErrorActionPreference = "Stop"
$ServiceName = "AoiLineServer"
$ExeName = "AoiLine.Server.exe"
$src = $PSScriptRoot

function Test-Admin {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Write-Utf8($path, $text) { [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false))) }

if (-not $NoService -and -not (Test-Admin)) {
    throw "Please run this script in PowerShell opened with 'Run as administrator'."
}
if (-not (Test-Path (Join-Path $src $ExeName))) {
    throw "$ExeName not found next to this script. Run it from the Server package folder (Release\Server)."
}

# 1. Stop the running service (update)
$svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($svc -and -not $NoService -and $svc.Status -ne "Stopped") {
    Write-Host "Stopping existing service..."
    Stop-Service $ServiceName -Force
    (Get-Service $ServiceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
}

# 2. Copy program files (settings.json, Lines\ and Logs\ in the install folder are not touched)
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
if ([IO.Path]::GetFullPath($src).TrimEnd('\') -ne $InstallDir.TrimEnd('\')) {
    Write-Host "Copying program to $InstallDir ..."
    Get-ChildItem $src | Where-Object { $_.Name -notin "settings.json", "Lines", "Logs" } |
        Copy-Item -Destination $InstallDir -Recurse -Force
}

# 3. Port in settings.json (created with defaults on first install)
$settingsPath = Join-Path $InstallDir "settings.json"
if (-not (Test-Path $settingsPath)) {
    Write-Utf8 $settingsPath ("{`r`n  `"Port`": $Port`r`n}`r`n")
    Write-Host "Created settings.json (port $Port). Configure the rest on the Settings web page."
} elseif ($PSBoundParameters.ContainsKey("Port")) {
    $json = Get-Content $settingsPath -Raw
    $json = [regex]::Replace($json, '"Port"\s*:\s*\d+', "`"Port`": $Port")
    Write-Utf8 $settingsPath $json
    Write-Host "Port in settings.json set to $Port."
} else {
    $m = [regex]::Match((Get-Content $settingsPath -Raw), '"Port"\s*:\s*(\d+)')
    if ($m.Success) { $Port = [int]$m.Groups[1].Value }
}

$exe = Join-Path $InstallDir $ExeName
if (-not $NoService) {
    # 4. Windows service
    if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
        Write-Host "Creating service $ServiceName ..."
        New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName "AOI Line Server" `
            -Description "Receives AOI result files from the line agents and sends them to the API. Web pages on port $Port." `
            -StartupType Automatic | Out-Null
    }
    # Restart automatically if it stops unexpectedly
    sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null

    # 5. Firewall: allow line PCs to connect (all network profiles - factory networks are often "Public")
    $ruleName = "AOI Line Server (TCP $Port)"
    Get-NetFirewallRule -DisplayName "AOI Line Server*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Any | Out-Null
    Write-Host "Firewall rule '$ruleName' created."

    Start-Service $ServiceName
    Write-Host "Service started. Waiting for the web server..."
    $ok = $false
    for ($i = 0; $i -lt 20 -and -not $ok; $i++) {
        Start-Sleep -Milliseconds 500
        try { Invoke-RestMethod "http://localhost:$Port/api/info" -TimeoutSec 2 | Out-Null; $ok = $true } catch { }
    }
    if ($ok) { Write-Host "Server is running." -ForegroundColor Green }
    else { Write-Warning "Server did not answer yet. Check $InstallDir\Logs and the Windows service '$ServiceName'." }
}

# 6. Desktop shortcut
if ($ShortcutFolder -and (Test-Path $ShortcutFolder)) {
    Write-Utf8 (Join-Path $ShortcutFolder "AOI - All lines.url") "[InternetShortcut]`r`nURL=http://localhost:$Port/`r`n"
    Write-Host "Shortcut 'AOI - All lines' created in $ShortcutFolder"
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Web pages on this PC : http://localhost:$Port/"
Write-Host "  Address for agents   : http://$($env:COMPUTERNAME):$Port   (use this as -ServerUrl in Install-Agent.ps1)"
Write-Host "  Settings, history    : $InstallDir"
