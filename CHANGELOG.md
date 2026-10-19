# Changelog

## 0.2.0 — source ready 2026-10-19

Not compiled in NinjaTrader yet. Step 0 (which WebView2 DLLs NinjaTrader has) and the Windows tests come first. Includes the 0.1.1 changes.

- Automatic reset: after a closed trade (exit at most 10 minutes ago, during this NinjaTrader run) one reused "ZoneKeeper Reset" window opens on top of the chart without taking the keyboard focus. Closing it = skip.
- Checkbox "Open the reset after each closed trade" (on by default) and button "Open reset window".
- Reads the trade `ids` from the `POST /api/nt/trades` answer; gets a one-use login key from `POST /api/nt/handoff`. The device key never enters the browser.
- No answer: the window stays and tries again with growing waits. Plan not active: it says so, no retry loop. Disconnect, F5 and NinjaTrader exit close the window.
- Step 0 probe (`probes/BrowserProbe`) and path S tools (`tools/get-webview2.ps1`, `tools/pack-zip.ps1`).

## 0.1.1 — source ready 2026-10-16

Windows compile and ZIP export are pending. The live download is still 0.1.0.

- Label the sample connect code `Example: ZK-AB3D-9KXP`.
- Show the saved account hint: `Saved. You can close this window.`
- Keep the hint through connection updates; a failed file save shows an error until it succeeds.

## 0.1.0 — first version

Released 2026-10-15. ZIP SHA256 702d472f160d86040dc0ed4e462bfc1f9df5b9b4327c2452dbbfa139a772bc8c

- Fix: ZkEngine.ReportError could not read Run.log (CS0122 on NinjaTrader compile)
- Connect a PC with a one-time ZoneKeeper code; keep its key protected with Windows DPAPI.
- Pair fills into closed trades, including scale-ins, partial exits and reversals; send net P/L, commission and the final exit order name.
- Select accounts; block Playback uploads on the live site.
- Keep waiting trades and open state on disk; retry when offline or when access returns.
- Add the ZoneKeeper window, event log, pure test project and Windows development/release tools.
