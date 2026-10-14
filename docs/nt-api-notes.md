# NinjaTrader API notes

Checked against official NinjaTrader help pages and the official Basic sample on 2026-10-14. This is source evidence, **not** an installed NinjaTrader compile or runtime result. This Mac has not verified the Windows assemblies, event ordering, lifecycle, UI dispatch or export/import behavior.

The Phase B handoff uses **only `https://getzonekeeper.com`**, with Roman placing Sim101 orders. The older plans' preview instructions do not apply. Playback uploads stay blocked on live, including when `allow_playback` is enabled. G9 runtime replay upload testing is blocked; pure tests cover parsing.

## Official sample inspected

Downloaded [Addon_Framework_NinjaScript_Basic.zip](https://static.ninjatrader.com/support/helpGuides/nt8/samples/Addon_Framework_NinjaScript_Basic.zip) into a separate temporary folder and read `AddOns/AddOnFramework.cs`; never imported, executed or compiled the sample. Archive entries are the C# file, `AddOnFrameworkTab.xaml` and `Info.xml`, dated March 19, 2021.

ZIP SHA-256: `736557cfa45a23ff1be846e1ff9378f4dda771a59285880c9a09f2b598d565b3`.

Sample imports: `NinjaTrader.Cbi`, `NinjaTrader.Data`, `NinjaTrader.Gui.Tools`, `NinjaTrader.NinjaScript`, and WPF namespaces. Its classes are in `NinjaTrader.Gui.NinjaScript`. ZoneKeeper uses the plan's namespace `NinjaTrader.NinjaScript.AddOns.ZoneKeeper`, with explicit imports. The sample does not fully qualify every type, so verify installed type namespaces in Object Browser rather than treating imports as proof of each type's namespace.

## Verified names and signatures

| API | Official evidence | Remaining limit |
| --- | --- | --- |
| `AddOnBase`; `protected override void OnStateChange()` | [OnStateChange](https://static.ninjatrader.com/support/helpGuides/nt8/onstatechange.htm) classifies AddOns as Active-state objects; [OnWindowCreated](https://static.ninjatrader.com/support/helpGuides/nt8/onwindowcreated.htm) shows the subclass/override. | Installed base type and actual engine start/stop still need checking. |
| `State.SetDefaults`, `State.Active`, `State.Terminated` | [Lifecycle states](https://static.ninjatrader.com/support/helpGuides/nt8/onstatechange.htm). Active is readiness; Terminated is cleanup. Sample lines 34–41 handle SetDefaults only. | The Basic sample alone does not prove Active fires; G0/G15 must show it. Do not claim a Configure fallback is needed without runtime evidence. |
| `protected override void OnWindowCreated(Window window)`; `protected override void OnWindowDestroyed(Window window)` | [Own window](https://static.ninjatrader.com/support/helpGuides/nt8/creating_your_own_addon_window.htm); sample lines 44 and 68. `Window` is the WPF type. | Callbacks run on the window thread; OnWindowCreated also runs on recompilation. Check cleanup/no duplicate menus. |
| `ControlCenter`; `FindFirst("ControlCenterMenuItemNew")`; `NTMenuItem`; `Click` handler `(object sender, RoutedEventArgs e)` | [OnWindowCreated](https://static.ninjatrader.com/support/helpGuides/nt8/onwindowcreated.htm); sample lines 44–84. Style uses `Application.Current.TryFindResource("MainMenuItem") as Style`. | Confirm installed namespaces and menu behavior. Remove menu item, unsubscribe Click and clear reference on destruction. |
| `Core.Globals.RandomDispatcher.InvokeAsync(new Action(...))` | [OnWindowCreated example](https://static.ninjatrader.com/support/helpGuides/nt8/onwindowcreated.htm). Basic sample line 83 uses `BeginInvoke` instead. | Both are official patterns; installed API and chosen dispatcher behavior need runtime verification. |
| `NTWindow`, `Caption`, `Width`, `Height`, `Content` | [NTWindow](https://static.ninjatrader.com/support/helpGuides/nt8/ntwindow.htm) and [own window](https://static.ninjatrader.com/support/helpGuides/nt8/creating_your_own_addon_window.htm) describe the `Tools.NTWindow` type; sample imports `NinjaTrader.Gui.Tools`. | Non-nested type and default constructor required. Sample uses tabs and workspace persistence; ZoneKeeper's direct Grid/no-tabs window requires G14. |
| `Account.All`; `account.Executions`; `account.Positions` | [Account class](https://static.ninjatrader.com/support/helpGuides/nt8/account_class.htm) locks `Account.All` and `account.Executions`; [account Positions](https://static.ninjatrader.com/support/helpGuides/nt8/positions_account.htm) locks `account.Positions`. | Enumerate each collection under its own lock; verify installed collection types if needed. |
| Instance `account.ExecutionUpdate`; `private void OnExecutionUpdate(object sender, ExecutionEventArgs e)`; `e.Execution` | [ExecutionUpdate](https://static.ninjatrader.com/support/helpGuides/nt8/executionupdate.htm); sample line 1700. Event covers added, amended and removed executions. | Unsubscribe on shutdown. Installed event/args namespaces and operation semantics still need checking. |
| `ExecutionEventArgs.Operation` | **Confirmed in official Basic sample**, line 1721, where `e.Operation` is printed. This is stronger evidence than the older plan's forum-only reference. | Sample does not name the enum type or its members. Verify `Add`, `Update`, `Remove` in Object Browser. |
| Static `Account.AccountStatusUpdate`; `private void OnAccountStatusUpdate(object sender, AccountStatusEventArgs e)`; `e.Account`, `e.Status` | [AccountStatusUpdate](https://static.ninjatrader.com/support/helpGuides/nt8/accountstatusupdate.htm); sample line 1183 and output at 1197–1198. | Status property type/enum and full args namespace not stated by the sample; verify on Windows. |
| Static `Account.SimulationAccountReset`; `private void OnSimulationAccountReset(object sender, EventArgs e)`; sender cast to `Account`; `Provider.Playback` | [SimulationAccountReset](https://static.ninjatrader.com/support/helpGuides/nt8/simulationaccountreset.htm); sample lines 1973–1989. Manual reset and Playback rewind/fast-forward trigger it. | Use `System.EventArgs`; verify installed Account/Provider namespaces and reset handling. The help page's syntax heading has a typo, `SimulationAccountRest`; use the example's `SimulationAccountReset`. |
| Account position `Instrument`, `MarketPosition`, `Quantity`, `AveragePrice` | Basic sample lines 697–706 enumerate `account.Positions` and use all four members; [account Positions](https://static.ninjatrader.com/support/helpGuides/nt8/positions_account.htm) also uses MarketPosition/AveragePrice. | Verify installed member types and signed size handling. This is account-position evidence, not only the strategy Position page. |
| Execution `Account`, `Instrument`, `ExecutionId`, `MarketPosition`, `Name`, `Order`, `OrderId`, `Quantity`, `Position`, `Price`, `Commission`, `Time` | [Execution](https://static.ninjatrader.com/support/helpGuides/nt8/execution.htm): Quantity/Position are int; Price/Commission double; Time DateTime; ExecutionId string; MarketPosition Long/Short. | Order can be null. Position is described as account quantity at execution time, without sign or before/after guarantees. G0 must verify. |
| `MasterInstrument.PointValue`, `.TickSize`, `.Currency`; `Currency.UsDollar` | [MasterInstrument](https://static.ninjatrader.com/support/helpGuides/nt8/masterinstrument.htm) and [Currency](https://static.ninjatrader.com/support/helpGuides/nt8/masterinstrument_currency.htm). | Other six currency enum names are not enumerated. Check denomination vs instrument currency and point value on Windows. |
| `Core.Globals.GeneralOptions.TimeZoneInfo` → `TimeZoneInfo` | **Directly documented** by [Application TimeZoneInfo](https://static.ninjatrader.com/support/helpGuides/nt8/application_timezoneinfo.htm): Tools → Options → General → Preferences → Time zone. It is not forum-only as the older plan states. | Installed availability and Execution.Time's relationship to this zone still need verification. |

## Windows Object Browser and runtime checklist

Use NinjaScript Editor's Visual Studio button, then View → Object Browser. Record the NinjaTrader version, assembly versions, actual type names and answers below. Do not build the add-on in Visual Studio.

- [ ] Base type `NinjaTrader.NinjaScript.AddOnBase`, `NinjaTrader.Gui.Tools.NTWindow`, `NinjaTrader.Gui.ControlCenter`, and the installed namespaces for Account, Execution, Position, MasterInstrument, Provider, Currency and both event-args types.
- [ ] `ExecutionEventArgs.Operation`: property type and exact `Add`/`Update`/`Remove` enum members. Sample proves property existence for its version, not the installed version. If absent, the plan's ID-based Add/Update fallback cannot detect Remove; record that limit.
- [ ] `AccountStatusEventArgs.Account` and `.Status`: property types, enum names and which transitions indicate reconnect. Do not infer the Status enum from string examples.
- [ ] `Position.Instrument`, `.MarketPosition`, `.Quantity`, `.AveragePrice`: installed types; account signed size derived from MarketPosition. Verify resync for long, short and flat positions.
- [ ] `Currency`: confirm `UsDollar`, and the expected `CanadianDollar`, `Euro`, `BritishPound`, `AustralianDollar`, `SwissFranc`, `NewZealandDollar`. Fix the string map if actual names differ; unknown currencies must not be sent.
- [ ] `Core.Globals.UserDataDir`: member type and actual folder, including redirected Documents. No current help-guide member page was found in this bounded search. The plan's cited forum URL returned 404, so this member remains unverified here. Use `Path.Combine`.
- [ ] `Core.Globals.GeneralOptions.TimeZoneInfo`: installed member/type; compare Tools → Options zone with actual fill timestamps. Convert an Unspecified fill DateTime through the NinjaTrader zone to UTC; verify the local fallback and DST behavior.
- [ ] `Execution.Position`: long/short sign and whether it is before or after the fill. Record entry, scale-in, partial exit, flat close and reversal values. The pairing checks currently assume quantity after the fill; change only with Windows evidence.
- [ ] `Execution.MarketPosition`: confirm fill buy/sell direction for long entries/exits, short entries/exits and reversals. Confirm null Order handling, commission update behavior and account denomination.
- [ ] `State.Active`: engine starts once; compile-created copies do not start duplicate workers; Terminated unsubscribes and disposes. G15 recompile must keep one engine and one trade delivery. Verify owner-aware start/stop prevents an old instance from stopping or replacing the newer pending engine.
- [ ] Account replacement and shutdown drain: fills, reset and resync use captured Account objects and reject stale replaced objects at worker execution. G0 checks reconnect/replacement; G7/G15 check shutdown drains queued work against frozen final account identities and saves the result without accepting replaced-account events.
- [ ] Dispatcher APIs and thread behavior: callbacks/events, window opening via RandomDispatcher, UI updates through the window Dispatcher, closing/reopening and recompiling with a window open. Activating the existing single window must run on that window's owning Dispatcher.
- [ ] Startup file access: distinguish actual Windows sharing/lock failures from other I/O/access failures. Verify sharing errors retry before OtherInstance, other file/access failures retry without claiming another engine owns the lock, and normal shutdown releases it.
- [ ] Direct Grid NTWindow with public default constructor; one window only; no workspace persistence required for this settings panel. G14 must prove restart/save has no errors and engine keeps working.
- [ ] Exact import/export/remove menu names; F5 compiler accepts C# 7.3 source and required framework references. Export/import, clean installation and same-name assembly update remain unverified until G12/G13.

## Final server contract used by Phase B

The 2026-10-13 additions to the server plan override older Phase B proposals:

- `POST /api/nt/disconnect` uses Bearer auth and no body. `200 {"ok":true}` or `401 {"error":"not_connected"}` means delete the local key. There is no paid gate on this route; trades remain saved. Do not expect `{"revoked":true}`.
- Optional `exit_name` is singular: final exit order name, trimmed, at most 32 characters, no control characters. Missing/null/empty becomes null. `/api/trades` returns it. Do not send `exit_names`.
- Duplicate saved trades do not count toward the daily new-trade cap. On a 200 trades answer the whole batch is finished, including rejected entries; remove it from the outbox and log rejections.
- Use the exact live base `https://getzonekeeper.com`, no `www`. Disable redirects and cookies in HttpClient. Never log the device key, connection code, full settings or raw HTTP body.

## Local implementation notes

The pure `Settings` class receives an `ITokenProtector`; `WindowsTokenProtector` is the Windows DPAPI adapter. Check encryption/decryption with the current Windows user on Windows.

`open-trades.json` is an atomic checkpoint object `{version:1,open_trades:[...],pending_closed:[...]}`, rather than the original plan's bare list. Pending closed trades preserve a failed outbox handoff. The outbox rewrites at most 500 entries atomically and flushes to disk; only a clean durable snapshot is sent. If outbox and checkpoint writes both fail, pending memory needs the process to stay running until a retry saves it. These details do not constitute a Windows filesystem/runtime test.

Additional static source decisions from the builder's handoff:

- Owner-aware `ZkEngine.Start(this)` / `Stop(this)` protects the newer pending engine during F5 recompilation.
- Fill, reset and resync work carries the captured Account object and checks `IsCurrent` when the worker runs it. Events from a replaced account object must not alter the new object's restored state.
- Shutdown drain uses frozen final account identities so queued fills from the final current objects can finish after subscriptions stop, while stale replaced objects stay excluded.
- Reopening the single window activates it on its own Dispatcher. The browser button uses the current cabinet address `https://getzonekeeper.com/app/#platforms`.
- Directory/access and non-sharing I/O failures retry after 15 seconds. Actual Windows sharing/lock errors 32/33 retry for 10 seconds before OtherInstance.

These are source decisions, not Windows pass results. G0 account replacement and G7/G15 shutdown, queued-fill drain, dispatcher and recompile behavior remain unverified until Roman tests them on Windows.

No test count or Windows pass claim is recorded here. Pure test results belong in the coordinator's final report; Windows G0–G15 results must be recorded separately, with G8 live lapse deferred unless naturally available, G9 blocked for live uploads, and G11 optional.
