# Knowledge Base — Verified Learnings

**Read this before debugging, implementing handlers, or investigating failures.**
**Update this whenever you discover a new API quirk, extraction pitfall, scoring insight, or fix a bug.**
Keep entries short (1–2 lines). Include the date discovered.

---

## Tripletex API Quirks

- **Employee `startDate` is NOT a field on `/employee`** — use `POST /employee/employment` as a separate call after creating the employee. The employee POST will 422 with "Feltet eksisterer ikke i objektet" if you include `startDate`. _(2026-03-20)_
- **Employee `dateOfBirth` must be set** before creating employment — otherwise 422 "Feltet må fylles ut". Always extract DOB from prompt. _(2026-03-20)_
- **Employee `userType` must not be `"0"` or empty** — use `"STANDARD"`. Error: "Brukertype kan ikke være «0» eller tom." _(2026-03-20)_
- **Employee `email` is required** for Tripletex users — 422 "Må angis for Tripletex-brukere" if missing. _(2026-03-20)_
- **Project `startDate` is required** — defaults to today if not specified in prompt. Error: "Feltet må fylles ut." _(2026-03-20)_
- **Project manager needs entitlements** — must call `PUT /employee/entitlement/:grantEntitlementsByTemplate?template=ALL_PRIVILEGES` before assigning as PM. Error: "Oppgitt prosjektleder har ikke fått tilgang som prosjektleder i kontoen". _(2026-03-20)_
- **Invoice requires company bank account** — the company (not customer) must have a bank account registered. Error: "Faktura kan ikke opprettes før selskapet har registrert et bankkontonummer." Seen in competition but not sandbox. _(2026-03-20)_
- **Voucher requires `postings` array** — cannot POST without it. Error: "Et bilag kan ikke registreres uten posteringer." _(2026-03-20)_
- **Department numbers collide** — pre-query existing departments with `GET /department?fields=departmentNumber,name`, then pick the next available number. _(2026-03-20)_
- **Customer lookup by org number** — use `?organizationNumber=` param, NOT `?name=` with the org number as value. _(2026-03-20)_

## LLM Extraction Pitfalls

- **firstName/lastName sometimes not extracted** — happened with German, Spanish, Portuguese, and Nynorsk prompts. The LLM returned only `email` without name fields. Root cause: extraction prompt needs explicit required-field emphasis. _(2026-03-20)_
- **Employee entity sometimes missing email** — even when clearly stated in the prompt (Portuguese prompts). Ensure extraction schema marks email as required. _(2026-03-20)_
- **Full JSON object serialized as query param** — bug where the entire employee JSON was URL-encoded into `firstName=` and `lastName=` search params instead of just the string values. Check handler code carefully when using extracted nested objects. _(2026-03-20)_
- **Travel expense misrouted to VoucherHandler** — extraction returned `create_voucher` instead of `create_travel_expense`. Ensure LLM system prompt clearly distinguishes the two task types. _(2026-03-20)_

## Scoring & Validation Insights

- **Payment `payment_registered` check** — passes when `amountOutstanding = 0`. Must pay the full invoice amount _including VAT_, not just the ex-VAT amount from the prompt. _(2026-03-20)_
- **Admin role is worth 5 out of 11 points** for employee tasks — nearly 50%. Always grant when requested. Use `template=administrator` (not `ALL_PRIVILEGES`) per the copilot-instructions, but `ALL_PRIVILEGES` also works. _(2026-03-20)_
- **Product `number` field is checked** — validator verifies the product number matches the prompt. Always extract and set it. _(2026-03-20)_
- **Department validation only checks `department_found`** (2 pts) — no field-level checks beyond existence. _(2026-03-20)_
- **Travel expense `has_costs` check** wants `> 0` cost lines — at least one `POST /travelExpense/cost` required. _(2026-03-20)_
- **Invoice validation checks**: `invoice_found`, `has_customer`, `has_amount > 0`. Amount includes VAT. _(2026-03-20)_
- **Credit note validation**: only checks `credit_note_created` (3 pts). _(2026-03-20)_
- **Delete entity validation**: only checks `entity_deleted` (3 pts). _(2026-03-20)_
- **All 14 validation tasks achieved 100% correctness** in latest sandbox run. _(2026-03-20)_

## Efficiency Baselines (Observed Optimal)

| Task                  | Optimal calls | Notes                                                             |
| --------------------- | ------------- | ----------------------------------------------------------------- |
| create_customer       | 1             | Just POST                                                         |
| create_supplier       | 1             | Just POST                                                         |
| create_product        | 2             | GET vatType + POST                                                |
| create_employee       | 3–5           | dept + POST + employment + entitlements                           |
| create_department     | 2+            | GET existing + POST (+ retries if collisions)                     |
| create_invoice        | 4–6           | customer + vatType + order + invoice + optional send              |
| register_payment      | 6–8           | Full invoice chain + GET invoice + GET paymentType + PUT :payment |
| create_project        | 4–8           | customer lookup + employee lookup/create + entitlements + POST    |
| create_travel_expense | 5–7           | employee lookup + POST expense + GET paymentType + POST cost(s)   |
| create_voucher        | 9             | Dimension + values + voucher with postings                        |
| create_credit_note    | 6             | Find invoice + create credit note                                 |
| delete_entity         | 2             | Find entity + DELETE                                              |

## Competition vs Sandbox Differences

- **Competition runs against a clean/fresh Tripletex instance** — no pre-existing data, no duplicate emails or department numbers from prior runs. _(2026-03-20)_
- **Sandbox accumulates data** — duplicate emails, department numbers 1–7+ already taken. Don't add sandbox-specific workarounds. _(2026-03-20)_
- **Bank account issue appeared only in competition** — sandbox had it pre-configured. May need to handle this for invoice tasks. _(2026-03-20)_
