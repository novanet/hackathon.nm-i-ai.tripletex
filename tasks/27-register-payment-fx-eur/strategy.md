# Task 27 — Register Payment (Foreign Currency / EUR, Tier 3)

## Overview

| Field                        | Value                                                                                                |
| ---------------------------- | ---------------------------------------------------------------------------------------------------- |
| **Task ID**                  | 27                                                                                                   |
| **Task Type**                | `register_payment`                                                                                   |
| **Variant**                  | Foreign currency (EUR) invoice + payment with exchange rate                                          |
| **Tier**                     | 3                                                                                                    |
| **Latest Competition Score** | 0.60                                                                                                 |
| **Leader Score**             | 6.00                                                                                                 |
| **Gap**                      | -5.40                                                                                                |
| **Current Runtime Status**   | Payment flow works; latest replay shows FX order total path fixed but VAT type still collapses to 0% |
| **Handler**                  | `PaymentHandler.cs` → `HandleFxPaymentAsync`                                                         |
| **Priority**                 | High leverage; current blocker is competition/local validator divergence                             |

## Current Assessment

Task 27 is still not a clear handler bug, but it is no longer just a submission-verification problem. The latest refreshed evidence shows:

- latest competition replay at `2026-03-21 17:53:56`: handler succeeded in 7 calls with 0 API errors, but competition score remained `0.60`
- fresh competition rerun still produced `0.60`, so the old “stale run” explanation is no longer credible
- latest sandbox replay at `2026-03-22 02:43:53`: handler succeeded and local validation still passed `14/14`
- local validator had been too permissive because it accepted any FX posting in `8050-8160` and ignored the requested account extracted from the prompt
- latest FX replay proves the cloned extraction now reaches the order payload: the order body used `unitPriceIncludingVatCurrency=11671` and `isPrioritizeAmountsIncludingVat=true`
- despite that, the same replay still resolved `vatType.id=6`, so Tripletex created an invoice with `amountCurrency=11671.0` and `amount=131829.78` instead of adding 25% VAT

That means the old root cause analysis in this file was stale. The handler now reaches the intended FX order-pricing path, but the environment still collapses VAT resolution to a 0% VAT type. The remaining uncertainty is whether task 27 fails in competition because of this VAT modeling gap, the FX-account mismatch, or both.

## What the Working Flow Does

- Resolve EUR via `GET /currency?code=EUR`.
- Create customer.
- Create foreign-currency order with `currency: { id = EUR }`.
- Create invoice from the order.
- Resolve payment type.
- Register payment with `paidAmount` set to the NOK amount using the payment-time exchange rate, and `paidAmountCurrency` set to the foreign amount actually paid by the customer.
- Let Tripletex auto-post the exchange gain/loss entry.

## Verified Findings

### Runtime

- `PaymentHandler.HandleFxPaymentAsync` now infers foreign currency and foreign amount from the prompt if extraction degrades them.
- Prompt-derived exchange rates are used when extracted rates are missing or obviously wrong.
- `InvoiceHandler.CreateInvoiceChainAsync(..., currencyId)` does pass the resolved currency into the order flow.
- The payment call includes `paidAmountCurrency`.

### Validation

- Local validation now checks:
  - invoice exists
  - payment clears the invoice
  - NOK payment amount is correct
  - currency is non-NOK and preserved on the invoice
  - foreign-currency amount is correct
  - exchange-rate difference posting exists in the ledger, and when the extraction specifies an account, the posting must use that account
- A live sandbox probe confirmed that Tripletex auto-posts the exchange difference during `PUT /invoice/{id}/:payment`; the missing piece was validator coverage, not an extra runtime voucher.

## Latest Evidence

### Latest Competition Run

- extraction already looked good: `EUR`, `11671`, `11.22`, `11.71`
- order request contained `currency.id = 5`
- payment request used `paidAmount=136667.41` and `paidAmountCurrency=11671.00`
- handler completed successfully with 0 API errors
- score still came back `0.60`

### Latest Sandbox Run

- handler completed successfully
- local validation passed `14/14`
- order payload used `unitPriceIncludingVatCurrency=11671` with `isPrioritizeAmountsIncludingVat=true`
- invoice still came back as `amountCurrency=11671.0`, `amount=131829.78`, `amountOutstanding=0.00`
- `exchange_rate_difference_posted` passed based on a real ledger posting on account `8060`

## Most Likely Explanation For The Remaining Gap

The fresh rerun rules out simple submission lag. There are now two concrete remaining mismatches:

1. local validation can still disagree with competition on FX-account strictness (`8070` requested vs `8060` auto-posted)
2. the invoice itself is still modeled with 0% VAT in sandbox because VAT resolution falls back to `vatType.id=6`, even after the FX order line was switched to VAT-inclusive pricing

## Next Action

The correct follow-up is no longer pure validator work:

1. Fix VAT resolution for invoice/order flows so FX invoices can use a real 25% output VAT type instead of falling through to `id=6`.
2. Re-run the Ironbridge prompt and confirm the invoice total actually changes, not just the order payload.
3. Keep the stricter FX-account validator in place so `8070` vs `8060` remains visible locally.
4. Only after the invoice amount model is corrected should another competition submission be used to evaluate the residual gap.

## Open Risk

Even after fixing VAT resolution, local validation may still be too optimistic. The competition may be checking both the invoice amount basis and the explicit FX gain account, while Tripletex continues to auto-post exchange gains to `8060`.
