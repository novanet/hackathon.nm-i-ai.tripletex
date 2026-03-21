# Knowledge Base — Verified Learnings

**Read this before debugging, implementing handlers, or investigating failures.**
**Update this whenever you discover a new API quirk, extraction pitfall, scoring insight, or fix a bug.**
Keep entries short (1–2 lines). Include the date discovered.

---

## Tripletex API Quirks

- **Employee `startDate` is NOT a field on `/employee`** — use `POST /employee/employment` as a separate call after creating the employee. The employee POST will 422 with "Feltet eksisterer ikke i objektet" if you include `startDate`. _(2026-03-20)_
- **Employee `dateOfBirth` must be set** before creating employment — otherwise 422 "Feltet må fylles ut". Always extract DOB from prompt. EmployeeHandler now defaults to `1990-01-01` if not provided. _(2026-03-20, UPDATED 2026-03-21)_
- **Employee `userType` must not be `"0"` or empty** — use `"STANDARD"`. Error: "Brukertype kan ikke være «0» eller tom." Must be set when creating employees anywhere (specifically in TravelExpenseHandler and other handlers, not just EmployeeHandler). _(2026-03-20)_
- **Employee `department.id` required when department module active** — query `GET /department?count=1` and use first available ID. If none exists, EmployeeHandler now auto-creates a default "Hovedavdeling" department. Sandbox and competition both have department module enabled. _(2026-03-20, UPDATED 2026-03-21)_
- **Employee `email` is required** for Tripletex users — 422 "Må angis for Tripletex-brukere" if missing. Use `userType = "NO_ACCESS"` when creating employees without an email (e.g. in PayrollHandler auto-create). NO*ACCESS employees don't need email and can still receive payroll. *(2026-03-20, UPDATED)\_
- **Employment `division` required in some environments** — sandbox requires `division.id` on `POST /employee/employment` ("Arbeidsforholdet må knyttes til en virksomhet/underenhet"), but competition environments work without it. Handler now proactively fetches `GET /division?count=1&fields=id` and includes division in the employment body (no retry needed). _(2026-03-20, UPDATED)_
- **Project `startDate` is required** — defaults to today if not specified in prompt. Error: "Feltet må fylles ut." _(2026-03-20)_
- **Project manager needs entitlements** — must call `PUT /employee/entitlement/:grantEntitlementsByTemplate?template=ALL_PRIVILEGES` before assigning as PM. Error: "Oppgitt prosjektleder har ikke fått tilgang som prosjektleder i kontoen". _(2026-03-20)_
- **Invoice requires company bank account** — the company (not customer) must have a bank account registered. Error: "Faktura kan ikke opprettes før selskapet har registrert et bankkontonummer." Seen in competition but not sandbox. _(2026-03-20)_
- **Voucher requires `postings` array** — cannot POST without it. Error: "Et bilag kan ikke registreres uten posteringer." _(2026-03-20)_
- **Multi-voucher support** — When prompts request multiple vouchers (corrections, year-end closings), the LLM extracts `voucher1`, `voucher2`, etc. instead of a single `voucher` entity. VoucherHandler now detects keys matching `voucherN` (where N is a digit), iterates each, builds postings per-voucher, and POSTs separately. Two extraction formats observed: (1) `debitAccount`/`creditAccount`/`amount` pairs, (2) structured `postings[]` array with `debitCredit` field. `BuildPostingFromJson` now handles `debitCredit`: "credit" negates the amount. Non-numeric amounts (e.g. "calculated*tax_cost") are safely skipped. Per-voucher try/catch prevents one 422 from crashing the entire batch. *(2026-03-21)\_
- **Department numbers collide** — pre-query existing departments with `GET /department?fields=departmentNumber,name`, then pick the next available number. _(2026-03-20)_
- **Customer lookup by org number** — use `?organizationNumber=` param, NOT `?name=` with the org number as value. _(2026-03-20)_
- **Timesheet entry requires `activity`, `date`, `employee`** — minimum fields. Also supports `project`, `hours`, `comment`. Only one entry per employee/date/activity/project combo. 38 hours on a single date works (no 24h cap on `hours` field). _(2026-03-20)_
- **Activity creation requires `activityType`** — use `PROJECT_GENERAL_ACTIVITY` for project-related billable activities. Link to project via `POST /project/projectActivity`. _(2026-03-20)_

