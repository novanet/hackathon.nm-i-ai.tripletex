# Task 30 — Create Voucher (Annual Accounts, Tier 3)

## Overview

| Field            | Value                                       |
| ---------------- | ------------------------------------------- |
| **Task ID**      | 30                                          |
| **Task Type**    | `create_voucher` / `annual_accounts`        |
| **Variant**      | Simplified annual accounts/year-end closing |
| **Tier**         | 3                                           |
| **Our Score**    | 1.80                                        |
| **Leader Score** | 2.40                                        |
| **Gap**          | -0.60                                       |
| **Status**       | ⚠️ Behind                                   |
| **Handler**      | `AnnualAccountsHandler.cs`                  |
| **Priority**     | Low — small but real gap                    |

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

| Check              | Points | Status |
| ------------------ | :----: | :----: |
| `voucher_found`    |   —    |   ✅   |
| `has_description`  |   —    |   ✅   |
| `has_postings`     |   —    |   ✅   |
| `correct_accounts` |   —    |   ⚠️   |
| `correct_amount`   |   —    |   ⚠️   |

## Current State

The handler creates depreciation vouchers per asset, prepaid reversal, and tax calculation. P&L result is calculated by summing accounts 3000-8699, but the latest leaderboard snapshot shows the leader has pushed this task to 2.40 while we remain at 1.80.

## Possible Improvement

The remaining gap is modest, but it means at least one check is now being hit by the leader that we still miss. Improving would mean:

- More accurate depreciation calculation
- Correct tax rate/amount
- Correct account numbers for specific entries

This is not a first-wave target, but it is no longer parity.

## Action Required

- [ ] Compare our latest task-30 replay against the newest competition result
- [ ] Identify which annual-closing check the leader likely unlocked
- [ ] Revisit only after higher-value gaps are addressed
