# Task 11 — Create Voucher (Supplier Invoice)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 11 |
| **Task Type** | `create_voucher` |
| **Variant** | Supplier invoice voucher (incoming) |
| **Tier** | 2 |
| **Our Score** | 0.00 |
| **Leader Score** | 4.00 |
| **Gap** | -4.00 |
| **Status** | ❌ Failing — but handler was FIXED locally (Fix 10) |
| **Handler** | `VoucherHandler.cs` → `HandleSupplierInvoice` |
| **Priority** | #1 — TRIVIAL effort, high gain |

## What It Does

French prompt: "Nous avons reçu la facture INV-2026-5683 du fournisseur..." — create a supplier, then create a voucher with:
- Debit posting on expense account (e.g., 6500, 6800)
- Credit posting on creditor account 2400
- Supplier linked to postings
- Invoice number as externalVoucherNumber
- VoucherType = "Leverandørfaktura"

## API Flow

1. `POST /supplier` — create supplier with name, orgNumber
2. `GET /ledger/account?number=6500` — resolve expense account (with `vatLocked` check)
3. `GET /ledger/vatType?number=1` — resolve input VAT type (if account not locked)
4. `GET /ledger/voucherType?name=Leverandørfaktura` — resolve voucher type
5. `GET /ledger/account?number=2400` — resolve creditor account
6. `POST /ledger/voucher?sendToLedger=true` — create double-entry voucher

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `voucher_found` | 2 | ❌ → should be ✅ after Fix 10 |
| `has_description` | 2 | ❌ → should be ✅ |
| `has_postings` | 2 | ❌ → should be ✅ |
| `correct_accounts` | 2 | ❌ → unknown |
| `correct_amount` | 3 | ❌ → unknown |

## Why We Score 0

All previous attempts (10 tries) used the old code BEFORE Fix 10 (VAT lock detection). The bug was: account 6540 has `vatLocked=false` but a default vatType. Old code assumed any vatType = locked, skipping the input VAT lookup entirely → postings used wrong/no VAT → 0/4 competition score.

**Fix 10 was applied locally and tested successfully (5/5)** but has NOT been run in competition yet.

## How to Fix

**Just submit.** The code is already fixed. If the first submission still scores 0:
1. Check extraction: does `voucher.supplierName` get populated from the French prompt?
2. Check if the supplier-invoice branch triggers (vs falling through to generic voucher path)
3. Verify the expense account number extracted from the prompt

## Effort

**TRIVIAL** — no code changes expected. Just submit.

## Action Required

- [ ] Submit a competition run
- [ ] Verify Task 11 scores > 0
- [ ] If still 0, check `Analyze-Run.ps1 -ShowExtraction -ShowApiCalls` for this task
