# FlexRiskGuard — P0 SIM-only

This branch incrementally extends the original AddOn and local UI iterations. P0 is internal lock-state management ONLY: no order submission, cancellation or flattening. An internally locked account can still trade in NT8. Do not use this as broker-side protection.

## Delivered scope
- Selected Sim account manual lock/resume; explicit confirmation on resume.
- Global manual lock applies to every Sim account, including accounts selected later. Global resume does not clear individual locks.
- User-triggered violation starts a configurable 1–1440 minute cooldown (default 30). Cooldown applies across accounts, survives restart and requires explicit resume after expiry.
- Second violation and End Day lock all Sim accounts for the current Beijing 06:00 session. Manual and pending cooldown locks survive day changes.
- Atomic XML state replacement with a backup in Documents/NinjaTrader 8/FlexRiskGuard50K/behavior-v1.xml. Invalid files display an unreliable state; never silently overwrite corrupt state.
- Operation log and selected account status. SIM classification currently uses the existing Sim name-prefix convention, not a verified account capability API.

## P1 evaluation, not automated enforcement
AccountItem supplies realized/unrealized P&L and net liquidation. MLL remains manual. ExecutionUpdate supplies approximate entry counts, deduplicated by execution/order IDs in memory; partials do not normally count as separate entries. Buy/SellShort classification is inadequate for reversals, offline history and some copied/ATM orders. These figures are explicitly unverified and do not enforce trading rules. Default displayed daily count is 5. Remaining prototype capital constants are illustrative calculator parameters, not validated rules; configurable capital settings, correlated/aggregate risk and reliable daily ledger are deferred to P1/P2. No initiative/setup automation.

## Install
Back up existing Custom files. Copy BOTH FlexRiskGuard50K.cs and P0Store.cs into Documents/NinjaTrader 8/bin/Custom/AddOns, compile with NinjaScript Editor F5 and restart. Do not use the earlier one-file copy command alone. Open New > Flex Risk Guard 50K (SIM). The previous automatic eligibility, cancellation and testing-reset workflow is superseded by P0. Legacy P&L files remain separate; P0 lock state starts in its own XML file.

## Validation performed
Compiled both files against installed NT8 8.1.9.0 assemblies using the .NET Framework compiler. Standalone P0 tests passed for lock persistence, account isolation, global lock, cross-account cooldown, early-resume refusal, second violation, Sim-name restriction and corrupt-file reporting. This is NOT an NT8 F5/in-app/SIM order test. Test source is in tests/P0Tests.cs.

## SIM acceptance plan (not yet executed)
1. Sim101: lock, close/reopen panel, restart NT8; verify lock remains.
2. Select Sim102: individual Sim101 lock must not leak; global lock must cover both. Release global lock and verify individual lock stays.
3. Set 1-minute cooldown, record violation; switch accounts/restart and verify countdown persists. Early resume fails; expiry remains locked until confirmed resume.
4. Record second violation: all Sim accounts remain internally locked until next Beijing 06:00 session. End Day has the same daily scope. Manual global lock survives this boundary.
5. Confirm no cancel/flatten/submission calls occur, including when protective orders exist. No test order is required to validate P0 state management.
6. With NT8 closed, back up and corrupt the P0 file; reopening must show unreliable state. Restore backup. Exercise unwritable storage and verify operation failure is reported.
7. Verify different Sim names maintain independent manual locks; inspect log for each action and refusal. Logs are session-memory only, while lock state is persisted.

## Known limitations / remaining work
Only the selected account has live statistics; an all-account status table, durable audit log, verified SIM capability detection, connection health/reconciliation and configurable capital settings remain future work. A write failure leaves the last successfully saved state intact and reports the failed action; it is not a platform lock. No password or token is stored. No P2 order controls are enabled.