## LLM Extraction Pitfalls

- **firstName/lastName sometimes not extracted** — happened with German, Spanish, Portuguese, and Nynorsk prompts. The LLM returned only `email` without name fields. Root cause: extraction prompt needs explicit required-field emphasis. _(2026-03-20)_
- **Employee entity sometimes missing email** — even when clearly stated in the prompt (Portuguese prompts). Ensure extraction schema marks email as required. _(2026-03-20)_
- **Full JSON object serialized as query param** — bug where the entire employee JSON was URL-encoded into `firstName=` and `lastName=` search params instead of just the string values. Check handler code carefully when using extracted nested objects. Use `GetScalarString` (returns null for Object/Array types) instead of `GetStringField` when extracting name/email fields that may be nested objects. _(2026-03-20)_
- **Travel expense misrouted to VoucherHandler** — extraction returned `create_voucher` instead of `create_travel_expense`. Ensure LLM system prompt clearly distinguishes the two task types. _(2026-03-20)_
- **Handler name logging race condition** — `TaskRouter.LastHandlerName` was shared singleton state, corrupted by concurrent requests. Fixed: `RouteAsync` now returns handler name in tuple. Previous competition logs showing "TravelExpenseHandler" for employee tasks were false — actual routing was correct. _(2026-03-20)_
- **Composite task extraction varies** — LLM may put employee/activity data under `timeRegistration` entity OR as separate `employee`/`project.activity` entities. Handler must check both paths. _(2026-03-20)_
- **Voucher dimension nested in voucher entity** — LLM sometimes puts `"dimension": {"name": "Region", "value": "Vestlandet"}` inside the voucher entity instead of as a separate `"dimension"` entity. VoucherHandler has fallback to extract it from `voucher["dimension"]`. Also check singular `"value"` vs plural `"values"` key. _(2026-03-20)_
- **LLM hallucinate `vatIncluded: true`** — when prompts don't mention VAT at all ("a 21950 NOK"), the LLM sometimes set `vatIncluded: true`, causing prices to go into `unitPriceIncludingVatCurrency`. This makes invoice total = sum of stated prices (no VAT added), but competition expects excl. VAT pricing with VAT applied on top. Fixed: LLM prompt explicitly says "do NOT set vatIncluded unless prompt explicitly says prices include VAT." Code safeguard: `BuildOrderLines` checks raw prompt for VAT-inclusive keywords before trusting `vatIncluded=true`. _(2026-03-20)_

## Scoring & Validation Insights

