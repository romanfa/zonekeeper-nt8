# Automatic reset browser probe

This is an isolated, disposable prototype for the first step of the agreed
automatic-reset plan. It is not the automatic reset implementation and does not
subscribe to executions, upload trades, read device keys, or mark resets complete.

## Implementation and verification plan

- [x] Obtain website source and packages without changing machine Git/TLS settings.
- [x] Inspect website login, trade ownership, completion and trade totals.
- [x] Build a separate C# 7.3 / .NET Framework 4.8 NT8 add-on with a WebView2 window.
- [x] Compile against the installed NT8 assemblies and the downloaded WebView2 SDK.
- [ ] Roman: verify login, audio, early close, reopen and F5 cleanup in NT8.
- [x] Server contract and durable queue implemented locally; runtime proof remains a release gate.

Keep this prototype outside `src/ZoneKeeper`: the installed development junction
can point there. Do not copy it into NinjaTrader automatically. Compilation here
checks types only; the real NinjaScript compile and host behavior are separate gates.

## Source findings

Website checkout: `dist/website-source`, commit
`7aed9679cfc5ac4d574e9d944ae4cdfcc1adaa75` (local, ignored by the parent repository).

- `src/auth.js` uses an email magic link and an HttpOnly browser session, separate
  from device bearer authentication. An email link must be opened inside the
  embedded browser; a login in Chrome does not establish its session.
- `src/api.js` authenticates device uploads and checks subscription access. Trade
  deduplication uses `(user_id, account, exit_exec_id)`. Upload responses contain
  counts and rejected indices, not confirmed trade IDs.
- Entry creation validates `trade_id` against the logged-in user and rejects a
  second linked entry. Interrupted resets therefore need to resume the existing
  entry, not create another one.
- `public/app/js/reset.js` saves an entry at the first rating or last step. Skip,
  voice-off fallback, Stop and Done exist. An entry, `audio_id`, or a final rating
  alone does not prove the required audio played and Done was confirmed.
- `public/app/js/nt-trades.js` currently labels any linked entry as reset done.
  This must use explicit completion state for automatic resets.
- `public/js/stats.js` already has `dayTotals` logic for imported trades and
  unlinked manual entries. Preserve that behavior.
- `public/app/js/calendar.js` passes entries but no imported trades to
  `renderDayPnlChart`. `src/cron.js` aggregates P/L from entries and daily summaries
  without imported trade rows. Both still require fixes and regression tests.

The integration needs server-confirmed trade identity, a short-lived browser
handoff bound to that member/trade, explicit completion persisted separately from
trade saving, and a device-authenticated way to recover pending work. Endpoint
names and schemas are not yet implemented. Never send a device key to the browser.

## Browser design

One non-modal NTWindow per probe add-on instance. All window operations run on
its dispatcher. Closing disposes WebView2; add-on termination closes the window
and removes its menu handlers. A separate persistent browser profile preserves
the website session across reopening. Browser navigation is restricted to
`https://getzonekeeper.com` on port 443. No host objects or web message bridge.

The address field permits pasting a fresh magic login link into this browser;
it clears immediately and neither URLs nor exception messages are logged.
Use the website's audio library for playback testing without adding a trade/reset.

WebView2 SDK: `1.0.4258.31`, .NET Framework `net462` assets plus x64 loader.
Reference: Microsoft's [WPF initialization documentation](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.wpf.webview2.ensurecorewebview2async).

## Build and install the probe (Roman)

`powershell -File tools/build-browser-probe.ps1` compiles the prototype with
C# 7.3 and warnings as errors against the installed NT8 assemblies. It writes
only `dist/browser-probe`. This check passed on October 16, 2026.

No probe has been installed or run in NinjaTrader by the agent. Installation and
NinjaScript compilation remain unverified. The three SDK files are downloaded and
ready; no NuGet setup is needed for this manual test.

