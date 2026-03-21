# Task 27 — Register Payment (Foreign Currency / EUR, Tier 3)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 27 |
| **Task Type** | `register_payment` |
| **Variant** | Foreign currency (EUR) invoice + payment with exchange rate |
| **Tier** | 3 |
| **Our Score** | 0.60 |
| **Leader Score** | 6.00 |
| **Gap** | -5.40 |
| **Status** | ❌ Failing — large gap |
| **Handler** | `PaymentHandler.cs` → `HandleFxPaymentAsync` |
| **Priority** | #7 — MEDIUM effort, HIGH gain |

## What It Does

Create an invoice in a foreign currency (EUR), then register payment with exchange rate difference (agio/disagio). The prompt specifies:
- Invoice amount in EUR
- Exchange rate at invoice time
- Exchange rate at payment time
- NOK payment amount = EUR amount × rate at payment

## API Flow

1. `GET /currency?code=EUR` — resolve currency ID
2. `POST /customer` — create customer
3. `POST /order` — create order with `currency: {id: X}`
4. `POST /invoice` — create invoice
5. `GET /invoice/paymentType` — resolve payment type
6. `PUT /invoice/{id}/:payment` — pay with `paidAmount` (NOK) + `paidAmountCurrency` (EUR)

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `invoice_found` | — | ⚠️ |
| `has_currency` | — | ❌ |
| `payment_registered` | — | ❌ |
| `correct_amount_nok` | — | ❌ |
| `exchange_rate_diff` | — | ❌ |

## Why We Score 0.60

Score 0.60/6.00 = 10% correctness. Possible issues:

1. **FX detection fails**: `IsFxPayment()` checks for `currency` or `exchangeRateAtPayment` in payment/invoice entities. If LLM doesn't extract these fields, the task falls through to simple payment → no currency handling.

2. **Currency not passed to order**: `CreateInvoiceChainAsync(api, extracted, currencyId)` receives `currencyId`, but the chain might not apply it to the order body correctly.

3. **Exchange rate calculation wrong**: `nokAmount = foreignAmount × rateAtPayment`. If `rateAtPayment` is extracted as 0 or 1, the calculation is wrong.

4. **`paidAmountCurrency` parameter**: The `:payment` call must include both `paidAmount` (NOK) and `paidAmountCurrency` (foreign). If `paidAmountCurrency` is missing, Tripletex can't reconcile the FX difference.

## Key Knowledge (from knowledge.md)

- `PUT /invoice/{id}/:payment` supports `paidAmountCurrency` query param
- `GET /currency?code=EUR&count=1&fields=id` → EUR = ID 5 in sandbox
- LLM already extracts FX fields from prompts mentioning exchange rates
- FX detection does NOT false-trigger on normal NOK payments

## How to Fix

1. **Check submission logs** — what did LLM extract for this task? Were currency/rates populated?
2. **Verify InvoiceHandler passes currency to order** — read `CreateInvoiceChainAsync` to confirm `currencyId` is used in the order body
3. **Test locally** with an FX prompt — verify currency flows end-to-end
4. **Check `paidAmountCurrency`** is actually sent as a query param to `:payment`

## Debugging Steps

```powershell
.\scripts\Analyze-Run.ps1 -ShowExtraction -ShowApiCalls
# Find task 27, check:
# 1. extraction.entities.payment.currency = "EUR"?
# 2. extraction.entities.payment.exchangeRateAtPayment = X?
# 3. POST /order body includes currency?
# 4. PUT /:payment includes paidAmountCurrency param?
```

## Effort

**MEDIUM** — likely 1-2 specific bugs in the FX flow.

## Action Required

- [ ] Analyze latest run for task 27
- [ ] Verify LLM extracts currency + exchange rates
- [ ] Verify order creation passes currencyId
- [ ] Verify `:payment` call includes `paidAmountCurrency`
- [ ] Test locally with EUR prompt
- [ ] Submit to verify