- **Payment `payment_registered` check** — passes when `amountOutstanding = 0`. Must pay the `amountOutstanding` field (VAT-inclusive remaining balance), NOT the `amount` field or the chain-returned amount. PaymentHandler now GETs `/invoice/{id}?fields=id,amount,amountOutstanding` and uses `amountOutstanding` for `paidAmount`. _(2026-03-20, CORRECTED)_
- **Payment has 3 prompt sub-types**: (A) Full chain with order lines — create customer→order→invoice→pay; (B) Simple pay — customer has pending unpaid invoice, find it and pay `amountOutstanding`; (C) Reversal — payment returned by bank, reverse via credit note. Each has different competition checks (2–5 checks, 7–8 pts max). Handler detects variant by: `action=reverse` → reversal, `hasOrderLines` → full chain, else → simple pay with full-chain fallback. _(2026-03-20)_
- **LLM extracts `orgNumber` not `organizationNumber`** — both InvoiceHandler and PaymentHandler must check both key names when looking up customers. Fixed in `ResolveOrCreateCustomer` and `FindCustomerId`. SupplierHandler also affected — added fallback from `orgNumber` to `organizationNumber` in POST body. _(2026-03-20, UPDATED)_
- **`vatIncluded: false` does NOT mean 0% VAT** — it means the stated amount EXCLUDES VAT. VAT should still be applied at 25%. Use `unitPriceExcludingVatCurrency` with the amount as-is. When `vatIncluded: true`, use `unitPriceIncludingVatCurrency` so Tripletex calculates the ex-VAT amount from the incl-VAT price. Previous code incorrectly switched to 0% VAT type when `vatIncluded: false`, causing wrong invoice totals and failed competition checks. _(2026-03-20, CORRECTED)_
- **Admin role is worth 5 out of 11 points** for employee tasks — nearly 50%. Always grant when requested. Use `template=administrator` (not `ALL_PRIVILEGES`) per the copilot-instructions, but `ALL_PRIVILEGES` also works. _(2026-03-20)_
- **Product `number` field is checked** — validator verifies the product number matches the prompt. Always extract and set it. _(2026-03-20)_
- **Product price key alias** — LLM extracts price as `"price"` or `"unitPrice"` key. Handler must alias these to `priceExcludingVatCurrency`. SandboxValidator also needs alias check on the entity dict (uses `"price"` → `"priceExcludingVatCurrency"`). _(2026-03-20)_
- **Product number key alias** — LLM extracts number as `"productNumber"` key. Handler aliases it to `"number"` via `SetWithAlias`. SandboxValidator must check `"productNumber"` as alias for `"number"`. _(2026-03-20)_
- **Product vatType sandbox rejection** — local sandbox rejects ALL vatType IDs for POST /product with "Ugyldig mva-kode". Competition sandbox accepts vatType=1. Do NOT default vatType unless explicitly extracted. If extracted and fails 422, retry without vatType. _(2026-03-20)_
- **Department validation only checks `department_found`** (2 pts) — no field-level checks beyond existence. _(2026-03-20)_
- **Travel expense `has_costs` check** wants `> 0` cost lines — at least one `POST /travelExpense/cost` required. _(2026-03-20)_
- **Travel expense has 6 checks worth 8 points total** — 6/6 passed with correct employee, title, and all cost lines including per diem. _(2026-03-20)_
- **Travel expense `costItems` key** — LLM extracts cost items as `costItems` (camelCase). Handler must check `costs`, `costItems`, `cost_items`, and `costLines` variants. _(2026-03-20)_
- **Travel expense employee resolution** — use `extracted.Entities["employee"]` (has firstName/lastName/email), NOT `extracted.Relationships["employee"]` (often contains just the email as a flat string). Search by firstName+lastName first, then by email. ALSO: LLM sometimes nests employee inside `travelExpense` entity instead of as a top-level entity — handler must parse nested `travel["employee"]` JsonElement to extract firstName/lastName/email. If still not found, CREATE the employee instead of falling back to "first available" (which links to the wrong person → 0/6). _(2026-03-20, UPDATED)_
- **Travel expense per diem** — when `durationDays` and `dailyAllowanceRate` are in travelDetails, generate a per diem cost line (days × rate). This is a separate cost from explicit expense items. LLM may use `perDiemRate` or `dailyRate` instead of `dailyAllowanceRate` — handler checks all three aliases. _(2026-03-20, UPDATED)_
- **Invoice validation checks**: `invoice_found`, `has_customer`, `has_amount > 0`. Amount includes VAT. _(2026-03-20)_
- **Order line `count` defaults to 0 in Tripletex** — if you omit `count` on an order line, Tripletex treats it as 0 and the invoice amount becomes 0. Always set `count = 1` as default. _(2026-03-20)_
- **Composite invoice tasks (project + time + invoice)** — competition checks 8 points across 4 checks: project/timesheet + invoice. Our local validator only checks invoice (5/5). Must create project, link activity, register timesheet hours, AND create invoice. _(2026-03-20)_
- **Credit note validation**: only checks `credit_note_created` (3 pts). _(2026-03-20)_
- **Voucher account lock detection requires `vatLocked` field** — use `fields=id,number,vatLocked,vatType(id,number)` when fetching account. The `vatLocked` boolean is the only reliable way to determine if an account is locked to its vatType. Having a default `vatType` does NOT mean locked (e.g. account 6540 has default vatType 0 but `vatLocked=false` → can override with input VAT). Account 7100 has `vatLocked=true` → must respect its locked vatType. Previous bug: assuming any vatType presence = locked caused supplier invoices on account 6540 to skip VAT lookup entirely, resulting in 0/4 competition score. _(2026-03-20)_
- **Delete entity validation**: only checks `entity_deleted` (3 pts). _(2026-03-20)_
- **All 14 validation tasks achieved 100% correctness** in latest sandbox run. _(2026-03-20)_
- **Payroll STJ serialization fix** — anonymous types inside `object` parameters (e.g., payslip body with `employee = new { id = X }`) silently dropped properties during `JsonSerializer.Serialize(body)`. Converting to `Dictionary<string, object>` throughout PayrollHandler fixed `has_employee_link` and `payslip_generated` checks (0/4 → 8/8). Same fix applied to EmployeeHandler entity refs. Always use Dictionary, never anonymous types, for nested objects inside Dictionary/object parameters. _(2026-03-21)_

