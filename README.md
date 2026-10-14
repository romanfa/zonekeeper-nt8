# ZoneKeeper for NinjaTrader 8

The free ZoneKeeper add-on sends closed trades from NinjaTrader 8 to your ZoneKeeper cabinet: the time, instrument and P/L. It pairs entry and exit fills into trades, including scale-ins, partial exits and reversals. You choose which accounts to send, then add the emotional part in ZoneKeeper.

Works with NinjaTrader 8 (8.1 or newer) on Windows. A ZoneKeeper subscription with access is needed to connect. Not made by or tied to NinjaTrader. Source is under the [MIT license](LICENSE).

**Development status:** Version 0.1.0 source was written on the Mac. NinjaTrader compilation, runtime, ZIP export, clean installation and updates are unverified on this Mac. No release ZIP has been built yet. The GitHub repository is private.

## Install the release ZIP

These are the intended member steps. Confirm the exact Import menu text and installation on Windows before publishing a release.

1. Download the add-on. You get a `.zip` file. **Do not unzip it.** The intended download address is `https://getzonekeeper.com/downloads/ZoneKeeper-NT8.zip`; it is not published by this repository yet.
2. In NinjaTrader: Control Center → Tools → Import → NinjaScript Add-On… → pick the `.zip` file. Then restart NinjaTrader.
3. In NinjaTrader: Control Center → New → ZoneKeeper. The ZoneKeeper window opens.
4. On [getzonekeeper.com](https://getzonekeeper.com): Cabinet → Platforms → NinjaTrader 8 → Connect NinjaTrader. Copy the code, paste it in the ZoneKeeper window, and click Connect. It works for 10 minutes, one time.

Choose accounts under **Send trades from these accounts**. New accounts are enabled by default; Playback accounts are blocked on the live site. Each PC needs its own connection code; up to five PCs may be connected. The key is kept on the PC for automatic reconnection. Use **Disconnect this PC** in the add-on or disconnect the PC in Cabinet → Platforms → NinjaTrader 8.

## What is sent and kept

Each closed trade sends `account`, `instrument`, `side`, `qty`, `entry_time`, `exit_time`, `entry_price`, `exit_price`, `pnl_cents`, `commission_cents`, `currency`, `point_value`, `exit_exec_id`, optional `entry_exec_ids`, optional `exit_name`, and `local_date`. Times are UTC milliseconds; `local_date` is the exit date in the Windows time zone. P/L is net after commission. Supported currencies are USD, CAD, EUR, GBP, AUD, CHF and NZD.

Connecting also sends the PC name and add-on version. The add-on uses a device key, not the browser login cookie. It does not import past trading days. Open positions with an unknown start are followed but are not sent. Two PCs sending the same account and exit execution ID produce one saved trade.

After a restart or reconnect, saved open trades require unchanged execution history, position direction, size and average price. Waiting trades restored from disk require complete unchanged fill history before upload. Missing or amended history leaves uncertain trades unsent and writes the reason to the log. An execution Update or Remove invalidates affected waiting trades; a trade already dispatched to the server cannot be changed locally.

Local files live in `Documents\NinjaTrader 8\ZoneKeeper\` (the NinjaTrader user folder may be redirected, for example to OneDrive):

| File | Purpose |
| --- | --- |
| `settings.json` | Account choices and device key protected with Windows DPAPI for the current Windows user. |
| `outbox.jsonl` | Up to 500 closed trades waiting to send, with local fill provenance. The oldest is dropped, with a log entry, when full. |
| `outbox.jsonl.invalidations` | Durable safety records that prevent corrected or removed fills from being uploaded again. |
| `open-trades.json` | Open trades, execution checkpoints, pending closed trades and local safety metadata. |
| `zonekeeper.log`, `zonekeeper.1.log` | Event log and rotated log. Codes and device keys are not logged. |
| `running.lock` | Keeps one engine active for this Windows user. |

Offline trades wait locally and retry. If the subscription loses access, the key and waiting trades stay and the add-on checks again hourly. A disconnected PC needs a new code. Waiting trades belong to whichever ZoneKeeper account the PC connects to next. A key copied to another PC or Windows user cannot be decrypted; connect that PC separately.

A closed trade is saved before it is sent. If a file cannot be saved, the add-on keeps it and tries again. If no save works, keep NinjaTrader open until it works.

See the [ZoneKeeper privacy policy](https://getzonekeeper.com/privacy/).

## Test on Windows

Roman owns all NinjaTrader actions and Sim101 orders. Use **only `https://getzonekeeper.com`** for this handoff; do not use the preview worker. Keep `base_url` unset or set to the live address. Playback remains blocked even if `allow_playback` is true. None of the following Windows checks has been completed on this Mac.

1. Check Control Center → Help → About: NinjaTrader must be 8.1 or newer. Check `git --version` and `dotnet --list-sdks`; a .NET 8 SDK is needed for the pure tests. Record the exact Import/Export menu text.
2. Clone the private repository with an authorized GitHub account:

   ```powershell
   git clone https://github.com/romanfa/zonekeeper-nt8.git C:\dev\zonekeeper-nt8
   Set-Location C:\dev\zonekeeper-nt8
   ```

3. **Close NinjaTrader.** Run `& .\tools\link-dev.ps1`. **Start NinjaTrader again.** The script refuses to run while NinjaTrader is open. It creates a junction from the Windows Documents folder's `NinjaTrader 8\bin\Custom\AddOns\ZoneKeeper` to `C:\dev\zonekeeper-nt8\src\ZoneKeeper`. Confirm the files appear in NinjaScript Editor → AddOns → ZoneKeeper. If a folder already exists there, stop and inspect it before proceeding.
4. In NinjaScript Editor → right-click → References, check `System.Net.Http`, `System.Security` and `System.Runtime.Serialization`. Add missing references from `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\`.
5. Run the pure tests and read the last 30 log lines:

   ```powershell
   dotnet test .\tests\ZoneKeeper.Tests *> "$env:TEMP\zk-test.log"
   Get-Content "$env:TEMP\zk-test.log" -Tail 30
   ```

6. Complete the installed API checks in [docs/nt-api-notes.md](docs/nt-api-notes.md). Press **F5 in NinjaScript Editor** to compile. Visual Studio is for editing and debugging; do not build this add-on in Visual Studio. Require no ZoneKeeper compile errors or warnings.
7. Open Control Center → New → ZoneKeeper. Get a code on the live Cabinet → Platforms → NinjaTrader 8, paste it into the add-on and click Connect. Confirm the PC and add-on version appear in the cabinet. Do not put codes or keys in logs or screenshots.
8. On **Sim101**, close **two trades** after connection (for example, buy 1/sell 1, then sell 1/buy 1). Check the add-on log and cabinet for both, once each.
9. While logged in to the live site, open `/api/trades?from=YYYY-MM-DD&to=YYYY-MM-DD` with today's date in both places. Compare the two trades' account, side, quantity, prices, net P/L, commission, times and exit name with NinjaTrader's executions. Zero commission is valid if Sim101 has no commission template.

## Windows acceptance checklist

All items are **unverified** until Roman records results. Use live + Sim101 throughout. This replaces the older plan's preview instructions. Fake HTTP tests cover error paths without changing live billing or account records.

| Check | Owner action and expected result |
| --- | --- |
| G0 Setup/API | Verify installed signatures, Active startup and single engine. Check account replacement/resync uses the current account object and stale queued events cannot alter its state. Temporarily enable `debug_fills` with NinjaTrader closed. Buy 1/sell 1 on Sim101 MNQ: Add operation, position 1 then 0, UTC time within 2 seconds of the PC clock, USD and point value 2. Turn debug logging off afterward. |
| G1 Scale-in | Buy 1, buy 1, sell 2: one long trade, quantity 2, weighted entry price and net P/L. |
| G2 Partial exit | Buy 2, sell 1, sell 1: one trade, weighted exit price; final sell's execution ID closes it. |
| G3 Flip | Buy 1, sell 2 in one fill, buy 1: one long and one short trade; split the reversal commission. |
| G4 Breakeven | Use an ATM stop at entry: gross zero, net P/L equals negative commission, exit name recorded. |
| G5 Compare | Compare G1–G4 with NinjaTrader Executions CSV and logged-in live `/api/trades`, to the cent. Roman may set a Sim101 commission template to check nonzero commission. |
| G6 Offline/disconnect | Roman interrupts the network, closes a Sim101 trade, then reconnects: queued trade sends once. Roman disconnects this PC in the cabinet, then reconnects with a new code: 401 clears the old key and waiting trades send. Check add-on Disconnect also frees the device slot. |
| G7 Restart | Roman ends NinjaTrader mid-trade and restarts: resume only if the saved side/size matches; otherwise drop and log. Also restart after an offline closed trade and verify one delivery. Check normal shutdown drains queued fills for the final current account objects and saves the resulting state; events from replaced objects must not alter it. |
| G8 Lapsed plan | Fake tests cover `403 not_paid`, paused queue and retry. **No agent changes live account or billing data.** Roman may verify live behavior if a naturally lapsed account is available; otherwise record the live check as deferred. |
| G9 Playback | **BLOCKED on live by the safety rule.** Verify Playback is excluded even when the setting is enabled. Replay fill parsing/reset behavior is covered by pure tests; do not upload replay trades to live. |
| G10 Sim101 reset | Roman opens a Sim101 position, then resets Sim101: drop open state, send nothing for it; the next closed trade works. |
| G11 Rollover | Optional, if two expiries have data: open and close both; two separate instrument names/trades. |
| G12 ZIP install | After export, close NinjaTrader, run `tools/unlink-dev.ps1`, then start NinjaTrader again. Recompile/restart to clear the dev copy, import the ZIP, restart, connect to live and close a Sim101 trade. To restore development, remove the imported assembly using the confirmed removal menu, close NinjaTrader, run `tools/link-dev.ps1`, start NinjaTrader again and press F5. Both junction scripts refuse running NinjaTrader. |
| G13 Update | Export `0.1.1-test` using the same `ZoneKeeper` assembly name, import over 0.1.0, restart: updated version appears, settings/key/outbox survive. Restore release version 0.1.0 afterward. Record the actual update steps. |
| G14 Workspace | Save a workspace with the window open, restart: no error; settings window need not reopen; engine still runs. |
| G15 Recompile | F5 with the window open, then close one Sim101 trade: engine stops/starts, one saved row, no duplicate subscriptions or windows. Check the old engine cannot stop or replace the newer one, queued fills drain against final account identities, replaced-account events are ignored, and reopening activates the existing window on its owning dispatcher. |

## Export and release

Do this on Windows after compilation, tests, fresh code review and acceptance checks. The ZIP has not been built or checked yet.

1. Keep `VERSION`, `ZkVersion.Value` and the changelog version at `0.1.0`. Turn NinjaScript Editor Debug Mode off and press F5.
2. Control Center → Tools → Export → NinjaScript… → **Export as compiled assembly**. Leave **Protect compiled assembly** unchecked. Select **all** source files under AddOns → ZoneKeeper; compare the export list with `src\ZoneKeeper\*.cs`.
3. Set Product `ZoneKeeper`, Version `0.1.0`, and export file name **`ZoneKeeper`**. Keep this name for every version so updates do not load a second assembly.
4. Inspect `Documents\NinjaTrader 8\bin\Custom\ExportNinjaScript\ZoneKeeper.zip`: require one `ZoneKeeper.dll` plus NinjaTrader's info file, and no `.cs` files.
5. Copy the same bytes to `dist\ZoneKeeper-NT8-0.1.0.zip` and `dist\ZoneKeeper-NT8.zip`. Run `tools\checksum.ps1`; confirm both hashes in `dist\SHA256SUMS.txt` match.
6. Complete G12 and G13 before release. For the clean install, **close NinjaTrader → run `tools/unlink-dev.ps1` → start NinjaTrader again** before importing the ZIP; follow G12 for restoring the development junction. Making the repository public, tagging/publishing a release and hosting the ZIP on the site remain separate coordinator/owner actions. This documentation does not publish or change the site.

## Update

Intended steps: download the new ZIP → Control Center → Tools → Import → NinjaScript Add-On… → choose the ZIP → restart NinjaTrader. Keep the ZIP intact. Settings, the key and waiting trades live outside the assembly folder and should remain.

**Update steps are unverified until G13 passes on Windows.** If import over the existing version fails, confirm the removal menu and test remove → restart → import → restart, then replace these instructions with the tested steps.
