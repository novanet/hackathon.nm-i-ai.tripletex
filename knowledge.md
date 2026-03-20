# Knowledge Base — Verified Learnings

**Read this before debugging, implementing handlers, or investigating failures.**
**Update this whenever you discover a new API quirk, extraction pitfall, scoring insight, or fix a bug.**
Keep entries short (1–2 lines). Include the date discovered.

---

## Tripletex API Quirks

- **Employee `startDate` is NOT a field on `/employee`** — use `POST /employee/employment` as a separate call after creating the employee. The employee POST will 422 with "Feltet eksisterer ikke i objektet" if you include `startDate`. _(2026-03-20)_
- **Employee `dateOfBirth` must be set** before creating employment — otherwise 422 "Feltet må fylles ut". Always extract DOB from prompt. _(2026-03-20)_
- **Employee `userType` must not be `"0"` or empty** — use `"STANDARD"`. Error: "Brukertype kan ikke være «0» eller tom." Must be set when creating employees anywhere (specifically in TravelExpenseHandler and other handlers, not just EmployeeHandler). _(2026-03-20)_
- **Employee `department.id` required when department module active** — query `GET /department?count=1` and use first available ID. Sandbox has department module enabled. Competition may or may not have it. _(2026-03-20)_
- **Employee `email` is required** for Tripletex users — 422 "Må angis for Tripletex-brukere" if missing. _(2026-03-20)_
- **Employment `division` required in some environments** — sandbox requires `division.id` on `POST /employee/employment` ("Arbeidsforholdet må knyttes til en virksomhet/underenhet"), but competition environments work without it. Handler retries with division lookup on failure. _(2026-03-20)_
- **Project `startDate` is required** — defaults to today if not specified in prompt. Error: "Feltet må fylles ut." _(2026-03-20)_
- **Project manager needs entitlements** — must call `PUT /employee/entitlement/:grantEntitlementsByTemplate?template=ALL_PRIVILEGES` before assigning as PM. Error: "Oppgitt prosjektleder har ikke fått tilgang som prosjektleder i kontoen". _(2026-03-20)_
- **Invoice requires company bank account** — the company (not customer) must have a bank account registered. Error: "Faktura kan ikke opprettes før selskapet har registrert et bankkontonummer." Seen in competition but not sandbox. _(2026-03-20)_
- **Voucher requires `postings` array** — cannot POST without it. Error: "Et bilag kan ikke registreres uten posteringer." _(2026-03-20)_
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
- **LLM extracts `orgNumber` not `organizationNumber`** — both InvoiceHandler and PaymentHandler must check both key names when looking up customers. Fixed in `ResolveOrCreateCustomer` and `FindCustomerId`. _(2026-03-20)_
- **`vatIncluded: false` does NOT mean 0% VAT** — it means the stated amount EXCLUDES VAT. VAT should still be applied at 25%. Use `unitPriceExcludingVatCurrency` with the amount as-is. When `vatIncluded: true`, use `unitPriceIncludingVatCurrency` so Tripletex calculates the ex-VAT amount from the incl-VAT price. Previous code incorrectly switched to 0% VAT type when `vatIncluded: false`, causing wrong invoice totals and failed competition checks. _(2026-03-20, CORRECTED)_
- **Admin role is worth 5 out of 11 points** for employee tasks — nearly 50%. Always grant when requested. Use `template=administrator` (not `ALL_PRIVILEGES`) per the copilot-instructions, but `ALL_PRIVILEGES` also works. _(2026-03-20)_
- **Product `number` field is checked** — validator verifies the product number matches the prompt. Always extract and set it. _(2026-03-20)_
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
- **Voucher account lock detection requires `vatType(id,number)` fields** — use `fields=id,number,vatType(id,number)` when fetching account. If `vatType.number == 0`, account is locked to no-VAT (do NOT add vatType to posting). If `vatType` object exists at all, account is locked to that type. Competition confirmed: account 7100 locked to code 0 → must omit vatType field from posting or get 422. _(2026-03-20)_
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

- **Tripletex `fields=*` does NOT expand nested objects** — `GET /customer/{id}?fields=*` returns `physicalAddress` as a reference `{id, url}` without field data. Must use `fields=*,physicalAddress(*),postalAddress(*)` to get actual address fields. This pattern applies to ALL entity types with nested object references. _(2026-03-20)_
- **Employee creation requires `department.id` in some environments** — when department module is active, `POST /employee` without `department` → 422 "department.id: Feltet må fylles ut". Always look up first department with `GET /department?count=1&fields=id` and include it. _(2026-03-20)_

## Reference Documents

- **`entity-model.md`** — Complete entity relationship reference: all entity schemas, required fields, cross-references, dependency chains per task type, action endpoints, and common pitfalls. Consult when implementing handlers or debugging entity relationship issues. _(2026-03-20)_

