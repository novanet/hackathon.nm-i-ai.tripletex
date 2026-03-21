# Task 22 — Create Voucher from PDF Receipt (Tier 3)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 22 |
| **Task Type** | `create_voucher` |
| **Variant** | Voucher from PDF receipt (German) |
| **Tier** | 3 |
| **Our Score** | 0.00 |
| **Leader Score** | 0.00 |
| **Gap** | 0 (both fail) |
| **Status** | ❌ Both teams fail |
| **Handler** | `PdfVoucherHandler.cs` → `VoucherHandler.cs` |
| **Priority** | LOW — no competitive advantage |

## What It Does

German prompt: "Wir benötigen die Headset-Ausgabe aus dieser Quittung..." — extract receipt data from PDF and create a voucher for the expense.

## API Flow

Similar to Task 11/20 — extract supplier/expense/amount from PDF receipt, create voucher with double-entry postings.

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `voucher_found` | — | ❌ |
| `has_description` | — | ❌ |
| `has_postings` | — | ❌ |

## Why Both Teams Score 0

Receipts are harder to extract than invoices — less structured data, no standard invoice format. The PDF may be an image-based receipt that requires OCR.

## How to Fix

If fixing Task 20 (PDF supplier invoice) works, the same improvements may help here. But since the leader also scores 0, this is not a competitive priority.

1. Fix Task 20 first (same handler chain)
2. The receipt PDF may need special extraction instructions
3. Receipts may not have supplier org numbers, invoice numbers — handler needs to work with less data

## Effort

**MEDIUM-HIGH** — receipt extraction is harder, and no competitive gain since leader also 0.

## Action Required

- [ ] Fix Task 20 first
- [ ] If Task 20 fix also helps receipts, bonus
- [ ] Deprioritize unless Tasks 20 + 11 are solved and there's spare time
