# Leader Comparison — Score Tracking

*Updated manually after each submission run.*

## Score Checkpoints

| Task | Type | Tier | CP1 | CP2 | Leader | Gap | Last Run |
|:---:|---|:---:|:---:|:---:|:---:|:---:|---|
| 01 | `create_employee` (basic) | 1 | 2.00 | 2.00 | 2.00 | 0 | 03/22 03:33 |
| 02 | `create_customer` | 1 | 2.00 | 2.00 | 2.00 | 0 | 03/22 07:17 |
| 03 | `create_product` | 1 | 2.00 | 2.00 | 2.00 | 0 | 03/22 03:03 |
| 04 | `create_supplier` | 1 | 2.00 | 2.00 | 2.00 | 0 | 03/22 03:16 |
| 05 | `create_department` (multi) | 1 | 1.33 | 1.50 | 2.00 | -0.50 | 03/21 23:59 |
| 06 | `create_invoice` (simple) | 2 | 1.33 | 1.33 | 1.33 | 0 | 03/22 03:30 |
| 07 | `register_payment` (simple) | 2 | 2.00 | 2.00 | 2.00 | 0 | 03/22 03:29 |
| 08 | `create_project` (basic) | 2 | 1.50 | 1.50 | 2.00 | -0.50 | 03/22 03:36 |
| 09 | `create_invoice` (multi-line) | 2 | 2.67 | 2.67 | 4.00 | -1.33 | 03/22 07:21 |
| 10 | `register_payment` (create+pay) | 2 | 2.67 | 2.67 | 4.00 | -1.33 | — |
| 11 | `create_voucher` (supplier inv) | 2 | — | — | 4.00 | -4.00 | 03/22 03:35 |
| 12 | `run_payroll` | 2 | 1.00 | 1.00 | 4.00 | -3.00 | 03/21 23:54 |
| 13 | `create_travel_expense` | 2 | 2.50 | 2.50 | 2.40 | +0.10 | 03/22 01:55 |
| 14 | `create_credit_note` | 2 | 2.67 | 2.67 | 4.00 | -1.33 | 03/22 00:03 |
| 15 | `create_project` (fixed-price) | 2 | 1.50 | 1.50 | 3.33 | -1.83 | 03/22 00:02 |
| 16 | `create_project` (timesheet) | 2 | 2.40 | 2.40 | 2.67 | -0.27 | — |
| 17 | `create_voucher` (dimension) | 2 | 3.50 | 3.50 | 3.50 | 0 | 03/22 07:18 |
| 18 | `register_payment` (full chain) | 2 | 4.00 | 4.00 | 4.00 | 0 | 03/22 02:33 |
| 19 | `create_employee` (PDF contract) | 3 | 2.73 | 2.73 | 2.73 | 0 | 03/22 07:20 |
| 20 | `create_voucher` (PDF supplier) | 3 | 0.60 | 0.60 | 4.80 | -4.20 | 03/22 06:43 |
| 21 | `create_employee` (PDF offer) | 3 | 2.36 | 2.36 | 2.57 | -0.21 | 03/21 23:39 |
| 22 | `create_voucher` (PDF receipt) | 3 | — | — | 2.70 | -2.70 | 03/22 01:51 |
| 23 | `bank_reconciliation` (CSV) | 3 | — | — | 6.00 | -6.00 | 03/22 07:17 |
| 24 | `create_voucher` (ledger corr.) | 3 | 2.25 | 2.25 | 6.00 | -3.75 | 03/22 03:37 |
| 25 | `register_payment` (overdue) | 3 | 2.40 | **4.80** | 6.00 | -1.20 | 03/22 02:31 |
| 26 | `annual_accounts` (monthly) | 3 | 3.60 | 3.60 | 6.00 | -2.40 | 03/22 03:37 |
| 27 | `register_payment` (FX/EUR) | 3 | 1.50 | 1.50 | 6.00 | -4.50 | 03/22 03:32 |
| 28 | `create_project` (cost analysis) | 3 | 1.50 | 1.50 | 1.50 | 0 | 03/22 07:20 |
| 29 | `create_project` (lifecycle) | 3 | 1.09 | 1.09 | 2.73 | -1.64 | — |
| 30 | `create_voucher` (annual accts) | 3 | 1.80 | 1.80 | 6.00 | -4.20 | 03/22 03:30 |
| | **TOTAL** | | **~56.9** | **~59.5** | **~104.3** | **-44.8** | | |

## Checkpoint Log

| CP | Date | Total | Delta | Notes |
|---|---|---:|---:|---|
| CP1 | 2026-03-22 | 56.9 | — | Baseline |
| CP2 | 2026-03-22 | 59.5 | +2.6 | Task 25 overdue payment +2.40, Task 05 dept +0.17 |

## How to Append a New Checkpoint

1. Add a new column (e.g. `CP3`) after the latest checkpoint in the table above
2. Fill in updated scores from the leaderboard
3. Update the `Leader` and `Gap` columns if the leader has changed
4. Add a row to the Checkpoint Log with date, total, delta, and notes on what improved
