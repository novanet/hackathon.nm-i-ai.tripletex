# create_voucher — Competition vs Sandbox Analysis

**Date:** 2026-03-21

## Summary

23 `create_voucher` prompts found in `submissions.jsonl`. 20 matched to competition results in `results.jsonl`. Five distinct sub-categories identified with very different pass rates.

## Competition Results by Category

| Category | Entries | Agent OK | Comp Perfect | Checks Passed | Points |
|---|---|---|---|---|---|
| Dimension + Voucher | 8 | 7/8 | 7/8 | 42/42 | 91/91 |
| Supplier Invoice | 11 | 9/11 | 1/11 | 6/38 | ~12/76 |
| PDF Supplier Invoice | 2 | 2/2 | 0/2 | 1/11 | ~2/22 |
| Ledger Error Correction | 1 | 0/1 | 0/1 | 0/4 | 0/8 |
| Year-End Closing | 1 | 0/1 | 0/1 | 0/6 | 0/12 |

## Sandbox Replay (current branch)

17 of 23 prompts validated (6 failed before validation due to sandbox state pollution).

- **Total checks:** 68/84 passed
- **Total points:** 136/184
- **Every single `correct_amount` check fails** (actual=0, expected=non-zero) — validator bug

### Per-category sandbox results

- **Dimension + Voucher:** 5 validated, all 4/5 checks (only `correct_amount` fails)
- **Supplier Invoice:** 11 validated, all 4/5 checks (only `correct_amount` fails)
- **Ledger Correction:** 1 validated, 4/4 checks PASS
- **PDF Supplier Invoice:** 0 validated (failed pre-validation)
- **Year-End Closing:** 0 validated (failed pre-validation)

## Competition Check Patterns

Three distinct check structures observed:

### Dimension + Voucher (6 checks, 13 pts max) — WORKING

| Check | Best Guess | Competition | Sandbox |
|---|---|---|---|
| 1 | `voucher_found` | PASS | PASS |
| 2 | `has_description` | PASS | PASS |
| 3 | `has_postings` (≥2) | PASS | PASS |
| 4 | `postings_balanced` | PASS | PASS |
| 5 | `correct_amount` | PASS | FAIL (actual=0) |
| 6 | `dimension_values_set` | PASS | untested |

### Supplier Invoice (4 checks, 8 pts max) — ALL FAIL

| Check | Best Guess | Competition | Sandbox |
|---|---|---|---|
| 1 | `supplier_invoice_found` (not just voucher) | FAIL | PASS (wrong entity type) |
| 2 | `correct_supplier` | FAIL | PASS |
| 3 | `has_postings` | FAIL | PASS |
| 4 | `correct_amount` | FAIL | FAIL |

### Complex Tasks (4-6 checks, 10 pts max) — MOSTLY FAIL

PDF invoices, ledger corrections, year-end closings. Agent either crashes or produces wrong output.

## Root Cause Analysis

### 1. Supplier Invoice Vouchers (biggest impact: ~40 pts missing)

The agent creates a **plain manual voucher** via `/ledger/voucher` when `importDocument` fails. Competition likely looks for a **supplier invoice** (`/supplierInvoice` entity) — not just any voucher. All 4 competition checks fail even when the agent successfully creates a balanced voucher with correct postings.

**Evidence:** Agent returns HTTP 200, voucher is created with correct postings in Tripletex, but competition scores 0/4 on ALL checks. This means the competition validator searches a different entity type entirely.

### 2. SandboxValidator `correct_amount` Bug

Local validator always reads `actual=0,00` for `correct_amount`. This is a code bug in `SandboxValidator.cs` — it's not reading posting amounts from the created voucher. Affects ALL categories but doesn't matter for competition correlation since dimension+voucher tasks pass `correct_amount` in competition anyway.

### 3. PDF Supplier Invoices

The `importDocument` → `PUT /supplierInvoice/voucher/{id}/postings` flow hits a 500 error. Fallback to plain voucher doesn't pass competition checks (same issue as #1).

### 4. Ledger Correction & Year-End Closing

Handler sends empty postings → 422 `Et bilag kan ikke registreres uten posteringer`. These need completely new handler logic.

## Fix Priority

| Priority | Fix | Impact | Effort |
|---|---|---|---|
| 1 | **Use `/supplierInvoice` API** instead of plain voucher for supplier invoice tasks | ~40 pts (10 prompts × 4 checks) | Medium |
| 2 | **Fix `correct_amount` in SandboxValidator** | Validator accuracy | Low |
| 3 | **Fix PDF supplier invoice flow** | ~10 pts (2 prompts) | Medium |
| 4 | **Implement ledger correction handler** | ~8 pts (1 prompt) | High |
| 5 | **Implement year-end closing handler** | ~12 pts (1 prompt) | High |