## Efficiency Baselines (Observed Optimal)

| Task                  | Optimal calls | Notes                                                                     |
| --------------------- | ------------- | ------------------------------------------------------------------------- |
| create_customer       | 1             | Just POST                                                                 |
| create_supplier       | 1             | Just POST                                                                 |
| create_product        | 2             | GET vatType + POST                                                        |
| create_employee       | 3–5           | dept + POST + employment + entitlements                                   |
| create_department     | 2+            | GET existing + POST (+ retries if collisions)                             |
| create_invoice        | 4–6           | customer + vatType + order + invoice + optional send                      |
| register_payment      | 6–8           | Full invoice chain + GET invoice + GET paymentType + PUT :payment         |
| create_project        | 4–8           | customer lookup + employee lookup/create + entitlements + POST            |
| create_travel_expense | 5–7           | employee lookup + POST expense + GET paymentType + POST cost(s)           |
| create_voucher        | 6–9           | Supplier invoice: 6 calls (no importDocument). Dimension voucher: 9 calls |
| create_credit_note    | 6             | Find invoice + create credit note                                         |
| delete_entity         | 2             | Find entity + DELETE                                                      |

## Reference Documents

- **`entity-model.md`** — Complete entity relationship reference: all entity schemas, required fields, cross-references, dependency chains per task type, action endpoints, and common pitfalls. Consult when implementing handlers or debugging entity relationship issues. _(2026-03-20)_

## Competition vs Sandbox Differences

