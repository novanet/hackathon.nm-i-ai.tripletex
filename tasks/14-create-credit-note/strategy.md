# Task 14 — Create Credit Note

## Overview

| Field | Value |
|---|---|
| **Task ID** | 14 |
| **Task Type** | `create_credit_note` |
| **Variant** | Credit note on existing invoice |
| **Tier** | 2 |
| **Our Score** | 2.67 |
| **Leader Score** | 4.00 |
| **Gap** | -1.33 |
| **Status** | ⚠️ Behind — efficiency gap |
| **Handler** | `CreditNoteHandler.cs` |
| **Priority** | #2 — LOW effort, good gain |

## What It Does

Create a full invoice chain, then issue a credit note against it.

## API Flow (current — 5+ write calls)

1. `POST /customer` — create customer
2. `POST /order` — create order with lines
3. `POST /invoice` — create invoice
4. `PUT /invoice/{id}/:send` — send invoice
5. `PUT /invoice/{id}/:createCreditNote` — create credit note

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `credit_note_created` | 3 | ✅ |

## Why We're Behind

Correctness is 100% (the only check `credit_note_created` passes). The gap is purely **efficiency** — we use more write calls than the leader.

## How to Fix

1. **Skip `:send`** — competition only checks `credit_note_created`. Sending the invoice before crediting is unnecessary → save 1 write call
2. **Check if bank account POST** is still happening from the invoice chain → remove if so
3. **Potentially skip creating the invoice chain** if there's already an invoice to credit (but in competition env it's clean, so we must create one)

Target: 4 write calls (customer + order + invoice + creditNote) vs current 5+.

## Effort

**LOW** — remove the `:send` call and review for other unnecessary writes.

## Action Required

- [ ] Remove or skip `PUT /:send` in `CreditNoteHandler` (or in the `CreateInvoiceChainAsync` call)
- [ ] Verify no extra bank account POST
- [ ] Submit and check improvement (2.67 → hopefully 3.5+)
