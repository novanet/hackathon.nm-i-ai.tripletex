# tasks — NM i AI 2026 Tripletex Agent

## Structure

Each task has its own folder with:

```
tasks/
  NN-task-variant/
    strategy.md     # Hand-written: analysis, root cause, fix plan (edit manually)
    prompts.md      # Auto-generated: all known prompts for this task
    runs.md         # Auto-generated: latest competition + sandbox results, API calls
    history.md      # Auto-generated: score progression across submissions
```

**Refresh auto-generated files:** `.\scripts\Refresh-Tasks.ps1`

Run this after every competition submission or local test session.

## Current Scores

| Metric | Value |
|---|---|
| **Our Total** | 48.58 pts |
| **Leader Total** | 83.92 pts |
| **Gap** | -35.34 pts |
| **Date** | 2026-03-21 |

## Priority Execution Order

Ordered by effort-to-gain ratio (lowest effort for most points first).

| # | Task | Folder | Gap | Expected Gain | Effort | Action |
|---|---:|---|---|---|---|---|
| 1 | 11 — Supplier Invoice Voucher | [11-create-voucher-supplier-inv](11-create-voucher-supplier-inv/) | -4.00 | +4.00 | TRIVIAL | Just submit — handler already fixed |
| 2 | 14 — Credit Note | [14-create-credit-note](14-create-credit-note/) | -1.33 | +1.00 | LOW | Trim 1–2 write calls |
| 3 | 06+09 — Invoices | [06](06-create-invoice-simple/), [09](09-create-invoice-multiline/) | -0.50 | +0.50 | LOW | Same invoice efficiency fix helps both |
| 4 | 15 — Fixed-Price Project | [15-create-project-fixed-price](15-create-project-fixed-price/) | -1.30 | +1.00 | LOW | Submit to verify Fix 11, then tune |
| 5 | 16 — Project + Timesheet | [16-create-project-timesheet](16-create-project-timesheet/) | -0.20 | +0.20 | TRIVIAL | Micro-optimization |
| 6 | 25 — Overdue Invoice + Reminder | [25-register-payment-overdue-reminder](25-register-payment-overdue-reminder/) | -5.25 | +3.00 | MEDIUM | Simplify reminder flow, fix routing |
| 7 | 27 — FX Payment (EUR) | [27-register-payment-fx-eur](27-register-payment-fx-eur/) | -5.40 | +3.00 | MEDIUM | Debug FX payment flow |
| 8 | 24 — Ledger Correction | [24-create-voucher-ledger-correction](24-create-voucher-ledger-correction/) | -3.75 | +2.00 | MEDIUM | Fix extraction + posting logic |
| 9 | 20 — PDF Supplier Invoice | [20-create-voucher-pdf-supplier](20-create-voucher-pdf-supplier/) | -5.40 | +2.00 | MEDIUM | Improve PDF → voucher extraction |
| 10 | 19+21 — PDF Employee | [19](19-create-employee-pdf-contract/), [21](21-create-employee-pdf-offer/) | -1.54 | +1.00 | MEDIUM | Improve PDF → employee extraction |
| 11 | 29 — Project Full Lifecycle | [29-create-project-lifecycle](29-create-project-lifecycle/) | -3.82 | +2.00 | HIGH | Fix composite project lifecycle |
| 12 | 26 — Unknown Task | [26-unknown](26-unknown/) | -3.75 | +1.50 | UNKNOWN | Identify task first |
| 13 | 12 — Payroll | [12-run-payroll](12-run-payroll/) | +1.00 | +1.00 | MEDIUM | Already ahead, refine |

**If you execute #1–8, expected gain is ~14.70 pts** → total ~63 pts.

## All Tasks