- **Competition runs against a clean/fresh Tripletex instance** — no pre-existing data, no duplicate emails or department numbers from prior runs. _(2026-03-20)_
- **Sandbox accumulates data** — duplicate emails, department numbers 1–7+ already taken. Don't add sandbox-specific workarounds. _(2026-03-20)_
- **Bank account issue appeared only in competition** — sandbox had it pre-configured. May need to handle this for invoice tasks. _(2026-03-20)_
- **Competition proxy blocks certain endpoints** — `POST /incomingInvoice` returns 403 "no permission" even with KOMPLETT module active. The proxy likely uses an endpoint allowlist. Our voucher-based approach for supplier invoices avoids this. Be cautious with exotic endpoints in FallbackAgentHandler. _(2026-03-20)_
- **Division (virksomhet) doesn't exist in clean env** — must create via `POST /division` before payroll. Requires `name`, `organizationNumber`, `startDate`, `municipalityDate`, `municipality` (ref from `/municipality`). Sandbox may already have one from prior runs. _(2026-03-20)_
- **Employment needs division for payroll** — `POST /salary/transaction` returns 422 "Arbeidsforholdet er ikke knyttet mot en virksomhet" if employment has no division set. Always GET/create division and PUT it onto employment before salary transaction. _(2026-03-20)_
- **Payroll (run_payroll) uses payslip search that is BROKEN in sandbox** — `GET /salary/payslip` with any filters (employeeId, wageTransactionId, yearFrom, etc.) always returns `fullResultSize:0` even when payslips exist. Individual `GET /salary/payslip/{id}` works fine. Competition also scores 0/4 despite 201 success — possibly same search issue in the competition validator. `isHistorical=true` + no generateTaxDeduction is NOT viable (different behavior). All action endpoints (`:close`, `:approve`, `:book`) return 404. No `/salary/transaction` list endpoint exists. _(2026-03-20)_
- **PAYROLL FIX: Dual salary transaction + voucher on 5000-series accounts** — Competition prompt explicitly says to use 5000-series voucher fallback. Fix: create salary transaction (generates payslips) AND ALSO create a manual voucher (POST /ledger/voucher?sendToLedger=true) with debit 5000 (Lønn) and credit 1920 (Bank). This scores 8/8 locally. Salary type mapping was also fixed: type #2000 = "Fastlønn" (NOT #1000 which is "Gjeld til ansatte"). Two-pass lookup: exact number match first (#2000 for base, #1350 for bonus), then name-based fallback. _(2026-03-20)_
- **SandboxValidator for payroll — correct_amount check uses grossAmount** — `payslip.grossAmount` = salary before tax. `payslip.amount` = net after tax deduction (approx half). Validator must sum `grossAmount`, not `amount`. Fixed 2026-03-20.

- **PAYROLL CHECKS 2&3 ROOT CAUSE (2026-03-20)** — Competition checks `has_employee_link` and `payslip_generated` fail even when salary transaction is created successfully (checks 1&4 pass). Root cause: `GET /salary/transaction/{id}` with `fields=id,date,year,month,payslips(...)` returns payslip stubs with only `{id, url}` — the employee field is NOT expanded even with `payslips(id,employee,...)`. Competition validator likely does individual `GET /salary/payslip/{id}` per stub to get full payslip with employee ref. Fixes: (1) PayrollHandler now also does `GET /salary/payslip/{id}` after transaction creation to verify payslip is accessible. (2) Voucher posting on account 5000 now includes `employee: {id}` reference. (3) SandboxValidator now fetches individual payslip by stub ID to correctly evaluate `has_employee_link` and `grossAmount`.

- **SalaryTransaction has NO `employee` field** — valid fields: `id`, `version`, `date`, `year`, `month`, `payslips`, `paySlipsAvailableDate`, `isHistorical`. Employee is on the payslip, not the transaction. Querying with `fields=employee` → 400. _(2026-03-20)_

- **CRITICAL: System.Text.Json drops properties from anonymous types inside `object[]`** — When serializing `new object[] { new { id = 1, employee = new { id = 2 } } }`, the array element type is `object`, so System.Text.Json can't discover anonymous type properties → they're silently dropped. This was the root cause of the voucher `employee` field being missing in competition submissions (Check 2 failure). Fix: use `Dictionary<string, object>` instead of anonymous types for any payload inside `object[]` or `List<object>`. _(2026-03-21)_
- **Division creation: don't use parent company orgNumber** — The company's organization number is a "juridisk enhet" (legal entity), which Tripletex rejects for divisions/sub-units: "Juridisk enhet kan ikke registreres som virksomhet/underenhet". Fix: try creating division WITHOUT `organizationNumber` first. Only fall back to using org number if the first attempt fails. _(2026-03-21)_
- **Sandbox chart of accounts is INCOMPLETE** — accounts 1209 (akkumulerte avskrivninger) and 8700 (skattekostnad) do NOT exist in the sandbox. Account 1200, 1210, 1230, 1250, 1700, 2920, 6010 DO exist. Competition may have different accounts. VoucherHandler now logs "Account {Number} not found in chart of accounts" and skips imbalanced vouchers instead of wasting API calls on 422 errors. _(2026-03-21)_
- **Bank reconciliation period lookup: `sorting=end:desc` not supported** — returns 422 "sorting field 'end:desc' does not exist". Fix: fetch all 100 periods and pick the last one by comparing end dates. _(2026-03-21)_
- **Bank reconciliation duplicate period check** — POST fails with 422 "Det eksisterer allerede en bankavstemming for denne kontoen i valgte periode" if a reconciliation already exists for the same account/period. Expected in sandbox (state persists). Fresh competition environments won't have this. _(2026-03-21)_
- **Employment enrichment with full details** — set `taxDeductionCode = "loennFraHovedarbeidsgiver"`, `employmentDetails` with `employmentType=ORDINARY`, `employmentForm=PERMANENT`, `remunerationType=MONTHLY_WAGE`, `workingHoursScheme=NOT_SHIFT`, `percentageOfFullTimeEquivalent=100.0`. These are UPPERCASE enum values. _(2026-03-20)_

