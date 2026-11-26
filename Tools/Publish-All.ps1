<#
.SYNOPSIS
  Builds the install packages:
    Release\Server  - Line PC Server (copy to the server PC, run Install-Server.ps1)
    Release\Agent   - Line PC Agent  (copy to each line PC, run Install-Agent.ps1)
  Self-contained: the target PCs do not need .NET installed.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Tools\Publish-All.ps1
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot "..\Release")
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Output = [IO.Path]::GetFullPath($Output)

$packages = @(
    @{ Name = "Server"; Project = "src\AoiLine.Server\AoiLine.Server.csproj"; Scripts = @("Install-Server.ps1", "Uninstall-Server.ps1") },
    @{ Name = "Agent";  Project = "src\AoiLine.Agent\AoiLine.Agent.csproj";   Scripts = @("Install-Agent.ps1", "Uninstall-Agent.ps1") }
)

foreach ($p in $packages) {
    $out = Join-Path $Output $p.Name
    Write-Host "Publishing $($p.Name) -> $out" -ForegroundColor Cyan
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish (Join-Path $root $p.Project) -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Publish of $($p.Name) failed" }
    foreach ($s in $p.Scripts) { Copy-Item (Join-Path $PSScriptRoot $s) $out -Force }
}

Write-Host ""
Write-Host "Done. Packages:" -ForegroundColor Green
Get-ChildItem $Output -Directory | Where-Object { $_.Name -in "Server", "Agent" } | ForEach-Object {
    $size = (Get-ChildItem $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("  {0,-8} {1,6:N0} MB  {2}" -f $_.Name, $size, $_.FullName)
}
