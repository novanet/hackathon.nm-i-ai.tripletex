# Task 27 — Register Payment (Foreign Currency / EUR, Tier 3)

## Overview

| Field                        | Value                                                                                 |
| ---------------------------- | ------------------------------------------------------------------------------------- |
| **Task ID**                  | 27                                                                                    |
| **Task Type**                | `register_payment`                                                                    |
| **Variant**                  | Foreign currency (EUR) invoice + payment with exchange rate                           |
| **Tier**                     | 3                                                                                     |
| **Latest Competition Score** | 0.60                                                                                  |
| **Leader Score**             | 6.00                                                                                  |
| **Gap**                      | -5.40                                                                                 |
| **Current Runtime Status**   | Local flow passing; competition still unverified after latest validator/runtime fixes |
| **Handler**                  | `PaymentHandler.cs` → `HandleFxPaymentAsync`                                          |
| **Priority**                 | High leverage, but likely submission-verification rather than more code               |

## Current Assessment

Task 27 is no longer a clear handler bug. The latest refreshed evidence shows:

- latest competition replay at `2026-03-21 17:53:56`: handler succeeded in 7 calls with 0 API errors, but competition score remained `0.60`
- latest sandbox replay at `2026-03-21 21:52:58`: handler succeeded and local validation passed `14/14`
- validator now explicitly checks the FX bookkeeping side effect by reading ledger postings on accounts `8050-8070`

That means the old root cause analysis in this file is stale. The handler currently does the expected FX flow end-to-end; the remaining uncertainty is whether the competition validator agrees with our new local interpretation.

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
  - exchange-rate difference posting exists in the ledger
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
- `exchange_rate_difference_posted` passed based on a real ledger posting on account `8060`

## Most Likely Explanation For The Remaining Gap

The last competition score is almost certainly lagging behind our latest local understanding, not our latest runtime. The competition run predates the new FX-specific validator checks and the prompt-based inference hardening that we now see reflected in the sandbox pass.

## Next Action

The correct follow-up is not more speculative FX code changes. It is submission-level verification:

1. Submit a fresh run including task 27 with the current code.
2. Compare the new competition checks against the current local `14/14` validator result.
3. Only return to handler changes if competition still fails despite the clean runtime and clean local FX bookkeeping evidence.

## Open Risk

If competition still scores task 27 low after a fresh submission, the remaining mismatch is likely in our validator assumptions about what the competition considers sufficient evidence for the exchange-rate-difference posting, not in the payment flow itself.