## Fixed-Price Project Invoicing (create_project with invoice)

- **Linking order to project via `project: { id }` on POST /order body requires NO `vatType` on order lines** — if you include `vatType` on the order line AND link to a project that has no `vatType`, Tripletex returns 422 "project.orderLines.vatType.id: Ugyldig mva-kode". Fix: omit `vatType` from order lines entirely and let the project inherit. _(2026-03-20)_
- **Setting `vatType` directly on the Project body (POST /project) fails** — same vatType IDs that work for order lines (DB ID 3, number=3) are invalid for the project-level `vatType` field. Always omit `vatType` when creating projects. _(2026-03-20)_
- **`invoicingPlan` on Project is readOnly and auto-populated only by internal Tripletex billing** — creating an invoice from a project-linked order does NOT populate `invoicingPlan`. _(2026-03-20)_
- **`preliminaryInvoice` on Project APPEARS writable but always reads back as NULL** — PUT /project with `preliminaryInvoice: { id }` returns 200 but subsequent GET shows `preliminaryInvoice: null`. The field silently ignores the value. SKIP the GET+PUT cycle entirely — it wastes 2 API calls and has zero effect. Competition does NOT check this field. _(CORRECTED 2026-03-21, previously said writable)_
- **PUT /project is full replacement — MUST include all fields** — a PUT with only `{id, version}` resets `fixedprice=0`, `isFixedPrice=false`, customer, projectManager. Always echo all fields back in PUT body. _(2026-03-20)_ **UPDATE**: probing confirmed H1 is DISPROVEN — Tripletex actually preserves unspecified fields on PUT. Still safer to include them but the reset risk was overstated. _(2026-03-21)_
- **Project invoice root cause: gate on LLM invoice entity extraction was unreliable** — invoice creation was gated on `invoiceEntity != null`. When LLM didn't extract an `invoice` entity (even though the prompt mentioned a percentage), the invoice was silently skipped → Check 2 failed. Fix: always call `CreateProjectInvoice` if `isFixedPrice=true` AND the raw prompt contains `\d+\s*%`. Competition results showed: 12-call runs (invoice created) → 8/8 pass; 10-call runs (invoice skipped) → 6/8 fail. _(2026-03-21)_
- **Milestone percentage: calculate deterministically, never trust LLM math** — extract percentage from prompt via regex `(\d+)\s*%`, then compute `Math.Round(fixedPrice * pct / 100, 2)`. LLM calculated 170500×33%=56365 (wrong, correct=56265). _(2026-03-20)_
- **`fields=*` on GET /project does NOT return `preliminaryInvoice`** — must explicitly list it: `fields=id,name,...,preliminaryInvoice`. Always use explicit field list for project validation. _(2026-03-20)_
- **GET /invoice?projectId=X requires `invoiceDateFrom` and `invoiceDateTo`** — these are mandatory date parameters. Use `invoiceDateFrom=2020-01-01&invoiceDateTo=2030-12-31` when checking. _(2026-03-20)_
- **GET /order?projectId=X requires `orderDateFrom` and `orderDateTo`** — similar mandatory date range requirement. _(2026-03-20)_

