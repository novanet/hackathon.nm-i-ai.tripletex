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
| **Status** | ❌ Competition failing — sandbox passing 15/15 |
| **Handler** | `VoucherHandler.cs` → `HandleSupplierInvoice` |
| **Priority** | #3 — HIGH priority, large gap |

## What It Does

Multi-language prompt (FR/NB/EN/DE): "Nous avons reçu la facture INV-2026-XXXX du fournisseur..." — create a supplier, then create a voucher with:
- Debit posting on expense account (e.g., 6500, 6540, 6800)
- Credit posting on creditor account 2400
- Supplier linked to postings
- Invoice number as externalVoucherNumber
- VoucherType = "Leverandørfaktura"

## API Flow

1. `POST /supplier` — create supplier with name, orgNumber
2. `GET /ledger/account?number=6540` — resolve expense account (with `vatLocked` check)
3. `GET /ledger/vatType?number=1` — resolve input VAT type (if account not locked)
4. `GET /ledger/voucherType?name=Leverandørfaktura` — resolve voucher type
5. `GET /ledger/account?number=2400` — resolve creditor account
6. `POST /ledger/voucher?sendToLedger=true` — create double-entry voucher

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `voucher_found` | 2 | ✅ sandbox |
| `has_description` | 2 | ✅ sandbox |
| `has_postings` | 2 | ✅ sandbox |
| `postings_balanced` | 2 | ✅ sandbox |
| `correct_accounts` | 2 | ✅ sandbox |
| `has_department` | 2 | ✅ sandbox |
| `correct_amount` | 3 | ✅ sandbox |

**Local validation: 15/15 (100%)**

## Current State (2026-03-21)

**Sandbox fully passing** at 15/15. Latest competition run (2026-03-21 18:22): 6 API calls, 0 errors, handler succeeded. But competition score in leaderboard is still 0.00 — this may be because the latest submission hadn't propagated as best score yet, or competition checks differ from local.

Key fixes applied:
- **Fix 10**: VAT lock detection — account 6540 has `vatLocked=false` but a default vatType. Old code assumed any vatType = locked, skipping input VAT lookup → wrong postings
- Department resolution for supplier-invoice vouchers
- Correct posting structure: debit expense + VAT, credit creditor (3 postings when VAT involved)

## Root Cause of Competition Failure

Competition score 0 may be stale — last competition run showed handler succeeding with 0 errors. Need a fresh submission to verify if the fix is reflected in competition scoring.

## Action Required

- [ ] Submit a competition run and verify Task 11 scores > 0
- [ ] If still 0 after fresh submission, compare competition checks vs local validator
- [ ] Check if competition uses different voucher validation than our local 15/15
