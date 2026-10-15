# Changelog

## 0.1.0 — first version

Unreleased. NinjaTrader compilation, runtime, compiled ZIP installation and updates still need Windows verification.

- Fix: ZkEngine.ReportError could not read Run.log (CS0122 on NinjaTrader compile)
- Connect a PC with a one-time ZoneKeeper code; keep its key protected with Windows DPAPI.
- Pair fills into closed trades, including scale-ins, partial exits and reversals; send net P/L, commission and the final exit order name.
- Select accounts; block Playback uploads on the live site.
- Keep waiting trades and open state on disk; retry when offline or when access returns.
- Add the ZoneKeeper window, event log, pure test project and Windows development/release tools.