## SandboxValidator Check Updates (2026-03-20, session 2)

- **Phase 1 — CreditNote**: Validator now does 5 checks / 8pts. CreditNoteHandler sets `EntityId = originalInvoiceId` (NOT the credit note ID). Validator searches invoices for isCreditNote=true or negative amount and validates: credit_note_found (2), has_customer (2), has_amount (1), correct_amount (2), has_linked_invoice (1).
- **Phase 2 — Invoice**: Validator now does up to 6 checks / 7-8pts: invoice_found (2), has_customer (1), has_amount (1), has_order_lines (1), correct_amount (1 conditional), invoice_sent (1 conditional).

## Date Validation (2026-03-21)

- **LLM generates invalid dates like `2026-02-29`** (non-leap year) — causes 422 on any API endpoint using that date. Fixed by adding `ValidateDates()` post-processing in LlmExtractor. Snaps invalid dates to last valid day of month (e.g. Feb 29 → Feb 28). Applies to both `ExtractionResult.Dates` list and date-named fields in entities. _(2026-03-21)_

## FX Payments — Foreign Currency (2026-03-21)

- **`PUT /invoice/{id}/:payment` supports `paidAmountCurrency`** query param — "Amount paid by customer in the invoice currency. Optional, but required for invoices in alternate currencies." Type: number. _(2026-03-21)_
- **FX payment flow**: Create order with `currency: {id: X}` → create invoice → register payment with `paidAmount` (NOK = foreign × rateAtPayment) and `paidAmountCurrency` (foreign amount). Tripletex auto-handles agio/disagio. _(2026-03-21)_
- **`GET /currency?code=EUR&count=1&fields=id`** resolves currency code to Tripletex ID. EUR = ID 5 in sandbox. _(2026-03-21)_
- **LLM already extracts FX fields** (`currency`, `exchangeRateAtInvoice`, `exchangeRateAtPayment`) from prompts mentioning exchange rates — no system prompt changes needed. _(2026-03-21)_
- **FX detection**: `IsFxPayment()` checks for `currency` or `exchangeRateAtPayment` in payment/invoice entities. Does NOT false-trigger on normal NOK payments. _(2026-03-21)_
- **Phase 3 — Voucher**: Validator now does up to 6 checks / 8-13pts: voucher_found (2), has_description (2), has_postings (2), postings_balanced (2), correct_accounts (2 conditional), correct_amount (3 conditional). Sums debit/credit from amountGross or amount fields.

## Efficiency Optimizations (2026-03-21)

- **POST-first customer strategy** — `ResolveOrCreateCustomer` now tries `POST /customer` first (saves 1 GET in competition where env is clean). On 422 (duplicate in sandbox), falls back to `GET /customer?organizationNumber=` / `?name=`. This eliminates the GET for all competition runs. _(2026-03-21)_
- **Hardcoded paymentTypeId = 33295810** — constant across all clean Tripletex environments. `ResolvePaymentTypeId` returns immediately without an API call. On 422 in payment PUT, falls back to `ResolvePaymentTypeIdDynamic` (1 extra call). _(2026-03-21)_
- **VAT type fallback on 422** — `CreateInvoiceChainAsync` catches `TripletexApiException` with `StatusCode==422` and message containing "mva-kode" on `POST /order`. On catch: calls `ResolveVatTypesFull` (+1 GET), rebuilds lines, retries POST /order. In competition (IDs work), zero overhead. In sandbox, +2 calls but still succeeds. _(2026-03-21)_
- **Estimated competition call counts after optimizations**:
  - `create_invoice` (fresh env): bank GET + customer POST + order POST + invoice POST + validator GET = **5 calls** (was 8)
  - `register_payment` (full chain, fresh env): bank GET + customer POST + order POST + invoice POST + payment PUT + validator GET = **6 calls** (was 8, no paymentType GET)
  - `register_payment` (simple pay): customer GET + invoices GET + payment PUT + validator GET = **4 calls** (unchanged)
  - Note: bank GET is only incurred once per session (cached after first call). Subsequent requests skip it → 4/5 calls.
