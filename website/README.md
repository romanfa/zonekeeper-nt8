# Website automatic reset changes

`automatic-reset.patch` includes all website changes and new files. Base:
`romanfa/zonekeeper` commit `7aed9679cfc5ac4d574e9d944ae4cdfcc1adaa75`.

The same changes are committed on the website repository's `feat/automatic-reset`
branch as `782a611`. Use that branch directly or apply this patch to the base,
not both.

Apply in the website checkout after reviewing its working tree:

```powershell
git apply --check C:/dev/zonekeeper-nt8/website/automatic-reset.patch
git apply C:/dev/zonekeeper-nt8/website/automatic-reset.patch
```

The implementation also exists in ignored `dist/website-source`; do not apply
this patch twice there. No deployment has been performed.

Export verification: the patch applies cleanly to an archive of the base commit;
all 17 changed files match the working implementation after line-ending normalization.
SHA256: `77c8c7a8cad5a88af4df8969409898cd24fc62b9b8adba6375bb2e677b21f493`.

## Local verification

Use Node 24 with node:sqlite support. From the website repository:

```powershell
node scripts/automatic-resets-check.mjs
node scripts/automatic-resets-api-check.mjs
node --test --test-isolation=none scripts/automatic-playback.test.mjs
node scripts/reset-totals-check.mjs
node scripts/journal-check.mjs
node scripts/daily-check.mjs
node scripts/grade-check.mjs
node scripts/automatic-reset-dom-check.mjs
```

The DOM test requires `linkedom`, either resolvable normally or supplied through
`ZK_TEST_DOM_MODULE` as an absolute file URL to its ESM entry. This session used
`file:///C:/dev/zonekeeper-nt8/dist/ui-test/node_modules/linkedom/esm/index.js`.
It uses actual client/Worker/SQLite with simulated media and clock.

## Required rollout order

1. Apply migration `0017_automatic_resets.sql` after existing migrations in the
   intended website environment, then deploy matching Worker and frontend.
   Verify authenticated pending/reset routes BEFORE loading the new add-on.
   Legacy upload ACK compatibility means an old backend can save a trade without
   creating its reset. Such acknowledged trades are not backfilled.
2. Package WebView2 SDK 1.0.4258.31 `lib/net462` Core and Wpf assemblies plus
   `runtimes/win-x64/native/WebView2Loader.dll` in the NinjaTrader `bin/Custom`
   directory. Reference the two managed assemblies in NinjaScript, not the loader.
   A compatible installed WebView2 runtime is required. Include all current
   `src/ZoneKeeper` sources, including ResetCoordinator.cs and ResetWindow.cs.
   Existing development junctions can expose source edits immediately; wait for
   the backend before compiling/loading. The output of build-auto-reset.ps1 is
   only a type-check DLL and MUST NOT be installed as an add-on package.
3. Verify NT8 login, audible playback, close/reopen, offline recovery, queue,
   member change, expired subscription, shutdown and F5 disposal. Keep charts
   and trading buttons available. Roman performs any Sim101 trades.
The .NET suite was rerun with full access: all 244 tests pass. The earlier
File.Replace permission failure was environmental and is resolved.

Deployment, SDK installation and real-browser/NT8 acceptance remain outstanding.
No billing changes or production writes are part of this delivery.