1. Close NinjaTrader.
2. In your NinjaTrader user folder's `bin/Custom`, put these files from
   `C:\dev\zonekeeper-nt8\dist\browser-probe`:
   - `Microsoft.Web.WebView2.Core.dll`
   - `Microsoft.Web.WebView2.Wpf.dll`
   - `WebView2Loader.dll` (x64)
   If any already exists, check its version before proceeding; do not overwrite
   another add-on's dependency blindly.
3. Create `bin/Custom/AddOns/ZoneKeeperBrowserProbe` and copy
   `ZoneKeeperBrowserProbe.cs` from that same output folder into it. Keep it
   separate from the existing `AddOns/ZoneKeeper` junction.
4. Start NinjaTrader. In NinjaScript Editor, right-click -> References and add
   the two `Microsoft.Web.WebView2.*.dll` files from `bin/Custom`. The native
   `WebView2Loader.dll` is not a managed reference. Do not copy or reference
   `ZoneKeeper.BrowserProbe.CompileCheck.dll`; it is only the local type-check output.
5. Compile with F5. Open Control Center -> New -> ZoneKeeper browser probe.
6. Sign in on the displayed website. If an email login link would open Chrome,
   copy the fresh link into the probe's email-login-link field and click Open
   login link. This uses normal website login; it never reads the device key.

Use the website audio library for this proof. There is no need to create a
journal entry, run a trade-linked reset, or place an order.

To remove the probe, close it, remove its source folder and the two references
from NinjaScript Editor, then recompile/restart. Remove the three SDK files only
if no other add-on uses them. The separate `ZoneKeeper/BrowserProbe` profile may
be retained for subsequent testing; it contains the embedded browser session.

## Manual acceptance (Roman)

1. Open the probe twice: one window. Keep chart/trading buttons usable.
2. Sign in inside the probe, then close and reopen: session retained.
3. Play an audio from the website library: audible, pause/resume works.
4. Close during playback: sound stops. Reopen and play again.
5. Close immediately during browser initialization; reopen without errors.
6. F5 while the probe is open: window closes, old menu/events disappear, one new
   menu works. Repeat twice. Check for lingering probe browser processes.
7. Check offline/load failure and retry. No action claims a completed reset.

Record NT8/runtime versions and observed results here. These checks require no orders.

## Baseline tests

The .NET 8 test dependencies are restored from `dist/nuget-feed` to
`dist/nuget-packages`. Windows TLS cannot reach NuGet through .NET in this session,
so the packages were fetched using Python HTTPS with certificate verification.
NuGet vulnerability auditing was disabled for this local-feed restore only.
The local runtime at `dist/dotnet8` is .NET 8.0.26, verified against Microsoft's
SHA512 release metadata. No machine runtime or global settings were changed.

```powershell
dotnet restore tests/ZoneKeeper.Tests/ZoneKeeper.Tests.csproj --source C:\dev\zonekeeper-nt8\dist\nuget-feed --packages C:\dev\zonekeeper-nt8\dist\nuget-packages -p:NuGetAudit=false
$env:DOTNET_ROOT_X64 = 'C:\dev\zonekeeper-nt8\dist\dotnet8'
$env:DOTNET_ROOT = $env:DOTNET_ROOT_X64
$env:VSTEST_DOTNET_PATH = Join-Path $env:DOTNET_ROOT_X64 'dotnet.exe'
dotnet test tests/ZoneKeeper.Tests/ZoneKeeper.Tests.csproj --no-restore --verbosity minimal
```

October 16 result: **230 passed, 1 failed, 0 skipped**. The existing test
`T15_AtomicFileWritesNoOpenStateAndIgnoresLegacyFillContributions` fails at line 100.
A standalone .NET 8 diagnostic using the unchanged `AtomicFileStore` confirms
the initial write succeeds and `File.Replace` throws `UnauthorizedAccessException`
even under the writable workspace. This is consistent with session filesystem
restrictions, but still needs an ordinary local terminal run to confirm. Do not
weaken atomic persistence or replace it with delete-then-move to pass this check.
