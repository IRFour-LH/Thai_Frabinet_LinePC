# Line PC Server

Collects AOI result files (`*.TST`) from all production lines and sends them to a configurable API,
with a web page per line for the operator (Work Order + Product Name, Start / Stop).

```
 LINE PC 1 (also the SERVER)              LINE PC 2 ... LINE PC 10
 AOI -> D:\AOI RESULT FILE                AOI -> D:\AOI RESULT FILE
          |                                        |
   Line PC Agent (L1)                      Line PC Agent (L2...L10)
          |  forwards new files                    |
          v                                        |
   Line PC Server  <------------------------------+
     - one run per line (Work Order, Product Name)
     - Product Name check, PCB qty, G/NG
     - sends to the API  ------------------------------>  API
     - web pages:  /line/L3   /   /settings
```

* **Server** (`AoiLine.Server`) - Windows service on one PC. Web pages, settings, sending to the API.
* **Agent** (`AoiLine.Agent`) - Windows service on every line PC. Watches the local AOI folder and forwards new
  files to the server. AOI files are only read - never moved, changed or deleted.
* Line PCs need only a browser for the operator page. No .NET installation is needed on any PC.

The previous single-PC desktop version is kept in git as tag `v1-single-pc`.

---

## Install

### 1. Build the packages (on the development PC)
```powershell
powershell -ExecutionPolicy Bypass -File .\Tools\Publish-All.ps1
```
Creates `Release\Server` and `Release\Agent` (about 96 MB each, self-contained).

### 2. Server PC (one of the line PCs - must stay switched on)
Copy `Release\Server` to the PC, open **PowerShell as Administrator** in that folder:
```powershell
powershell -ExecutionPolicy Bypass -File .\Install-Server.ps1
```
* Installs to `C:\LinePC\Server`, creates service **LinePCServer** (auto start, restarts on failure)
* Opens TCP port 5000 in Windows Firewall, creates desktop shortcut *Line PC - All lines*
* Prints the address the agents must use, e.g. `http://LINE1-PC:5000`
* Options: `-Port 5000`, `-InstallDir C:\LinePC\Server`

Then open `http://localhost:5000/settings` and set up Column mapping and API (see below).

### 3. Every line PC (including the server PC if it is also a line)
Copy `Release\Agent` to the PC, open **PowerShell as Administrator** in that folder:
```powershell
powershell -ExecutionPolicy Bypass -File .\Install-Agent.ps1 -ServerUrl http://LINE1-PC:5000 -LineId L3 -LineName "Line 3"
```
* Installs to `C:\LinePC\Agent`, creates service **LinePCAgent**, writes `agent.json`
* Desktop shortcuts: **Line PC L3** (operator page) and **Line PC Agent** (agent status/settings, `http://localhost:5001`)
* Options: `-WatchFolder "D:\AOI RESULT FILE"` (default), `-LocalPort 5001`, `-InstallDir`
* The line appears on the server automatically within a few seconds.

**Update** = run the same install script from a newer package. Settings, history and logs are kept.
**Remove** = `Uninstall-Server.ps1` / `Uninstall-Agent.ps1` (add `-RemoveFiles` to also delete the install folder).

---

## Daily use (operator, page `http://SERVER:5000/line/L3`)
1. Scan / type **Work Order** -> Enter -> scan / type **Product Name** -> Enter (= **Start**).
   Start stays disabled until every required (*) box is filled.
2. Only AOI files **created after Start** whose Product Name matches are sent. Counters update live.
3. A file with another Product Name is **not sent**: red banner + sound on that line's page. Check the value entered.
4. To change Product Name / Work Order: **Stop** -> enter new values -> **Start**. Files arriving while stopped are not sent.
   If files are still waiting (API or network problem), Stop lists them and asks first.

Supervisor: `http://SERVER:5000/` shows all lines, their status, PCB qty, G/NG and warnings.

---

## Settings (`http://SERVER:5000/settings`) - same for all lines
| Tab | What |
|---|---|
| Lines | Rename / reorder / remove lines (agent PC and watch folder shown) |
| Operator input | Label of the main input (default **Product Name**), how it is matched (file name before `_`, recipe line, or a data field), only files after Start, warning, additional inputs (default **Work Order**, key `WorkOrder`) |
| File format & qty | Recipe / header / data line numbers, delimiter, date formats, result field and good values, **PCB qty by row or by distinct value** (e.g. BarcodeID; NG if any row of that barcode is NG) |
| Column mapping | API field name <-> file header (column index as fallback), type, send on/off. *Detect from sample file*, *Preview* |
| API | URL, method, headers (API key / `Authorization`), timeout, retries, success check, body template, *Preview body*, *Test send* |
| Server | Port, days of history to keep, agent offline timeout |

Body template placeholders include `{{LineId}}`, `{{LineName}}`, `{{WorkOrder}}`, `{{InputValue}}` (Product Name),
`{{PcbQty}}`, `{{GoodQty}}`, `{{NgQty}}`, `{{RowCount}}`, `{{Records}}`, `{{Field.BarcodeID}}` - full list on the API tab.

Agent settings (server address, Line ID/name, watch folder, timing) are per line PC on its agent page
`http://localhost:5001` or in `C:\LinePC\Agent\agent.json`.

---

## What happens when something fails
| Problem | Result |
|---|---|
| Line PC network down / agent stopped | Agent keeps the files and forwards them when back; they are still accepted (created after Start) |
| Server PC restarts | Every line's run, counters and log are restored; agents retry |
| API down | Server retries every *Retry failed file after* seconds; Stop warns about unsent files |
| Server PC switched off | No line sends until it is back on; files wait on the line PCs |
| Line PC clock differs from server | Handled - the run start is converted to each agent's clock |

## Files
| Where | What |
|---|---|
| `C:\LinePC\Server\settings.json` | All server settings (edit on the Settings page) |
| `C:\LinePC\Server\Lines\L3\state.json` | Current run of line L3 (survives restart) |
| `C:\LinePC\Server\Lines\L3\processed\yyyy-MM-dd.txt` | Files sent to the API with line, Product Name, Work Order, qty |
| `C:\LinePC\Server\Lines\L3\archive\yyyy-MM-dd\` | Copies of received files (for Resend) |
| `C:\LinePC\Server\Logs\yyyy-MM-dd.log` | Server log (all lines) |
| `C:\LinePC\Agent\agent.json`, `Forwarded\`, `Logs\` | Agent settings, forwarded lists, log |

History older than *Keep history (days)* (server) / *Delete forwarded lists after (days)* (agent) is deleted
automatically - only these program files, never the AOI result files.

---

## Development
```
src/AoiLine.Core     shared logic: parsing, mapping, qty, matching, payload, API sender, daily lists
src/AoiLine.Server   ASP.NET Core server + web pages (wwwroot, no external libraries)
src/AoiLine.Agent    agent service + local status page
tests/               xUnit tests for Core (dotnet test)
Tools/               Publish-All, Install/Uninstall scripts, New-DummyTst.ps1 (test files)
```
Run locally without installing:
```powershell
dotnet run --project src\AoiLine.Server -- --data C:\Temp\aoi-server --port 5000
dotnet run --project src\AoiLine.Agent  -- --config C:\Temp\aoi-agent1\agent.json
```
Create test AOI files:
`.\Tools\New-DummyTst.ps1 -Recipe B613351X1105 -Boards 7 -BoardsPerBarcode 2 -NgBoards 3 -Folder <watch folder>`
