# Leaderboard Task ID -> Submission Task Type Mapping

_Updated 2026-03-21 from `tasks/PRIORITY_EXECUTION_ORDER.md`, `knowledge.md`, task folders, and current competition log artifacts._

## Summary

- **30 task IDs** are now mapped to the current competition task set
- **14 distinct task types** are represented in our current runs
- The same task type still appears under **multiple IDs** because the competition uses prompt variants with different difficulty and evidence requirements
- **Current total best score: 53.6**
- **Leader total: 86.48**
- **Gap to leader: -32.88**
- **Task 26 is `annual_accounts`**, specifically the monthly-close variant

## Complete Mapping

| tx_task_id | task_type | Variant Description | Our Score | Leader | Gap | Status |
| :--------: | --------- | ------------------- | :-------: | :----: | :-: | :----: |
| 01 | `create_employee` | Basic employee creation | 2.00 | 2.00 | 0.00 | ✅ Tied |
| 02 | `create_customer` | Standard customer with address | 2.00 | 2.00 | 0.00 | ✅ Tied |
| 03 | `create_product` | Product with number and price | 2.00 | 2.00 | 0.00 | ✅ Tied |
| 04 | `create_supplier` | Supplier with org number and email | 2.00 | 2.00 | 0.00 | ✅ Tied |
| 05 | `create_department` | Multiple departments | 1.33 | 2.00 | -0.67 | ❌ Failing |
| 06 | `create_invoice` | Simple single-line invoice | 1.67 | 1.67 | 0.00 | ✅ Tied |
| 07 | `register_payment` | Simple payment on existing invoice | 2.00 | 2.00 | 0.00 | ✅ Tied |
| 08 | `create_project` | Basic project with customer and PM | 1.50 | 2.00 | -0.50 | ⚠️ Behind |
| 09 | `create_invoice` | Multi-line invoice | 2.67 | 4.00 | -1.33 | ❌ Failing |
| 10 | `register_payment` | Create order, invoice, and pay | 2.67 | 4.00 | -1.33 | ❌ Failing |
| 11 | `create_voucher` | Supplier invoice voucher | 0.00 | 4.00 | -4.00 | ❌ Failing |
| 12 | `run_payroll` | Standard payroll run | 1.00 | 4.00 | -3.00 | ❌ Failing |
| 13 | `create_travel_expense` | Travel expense with costs | 2.50 | 2.40 | +0.10 | ✅ Leading |
| 14 | `create_credit_note` | Credit note on existing invoice | 2.67 | 4.00 | -1.33 | ❌ Failing |
| 15 | `create_project` | Fixed-price project | 1.50 | 3.33 | -1.83 | ❌ Failing |
| 16 | `create_project` | Timesheet hours to project invoice | 2.80 | 3.00 | -0.20 | ⚠️ Behind |
| 17 | `create_voucher` | Custom dimension voucher | 3.50 | 3.50 | 0.00 | ✅ Tied |
| 18 | `register_payment` | Full payment chain | 4.00 | 4.00 | 0.00 | ✅ Tied |
| 19 | `create_employee` | Employee from PDF contract (Tier 3) | 2.45 | 2.73 | -0.28 | ⚠️ Behind |
| 20 | `create_voucher` | Supplier invoice from PDF (Tier 3) | 0.60 | 2.40 | -1.80 | ❌ Failing |
| 21 | `create_employee` | Employee from PDF offer letter (Tier 3) | 2.36 | 2.57 | -0.21 | ⚠️ Behind |
| 22 | `create_voucher` | Voucher from PDF receipt (Tier 3) | 0.00 | 0.00 | 0.00 | ❌ Both fail |
| 23 | `bank_reconciliation` | CSV bank statement matching (Tier 3) | 0.00 | 0.60 | -0.60 | ❌ Failing |
| 24 | `create_voucher` | Ledger correction (Tier 3) | 2.25 | 2.25 | 0.00 | ✅ Tied |
| 25 | `register_payment` | Find overdue invoice and pay (Tier 3) | 0.60 | 6.00 | -5.40 | ❌ Failing |
| 26 | `annual_accounts` | Monthly close or month-end closing (Tier 3) | 2.55 | 6.00 | -3.45 | ❌ Failing |
| 27 | `register_payment` | Foreign currency EUR payment (Tier 3) | 0.60 | 6.00 | -5.40 | ❌ Failing |
| 28 | `create_project` | Cost analysis from ledger (Tier 3) | 1.50 | 1.50 | 0.00 | ✅ Tied |
| 29 | `create_project` | Full project lifecycle (Tier 3) | 1.09 | 2.73 | -1.64 | ❌ Failing |
| 30 | `create_voucher` | Annual accounts voucher flow (Tier 3) | 1.80 | 1.80 | 0.00 | ✅ Tied |

