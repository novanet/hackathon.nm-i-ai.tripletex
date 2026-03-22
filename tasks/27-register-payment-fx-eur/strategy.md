# Task 27 — Register Payment (Foreign Currency / EUR, Tier 3)

## Overview

| Field                        | Value                                                                                                    |
| ---------------------------- | -------------------------------------------------------------------------------------------------------- |
| **Task ID**                  | 27                                                                                                       |
| **Task Type**                | `register_payment`                                                                                       |
| **Variant**                  | Foreign currency (EUR) invoice + payment with exchange rate                                              |
| **Tier**                     | 3                                                                                                        |
| **Latest Competition Score** | 0.60                                                                                                     |
| **Leader Score**             | 6.00                                                                                                     |
| **Gap**                      | -5.40                                                                                                    |
| **Current Runtime Status**   | FX runtime verified locally: real EUR invoice + actual NOK receipt + Tripletex auto-posted FX difference |
| **Handler**                  | `PaymentHandler.cs` → `HandleFxPaymentAsync`                                                             |
| **Priority**                 | High leverage; current blocker is competition/local validator divergence                                 |

## Current Assessment

Task 27 is no longer blocked by the runtime handler path. The latest refreshed evidence shows:

- latest competition replay at `2026-03-21 17:53:56`: handler succeeded in 7 calls with 0 API errors, but competition score remained `0.60`
- fresh competition rerun still produced `0.60`, so the old “stale run” explanation is no longer credible
- latest sandbox replay at `2026-03-22 08:31:39`: handler succeeded in 10 calls with 0 API errors and produced a real EUR invoice
- the order request now contains `currency.id = 5`, `unitPriceIncludingVatCurrency = 6893`, and `isPrioritizeAmountsIncludingVat = true`
- the payment request now contains `paidAmount=68033.91` and `paidAmountCurrency=6893.00`
- Tripletex auto-posted the FX loss on account `8160` with description `Payment: Faktura nummer 177 ... Loss on exchange.`
- the remaining local failure was validator-only: extraction hallucinated account `8070`, while the accounting-correct account for disagio is `8160`

That means the old root cause analysis in this file was stale. The handler now reaches the intended FX order-pricing path and produces the correct foreign-currency invoice locally. The remaining uncertainty is now mostly competition-local validator divergence, not the core payment flow.

## What the Working Flow Does

- Resolve EUR via `GET /currency?code=EUR`.
- Create customer.
- Create foreign-currency order with `currency: { id = EUR }`.
- Create invoice from the order.
- Resolve payment type.
- Register payment with `paidAmount` set to the actual NOK amount received using the payment-time exchange rate, and `paidAmountCurrency` set to the foreign amount actually paid by the customer.
- Let Tripletex auto-post the exchange gain/loss entry instead of forcing a manual correction voucher.

## Accounting Direction

The accounting-correct model for this task is:

- keep the original invoice in EUR
- book the actual NOK cash receipt at payment time
- clear the receivable through the payment flow
- let Tripletex post the realized FX difference on its native gain/loss accounts

The earlier NOK-invoice workaround was useful for isolating one validator mismatch, but it is not the right accounting treatment for the task as described by the domain expert.

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
  - exchange-rate difference posting exists in the ledger, using the accounting-correct disagio/agio account derived from prompt semantics and exchange-rate direction
- A live sandbox probe confirmed that Tripletex auto-posts the exchange difference during `PUT /invoice/{id}/:payment`; the missing piece was validator coverage, not an extra runtime voucher.

## Latest Evidence

### Latest Competition Run

- extraction already looked good: `EUR`, `11671`, `11.22`, `11.71`
- order request contained `currency.id = 5`
- payment request used `paidAmount=136667.41` and `paidAmountCurrency=11671.00`
- handler completed successfully with 0 API errors
- score still came back `0.60`

### Latest Sandbox Run

- handler completed successfully in 10 calls with 0 API errors
- order payload used `unitPriceIncludingVatCurrency=6893` with `isPrioritizeAmountsIncludingVat=true` and `currency.id = 5`
- invoice came back as `amountCurrency=6893.0`, `amount=77859.88`, `amountOutstanding=0.00`, `currency.code = EUR`
- payment call used `paidAmount=68033.91` and `paidAmountCurrency=6893.00`
- Tripletex auto-posted the loss on account `8160`
- the only failing local check before the validator fix was `exchange_rate_difference_posted`, because extraction hallucinated `8070`

## Most Likely Explanation For The Remaining Gap

The fresh rerun rules out simple submission lag. The concrete remaining mismatch is now:

1. competition may still score against a different FX-account expectation than the accounting-correct local validator, so another submission is needed after the validator fix

## Next Action

The correct follow-up is now:

1. Re-run the French disagio and Spanish agio prompts after the validator fix and confirm local `14/14` behavior.
2. Refresh task docs so `runs.md` and this strategy file reflect the verified EUR-invoice runtime, not the older NOK-workaround evidence.
3. Submit a fresh competition run to determine whether the remaining gap is still a validator mismatch or a real competition-specific rule.

## Open Risk

Competition may still disagree with the local validator about FX gain/loss account expectations, especially if the official checker accepts Tripletex-native postings differently for agio vs. disagio.
