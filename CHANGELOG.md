# Changelog

## 0.2.1 — source ready 2026-10-20

Not compiled in NinjaTrader yet. Includes the 0.2.0 changes.

- No reset pop-up while the account still has a position in that instrument. A reversal (buy 1, sell 2) still sends the closed trade and lists it with Reset, but the window waits. When the position goes flat, the window comes up once, for the newest closed trade; older ones keep Reset. Partial closes and two quick trades work as before; a position in another instrument or account does not block the reset.

## 0.2.0 — source ready 2026-10-19

Not compiled in NinjaTrader yet. The Windows tests come first. Includes the 0.1.1 changes.

- The ZoneKeeper window has two tabs: Today (today's trades with Reset / Saving… / Logged, the 5-step reset with normal Windows controls) and Connection (the old content).
- After a closed trade (exit at most 10 minutes ago, during this NinjaTrader run) the window comes on top of the chart without taking the keyboard, and opens the reset for that trade. A running reset is never interrupted.
- Reset texts come from the site (`GET /api/nt/catalog`, kept in `catalog.json`, refreshed every 6 hours). Today's list from `GET /api/nt/today`.
- The voice is downloaded for each reset with the device key (`GET /api/nt/audio/<id>`), played from one temp file and deleted after it plays. Nothing is cached. No internet → the reset runs with the timer.
- The step-5 tilt tap saves the reset (`resets.jsonl`, sent after the trades with `POST /api/nt/resets`). No Done button.
- Checkbox "Open the reset after each closed trade" (on by default). Reads the trade `ids` from the `POST /api/nt/trades` answer.
- The browser reset window of the earlier 0.2.0 source is gone: no extra DLLs, no browser login.

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
