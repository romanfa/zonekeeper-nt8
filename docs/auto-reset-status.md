# Automatic reset implementation status

Updated October 16, 2026 against the agreed vault implementation note.
Local implementation and automated verification are complete. Deployment and
actual NinjaTrader browser/audio acceptance remain outstanding.

## Implemented

- NT8: independent reset polling, durable member-scoped pending cache, one
  non-modal WebView2 window, queued trades, early-close suppression, manual
  reopen and restart recovery. Existing trade saving stays independent.
- Website: opt-in durable requests before upload acknowledgement, member/trade-bound
  one-use handoffs, cookie login and server subscription checks. Device credentials
  never enter browser URLs or scripts.
- UI: trade P/L, tilt/thought selection, audio, final tilt and explicit Done.
  Stop, close, interrupted audio and failed requests remain pending. Completion
  retries cannot create another linked entry. Separate reset quotas protect uploads.
- Totals: imported trades count independently of resets in calendar, daily chart
  and weekly email, including weeks with trades but no resets.

No trades, trading-control changes, billing changes or deployment were performed.
No NinjaTrader installation/recompilation was performed. Existing development
junctions may expose source edits; follow backend-first rollout before loading them.

## Access and verification

GitHub and NuGet HTTPS checks passed using Python with certificate verification.
The earlier restricted session's Windows TLS/default Git failed with SEC_E_NO_CREDENTIALS. A command-local OpenSSL
Git clone used the installed credential manager without printing or persisting
credentials or changing global TLS settings. Local package restore succeeded.

- Full NT8 compile: C# 7.3, .NET Framework 4.8, installed NT8 assemblies,
  WebView2 SDK 1.0.4258.31, warnings as errors: passed.
- .NET suite after restoring full access: **244 passed, 0 failed, 0 skipped**.
  The earlier File.Replace permission failure is resolved by the permission change;
  no persistence code was weakened. All website suites and the NT8 compile also
  passed again before committing.
- Durable-reset and real SQLite API suites passed: member/paid/origin isolation,
  revoked device, handoff expiry/replay, retry, pagination, atomic completion,
  metadata preservation and independent quota tests.
- Playback 7/7; total regressions 8/8; journal 29/29; daily 43/43; grade 16/16.
- Actual client + Worker + SQLite DOM integration passed: stale feature flag,
  Stop, manual flow, resume, failed start/progress, final tilt and explicit Done.
  Media and clock are simulated; this does not prove audible playback or NT8 hosting.
- Headless Edge launch was denied (EPERM), and no connected browser was available.
  Actual NT8 login/audio/chart responsiveness/F5 disposal remain unverified.
  Server elapsed-playback checks cannot attest that a person listened.

## Delivery and remaining release gates

Website base: 7aed9679cfc5ac4d574e9d944ae4cdfcc1adaa75, local ignored checkout
`dist/website-source`. Complete changes are preserved in
[website/automatic-reset.patch](../website/automatic-reset.patch); see
[application and rollout instructions](../website/README.md).
Git write access and GitHub access were verified after restoring full access.
Both repositories use the `feat/automatic-reset` branch for this delivery.

1. Apply/review website patch, apply migration 0017 after prior migrations, deploy
   matching backend and frontend BEFORE loading the updated add-on. Legacy upload
   ACK compatibility means an old backend can save a trade without queuing a reset;
   those previously acknowledged trades are not retroactively queued.
2. Package WebView2 references/loader and perform actual NinjaScript compilation.
   Verify login, audio, close/reopen, disconnect and recompile disposal without orders.
3. Roman performs any Sim101 trade acceptance (full/partial closes, reversal
   and multiple queued trades).

`tools/build-auto-reset.ps1` produces a compile-check artifact, not an installable
add-on. The isolated browser probe remains an optional diagnostic.
