# Task 30 — Create Voucher (Annual Accounts, Tier 3)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 30 |
| **Task Type** | `create_voucher` / `annual_accounts` |
| **Variant** | Simplified annual accounts/year-end closing |
| **Tier** | 3 |
| **Our Score** | 1.80 |
| **Leader Score** | 1.80 |
| **Gap** | 0 (tied) |
| **Status** | ✅ FIXED — Tied with leader |
| **Handler** | `AnnualAccountsHandler.cs` |
| **Priority** | None — tied |

## What It Does

Nynorsk prompt: "Gjer forenkla årsoppgjer for 2025: 1) Rekn ut og bokfør avskriving..." — perform simplified annual accounting:
1. Calculate and post depreciation for each asset (bookValue / usefulLife)
2. Reverse prepaid expenses
3. Calculate and post tax expense (22% of P&L result)

Creates up to 5 vouchers total.

## API Flow

1. `GET /ledger/posting?dateFrom=...&accountNumberFrom=3000&accountNumberTo=8699` — calculate P&L for tax
2. `GET /ledger/account?number=X` — resolve accounts (depreciation, prepaid, tax)
3. `POST /ledger/voucher` × N — create vouchers for each year-end entry

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `voucher_found` | — | ✅ |
| `has_description` | — | ✅ |
| `has_postings` | — | ✅ |
| `correct_accounts` | — | ⚠️ |
| `correct_amount` | — | ⚠️ |

## Current State

**FIXED.** Both at 1.80. The handler creates depreciation vouchers per asset, prepaid reversal, and tax calculation. P&L result is calculated by summing accounts 3000-8699.

## Possible Improvement

Both teams at 1.80 out of a higher max suggests some checks fail for both. Improving would mean:
- More accurate depreciation calculation
- Correct tax rate/amount
- Correct account numbers for specific entries

But since leader also has 1.80, no competitive advantage.

## Action Required

None — tied with leader. Only improve if pursuing absolute score.
