# ZoneKeeper for NinjaTrader 8

The free ZoneKeeper add-on sends closed trades from NinjaTrader 8 to your ZoneKeeper cabinet: the time, instrument and P/L. It pairs entry and exit fills into trades, including scale-ins, partial exits and reversals. You choose which accounts to send, then add the emotional part in ZoneKeeper.

Works with NinjaTrader 8 (8.1 or newer) on Windows. A ZoneKeeper subscription with access is needed to connect. Not made by or tied to NinjaTrader. Source is under the [MIT license](LICENSE).

**Development status:** Main `3b3404e` compiled and was tested on Windows on 2026-10-15 with NinjaTrader 8.1.8.3. The release 0.1.0 compiled ZIP is built. Clean installation and updates remain unverified. The GitHub repository is public since 2026-10-15 (read the code; install with the ZIP).

Source 0.1.1 is ready for a Windows F5 compile and ZIP export. It labels the sample code and confirms saved account choices. The live ZIP below remains 0.1.0 until the new export is checked and uploaded.

## Install the release ZIP

The Import menu text below is confirmed on NinjaTrader 8.1.8.3. Clean installation remains unverified.

1. Download the add-on. You get a `.zip` file. **Do not unzip it.** Download: [ZoneKeeper-NT8.zip](https://getzonekeeper.com/downloads/ZoneKeeper-NT8.zip).
2. In NinjaTrader: Control Center → Tools → Import → NinjaScript Add-On… → pick the `.zip` file. Then restart NinjaTrader.
3. In NinjaTrader: Control Center → New → ZoneKeeper. The ZoneKeeper window opens.
4. On [getzonekeeper.com](https://getzonekeeper.com): Cabinet → Platforms → NinjaTrader 8 → Connect NinjaTrader. Copy the code, paste it in the ZoneKeeper window, and click Connect. It works for 10 minutes, one time.

Choose accounts under **Send trades from these accounts**. New accounts are enabled by default; Playback accounts are blocked on the live site. Each PC needs its own connection code; up to five PCs may be connected. The key is kept on the PC for automatic reconnection. Use **Disconnect this PC** in the add-on or disconnect the PC in Cabinet → Platforms → NinjaTrader 8.

## What is sent and kept

Each closed trade sends `account`, `instrument`, `side`, `qty`, `entry_time`, `exit_time`, `entry_price`, `exit_price`, `pnl_cents`, `commission_cents`, `currency`, `point_value`, `exit_exec_id`, optional `entry_exec_ids`, optional `exit_name`, and `local_date`. Times are UTC milliseconds; `local_date` is the exit date in the Windows time zone. P/L is net after commission. Supported currencies are USD, CAD, EUR, GBP, AUD, CHF and NZD.

Connecting also sends the PC name and add-on version. The add-on uses a device key, not the browser login cookie. It does not import past trading days. Open positions with an unknown start are followed but are not sent. Two PCs sending the same account and exit execution ID produce one saved trade.

A trade that is open while NinjaTrader restarts is not sent. Add it later by CSV.

No open trade is restored after a restart, crash or F5 recompile. Each new fill uses NinjaTrader's signed position after the fill to decide whether it opened from flat, continued an unknown position or reversed into a new trade. On reconnect without an engine restart, a live trade is kept only when signed size, point value, currency and average price still match within floating-point noise; extra executions after its first entry make it unknown. Missing offline fills that close and reopen at the exact same average cannot be detected locally.

Waiting closed trades restored from disk still require complete unchanged fill history before upload. Missing or amended history leaves those trades unsent. An execution Update or Remove invalidates affected waiting trades; a trade already dispatched to the server cannot be changed locally.

Local files live in `Documents\NinjaTrader 8\ZoneKeeper\` (the NinjaTrader user folder may be redirected, for example to OneDrive):

| File | Purpose |
| --- | --- |
| `settings.json` | Account choices and device key protected with Windows DPAPI for the current Windows user. |
| `outbox.jsonl` | Up to 500 closed trades waiting to send, with local fill provenance. The oldest is dropped, with a log entry, when full. |
| `outbox.jsonl.invalidations` | Durable safety records that prevent corrected or removed fills from being uploaded again. |
| `open-trades.json` | Pending closed trades, fill provenance and safety metadata for the outbox handoff. `open_trades` is always empty; saved open trades are ignored on load. |
| `zonekeeper.log`, `zonekeeper.1.log` | Event log and rotated log. Codes and device keys are not logged. |
| `running.lock` | Keeps one engine active for this Windows user. |

Offline trades wait locally and retry. If the subscription loses access, the key and waiting trades stay and the add-on checks again hourly. A disconnected PC needs a new code. Waiting trades belong to whichever ZoneKeeper account the PC connects to next. A key copied to another PC or Windows user cannot be decrypted; connect that PC separately.

A closed trade is saved before it is sent. If a file cannot be saved, the add-on keeps it and tries again. If no save works, keep NinjaTrader open until it works.

See the [ZoneKeeper privacy policy](https://getzonekeeper.com/privacy/).

## Test on Windows

Roman owns all NinjaTrader actions and Sim101 orders. Use **only `https://getzonekeeper.com`** for this handoff; do not use the preview worker. Keep `base_url` unset or set to the live address. Playback remains blocked even if `allow_playback` is true. The confirmed Windows results are recorded below; other checks remain unverified.

1. **First trading check on Sim101, with `debug_fills: true`, after installing and compiling the add-on below:** from flat, sell 1 MNQ, buy 2, then sell 1. The log's `position=` must read **-1, +1, 0**: signed and after the fill. Then buy 1@X and 2@X+0.25; account.Positions AveragePrice must be the unrounded **X+0.1666…**. If either check fails, change only the `before` math or the floating-point tolerance. Turn debug logging off after the check.

2. Check Control Center → Help → About: NinjaTrader must be 8.1 or newer. Check `git --version` and `dotnet --list-sdks`; a .NET 8 SDK is needed for the pure tests. Record the exact Import/Export menu text.
3. Clone the private repository with an authorized GitHub account:

   ```powershell
   git clone https://github.com/romanfa/zonekeeper-nt8.git C:\dev\zonekeeper-nt8
   Set-Location C:\dev\zonekeeper-nt8
   ```

4. **Close NinjaTrader.** Run `& .\tools\link-dev.ps1`. **Start NinjaTrader again.** The script refuses to run while NinjaTrader is open. It creates a junction from the Windows Documents folder's `NinjaTrader 8\bin\Custom\AddOns\ZoneKeeper` to `C:\dev\zonekeeper-nt8\src\ZoneKeeper`. Confirm the files appear in NinjaScript Editor → AddOns → ZoneKeeper. If a folder already exists there, stop and inspect it before proceeding.
5. In NinjaScript Editor → right-click → References, check `System.Net.Http`, `System.Security` and `System.Runtime.Serialization`. Add missing references from `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\` before the first F5 compile. If `NinjaTrader.Custom.dll` is not rebuilt, the references are missing; the trace may have no compile lines.
6. Run the pure tests and read the last 30 log lines:

   ```powershell
   dotnet test .\tests\ZoneKeeper.Tests *> "$env:TEMP\zk-test.log"
   Get-Content "$env:TEMP\zk-test.log" -Tail 30
   ```

7. Complete the installed API checks in [docs/nt-api-notes.md](docs/nt-api-notes.md). Only after the references in step 5 are present, press **F5 in NinjaScript Editor** to compile. Visual Studio is for editing and debugging; do not build this add-on in Visual Studio. Require no ZoneKeeper compile errors or warnings.
8. Open Control Center → New → ZoneKeeper. Get a code on the live Cabinet → Platforms → NinjaTrader 8, paste it into the add-on and click Connect. Confirm the PC and add-on version appear in the cabinet. Do not put codes or keys in logs or screenshots.
9. On **Sim101**, close **two trades** after connection (for example, buy 1/sell 1, then sell 1/buy 1). Check the add-on log and `/api/trades` for both, once each. Cabinet → Journal trade rows are Phase C and are expected to be absent for now.
10. While logged in to the live site, open `/api/trades?from=YYYY-MM-DD&to=YYYY-MM-DD` with today's date in both places. Compare the two trades' account, side, quantity, prices, net P/L, commission, times and exit name with NinjaTrader's executions. Zero commission is valid if Sim101 has no commission template.

## Windows acceptance checklist

G0, G1 and G5 have the limited **PASSED 2026-10-15** results below. All other checks remain **unverified** until Roman records results. Use live + Sim101 throughout. This replaces the older plan's preview instructions. Fake HTTP tests cover error paths without changing live billing or account records.

| Check | Owner action and expected result |
| --- | --- |
| G0 Setup/API | **PASSED 2026-10-15:** main `3b3404e` compiled on NT 8.1.8.3; signed positions after fills were -1, +1, 0. |
| G1 Scale-in | **PASSED 2026-10-15:** buy 1 + buy 2 produced quantity 3 with unrounded average 7770.58333333333. |
| G2 Partial exit | Buy 2, sell 1, sell 1: one trade, weighted exit price; final sell's execution ID closes it. |
| G3 Flip | Buy 1, sell 2 in one fill, buy 1: one long and one short trade; split the reversal commission. |
| G4 Breakeven | Use an ATM stop at entry: gross zero, net P/L equals negative commission, exit name recorded. |
| G5 Compare | **PASSED 2026-10-15:** six Sim101 ES 12-26 trades sent once each matched NinjaTrader Executions and live `/api/trades` to the cent, including 2.88/side commission. |
| G6 Offline/disconnect | Roman interrupts the network, closes a Sim101 trade, then reconnects: queued trade sends once. Roman disconnects this PC in the cabinet, then reconnects with a new code: 401 clears the old key and waiting trades send. Check add-on Disconnect also frees the device slot. |
| G7 Restart | Roman ends NinjaTrader mid-trade and restarts: never resume the open trade; its close sends nothing and the next trade opened from flat sends once. Repeat with stale execution history and a second restart. Also restart after an offline closed trade and verify one delivery when its fill history remains available. Normal shutdown drains queued fills for the final current account objects and saves pending closed state; events from replaced objects must not alter it. |
| G8 Lapsed plan | Fake tests cover `403 not_paid`, paused queue and retry. **No agent changes live account or billing data.** Roman may verify live behavior if a naturally lapsed account is available; otherwise record the live check as deferred. |
| G9 Playback | **BLOCKED on live by the safety rule.** Verify Playback is excluded even when the setting is enabled. Replay fill parsing/reset behavior is covered by pure tests; do not upload replay trades to live. |
| G10 Sim101 reset | Roman opens a Sim101 position, then resets Sim101: drop open state, send nothing for it; the next closed trade works. |
| G11 Rollover | Optional, if two expiries have data: open and close both; two separate instrument names/trades. |
| G12 ZIP install | After export, close NinjaTrader, run `tools/unlink-dev.ps1`, then start NinjaTrader again. Recompile/restart to clear the dev copy, import the ZIP, restart, connect to live and close a Sim101 trade. To restore development, remove the imported assembly using the confirmed removal menu, close NinjaTrader, run `tools/link-dev.ps1`, start NinjaTrader again and press F5. Both junction scripts refuse running NinjaTrader. |
| G13 Update | Export `0.1.1-test` using the same `ZoneKeeper` assembly name, import over 0.1.0, restart: updated version appears, settings/key/outbox survive. Restore release version 0.1.0 afterward. Record the actual update steps. |
| G14 Workspace | Save a workspace with the window open, restart: no error; settings window need not reopen; engine still runs. |
| G15 Recompile | F5 with the window open: engine stops/starts and any already-open trade is never resumed or sent. A new Sim101 trade opened from flat sends once, with no duplicate subscriptions or windows. Check the old engine cannot stop or replace the newer one, queued fills drain against final account identities, replaced-account events are ignored, and reopening activates the existing window on its owning dispatcher. |

## Export and release

The release 0.1.0 compiled ZIP was built and checked on Windows on 2026-10-15. For later exports, use the steps below after compilation, tests, fresh code review and acceptance checks.

1. Keep `VERSION`, `ZkVersion.Value` and the changelog version at `0.1.1`. Turn NinjaScript Editor Debug Mode off and press F5.
2. Control Center → Tools → Export → NinjaScript… → **Export as compiled assembly**. Leave **Protect compiled assembly** unchecked. Select **all** source files under AddOns → ZoneKeeper; compare the export list with `src\ZoneKeeper\*.cs`. The export file name must be exactly **`ZoneKeeper`** (confirmed on Windows).
3. Set Product `ZoneKeeper`, Version `0.1.1`, and export file name **`ZoneKeeper`**. Keep this name for every version so updates do not load a second assembly.
4. Inspect `Documents\NinjaTrader 8\bin\Custom\ExportNinjaScript\ZoneKeeper.zip`: require one `ZoneKeeper.dll`, `Info.xml`, and the small NT-generated `ZoneKeeper.cs` stub (1143 bytes, no real source code).
5. Copy the same bytes to `dist\ZoneKeeper-NT8-0.1.1.zip` and `dist\ZoneKeeper-NT8.zip`. Run `tools\checksum.ps1`; confirm both hashes in `dist\SHA256SUMS.txt` match. For the site handoff, also copy the ZIP to `C:\Users\Admin\Documents\Vault\Home\Trading\ZoneKeeper\ZoneKeeper-NT8.zip`.
6. Complete G12 and G13 before release. For the clean install, **close NinjaTrader → run `tools/unlink-dev.ps1` → start NinjaTrader again** before importing the ZIP; follow G12 for restoring the development junction. Making the repository public, tagging/publishing a release and hosting the ZIP on the site remain separate coordinator/owner actions. This documentation does not publish or change the site.

The final 0.1.0 ZIP is 224013 bytes; SHA256 `702d472f160d86040dc0ed4e462bfc1f9df5b9b4327c2452dbbfa139a772bc8c`. `ZoneKeeper.dll` is 755200 bytes. The same ZIP is available at [the live download address](https://getzonekeeper.com/downloads/ZoneKeeper-NT8.zip).

## Update

Intended steps: download the new ZIP → Control Center → Tools → Import → NinjaScript Add-On… → choose the ZIP → restart NinjaTrader. Keep the ZIP intact. Settings, the key and waiting trades live outside the assembly folder and should remain.

**Update steps are unverified until G13 passes on Windows.** If import over the existing version fails, confirm the removal menu and test remove → restart → import → restart, then replace these instructions with the tested steps.