- **Phase 4 — TravelExpense**: Validator now does up to 6 checks / 8pts: travel_expense_found (1), has_title (1), has_employee (2), has_costs (1), correct_cost_count (1 conditional), has_dates (2 conditional). Fetches cost details via GET /travelExpense/cost.
- **Phase 5 — Payment**: Validator now does 5 checks / 8pts: invoice_found (2), payment_registered (2), correct_paid_amount (2), has_customer (1), has_amount (1).
- **Phase 6 — Point normalization**: employee_found=1 (was 2), admin_role=2 (was 5), dept name=3+number=2 (was 2+1), product_found=1+number=2+price=2 (was 2+1+1), customer_found=1 (was 2), project has_customer=2+has_PM=2 (was 1+1).
- **Phase 7 — Delete validator**: Now async. Verifies deletion by GET → 404 (exception = confirmed). Falls back to metadata flag for unknown entity types.
- **Program.cs bug**: `SaveReceivedFiles` had ambiguous `ILogger` parameter — resolved with fully qualified `Microsoft.Extensions.Logging.ILogger`. This was a pre-existing bug unrelated to validator changes. _(2026-03-20)_
- **Payroll: Salary transactions do NOT auto-create vouchers** — the only discoverable accounting artifact is our manual 5000-series voucher. Without it, the competition finds nothing. _(2026-03-21)_
- **Payroll: payslip search is broken in sandbox** — always returns count=0 regardless of filters (employeeId, voucherDateFrom/To, wageTransactionId). Salary transaction search returns 403. These may work in competition. _(2026-03-21)_
- **Payroll: `paySlipsAvailableDate` ignored by API** — always set to the transaction date regardless of the value you send. _(2026-03-21)_
- **Payroll: `isHistorical=true` + `generateTaxDeduction=true` is invalid** — produces 422 "Skatt kan ikke genereres for historiske lønnsbilag". Use `isHistorical=true` without generateTaxDeduction, or `isHistorical=false` with it. _(2026-03-21)_
- **Payroll: voucherType must be set to "Lønnsbilag"** — without it, the voucher has `voucherType: null`. Look up the type ID dynamically via `GET /ledger/voucherType?name=Lønnsbilag&count=1&fields=id`. _(2026-03-21)_
- **Payroll: employee must be on BOTH voucher posting rows** — debit (5000) AND credit (1920). Previously only row 1 had employee, row 2 was null. This may affect `has_employee_link` competition check. _(2026-03-21)_

## Supplier Invoice Voucher — importDocument Path Removed (2026-03-21)

- **`PUT /supplierInvoice/voucher/{id}/postings` always returns 500 (code 1000, null message)** on the competition proxy — 6/6 observations, zero successes ever. This endpoint is effectively broken via the proxy. _(2026-03-21)_
- **importDocument + PUT path removed from VoucherHandler** — HandleSupplierInvoice() previously tried `POST /ledger/voucher/importDocument` then `PUT /supplierInvoice/voucher/{id}/postings`, caught the 500, and fell back to classic voucher. This wasted 2 API calls and logged 1 error per request. Fix: removed the try/catch block entirely; handler goes straight to the classic Leverandørfaktura voucher path. _(2026-03-21)_
- **Classic supplier voucher path (now the ONLY path)**: POST /supplier → GET /ledger/account?number={expenseAcct} → GET /ledger/vatType → GET /ledger/voucherType?name=Leverandørfaktura → GET /ledger/account?number=2400 → POST /ledger/voucher?sendToLedger=true = **6 calls, 0 errors** (was 8 calls, 1 error). _(2026-03-21)_
- **PDF content is still consumed** — even without importDocument, the LLM reads the PDF bytes via file attachment and extracts supplier name, amount, account, VAT from OCR. The importDocument call was purely for creating a voucher shell, not for PDF extraction. _(2026-03-21)_
