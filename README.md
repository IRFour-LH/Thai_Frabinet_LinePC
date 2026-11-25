# AOI Uploader

WPF program (.NET 9, Windows 10/11) that watches a folder for AOI result files (`*.TST`),
reads them using a configurable column mapping, and sends the data to a configurable API.

## Install on the line PC
1. Copy the folder `Release\AoiUploader` to the line PC (e.g. `C:\AoiUploader`). No .NET install needed.
2. Run `AoiUploader.exe` → **Settings**.
3. **General**: choose the watch folder. Set *Ignore files older than (days)* to `1` if the folder has old history you do not want to send.
4. **Column Mapping**: *Detect from sample file* → rename the **API Field Name** to what your API expects → **Preview**.
5. **API**: URL, method, headers (API key / `Authorization: Bearer ...`), body template → **Preview body** → **Test send** → tick **Enable sending to API** → **Save**.

## Daily use (operator)
1. Type or scan the **Work Order** → Enter → type or scan the **Product Name** → Enter (= **Start**).
   Start stays disabled until every required (*) box is filled.
2. Only AOI files **created after Start** whose Product Name matches are sent. The "This run" counters show the totals.
3. A file for a different Product Name is **not sent** – a red banner + sound warns the operator. That file stays unsent.
4. To change Product Name / Work Order: **Stop** → enter the new value → **Start**. Files that arrive while stopped are not sent.
   If some files of the run failed to send and are still retrying, **Stop** warns and lists them first.

Settings > **Operator Input** controls this: the label (default **Product Name**), how the value is
matched (file name before `_`, recipe line, or a data field from Column Mapping), and the options above.
The entered value is available in the body template as `{{InputValue}}`.

*Additional inputs* (same tab) are extra boxes the operator fills before Start – by default **Work Order**
(key `WorkOrder`, required). They are not used to match files; they are sent to the API as `{{WorkOrder}}`
(or `{{Input.WorkOrder}}`) and written to the daily processed lists. Add more rows for e.g. Operator ID or Line.

## Files next to the exe
| File | Purpose |
|---|---|
| `settings.json` | All settings (edited through the Settings window) |
| `Processed\yyyy-MM-dd.txt` | One list per day of files successfully sent (with Recipe, Work Order, qty). Lists older than *Delete lists older than (days)* are deleted automatically (0 = keep forever). **Only these list files are deleted – AOI result files are never touched.** |
| `processed.txt` | Old single list from earlier versions – still read so those files are not re-sent, never written or deleted |
| `Logs\yyyy-MM-dd.log` | Daily log |

## Behaviour
- Original AOI files are **never moved, changed or locked**.
- A file is written to `processed.txt` **only after the API returns success** (HTTP 2xx, plus optional "response contains" text).
- Failed files are retried automatically after *Retry failed file after (s)*.
- When the API is disabled, files are parsed and shown only (nothing is marked as sent).
- PCB qty (Settings > General > *PCB qty calculation*):
  - **By row** – every data row = 1 PCB.
  - **By distinct value of field** (e.g. `BarcodeID`) – rows with the same value = 1 PCB.
    That PCB is NG if any of its rows is NG. Rows with an empty value are each counted as 1 PCB.
- G/NG is counted from the *Result field* using *Good values*, in the same unit as PCB qty.
- Body template: `{{PcbQty}}` follows the chosen mode; `{{RowCount}}` is always the number of rows.
- Double-click a row in *Recent files* to see the exact payload and API response.

## Build
```
cd AoiUploader
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o ..\Release\AoiUploader
```
