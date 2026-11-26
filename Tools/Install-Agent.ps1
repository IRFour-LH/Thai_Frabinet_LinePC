<#
.SYNOPSIS
  Installs (or updates) the Line PC Agent on a line PC as a Windows service.
  Run in an Administrator PowerShell from the Agent package folder.

  - Copies the program to the install folder (agent.json and history there are kept on update)
  - Writes server address, Line ID, line name and watch folder to agent.json
  - Creates the Windows service "LinePCAgent" (starts with Windows, restarts if it crashes)
  - Desktop shortcuts: "Line PC <ID>" (operator page on the server) and "Line PC Agent" (this PC's agent page)

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Install-Agent.ps1 -ServerUrl http://LINE1-PC:5000 -LineId L3 -LineName "Line 3"
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Install-Agent.ps1 -ServerUrl http://LINE1-PC:5000 -LineId L3 -WatchFolder "E:\AOI\Result"
#>
param(
    [Parameter(Mandatory = $true)] [string]$ServerUrl,
    [Parameter(Mandatory = $true)] [string]$LineId,
    [string]$LineName = "",
    [string]$WatchFolder = "D:\AOI RESULT FILE",
    [string]$InstallDir = "C:\LinePC\Agent",
    [int]$LocalPort = 5001,
    # Copy files and write agent.json only - no service (for testing without admin rights).
    [switch]$NoService,
    [string]$ShortcutFolder = [Environment]::GetFolderPath("CommonDesktopDirectory")
)
$ErrorActionPreference = "Stop"
$ServiceName = "LinePCAgent"
$ExeName = "LinePC.Agent.exe"
$src = $PSScriptRoot

function Test-Admin {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Write-Utf8($path, $text) { [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false))) }
function Json-Escape($s) { return ($s -replace '\\', '\\' -replace '"', '\"') }

if ($LineId -notmatch '^[A-Za-z0-9_-]{1,20}$') { throw "LineId must be letters, numbers, - or _ (max 20), e.g. L3." }
if ($ServerUrl -notmatch '^https?://') { throw "ServerUrl must look like http://SERVER-PC:5000" }
if (-not $LineName) { $LineName = "Line " + ($LineId -replace '^[Ll]', '') }
if (-not $NoService -and -not (Test-Admin)) {
    throw "Please run this script in PowerShell opened with 'Run as administrator'."
}
if (-not (Test-Path (Join-Path $src $ExeName))) {
    throw "$ExeName not found next to this script. Run it from the Agent package folder (Release\Agent)."
}

# 1. Stop the running service (update)
$svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($svc -and -not $NoService -and $svc.Status -ne "Stopped") {
    Write-Host "Stopping existing service..."
    Stop-Service $ServiceName -Force
    (Get-Service $ServiceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
}

# 2. Copy program files (agent.json, Forwarded\ and Logs\ in the install folder are not touched)
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
if ([IO.Path]::GetFullPath($src).TrimEnd('\') -ne $InstallDir.TrimEnd('\')) {
    Write-Host "Copying program to $InstallDir ..."
    Get-ChildItem $src | Where-Object { $_.Name -notin "agent.json", "Forwarded", "Logs" } |
        Copy-Item -Destination $InstallDir -Recurse -Force
}

# 3. agent.json: the given values; other settings are kept if the file exists
$cfgPath = Join-Path $InstallDir "agent.json"
$values = [ordered]@{ ServerUrl = $ServerUrl.TrimEnd('/'); LineId = $LineId; LineName = $LineName; WatchFolder = $WatchFolder; LocalPort = $LocalPort }
if (Test-Path $cfgPath) {
    $json = Get-Content $cfgPath -Raw
    foreach ($k in $values.Keys) {
        $v = $values[$k]
        $rep = if ($v -is [int]) { "`"$k`": $v" } else { "`"$k`": `"$(Json-Escape $v)`"" }
        if ($json -match "`"$k`"\s*:") {
            $pattern = if ($v -is [int]) { "`"$k`"\s*:\s*\d+" } else { "`"$k`"\s*:\s*`"(?:[^`"\\]|\\.)*`"" }
            $json = [regex]::Replace($json, $pattern, { param($m) $rep })
        } else {
            $json = $json -replace '^\s*\{', "{`r`n  $rep,"
        }
    }
    Write-Utf8 $cfgPath $json
    Write-Host "agent.json updated."
} else {
    $lines = foreach ($k in $values.Keys) {
        $v = $values[$k]
        if ($v -is [int]) { "  `"$k`": $v" } else { "  `"$k`": `"$(Json-Escape $v)`"" }
    }
    Write-Utf8 $cfgPath ("{`r`n" + ($lines -join ",`r`n") + "`r`n}`r`n")
    Write-Host "agent.json created."
}
if (-not (Test-Path $WatchFolder)) { Write-Warning "Watch folder '$WatchFolder' does not exist (yet). The agent keeps checking for it." }

$exe = Join-Path $InstallDir $ExeName
if (-not $NoService) {
    # 4. Windows service
    if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
        Write-Host "Creating service $ServiceName ..."
        New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName "Line PC Agent" `
            -Description "Watches the AOI result folder and forwards new files to the Line PC Server ($ServerUrl), line $LineId." `
            -StartupType Automatic | Out-Null
    }
    sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
    Start-Service $ServiceName
    Write-Host "Service started." -ForegroundColor Green
}

# 5. Check the server can be reached from this PC
try {
    $info = Invoke-RestMethod "$($ServerUrl.TrimEnd('/'))/api/info" -TimeoutSec 5
    Write-Host "Server reachable: $($info.machine), version $($info.version)" -ForegroundColor Green
} catch {
    Write-Warning "Cannot reach the server at $ServerUrl from this PC. Check the address, that the server is running, and the firewall on the server PC."
}

# 6. Desktop shortcuts
if ($ShortcutFolder -and (Test-Path $ShortcutFolder)) {
    $lineUrl = "$($ServerUrl.TrimEnd('/'))/line/$LineId"
    Write-Utf8 (Join-Path $ShortcutFolder "Line PC $LineId.url") "[InternetShortcut]`r`nURL=$lineUrl`r`n"
    Write-Utf8 (Join-Path $ShortcutFolder "Line PC Agent.url") "[InternetShortcut]`r`nURL=http://localhost:$LocalPort/`r`n"
    Write-Host "Shortcuts 'Line PC $LineId' and 'Line PC Agent' created in $ShortcutFolder"
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Operator page : $($ServerUrl.TrimEnd('/'))/line/$LineId"
Write-Host "  Agent page    : http://localhost:$LocalPort/"
Write-Host "  Watch folder  : $WatchFolder"