## Task Types with Multiple IDs

Each task type still has multiple variants of increasing complexity:

| Task Type | IDs | Count | Combined Score |
| --------- | --- | :---: | :------------: |
| `create_employee` | 01, 19, 21 | 3 | 6.81 |
| `create_customer` | 02 | 1 | 2.00 |
| `create_product` | 03 | 1 | 2.00 |
| `create_supplier` | 04 | 1 | 2.00 |
| `create_department` | 05 | 1 | 1.33 |
| `create_invoice` | 06, 09 | 2 | 4.34 |
| `register_payment` | 07, 10, 18, 25, 27 | 5 | 9.87 |
| `create_project` | 08, 15, 16, 28, 29 | 5 | 8.39 |
| `create_voucher` | 11, 17, 20, 22, 24, 30 | 6 | 8.15 |
| `run_payroll` | 12 | 1 | 1.00 |
| `create_travel_expense` | 13 | 1 | 2.50 |
| `create_credit_note` | 14 | 1 | 2.67 |
| `bank_reconciliation` | 23 | 1 | 0.00 |
| `annual_accounts` | 26 | 1 | 2.55 |

## Current Zero-Score Tasks

Only **three** tasks are currently at 0.00. The previous zero-score list in this file was outdated.

| ID | Type | Variant | Current State |
| :-: | ---- | ------- | ------------- |
| 11 | `create_voucher` | Supplier invoice voucher | Still fully failing |
| 22 | `create_voucher` | Voucher from PDF receipt | Both teams currently score 0 |
| 23 | `bank_reconciliation` | CSV bank reconciliation | Still fully failing |

## Largest Remaining Gaps

These are the highest-value tasks to improve next based on the latest generated ranking:

| Priority | ID | Type | Variant | Us | Leader | Gap |
| :------: | :-: | ---- | ------- | :-: | :----: | :-: |
| 1 | 27 | `register_payment` | FX/EUR (T3) | 0.6 | 6.0 | -5.4 |
| 2 | 25 | `register_payment` | Overdue + reminder (T3) | 0.6 | 6.0 | -5.4 |
| 3 | 11 | `create_voucher` | Supplier invoice | 0.0 | 4.0 | -4.0 |
| 4 | 26 | `annual_accounts` | Monthly close (T3) | 2.55 | 6.0 | -3.45 |
| 5 | 12 | `run_payroll` | Standard | 1.0 | 4.0 | -3.0 |
| 6 | 15 | `create_project` | Fixed-price | 1.5 | 3.33 | -1.83 |
| 7 | 20 | `create_voucher` | PDF supplier invoice (T3) | 0.6 | 2.4 | -1.8 |
| 8 | 29 | `create_project` | Full lifecycle (T3) | 1.09 | 2.73 | -1.64 |

## Tier Analysis

| Tier | IDs | Our Total | Leader Total | Gap |
| ---- | --- | :-------: | :----------: | :-: |
| **Tier 1** (basic CRUD) | 01-05 | 9.33 | 10.00 | -0.67 |
| **Tier 2** (multi-step) | 06-18 | 28.48 | 41.90 | -13.42 |
| **Tier 3** (advanced/PDF) | 19-30 | 15.80 | 34.58 | -18.78 |

## Methodology

This file now uses a more reliable source stack than the original version:

- **Current scores, gap, and statuses** come from `tasks/PRIORITY_EXECUTION_ORDER.md`, which reflects the latest generated competition summary in this repository.
- **Task names and variants** are aligned with the task folder set under `tasks/`.
- **Task 26 mapping** is corrected from `knowledge.md`, which explicitly records that Task 26 is `annual_accounts` and not unknown.
- **Historical timestamp correlation** from `leaderboard.jsonl`, `results.jsonl`, and `submissions.jsonl` still supports the ID-to-task-type mapping, especially for the earlier task set.

### Important Note About Attempts

The earlier version of this file showed an `Attempts` column derived from older leaderboard snapshots. Those snapshots lag behind the latest generated 53.6-point table, so the attempt counts were removed rather than keeping numbers that would now be misleading.

If we want to restore `Attempts`, they should be rebuilt from the newest full JSONL set rather than copied forward from the older 38.24-point snapshot.

## Confidence

- **High confidence**: All 30 ID-to-task mappings match the current task folders and latest generated priority table.
- **Explicitly verified special case**: Task 26 = `annual_accounts`, monthly-close variant.
- **Retired claim**: Task 26 is no longer unknown, and the previous Tier 3 zero-score block is no longer valid.
