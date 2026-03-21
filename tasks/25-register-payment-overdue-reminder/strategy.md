# Task 25 — Register Payment (Overdue Invoice + Reminder, Tier 3)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 25 |
| **Task Type** | `register_payment` / `reminder_fee` / `overdue_invoice_reminder` |
| **Variant** | Find overdue invoice + register reminder fee + pay |
| **Tier** | 3 |
| **Our Score** | 0.00 |
| **Leader Score** | 5.25 |
| **Gap** | -5.25 |
| **Status** | ❌ Failing — large gap |
| **Handler** | `PaymentHandler.cs` → `HandleReminderFeesAsync` (also `OverdueInvoiceReminderHandler.cs` exists) |
| **Priority** | #6 — MEDIUM effort, HIGH gain |

## What It Does

French prompt: "L'un de vos clients a une facture en retard. Trouvez la facture en retard et enregistrez des frais de rappel de 50 NOK..." — find an overdue invoice, register a reminder fee, and register payment.

## API Flow (current — complex, error-prone)

1. `GET /invoice?invoiceDateFrom=...&invoiceDateTo=...` — find overdue invoices (amountOutstanding > 0)
2. Resolve debit/credit accounts for reminder voucher
3. `POST /ledger/voucher` — create reminder fee voucher (often 422 on control account 1500!)
4. `POST /order` → `POST /invoice` → `PUT /:send` — create reminder fee invoice for customer
5. `GET /invoice/paymentType` — resolve payment type
6. `PUT /invoice/{id}/:payment` — register payment on overdue invoice

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `invoice_found` (overdue) | — | ❌ |
| `payment_registered` | — | ❌ |
| `reminder_fee_created` | — | ❌ |

## Why We Score 0

Multiple issues:

1. **Routing confusion**: Both `OverdueInvoiceReminderHandler` and `PaymentHandler.HandleReminderFeesAsync` exist. TaskRouter maps `overdue_invoice_reminder` → PaymentHandler (via direct mapping). But `HandleReminderFeesAsync` has a complex flow that creates a reminder fee *invoice* (order → invoice → send) which adds many unnecessary write calls and failure points.

2. **Voucher creation 422**: Account 1500 (Kundefordringer) is a system control account — manual voucher postings against it fail with 422. The `catch` block swallows the error, but the voucher is never created.

3. **The reminder fee invoice creation is unnecessary complexity**: It creates an order + invoice + tries to send — that's 3+ write calls with potential 422s. Competition probably just wants: find overdue → pay it → maybe create a simple voucher on non-control accounts.

4. **Payment amount**: knowledge.md says "even if prompt says partial payment of 5000 NOK, the correct behavior is to pay `amountOutstanding`". But `HandleReminderFeesAsync` uses the LLM-extracted partial amount.

## How to Fix

**Option A: Simplify `HandleReminderFeesAsync`**
1. Find overdue invoice (amountOutstanding > 0) — this part works
2. Create reminder fee voucher using non-control accounts (e.g., debit 3400 / credit 1920) — or skip if always 422
3. Register payment for `amountOutstanding` (not partial amount from prompt)
4. **Skip the reminder fee invoice creation entirely** — too many writes, too error-prone

**Option B: Route to `OverdueInvoiceReminderHandler` instead**
1. The `OverdueInvoiceReminderHandler` has a simpler flow — find overdue, create voucher, update due dates
2. But it doesn't register the payment, which is the key check

**Recommended: Option A** — simplify the PaymentHandler flow.

### Key Fix

```csharp
// BEFORE: complex flow with invoice chain
// AFTER: simple flow
// 1. Find overdue invoice
// 2. Create voucher (skip if 422)
// 3. Pay amountOutstanding (NOT partial amount)
```

Also ensure the LLM extracts `task_type = "reminder_fee"` — the fix from knowledge.md (added `reminder_fee` as explicit task type) should handle routing.

## Effort

**MEDIUM** — simplify `HandleReminderFeesAsync`, ensure routing works, fix payment amount.

## Action Required

- [ ] Simplify `HandleReminderFeesAsync` — remove invoice chain creation
- [ ] Pay `amountOutstanding`, not LLM-extracted partial amount
- [ ] Use non-control accounts for voucher (or skip voucher if always fails)
- [ ] Verify LLM extracts `reminder_fee` task type
- [ ] Test locally
- [ ] Submit and verify
