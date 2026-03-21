# Task 26 — Annual Accounts (Monthly Close, Tier 3)

## Overview

| Field            | Value                               |
| ---------------- | ----------------------------------- |
| **Task ID**      | 26                                  |
| **Task Type**    | `annual_accounts`                   |
| **Variant**      | Monthly close / month-end closing   |
| **Tier**         | 3                                   |
| **Our Score**    | 2.55                                |
| **Leader Score** | 6.00                                |
| **Gap**          | -3.45                               |
| **Status**       | ⚠️ Identified, partially solved     |
| **Handler**      | `AnnualAccountsHandler.cs`          |
| **Priority**     | Medium — improve final failed check |

## What It Does

Task 26 is no longer unknown. Raw competition logs show it is an `annual_accounts` variant handled by `AnnualAccountsHandler`, distinct from Task 30's simplified year-end closing.

Observed prompt pattern: month-end close for a named month, with a mix of:

1. Prepaid expense reversal / periodization
2. Depreciation of a fixed asset
3. Salary accrual / provision
4. Balance check or tax-related close entries

Example prompt from the scoring run: Spanish month-end close for March 2026 requesting periodization from account 1720, monthly depreciation to account 6020, zero trial-balance verification, and a salary provision on accounts 5000/2900.

Current best competition result: 5/6 checks passed, normalized score 2.55.

Source-of-truth evidence:

- `src/logs/submissions.jsonl` run `2b2ffac8` shows `task_type = annual_accounts`, `handler = AnnualAccountsHandler`
- `src/logs/results.jsonl` submission `e5c6b5db-e1f0-40cd-87c9-f7d8ce238eeb` shows `normalized_score = 2.55`
- `src/logs/leaderboard.jsonl` shows tx_task_id 26 improved from 0 -> 2.10 -> 2.55

## API Flow

Current successful flow mirrors `AnnualAccountsHandler`:

1. `GET /ledger/account?number=X` — resolve or create missing accounts used by the close
2. `POST /ledger/account` — create missing accounts when needed (e.g. 1209 / 8700 in some environments)
3. `POST /ledger/voucher?sendToLedger=true` — create vouchers for depreciation, prepaid reversal, and tax/accrual entries
4. `GET /ledger/posting?dateFrom=...&dateTo=...&accountNumberFrom=3000&accountNumberTo=8699` — compute P&L basis when tax entry is needed

## Competition Evidence

Known scoring progression from leaderboard snapshots:

- Attempt 1: 0.00
- Attempt 2: 0.00
- Attempt 3: 2.10
- Attempt 4: 2.55

Latest confirmed scored run:

- **Run ID**: `2b2ffac8`
- **Submission ID**: `e5c6b5db-e1f0-40cd-87c9-f7d8ce238eeb`
- **Task Type**: `annual_accounts`
- **Handler**: `AnnualAccountsHandler`
- **Result**: 5/6 checks passed

## Relationship To Task 30

Task 30 is also in the annual-accounts family, but it is the simplified year-end closing variant. Task 26 appears to be a month-end / monthly-close variant with a higher leader score and stricter checks. Treat them as separate prompt variants that share the same handler family.

## Current Gaps

We already create the relevant vouchers, but one competition check still fails. Likely remaining causes:

1. One expected entry is missing for the monthly-close variant
2. One amount/account pair is calculated differently from competition expectations
3. Local validator coverage for `annual_accounts` may still not fully mirror the competition checks for Task 26

## Action Required

- [x] Identify the prompt and task type
- [x] Confirm handler routing from raw logs
- [ ] Compare Task 26 failed check against Task 30's passing/tied behavior
- [ ] Update `SandboxValidator.cs` if local checks still diverge from competition for this variant
- [ ] Improve `AnnualAccountsHandler` for the remaining failed Task 26 check
