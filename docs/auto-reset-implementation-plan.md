# Automatic reset implementation plan

Continue the agreed vault scope. Browser runtime acceptance remains unverified;
it does not prevent implementing and testing the remaining code locally. Nothing
is deployed or installed by this plan. Do not place orders or alter trading controls.

The earlier restricted session used the supplied checkout and exported the website
patch. Full access now permits the `feat/automatic-reset` branch in both repositories;
Git write access is verified and all 244 .NET tests pass.

## Contract and responsibilities

1. Website backend (`dist/website-source/src`, migration and backend tests):
   - Opt-in upload body `auto_reset: true`. Preserve existing counts/rejections,
     add `accepted: [{index, trade_id}]` after member-scoped confirmation. Create
     durable pending requests idempotently before acknowledging accepted trades.
   - Device `GET /api/nt/resets?after=<trade_id>` returns
     `{member_id, pending:[{trade_id,instrument,pnl_cents,currency,exit_time}], next_cursor}`.
     Ascending trade IDs, page size 100, next_cursor=0 at end. Only pending rows.
   - Device `POST /api/nt/resets/open {trade_id}` returns `{url, expires_at}`.
     URL is same-origin `/app/#nt-reset=<opaque short-lived one-use handoff>`;
     no device credential appears in browser storage, scripts or URLs.
   - Browser `POST /api/resets/open {handoff}` requires same-origin cookie auth,
     paid access, matching member and an unrevoked issuing device. Returns the
     trade and enough draft information to resume the existing reset safely.
   - Browser start/progress/complete routes enforce playback attempt identity,
     elapsed playback, final tilt and explicit Done. Closing/logging alone never
     completes a reset. All SQL binds member/trade identity and retries are safe.
2. NT8 (`src/ZoneKeeper`, tests): separate reset API/coordinator and durable local
   pending cache. Server is authoritative, so local cache failure never prevents
   independent trade saving. Poll after acknowledged uploads and on restart.
   Authenticate before showing cached items; member/origin changes cannot expose
   another member's queue. One non-modal WebView2 window with a queue, early-close
   suppression and manual reopen. Close/dispose on stop/recompile/disconnect.
3. Website client (`public/app/js/reset.js`, integration module/main): reuse picker,
   audio and final rating. Disable Skip and silent fallback only in the automatic
   reset web flow, never NinjaTrader controls. Track real playback and await server
   confirmation before showing completion. Interrupted audio/reset remains pending.
4. Trade totals (`public/js` stats/chart, calendar, cron and focused tests): imported
   trades count independently of linked reset entries everywhere, including daily
   P/L chart and weekly email. Preserve manual daily overrides and currency behavior.

## Verification

Write focused failures before each implementation. Run .NET tests with the local
.NET 8 runtime/feed; preserve/report the existing File.Replace permission failure.
Run website tests against SQLite/in-memory fakes, never production. Compile NT8
sources against installed assemblies/WebView2 without installing. Review the final
change and fix important findings. Save website changes as an applyable patch in
this repository. Keep manual NT8 login/audio/F5/Sim101 checks explicitly unverified.

## Progress

- [x] Source access, local packages, baseline tests, isolated browser prototype.
- [x] Backend durable requests/handoffs/completion and isolation tests.
- [x] NT8 durable queue/API/window and regression tests.
- [x] Website reset-flow integration and interruption tests.
- [x] Trade-total consumers and regression tests.
- [x] Combined local verification, review, website patch and updated status.

- [ ] Deployment and actual NT8 login/audio/disposal/Sim101 acceptance.
