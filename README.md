# Flex Risk Guard 50K — NinjaTrader 8 / SIM-ONLY v0.1

**Status:** source-only prototype; documented public NT8 APIs checked, but **not compiled or executed in NinjaTrader 8** in this environment. Test on Sim101/Playback before considering any further development. This is NOT a verified live-trading risk-control product.

## What this version actually does

- Adds `Control Center > New > Flex Risk Guard 50K (SIM)`.
- Lists and subscribes to accounts whose NT8 name begins with `Sim`.
- Reads NT8 realized P&L, unrealized P&L and net liquidation; allows manual entry of the **current applicable MLL**; estimates distance to that line.
- Manual `start day` is allowed only when flat with no working orders; resets the **local** baseline and entry counter. The day is computed from PC local date, **not** the CME or FundedNext trading session.
- Configures a demo-only stop threshold ($300 approximate loss), max new entry count (3), buffer floor ($500), manual lock and 30-minute cooldown.
- On lock, attempts to cancel newly submitted **opening orders when the instrument is flat**. This is an **asynchronous, best-effort** cancellation, NEVER a pre-submit block. A market entry may fill before cancellation. It deliberately skips orders when any position is open so as not to cancel protective/exit orders.
- Provides a manually entered position risk calculator for 1+ lots of MNQ ($2/point), MES ($5/point), MGC ($10 per $1 gold move). This **does not** directly block Chart Trader.
- Writes a local data file in `Documents/NinjaTrader 8/FlexRiskGuard50K/`. Account selection/re-attachment locks by default; a restart cannot silently re-arm the plugin. State load does not reconstruct missed executions.

## Important missing safeguards

1. It does **not** enforce $150 risk per entry or $200 aggregate open risk against all NT8 orders. Ordinary Chart Trader entry orders have no dependable pre-submission stop information; protective ATM stops may be created only after entry fill. A correct pre-order gate needs a dedicated, risk-aware order entry flow and extensively tested bracket handling.
2. `Account.OrderUpdate` is notified **after** an order has been submitted. It cannot stop immediate fills or remotely placed orders, and cannot guarantee complete coverage after disconnects, process crash, workspace close or any NT8 / broker error.
3. It does not flatten positions or cancel protective stops. Locking with existing positions does NOT ensure no further risk can be added by other interfaces. Do not treat this as a hard trading lock.
4. Realized P&L baseline and trade count only reflect a **manually initialized** local period while observed. Session resets, fills while disconnected, amended executions, partials, reversals and multiple connections can make counts and P&L inaccurate. The entry counter sees `Buy`/`SellShort` executions and is approximate.
5. FundedNext's live MLL **cannot** be assumed from an NT8 account name. Your simulated account may have a different initial balance; calibrate the MLL to that account to test. FundedNext controls and payout rules remain authoritative.
6. The file persists a few values but never restores an **unlocked** state. It is not an atomic, tamper-proof ledger; write failures cause the UI to lock if the error is observed.
7. The simulator's net liquidation may differ from FundedNext's actual margin, fee, payout, and loss-limit calculations. Broker/prop-firm platform protections should be used independently where supported.

## Installation (source file, not NT8 import ZIP)

1. Back up the existing `Documents/NinjaTrader 8/bin/Custom` folder. Do not overwrite an existing AddOn with this file name.
2. Copy `FlexRiskGuard50K.cs` to `Documents/NinjaTrader 8/bin/Custom/AddOns/` (create folder only if NinjaTrader already uses that structure). Alternatively use the NT8 NinjaScript Editor > AddOn > New to create `FlexRiskGuard50K` and replace its content with this file.
3. In NT8 NinjaScript Editor, press **F5** to compile. If compilation fails, preserve the compiler error and line numbers; the build is not verified here. The NT8 application's installed build is the authority.
4. Restart NinjaTrader (recommended), select **Sim101**, and open `Control Center > New > Flex Risk Guard 50K (SIM)`.
5. With no positions or active orders, use **开始新交易日**; set **current MLL** relative to your actual simulation net liquidation (e.g. `net liquidation - 1500` for a test of the initial buffer), then use **模拟测试：复核后解锁**.
6. Experiment on Sim101 with limit and market entries. Expect the market order to fill even if cancellation is requested. **Do not test on FundedNext evaluation or funded accounts.**

> Do NOT import this project's general-purpose .zip through `Tools > Import > NinjaScript Add-On`: it is a download bundle, not an NT8-exported NinjaScript archive. Compile the .cs file in the editor first.

## Acceptance tests (Sim101/Playback only)

- [ ] Fresh install: selects only `Sim` accounts and remains locked until explicitly initialized/reviewed.
- [ ] Persisted manual lock remains locked after reopening/restarting NT8.
- [ ] Locked flat account + resting limit entry: attempts cancellation; check the NT8 Orders grid for an actual **Cancelled** acknowledgment.
- [ ] Locked flat account + market order: record if it fills before cancellation. **Expected possibility:** fill goes through.
- [ ] With a position open: protective stop/target orders must remain unchanged. Manually verify no cancellation of protective orders.
- [ ] Manual cooldown cannot be cleared early via the UI; after 30 minutes it still requires review/unlock.
- [ ] Daily P&L limit and number of entry executions result in local locked status, keeping in mind their documented estimation limits.
- [ ] MLL is unknown until entered. Input an MLL within $500 of simulated account equity and confirm unlock is refused.
- [ ] Stop risk calculator: 1 MNQ, 75 points -> $150; 1 MES, 30 points -> $150; 1 MGC, $15 price distance -> $150.
- [ ] Close/reopen the panel, restart application and verify monitoring is **not assumed** when UI is closed and that re-arming requires checks.
- [ ] NT8 is disconnected while orders exist; document that execution/cancel cannot be guaranteed.

## Rationale and next stage

This prototype deliberately prioritizes accurate **disclosure of limitations** over pretending to have a complete hard block. A v0.2 would add a guarded custom entry interface to pre-check structural stop and portfolio risk before submitting any bracket; only orders entered through that guarded interface would benefit from a true local pre-submit rule. Live use would still need platform-side protections, reconnection reconciliation and extensive SIM tests.

## Official technical references

- https://static.ninjatrader.com/support/helpGuides/nt8/account_class.htm
- https://static.ninjatrader.com/support/helpGuides/nt8/orderupdate.htm
- https://static.ninjatrader.com/support/helpGuides/nt8/cancel.htm
- https://static.ninjatrader.com/support/helpGuides/nt8/accountitem.htm
- https://static.ninjatrader.com/support/helpGuides/nt8/creating_your_own_addon_window.htm
- https://helpfutures.fundednext.com/en/articles/17229848-fundednext-futures-flex-fundednext-account-all-rules