# Task 23 — Bank Reconciliation (CSV, Tier 3)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 23 |
| **Task Type** | `bank_reconciliation` |
| **Variant** | CSV bank statement matching |
| **Tier** | 3 |
| **Our Score** | 0.00 |
| **Leader Score** | 0.60 |
| **Gap** | -0.60 |
| **Status** | ❌ Failing — partially working but key check fails |
| **Handler** | `BankReconciliationHandler.cs` |
| **Priority** | #13 — LOW priority, tiny gap, high complexity |

## What It Does

Multi-language prompt: "Avstem bankutskrifta (vedlagt CSV) mot opne fakturaer..." — reconcile a bank statement CSV against open invoices in Tripletex.

## API Flow

1. `GET /ledger/account?number=1920` — resolve bank account
2. `GET /ledger/accountingPeriod?startTo=X&endFrom=Y` — resolve accounting period
3. `POST /bank/reconciliation` — create reconciliation
4. `POST /bank/statement/import` — import CSV as bank transactions (TRANSFERWISE format, ISO-8859-1 encoding)
5. `PUT /bank/reconciliation/{id}/:adjustment` — close with adjustment

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `reconciliation_found` | 5 | ✅ (sandbox: account=1920, balance matches) |
| `invoice_and_supplier_rows_matched` | 5 | ❌ (0 matched, expected 8) |

**Local validation: 5/10 (50%)**

## Current State (2026-03-21)

**Half working.** Reconciliation is created successfully and balance matches, but the invoice/supplier row matching check fails completely (0/8 matched).

Latest competition run (2026-03-21 17:53): 5 calls, 2 errors — handler succeeded overall. The bank statement import works (TRANSFERWISE format, ISO-8859-1 encoding).

**Key issue:** The `invoice_and_supplier_rows_matched` check requires matching imported bank transactions to open invoices/supplier invoices. We currently don't do any matching — we just import the CSV and close with an adjustment. The competition expects us to:
1. Find open invoices that match transaction amounts
2. Link bank transactions to those invoices
3. Possibly use the bank reconciliation matching API

24 total runs. Error rate remains high (~50% of sandbox runs fail due to "allerede en bankavstemming" — duplicate reconciliation in reused sandbox).

## Known Quirks

- **TRANSFERWISE format required** — DNB_CSV, DANSKE_BANK_CSV, NORDEA_CSV all return 422
- **ISO-8859-1 encoding** — server interprets file bytes as Latin-1, not UTF-8
- **Date format**: DD-MM-YYYY for TRANSFERWISE CSV
- **Sandbox reuse**: Sandbox fails with "already exists" errors since reconciliation persists across runs  
- **fromDate/toDate** must span the full accounting period

## How to Improve

To get `invoice_and_supplier_rows_matched` check passing:
1. After importing bank statement, query open outgoing invoices (`GET /invoice?invoicesDueIn=0`)
2. Query open supplier invoices
3. Match bank transactions to invoices by amount
4. Use reconciliation matching/posting API to link them

This is significant additional work for only 5 more points at Tier 3 multiplier.

## Effort

**HIGH** — matching logic is complex and poorly documented. Only 0.60 gap.

## Action Required

- [ ] Deprioritize unless all higher-gap tasks are resolved
- [ ] If pursued: implement invoice matching after bank statement import
- [ ] Consider if the 5 extra points (×T3 multiplier) justify the complexity