## Competition vs Sandbox Differences

- **Competition runs against a clean/fresh Tripletex instance** — no pre-existing data, no duplicate emails or department numbers from prior runs. _(2026-03-20)_
- **Sandbox accumulates data** — duplicate emails, department numbers 1–7+ already taken. Don't add sandbox-specific workarounds. _(2026-03-20)_
- **Bank account issue appeared only in competition** — sandbox had it pre-configured. May need to handle this for invoice tasks. _(2026-03-20)_
- **Competition proxy blocks certain endpoints** — `POST /incomingInvoice` returns 403 "no permission" even with KOMPLETT module active. The proxy likely uses an endpoint allowlist. Our voucher-based approach for supplier invoices avoids this. Be cautious with exotic endpoints in FallbackAgentHandler. _(2026-03-20)_
- **Division (virksomhet) doesn't exist in clean env** — must create via `POST /division` before payroll. Requires `name`, `organizationNumber`, `startDate`, `municipalityDate`, `municipality` (ref from `/municipality`). Sandbox may already have one from prior runs. _(2026-03-20)_
- **Employment needs division for payroll** — `POST /salary/transaction` returns 422 "Arbeidsforholdet er ikke knyttet mot en virksomhet" if employment has no division set. Always GET/create division and PUT it onto employment before salary transaction. _(2026-03-20)_
- **Payroll (run_payroll) uses payslip search that is BROKEN in sandbox** — `GET /salary/payslip` with any filters (employeeId, wageTransactionId, yearFrom, etc.) always returns `fullResultSize:0` even when payslips exist. Individual `GET /salary/payslip/{id}` works fine. Competition also scores 0/4 despite 201 success — possibly same search issue in the competition validator. `isHistorical=true` + no generateTaxDeduction is NOT viable (different behavior). All action endpoints (`:close`, `:approve`, `:book`) return 404. No `/salary/transaction` list endpoint exists. **Status: unresolved — nobody in competition has solved this either.** _(2026-03-20, UPDATED 2026-03-20)_
- **SandboxValidator for payroll gives false positives** — uses `GET /salary/transaction/{entityId}` which works because we have the ID. Competition validator can't do this. The local 6/6 score is unreliable. _(2026-03-20)_

- **SalaryTransaction has NO `employee` field** — valid fields: `id`, `version`, `date`, `year`, `month`, `payslips`, `paySlipsAvailableDate`, `isHistorical`. Employee is on the payslip, not the transaction. Querying with `fields=employee` → 400. _(2026-03-20)_
- **Employment enrichment with full details** — set `taxDeductionCode = "loennFraHovedarbeidsgiver"`, `employmentDetails` with `employmentType=ORDINARY`, `employmentForm=PERMANENT`, `remunerationType=MONTHLY_WAGE`, `workingHoursScheme=NOT_SHIFT`, `percentageOfFullTimeEquivalent=100.0`. These are UPPERCASE enum values. _(2026-03-20)_

## Fixed-Price Project Invoicing (create_project with invoice)

- **Linking order to project via `project: { id }` on POST /order body requires NO `vatType` on order lines** — if you include `vatType` on the order line AND link to a project that has no `vatType`, Tripletex returns 422 "project.orderLines.vatType.id: Ugyldig mva-kode". Fix: omit `vatType` from order lines entirely and let the project inherit. _(2026-03-20)_
- **Setting `vatType` directly on the Project body (POST /project) fails** — same vatType IDs that work for order lines (DB ID 3, number=3) are invalid for the project-level `vatType` field. Always omit `vatType` when creating projects. _(2026-03-20)_
- **`invoicingPlan` on Project is readOnly and auto-populated only by internal Tripletex billing** — creating an invoice from a project-linked order does NOT populate `invoicingPlan`. _(2026-03-20)_
- **`preliminaryInvoice` on Project IS writable** — after creating the invoice, do `GET /project/{id}?fields=id,version` then `PUT /project/{id}` with `{ id, version, preliminaryInvoice: { id: invoiceId } }`. This persists the link. _(2026-03-20)_
- **`fields=*` on GET /project does NOT return `preliminaryInvoice`** — must explicitly list it: `fields=id,name,...,preliminaryInvoice`. Always use explicit field list for project validation. _(2026-03-20)_
- **GET /invoice?projectId=X requires `invoiceDateFrom` and `invoiceDateTo`** — these are mandatory date parameters. Use `invoiceDateFrom=2020-01-01&invoiceDateTo=2030-12-31` when checking. _(2026-03-20)_
- **GET /order?projectId=X requires `orderDateFrom` and `orderDateTo`** — similar mandatory date range requirement. _(2026-03-20)_
