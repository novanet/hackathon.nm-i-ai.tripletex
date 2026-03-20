# Competition Plan — NM i AI 2026 Tripletex Agent

**Date:** 2026-03-20 (updated 21:30)
**Current rank:** TBD (was #32, likely improved significantly)
**Estimated score:** ~47+ (up from ~40)
**Tasks solved:** 21/30 (up from 18)
**Tries used:** ~90+ (across 8 submission runs today)
**Competition ends:** March 22, 15:00 CET (~42 hours remaining)

---

## 1. Current Best Scores Per Task Type (from competition results)

Scores are `normalized_score` (best observed from results.jsonl). Competition takes best-per-task.

### Tier 1 — Simple CRUD (max 2.0 per task)

| Task Type         | Best Score | Checks    | Status      | Notes                                 |
| ----------------- | ---------- | --------- | ----------- | ------------------------------------- |
| create_customer   | **2.00**   | 7/7 ✅    | ✅ DONE     | 1 API call, works perfectly           |
| create_product    | **1.50**   | 5/5 ✅    | ✅ DONE     | 2 API calls, works perfectly          |
| create_department | **1.25**   | 3/3 ✅    | ✅ DONE     | 4 API calls (GET + 3 POST)            |
| create_employee   | **2.00**   | 11/11 ✅  | ✅ DONE     | Fixed: Nynorsk, division, routing     |
| create_supplier   | **2.00**   | (assumed) | ✅ DONE     | 1 API call                            |
| enable_module     | **0.00**   | —         | ❌ UNTESTED | Never appeared in competition results |

### Tier 2 — Multi-step (max 4.0 per task)

| Task Type                    | Best Score | Checks | Status     | Notes                                          |
| ---------------------------- | ---------- | ------ | ---------- | ---------------------------------------------- |
| create_invoice               | **3.11**   | 6/6 ✅ | ✅ DONE    | Works with multi-line+mixed VAT, bank acct fix |
| create_voucher (dim)         | **3.25**   | 6/6 ✅ | ✅ DONE    | Custom dimension + voucher                     |
| create_voucher (supp)        | **0.00**   | 0/4 ❌ | ✅ FIXED   | VAT lock detection fixed — tested 5/5 locally  |
| create_travel_expense        | **4.00**   | 6/6 ✅ | ✅ FIXED   | GetScalarString + regex fallback + create emp  |
| create_credit_note           | **2.67**   | 5/5 ✅ | ✅ DONE    | Works consistently                             |
| register_payment (fwd)       | **3.20**   | 5/5 ✅ | ⚠️ PARTIAL | Full chain w/ products works; simple fwd fails |
| register_payment (rev)       | **1.50**   | 2/3 ⚠️ | ❌ BROKEN  | Creates new invoice instead of reversing       |
| register_payment (simple)    | **0.29**   | 1/2 ⚠️ | ❌ BROKEN  | Check 2 fails (wrong payment amount)           |
| run_payroll                  | **0.00**   | 0/4 ❌ | ❌ BROKEN  | 201 success but validator finds nothing        |
| create_project (simple)      | **1.75**   | 4/4 ✅ | ✅ DONE    | PM resolution fixed                            |
| create_project (fixed price) | **1.50**   | 3/4 ⚠️ | ✅ FIXED   | GetScalarString PM fix — tested 8/8 locally    |
| create_project (composite)   | **0.00**   | 0/4 ❌ | ❌ BROKEN  | Hours+project+invoice — only creates invoice   |

---

## 2. Fixes Completed Since Original Plan

### ✅ Fix 1: Employee Handler — Routing & Nynorsk (commit a366009)

**Problem:** Employees misrouted to TravelExpenseHandler; Nynorsk keywords "fødd"/"heiter" not recognized.

**Fixes:**

- `TaskRouter.cs`: `RouteAsync` returns handler name in tuple (thread-safe)
- `Program.cs`: Broadened employee override with multi-language regex
- `LlmExtractor.cs`: Added Nynorsk keywords to regex fallback
- `EmployeeHandler.cs`: Employment POST retries with division lookup on failure

**Result:** 11/11 checks (with admin), all languages pass. Best score: **2.00**

### ✅ Fix 2: Project Handler — PM Resolution

**Problem:** JSON-as-query-param bug — full JSON object serialized into firstName/lastName search params.

**Fixes:**

- Extract firstName/lastName as plain strings from nested PM object
- Email-based fallback search when name search fails
- Create PM employee if not found
- Grant ALL_PRIVILEGES before assigning as PM

**Result:** Simple projects now 4/4 consistently. Best score: **1.75**

### ✅ Fix 3: Invoice Handler — Bank Account & VAT

**Problem:** "Faktura kan ikke opprettes før selskapet har registrert et bankkontonummer" in competition. Also `vatIncluded` logic was wrong — was switching to 0% VAT when `vatIncluded: false`.

**Fixes:**

- Auto-register bank account on company account 1920 before invoice creation
- Fixed VAT logic: `vatIncluded: false` = use excl-VAT pricing with correct VAT rate (not 0%)
- Added safeguard against LLM hallucinating `vatIncluded: true`

**Result:** 6/6 checks consistently, multi-line with mixed VAT rates works. Best score: **3.11**

### ✅ Fix 4: Voucher Handler — Custom Dimensions

**Problem:** Voucher posted without `postings` array. Dimension creation was missing.

**Fixes:**

- Complete dimension creation flow: search existing → create name → create values
- Proper double-entry bookkeeping (expense account + counter-account 1920)
- Link dimension value to posting
- Supplier invoice detection with INPUT VAT (inbound) type

**Result:** 6/6 checks for custom dimension vouchers. Best score: **3.25**

### ✅ Fix 5: Credit Note Handler

**Problem:** Unknown prior state.

**Fixes:** Create original invoice chain → creditNote action.

**Result:** 5/5 checks consistently. Best score: **2.67**

### ✅ Fix 6: Payment Handler — Forward with Order Lines

**Problem:** Paying wrong amount (ex-VAT instead of incl-VAT).

**Fixes:**

- GET invoice after creation to read `amountOutstanding` (always incl-VAT)
- Pay `amountOutstanding` instead of extracted amount
- Product lookup by number before creating duplicates

**Result:** 5/5 checks for order→invoice→payment. Best score: **3.20**

### ✅ Fix 7: Travel Expense Handler — Per Diem & Employee Resolution

**Problem:** Employee often not found; cost items not all posted; per diem missing.

**Fixes:**

- Check multiple entity paths for employee (top-level entity, nested in travelExpense)
- Per diem cost line from durationDays × dailyRate
- Create employee if not found instead of falling back to "first available"

**Result:** 6/6 when employee found by name. Best score: **4.00**. Still **0/6** when employee not found by name (falls back incorrectly in some code paths).

### ✅ Fix 8: Department Handler

**Fixes:** Pre-query existing departments to avoid number collisions.

**Result:** 3/3 checks. Best score: **1.25**

### ✅ Fix 9: Travel Expense — Employee Resolution (GetScalarString & Regex Fallback)

**Problem:** Employee lookup failed when LLM nested employee as JSON object inside `travelExpense` entity. `GetStringField` returned raw JSON as firstName/lastName query param → URL-encoded JSON → no match. Also: `userType` missing from employee creation body.

**Fixes:**

- Added `GetScalarString` helper — returns null for Object/Array-typed `JsonElement` (prevents raw JSON serialization)
- Unified nested `JsonElement` handling: one branch covers both Object and String kinds
- Added 7-language regex fallback on raw prompt: `(?:for|pour|para|für|f[oö]r)\s+([A-ZÆØÅ]+)\s+([A-ZÆØÅ]+)`
- Added email regex fallback from raw prompt
- Employee creation: `userType = "STANDARD"` (was missing — caused "Brukertype kan ikke være 0")
- Employee creation: lookup first department and include it (required when department module active)

**Result:** 6/6 locally (Portuguese prompt, Bruno Santos). Best score: **4.00** expected.

### ✅ Fix 10: Voucher Handler — Supplier Invoice VAT Lock Detection

**Problem:** Account 7100 is locked to VAT code 0. Old detection required `vatType.id == number` conditions to be met before setting `locked = true`. If id was 0 or missing, lock was never detected → 422 error.

**Fixes:**

- Treat ANY `vatType` object present on account as a lock
- If `vatType.number == 0` → locked to no-VAT → omit vatType field from posting entirely
- Use `fields=id,number,vatType(id,number)` (was `vatType(id)` — needed `number` to detect code 0)

**Result:** 5/5 locally (Norwegian account 7100). Supplier invoice VAT locking resolved. Best score: **3.25** expected.

### ✅ Fix 11: Project Handler — Fixed-Price PM GetScalarString

**Problem:** Same `GetStringField` JSON-as-query-param bug in ProjectHandler — PM `firstName`/`lastName`/`email` extracted as raw JSON object strings when LLM nests the `projectManager` entity.

**Fixes:**

- Added `GetScalarString` helper alongside `GetStringField`
- Changed `managerFirstName/LastName/Email` extraction to use `GetScalarString`
- Changed employee `fn/ln/em` extraction in `HandleTimesheetAndInvoice` to use `GetScalarString`

**Result:** 8/8 locally (fixed-price project with PM Hilde Johansen). Best score: **1.75** expected (up from 1.50).

---

## 3. Remaining Broken Tasks — Root Cause Analysis

### 3.1 Payroll (run_payroll) — 0.00 pts — CRITICAL

**Competition checks:** `salary_transaction_found`, `has_employee_link`, `payslip_generated`, `correct_amount`

**Observed runs:**

- Laura Schneider: 422 "not registered with employment" (3 calls, 1 error)
- Maria Almeida: 422 "dateOfBirth required" (7 calls, 1 error)
- Hannah Fischer: **201 success, 0/4 checks fail** (10 calls, 0 errors)
- Manon Durand: **201 success, 0/4 checks fail** (10 calls, 0 errors)

**Root cause:** Transaction created with 201 but **competition validator cannot find it**. The `GET /salary/payslip` search endpoint returns 0 results even when payslips exist. Nobody in the competition has solved this either (leader also has 0.00). Likely a Tripletex API/sandbox bug or the transaction needs to be "processed" via an action endpoint.

**Status:** UNRESOLVED — blocked by API behavior. Action endpoints (`/:close`, `/:approve`, `/:book`) all return 404.

### 3.2 Payment Reversal — 1.50 pts (best)

**Competition checks:** 3 checks. Check 3 always fails.

**Observed:** Handler creates a NEW invoice+payment chain instead of finding the existing one and reversing the payment. Competition expects the original invoice to show `amountOutstanding > 0` again.

**Root cause:** No logic to search for existing invoices or reverse existing payments. The "reverse" action path still creates a brand new invoice.

**Fix needed:** When `action=reverse`, search for existing invoice by customer + description, then find the payment and reverse it.

### 3.3 Simple Forward Payment — 0.29 pts (best)

**Competition checks:** 2 checks. Check 2 (`payment_registered`) fails.

**Observed:** Fjordkraft AS — `paidAmount=10100.00` but invoice `amountOutstanding` should be 12625.00 (10100 × 1.25 VAT). Handler pays the extracted amount instead of reading `amountOutstanding` from GET.

**Root cause:** For simple forward payments (no order lines in extraction), the handler may skip the GET invoice step or use the extracted amount directly.

### ~~3.4 Supplier Invoice Vouchers~~ — ✅ FIXED (Fix 10)

VAT lock detection now respects account-locked VAT codes. Account 7100 → `vatType.number==0` → omit vatType from posting. Tested 5/5 locally.

### ~~3.5 Travel Expense — Intermittent 0/6~~ — ✅ FIXED (Fix 9)

GetScalarString + 7-language regex fallback + create-if-not-found now handles all cases. Tested 6/6 locally.

### 3.6 Composite Tasks (Project + Hours + Invoice) — 0.00 pts

**Observed:** "Log 27 hours for Emily Smith on activity 'Rådgivning' in project 'System Upgrade'..." → ProjectHandler creates project only (3 calls), no timesheet entries, no invoice.

**Root cause:** ProjectHandler doesn't handle the composite flow. Needs: create project → create employee → create activity → log timesheet hours → create invoice based on hours.

### ~~3.7 Fixed-Price Project with Milestone~~ — ✅ FIXED (Fix 11)

GetScalarString fix prevents JSON-as-query-param for PM name. Now 4/4 locally. Still need competition run to confirm.

---

## 4. Improvement Opportunities — Ranked by Point Value

### Tier A: Fix Broken Tasks

| Priority | Task                             | Current → Target | Delta     | Effort | Status                |
| -------- | -------------------------------- | ---------------- | --------- | ------ | --------------------- |
| **A1**   | Payroll                          | 0.00 → 4.00      | **+4.00** | HIGH   | ❌ BLOCKED (API bug?) |
| **A2**   | Payment reversal                 | 1.50 → 3.20      | **+1.70** | MEDIUM | Ready to fix          |
| **A3**   | Simple forward payment           | 0.29 → 3.20      | **+2.91** | LOW    | Ready to fix          |
| **A4**   | Composite project+hours+invoice  | 0.00 → 3.20      | **+3.20** | HIGH   | Ready to fix          |
| **A5**   | Fixed-price project PM           | 1.50 → 1.75      | **+0.25** | LOW    | ✅ FIXED (Fix 11)     |
| **A6**   | Supplier invoice VAT locking     | 0.00 → 3.25      | **+3.25** | MEDIUM | ✅ FIXED (Fix 10)     |
| **A7**   | Travel expense employee fallback | 0.00 → 4.00      | **+4.00** | LOW    | ✅ FIXED (Fix 9)      |

### Tier B: Efficiency Optimization (minor gains)

| Priority | Task                 | Current → Target | Delta | Notes                          |
| -------- | -------------------- | ---------------- | ----- | ------------------------------ |
| B1       | Invoice API calls    | 3.11 → 3.50      | +0.39 | Skip redundant bank acct check |
| B2       | Travel expense cache | 4.00 → 4.00      | +0    | Already at max correctness     |

### Tier C: Tier 3 Tasks (Saturday — potential: +30-60 points)

Each task worth up to **6.0 points** (3× multiplier + efficiency doubling).

---

## 5. What We Learned (Key Insights)

### API Quirks Discovered

1. **Bank account required for invoices** — must auto-setup on account 1920. Only occurs in competition (sandbox pre-configured).
2. **`vatIncluded: false` ≠ 0% VAT** — means prices EXCLUDE VAT; VAT still applied at stated rate.
3. **Some accounts locked to specific VAT codes** — 7100 locked to code 0, cannot override.
4. **Salary transaction search is broken** — `GET /salary/payslip` with filters always returns 0 results.
5. **Division required for payroll** — must create division + link to employment before salary transaction.
6. **Employee `userType` cannot be "0"** — must use "STANDARD".
7. **Competition proxy blocks some endpoints** — `POST /incomingInvoice` returns 403.

### Extraction Pitfalls Discovered

1. **JSON-as-query-param** — nested objects serialized as URL params instead of extracting string values. Seen in ProjectHandler and TravelExpenseHandler.
2. **LLM hallucinate `vatIncluded: true`** — added safeguard to check raw prompt for VAT-inclusive keywords.
3. **Multiple entity key paths** — employee data may be at `entities["employee"]`, `entities["travelExpense"]["employee"]`, or `relationships["employee"]`.
4. **`orgNumber` vs `organizationNumber`** — LLM uses both; handlers must check both.

### Scoring Insights

1. **Payment has 3 sub-types** with different check counts: forward with products (5 checks, 8 pts), simple forward (2 checks, 7 pts), reversal (3 checks, 8 pts).
2. **Voucher has 2 sub-types**: custom dimension (6 checks, 13 pts max), supplier invoice (4 checks, 8 pts max).
3. **Project has 3 sub-types**: simple (4 checks, 7 pts), fixed-price+milestone (4 checks, 8 pts), composite+hours (4 checks, 8 pts).
4. **Credit note has 5 checks worth 8 pts** — more than originally thought.
5. **Travel expense: 6 checks, 8 pts** — per diem counts as a cost line.

---

## 6. Execution Plan — Remaining Work

### ~~Phase 1: Quick Wins~~ — ✅ DONE

1. ✅ **Travel expense employee fallback** (A7) — GetScalarString + regex + create-if-not-found (6/6)
2. ⚠️ **Simple forward payment amount** (A3) — investigated, locally OK; competition still 0.29 — may need deeper inspection of simple-path code branch
3. ✅ **Fixed-price project PM extraction** (A5) — GetScalarString fix (8/8)
4. ✅ **Supplier invoice VAT locking** (A6) — lock detection fixed (5/5)

**→ Submit competition run to validate Phase 1 gains (~+7.5 pts expected)**

### Phase 2: Current Focus

5. **Fix payment reversal** (A2) — search existing invoice by customer+description, then reverse payment (PUT /:reversePayment)
6. **Fix simple forward payment** (A3) — trace why simple path ignores `amountOutstanding`; ensure GET invoice step is always hit
7. **Fix composite project handler** (A4) — add timesheet + activity + invoice flow

### Phase 3: Tier 3 (Saturday)

7. Strengthen FallbackAgentHandler for complex tasks
8. Add file processing (CSV, PDF)
9. Submit continuously for Tier 3 tasks

---

## 7. Point Projection (Updated)

### Conservative Estimate

| Category          | Current   | Target    | Delta      |
| ----------------- | --------- | --------- | ---------- |
| Tier 1 tasks (8)  | ~10.0     | 14.00     | +4.00      |
| Tier 2 tasks (10) | ~25.0     | 36.00     | +11.00     |
| Tier 3 tasks (12) | 0.00      | 24.00     | +24.00     |
| **Total**         | **~35.0** | **74.00** | **+39.00** |

### Optimistic Estimate

| Category          | Current   | Target    | Delta      |
| ----------------- | --------- | --------- | ---------- |
| Tier 1 tasks (8)  | ~10.0     | 16.00     | +6.00      |
| Tier 2 tasks (10) | ~25.0     | 40.00     | +15.00     |
| Tier 3 tasks (12) | 0.00      | 42.00     | +42.00     |
| **Total**         | **~35.0** | **98.00** | **+63.00** |

---

## 8. Known Bugs Still Open

1. **Payment reversal creates new invoice** — needs search-existing-invoice logic
2. **Simple forward payment pays extracted amount** — locally appears OK but competition still 0.29; suspect a separate simple-forward code path that skips the GET invoice step
3. ~~Supplier invoice on locked-VAT accounts~~ — ✅ FIXED (Fix 10)
4. ~~Travel expense employee JSON-as-query-param~~ — ✅ FIXED (Fix 9)
5. **Composite tasks (project+hours+invoice)** — ProjectHandler doesn't handle full flow
6. **Payroll 0/4 despite 201** — BLOCKED, possibly Tripletex API bug (nobody has solved it)
7. ~~Fixed-price project PM not found~~ — ✅ FIXED (Fix 11)

8. **Travel expense only creates 1 cost line** — French variant "Conférence Ålesund" only posted 1 cost line despite 3 items. The per diem cost line was missing.

9. **Customer searched by name instead of orgNumber** — `GET /customer?name=Estrela Lda` instead of `?organizationNumber=975389642`. Name search may fail with special characters.

10. **Payroll "success" but 0/4 checks** — Transaction created but in wrong structure. Need to verify payload format matches what competition expects.

11. **Token expiry mid-submission** — Some tasks get 403 "Invalid or expired token" partway through. This is likely the competition proxy timing out. Not much we can do except be faster.

12. **Product number collision in competition** — When creating invoices with product numbers, products from earlier tasks in the same submission may already exist. Handler hits 422 errors trying to create them again. Need to check-before-create or gracefully handle 409/422.

---

## 8. Efficiency Quick Wins

| Optimization                        | Calls Saved           | Tasks Affected                        |
| ----------------------------------- | --------------------- | ------------------------------------- |
| Cache vatTypes per request          | 1-2 per task          | Invoice, Payment, CreditNote, Product |
| Cache paymentType per request       | 1-2 per task          | Payment, TravelExpense                |
| Cache bank account check            | 1-2 per task          | Invoice, Payment, CreditNote          |
| Skip product creation if exists     | 1-3 per task          | Invoice with product numbers          |
| Parallelize customer+vatType lookup | 0 calls saved, faster | Invoice, Payment                      |
| Remove duplicate employee searches  | 1-2 per task          | TravelExpense, Project                |

Total potential: **3-8 fewer API calls per task** across Invoice/Payment tasks.
At scale: better efficiency bonus on every perfect run.

---

## 9. Risk Assessment

| Risk                           | Impact | Mitigation                                                               |
| ------------------------------ | ------ | ------------------------------------------------------------------------ |
| Tier 3 requires unknown APIs   | High   | Strengthen FallbackAgentHandler; study OpenAPI spec for unused endpoints |
| Token expiry on long tasks     | Medium | Optimize speed; fail fast on 403                                         |
| Competition adjusts benchmarks | Low    | Keep submitting to maintain relative position                            |
| Other teams catch up on Tier 2 | Medium | Fix our broken tasks ASAP                                                |
| Rate limits (10/task/day)      | Medium | Don't waste submissions; test locally first                              |
| New task types we can't handle | High   | Make fallback agent robust enough for partial credit                     |

---

## 10. Decision: What to Do First

**Recommended order:**

1. **A1: Fix Payroll** — Biggest single improvement (+4.0), currently zero
2. **A4: Fix Project PM** — Quick fix (+1.14), low effort
3. **A2+A3: Fix Payment** — +5.62 combined, medium effort
4. **B1-B4: Efficiency** — +1.46 combined from near-perfect tasks
5. **C: Tier 3 prep** — Start Saturday morning when tasks open

**Time budget:**

- Friday evening: Phase 1 (fix breaks) — 3-4 hours
- Friday night: Phase 2 (efficiency) — 2 hours
- Saturday AM: Phase 3 (Tier 3 handlers) — 4-6 hours
- Saturday PM → Sunday: Phase 4 (grind + fix) — continuous

**Submission budget:**

- ~10 submissions remaining today (32/day limit minus ~22 used)
- 32 submissions Saturday
- 32 submissions Sunday (until 15:00)
- Total: ~74 submissions remaining → ~2.5 per task type average
