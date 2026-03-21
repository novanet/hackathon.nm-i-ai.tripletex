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
| **Status** | ❌ Failing — leader also barely scores |
| **Handler** | `BankReconciliationHandler.cs` |
| **Priority** | LOW — high effort, tiny gap |

## What It Does

Nynorsk prompt: "Avstem bankutskrifta (vedlagt CSV) mot opne fakturaer..." — reconcile a bank statement CSV against open invoices in Tripletex.

## API Flow

1. `GET /bank` — find bank/account
2. `GET /ledger/account?number=1920` — resolve bank account
3. Create accounting period if needed
4. `POST /bank/reconciliation` — create reconciliation
5. Parse CSV for transactions
6. Import statements via bank reconciliation API
7. Match transactions to open invoices

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `reconciliation_found` | — | ❌ |
| `has_statements` | — | ❌ |
| `matches_correct` | — | ❌ |

## Why Both Teams Struggle

Bank reconciliation is the most complex Tier 3 task:
- CSV format varies and must be parsed correctly
- Reconciliation requires correct account + period resolution
- Statement import API is poorly documented
- Matching logic between CSV transactions and open invoices is complex
- Even the leader only scores 0.60 (1 out of several checks)

## How to Fix (if pursued)

1. Debug CSV parsing — verify format matches what API expects
2. Check period resolution — timestamps in CSV may be in unexpected format
3. Verify reconciliation POST actually creates the entity
4. Statement import may need specific field mapping

## Effort

**HIGH** — complex API with poor documentation. Both teams struggle. Low ROI.

## Action Required

**DEPRIORITIZE.** Only -0.60 gap. Spend time on Tasks 25, 27, 24 instead.

Only revisit if:
- All higher-priority tasks are done
- Spare submissions remain
