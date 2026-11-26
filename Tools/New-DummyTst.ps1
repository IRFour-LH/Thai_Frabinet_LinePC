<#
.SYNOPSIS
  Creates a dummy AOI result file (.TST) in the same format as the AOI machine, for testing Line PC Server.

.EXAMPLE
  .\New-DummyTst.ps1 -Recipe B613351X1105
  # 4 boards, all G, one barcode per board, written to D:\AOI RESULT FILE

.EXAMPLE
  .\New-DummyTst.ps1 -Recipe B603401X1101 -Boards 7 -BoardsPerBarcode 2 -NgBoards 3,5
  # 7 boards, every 2 boards share a barcode (4 distinct barcodes), boards 3 and 5 are NG

.EXAMPLE
  .\New-DummyTst.ps1 -Recipe B613351X1105 -Count 5 -DelaySeconds 10
  # 5 files, one every 10 seconds (simulates the production line running)
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Recipe,

    [int]$Boards = 4,

    # How many boards share one barcode (1 = every board has its own barcode)
    [int]$BoardsPerBarcode = 1,

    # Board numbers that should be NG, e.g. -NgBoards 2,5
    [int[]]$NgBoards = @(),

    [string]$Folder = "D:\AOI RESULT FILE",

    [string]$Side = "Top",

    [string]$BarcodePrefix = "DUMMY",

    # Number of files to create
    [int]$Count = 1,

    # Wait between files when Count > 1
    [int]$DelaySeconds = 5
)

if (-not (Test-Path -LiteralPath $Folder)) {
    New-Item -ItemType Directory -Path $Folder | Out-Null
}

for ($f = 1; $f -le $Count; $f++) {
    $now      = Get-Date
    $testDate = $now.AddSeconds(-30).ToString("yyyyMMddHHmmss")      # test time, a bit before the file time
    $fileTime = $now.ToString("yyyyMMddHHmmssfff")                   # e.g. 20261125173012345
    $fileName = "{0}_{1}.TST" -f $Recipe, $fileTime
    $path     = Join-Path $Folder $fileName
    $serial   = $now.ToString("HHmmss")

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add($Recipe)
    $lines.Add(" Recipe              Tagset   BoardID  Status   TestDate        BarcodeID            Side     ")
    $lines.Add("|-------------------|--------|--------|--------|---------------|--------------------|--------|")

    for ($b = 1; $b -le $Boards; $b++) {
        $status  = if ($NgBoards -contains $b) { "NG" } else { "G" }
        $group   = [int][math]::Ceiling($b / [math]::Max(1, $BoardsPerBarcode))
        $barcode = "{0}{1}{2:D2}" -f $BarcodePrefix, $serial, $group
        $lines.Add(("{0},Default,{1},{2},{3},{4},{5}" -f $Recipe, $b, $status, $testDate, $barcode, $Side))
    }
    $lines.Add("")

    # Write to a temp name first, then rename, so the uploader never sees a half-written file.
    $tmp = "$path.tmp"
    [System.IO.File]::WriteAllLines($tmp, $lines, [System.Text.Encoding]::ASCII)
    Move-Item -LiteralPath $tmp -Destination $path

    $distinct = [math]::Ceiling($Boards / [math]::Max(1, $BoardsPerBarcode))
    Write-Host ("Created {0}  (boards={1}, distinct barcodes={2}, NG={3})" -f $path, $Boards, $distinct, $NgBoards.Count)

    if ($f -lt $Count) { Start-Sleep -Seconds $DelaySeconds }
}