| Task | Type | Variant | Tier | Us | Leader | Gap | Status | Folder |
|:---:|---|---|:---:|:---:|:---:|---:|---|---|
| 01 | `create_employee` | Basic | 1 | 2.00 | 1.50 | +0.50 | ✅ Leading | [01-create-employee-basic](01-create-employee-basic/) |
| 02 | `create_customer` | Standard | 1 | 2.00 | 2.00 | 0 | ✅ Tied | [02-create-customer](02-create-customer/) |
| 03 | `create_product` | Standard | 1 | 2.00 | 2.00 | 0 | ✅ Tied | [03-create-product](03-create-product/) |
| 04 | `create_supplier` | Standard | 1 | 2.00 | 2.00 | 0 | ✅ Tied | [04-create-supplier](04-create-supplier/) |
| 05 | `create_department` | Multi | 1 | 1.33 | 1.33 | 0 | ✅ Tied | [05-create-department-multi](05-create-department-multi/) |
| 06 | `create_invoice` | Simple | 2 | 1.33 | 1.50 | -0.17 | ⚠️ Behind | [06-create-invoice-simple](06-create-invoice-simple/) |
| 07 | `register_payment` | Simple existing | 2 | 2.00 | 2.00 | 0 | ✅ Tied | [07-register-payment-simple](07-register-payment-simple/) |
| 08 | `create_project` | Basic | 2 | 1.50 | 1.50 | 0 | ✅ Tied | [08-create-project-basic](08-create-project-basic/) |
| 09 | `create_invoice` | Multi-line | 2 | 2.67 | 3.00 | -0.33 | ⚠️ Behind | [09-create-invoice-multiline](09-create-invoice-multiline/) |
| 10 | `register_payment` | Create + pay | 2 | 2.67 | 2.67 | 0 | ✅ Tied | [10-register-payment-create-pay](10-register-payment-create-pay/) |
| 11 | `create_voucher` | Supplier invoice | 2 | 0.00 | 4.00 | -4.00 | ❌ Failing | [11-create-voucher-supplier-inv](11-create-voucher-supplier-inv/) |
| 12 | `run_payroll` | Standard | 2 | 1.00 | 0.00 | +1.00 | ✅ Leading | [12-run-payroll](12-run-payroll/) |
| 13 | `create_travel_expense` | With costs | 2 | 2.50 | 2.50 | 0 | ✅ Tied | [13-create-travel-expense](13-create-travel-expense/) |
| 14 | `create_credit_note` | Standard | 2 | 2.67 | 4.00 | -1.33 | ⚠️ Behind | [14-create-credit-note](14-create-credit-note/) |
| 15 | `create_project` | Fixed-price | 2 | 1.50 | 2.80 | -1.30 | ⚠️ Behind | [15-create-project-fixed-price](15-create-project-fixed-price/) |
| 16 | `create_project` | Timesheet hours | 2 | 2.80 | 3.00 | -0.20 | ⚠️ Behind | [16-create-project-timesheet](16-create-project-timesheet/) |
| 17 | `create_voucher` | Custom dimension | 2 | 3.50 | 3.50 | 0 | ✅ Tied | [17-create-voucher-dimension](17-create-voucher-dimension/) |
| 18 | `register_payment` | Full chain | 2 | 4.00 | 4.00 | 0 | ✅ Full | [18-register-payment-full-chain](18-register-payment-full-chain/) |
| 19 | `create_employee` | PDF contract (T3) | 3 | 1.77 | 2.45 | -0.68 | ⚠️ Behind | [19-create-employee-pdf-contract](19-create-employee-pdf-contract/) |
| 20 | `create_voucher` | PDF supplier inv (T3) | 3 | 0.60 | 6.00 | -5.40 | ❌ Failing | [20-create-voucher-pdf-supplier](20-create-voucher-pdf-supplier/) |
| 21 | `create_employee` | PDF offer letter (T3) | 3 | 1.50 | 2.36 | -0.86 | ⚠️ Behind | [21-create-employee-pdf-offer](21-create-employee-pdf-offer/) |
| 22 | `create_voucher` | PDF receipt (T3) | 3 | 0.00 | 0.00 | 0 | ❌ Both fail | [22-create-voucher-pdf-receipt](22-create-voucher-pdf-receipt/) |
| 23 | `bank_reconciliation` | CSV (T3) | 3 | 0.00 | 0.60 | -0.60 | ❌ Failing | [23-bank-reconciliation-csv](23-bank-reconciliation-csv/) |
| 24 | `create_voucher` | Ledger correction (T3) | 3 | 2.25 | 6.00 | -3.75 | ⚠️ Behind | [24-create-voucher-ledger-correction](24-create-voucher-ledger-correction/) |
| 25 | `register_payment` | Overdue + reminder (T3) | 3 | 0.00 | 5.25 | -5.25 | ❌ Failing | [25-register-payment-overdue-reminder](25-register-payment-overdue-reminder/) |
| 26 | ??? | Unknown | 3 | 0.00 | 3.75 | -3.75 | ❓ Unknown | [26-unknown](26-unknown/) |
| 27 | `register_payment` | FX/EUR (T3) | 3 | 0.60 | 6.00 | -5.40 | ❌ Failing | [27-register-payment-fx-eur](27-register-payment-fx-eur/) |
| 28 | `create_project` | Cost analysis (T3) | 3 | 1.50 | 1.50 | 0 | ✅ Tied | [28-create-project-cost-analysis](28-create-project-cost-analysis/) |
| 29 | `create_project` | Full lifecycle (T3) | 3 | 1.09 | 4.91 | -3.82 | ⚠️ Behind | [29-create-project-lifecycle](29-create-project-lifecycle/) |
| 30 | `create_voucher` | Annual accounts (T3) | 3 | 1.80 | 1.80 | 0 | ✅ Tied | [30-create-voucher-annual-accounts](30-create-voucher-annual-accounts/) |

## Tier Summary

| Tier | Tasks | Our Total | Leader Total | Gap |
|---|:---:|:---:|:---:|---:|
| Tier 1 (basic CRUD) | 01–05 | 9.33 | 8.83 | +0.50 |
| Tier 2 (multi-step) | 06–18 | 26.14 | 32.67 | -6.53 |
| Tier 3 (advanced/PDF) | 19–30 | 13.11 | 42.42 | -29.31 |
