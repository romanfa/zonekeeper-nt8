# Changelog

## Planned for 0.1.1

- Label the sample connect code `Example: ZK-AB3D-9KXP`.
- Show the saved account hint: `Saved. You can close this window.`

## 0.1.0 — first version

Released 2026-10-15. ZIP SHA256 702d472f160d86040dc0ed4e462bfc1f9df5b9b4327c2452dbbfa139a772bc8c

- Fix: ZkEngine.ReportError could not read Run.log (CS0122 on NinjaTrader compile)
- Connect a PC with a one-time ZoneKeeper code; keep its key protected with Windows DPAPI.
- Pair fills into closed trades, including scale-ins, partial exits and reversals; send net P/L, commission and the final exit order name.
- Select accounts; block Playback uploads on the live site.
- Keep waiting trades and open state on disk; retry when offline or when access returns.
- Add the ZoneKeeper window, event log, pure test project and Windows development/release tools.
