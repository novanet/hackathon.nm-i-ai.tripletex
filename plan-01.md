# Competition Plan — NM i AI 2026 Tripletex Agent

**Date:** 2026-03-20
**Current rank:** #1 (websecured.io)
**Current score:** 43.78 / theoretical max ~120+
**Tasks solved:** 18/30
**Tries used:** 121
**Competition ends:** March 22, 15:00 CET (~44 hours remaining)

---

## 1. Current Score Breakdown

**Leader:** websecured.io — Rank #1, Score **43.78**
**Novanet:** Rank #32, Score **29.67**, 18/30 tasks, 57 tries

| Task  | Leader   | Novanet | Max      | Tries (L / N) | Gap       | Analysis                                    |
| ----- | -------- | ------- | -------- | ------------- | --------- | ------------------------------------------- |
| 01    | 2.00     | 1.67    | 2.0      | 9 / 3         | -0.33     | ⚠️ Leader perfect, we're close              |
| 02    | 2.00     | 2.00    | 2.0      | 10 / 2        | 0         | ✅ Both perfect — we did it in fewer tries! |
| 03    | 2.00     | 1.50    | 2.0      | 10 / 2        | -0.50     | ⚠️ We're losing points here                 |
| 04    | 0.86     | 0.86    | 2.0      | 9 / 3         | 0         | 🔴 Both broken — PM resolution fails        |
| 05    | 2.00     | 1.25    | 2.0      | 9 / 4         | -0.75     | ⚠️ Significant gap                          |
| 06    | 1.83     | —       | 2.0      | 10 / 1        | -1.83     | 🔴 We scored 0 — only 1 try                 |
| 07    | 2.00     | 0.29    | 2.0      | 6 / 3         | -1.71     | 🔴 Major gap — something very broken        |
| 08    | 2.00     | 1.75    | 2.0      | 11 / 4        | -0.25     | ⚠️ Close but not perfect                    |
| 09    | 3.67     | 3.11    | 4.0      | 4 / 5         | -0.56     | ⚠️ Both imperfect (Tier 2)                  |
| 10    | 3.71     | 3.33    | 4.0      | 5 / 2         | -0.38     | ⚠️ Both imperfect (Tier 2)                  |
| 11    | **0.00** | —       | 4.0      | 5 / 2         | 0         | 🔴 Both zero — Payroll broken               |
| 12    | 1.00     | —       | 4.0      | 5 / 4         | -1.00     | 🔴 We scored 0, leader has 1.00             |
| 13    | 1.38     | 4.00    | 4.0      | 5 / 5         | **+2.62** | 🟢 **We beat the leader!**                  |
| 14    | 4.00     | 2.67    | 4.0      | 2 / 2         | -1.33     | ⚠️ Leader perfect, we're partial            |
| 15    | 4.00     | 1.50    | 4.0      | 6 / 3         | -2.50     | 🔴 Big gap                                  |
| 16    | 4.00     | 1.00    | 4.0      | 6 / 1         | -3.00     | 🔴 Biggest gap — only 1 try                 |
| 17    | 4.00     | 3.25    | 4.0      | 4 / 3         | -0.75     | ⚠️ Close                                    |
| 18    | 3.33     | 1.50    | 4.0      | 5 / 2         | -1.83     | 🔴 Significant gap                          |
| 19–30 | —        | —       | 6.0 each | —             | —         | 🟡 **Tier 3 — not yet open**                |

**Summary:**

- **Total gap: -14.11** (43.78 vs 29.67)
- We beat the leader on Task 13 (+2.62)
- Biggest losses: Task 16 (-3.00), Task 15 (-2.50), Task 06 (-1.83), Task 18 (-1.83), Task 07 (-1.71)
- Tasks where we haven't scored: 06, 11, 12 (= potential +8.0 if fixed)
- Many tasks with only 1-2 tries — more attempts could improve scores significantly

---

## 2. Task-to-Handler Mapping (Best Guess)

We don't know the exact task number ↔ task type mapping, but from submission logs and score patterns:

| Likely Task Type            | Task # (guess) | Score     | Evidence                  |
| --------------------------- | -------------- | --------- | ------------------------- |
| create_employee             | 01-03 range    | 2.00      | Tier 1, works well        |
| create_customer             | 01-03 range    | 2.00      | Tier 1, 1 API call        |
| create_supplier             | 05/07/08       | 2.00      | Tier 1, 1 API call        |
| create_product              | 05/07/08       | 2.00      | Tier 1, 2 API calls       |
| create_department           | 05/07/08       | 2.00      | Tier 1, works well        |
| create_project              | **04**         | **0.86**  | PM fails on some variants |
| create_invoice              | 09/10          | 3.67-3.71 | Tier 2, near-perfect      |
| register_payment            | 12 or 13       | 1.00-1.38 | Tier 2, amount bugs       |
| run_payroll                 | **11**         | **0.00**  | Tier 2, completely broken |
| create_travel_expense       | 14-17 range    | 4.00      | Tier 2, works perfectly   |
| create_voucher              | 14-17 range    | 4.00      | Tier 2, works perfectly   |
| create_credit_note          | 14-17 range    | 4.00      | Tier 2, works perfectly   |
| delete_entity               | 14-17 range    | 4.00      | Tier 2, works well        |
| composite (project+invoice) | 12 or 13       | 1.00-1.38 | Tier 2, partially broken  |
| enable_module               | 06?            | 1.83      | Near-perfect              |

---

## 3. Root Cause Analysis — Broken Tasks

### 3.1 Task 11: Payroll (0.00 points) — CRITICAL

**Competition checks:** `salary_transaction_found`, `has_employee_link`, `payslip_generated`, `correct_amount`

**What happens:** Handler reports success but all 4 checks fail. Two failure modes observed:

1. **"employee.dateOfBirth: Feltet må fylles ut"** — Employment creation fails because employee has no DOB. Handler now patches DOB if missing, but this failed for Maria Almeida (call 7 error).

2. **All checks fail despite handler "success"** — Manon Durand run: 10 API calls, 0 errors, handler reports success. But competition says 4/4 checks failed. This means the salary transaction was created but **in a structure the validator doesn't recognize**.

**Likely root causes:**

- The payslip `specifications` array may need a different structure
- The `employee` on payslip may need to be linked differently
- The `correct_amount` check may expect `baseSalary + bonus` as total, but we might be posting them as separate line items with wrong salary type IDs
- Division/employment setup may be incomplete in a clean environment

**Fix strategy:** Investigate the exact salary transaction structure Tripletex expects. May need to look at what `GET /salary/transaction/{id}` returns after creation and compare to validator expectations.

### 3.2 Task 04: Project (0.86 points)

**Competition checks:** `project_found`, `name`, `has_customer`, `has_project_manager`

**What fails:** Check 2 (has_project_manager) fails intermittently.

**Root causes from logs:**

1. **JSON-as-query-param bug** — When LLM puts projectManager as nested object, the handler sometimes serializes the entire JSON object into query params: `firstName={full JSON}`. This returns 0 results, so PM is skipped.
2. **Employee not found by name** — In a clean environment, the PM employee doesn't exist yet. Handler falls back to first available employee, but sometimes no employees exist.
3. **"fixed price + milestone invoice" variant** — PM (bjrn.aasen) wasn't searched by email, only by name. Handler doesn't always extract PM from the `Entities["project"]["projectManager"]` correctly.

**Fix strategy:**

- Always extract firstName/lastName as strings, not JSON objects
- If PM not found, create the employee
- Grant PM entitlements before assigning to project

### 3.3 Tasks 12-13: Payment/Composite (1.00-1.38 points)

**Multiple issues:**

1. **Wrong payment amount** — "10100 kr eksklusiv MVA" → handler pays 10100.00 instead of 12625.00 (with 25% VAT). The `vatIncluded: false` flag means the invoice amount includes VAT on top, so payment must be the invoice total, not the ex-VAT amount.
2. **Composite task routing** — "Log hours + create project + invoice" gets routed to InvoiceHandler which creates an invoice but misses project/timesheet creation (worth 50% of points).
3. **Payment reversal** — "Die Zahlung wurde zurückgebucht. Stornieren Sie die Zahlung" — handler creates a NEW invoice+payment instead of finding the existing one and reversing.

### 3.4 Tasks 09-10: Invoice (3.67-3.71)

**Near-perfect but efficiency could improve:**

- Currently 6-9 API calls per invoice
- Product number collision errors (422s) waste efficiency when products already exist in the same submission
- Bank account check + PUT adds 2 calls every time

### 3.5 Task 06/18: Near-perfect (1.83 / 3.33)

**Likely issues:**

- Minor extraction failures in specific languages
- One failing check out of several

---

## 4. Improvement Opportunities — Ranked by Point Value

### Tier A: Fix Broken Tasks (potential: +8-10 points)

| Priority | Task                        | Current → Target | Delta     | Effort                                       |
| -------- | --------------------------- | ---------------- | --------- | -------------------------------------------- |
| **A1**   | Payroll (Task 11)           | 0.00 → 4.00      | **+4.00** | High — need to debug transaction structure   |
| **A2**   | Payment/Composite (Task 12) | 1.00 → 4.00      | **+3.00** | Medium — fix amount calc + composite routing |
| **A3**   | Payment/Composite (Task 13) | 1.38 → 4.00      | **+2.62** | Medium — same fixes as A2                    |
| **A4**   | Project (Task 04)           | 0.86 → 2.00      | **+1.14** | Low — fix PM resolution                      |

### Tier B: Optimize Near-Perfect Tasks (potential: +2-3 points)

| Priority | Task              | Current → Target | Delta | Effort                 |
| -------- | ----------------- | ---------------- | ----- | ---------------------- |
| B1       | Invoice (Task 09) | 3.67 → 4.00      | +0.33 | Low — reduce API calls |
| B2       | Invoice (Task 10) | 3.71 → 4.00      | +0.29 | Low — reduce API calls |
| B3       | Task 18           | 3.33 → 4.00      | +0.67 | Medium                 |
| B4       | Task 06           | 1.83 → 2.00      | +0.17 | Low                    |

### Tier C: Tier 3 Tasks (potential: +30-60 points) — THE KINGMAKER

Tier 3 opens Saturday morning. Each task is worth up to **6.0 points** (3× multiplier + efficiency doubling).

**Expected Tier 3 task types** (based on "complex multi-step workflows"):

- Bank reconciliation from CSV file
- Ledger corrections / error fixing
- Year-end closing procedures
- Complex supplier invoice booking
- Budget entries for departments/projects
- Multi-entity workflows (create customer + products + invoice + payment in one prompt)
- Import from file (PDF invoice processing)
- Balance sheet verification
- Salary slip corrections
- Tax reporting?

**Key insight:** Tier 3 is 12 tasks × up to 6.0 = **72 theoretical points**. Even getting 50% on these (average 3.0/task) = **36 points**, nearly doubling our current score.

---

## 5. Execution Plan — Priority Order

### Phase 1: Fix Critical Breaks (Friday evening — ~3-4 hours)

**1.1 Fix PayrollHandler** (+4.0 potential)

- Debug: what exact structure does `POST /salary/transaction` need?
- Verify: does the payslip specification format match Tripletex expectations?
- Test: run against sandbox, verify all 4 competition checks pass
- Key: ensure employment + division are properly set up in clean env

**1.2 Fix ProjectHandler PM Resolution** (+1.14 potential)

- Fix: extract PM firstName/lastName as plain strings, not nested objects
- Fix: if PM employee not found, create them with proper entitlements
- Fix: always try email-based search as fallback
- Test: verify has_project_manager check passes

**1.3 Fix Payment Amount Calculation** (+5.62 potential for Tasks 12+13)

- Fix: when `vatIncluded: false`, GET the invoice after creation to read actual `amount` (includes VAT), then pay that
- Fix: payment reversal should find existing invoice, not create new one
- Fix: composite tasks (project+time+invoice) need proper routing

### Phase 2: Efficiency Optimization (Friday night — ~2 hours)

**2.1 Per-request reference data caching**

- vatTypes, paymentTypes, bank account status — fetch once, reuse across all handlers in same request
- Saves 2-4 API calls per task = significant efficiency gain

**2.2 Reduce invoice chain calls**

- Skip bank account check if already done
- Don't look up products by number if we just created them
- Parallelize independent lookups (customer + vatTypes simultaneously)

**2.3 Travel expense: cache paymentType**

- Currently fetching `/travelExpense/paymentType` once per cost line
- Fetch once, reuse for all cost lines
- Saves 2 calls on 3-item travel expenses

### Phase 3: Tier 3 Preparation (Saturday morning — ~4-6 hours)

**3.1 File Processing Infrastructure**

- PDF text extraction (already using PdfPig)
- CSV parsing for bank reconciliation
- Image OCR via GPT-4o vision (already supports this)

**3.2 Bank Reconciliation Handler**

- Parse CSV bank statement from file attachment
- Match transactions to existing invoices/payments
- Create reconciliation entries
- This is likely the highest-value Tier 3 task

**3.3 Ledger Correction Handler**

- Find incorrect voucher postings
- Create reversing entries
- Post corrected entries

**3.4 Strengthen FallbackAgentHandler**

- Add more Tripletex knowledge context
- Better tool descriptions for the LLM
- Increase iteration limit for complex tasks
- Add common Tier 3 patterns to the system prompt

**3.5 Period/Year-End Handler**

- Close accounting period
- Generate required reports

### Phase 4: Grind Submissions (Saturday afternoon → Sunday)

- Submit continuously to get Tier 3 tasks
- Fix failures as they appear
- Each fix is worth 3-6 points at Tier 3
- Target: 10+ Tier 3 tasks at average 3.0 = +30 points

---

## 6. Point Projection

### Conservative Estimate

| Category          | Current   | Target    | Delta      |
| ----------------- | --------- | --------- | ---------- |
| Tier 1 tasks (8)  | 14.69     | 16.00     | +1.31      |
| Tier 2 tasks (10) | 29.09     | 38.00     | +8.91      |
| Tier 3 tasks (12) | 0.00      | 24.00     | +24.00     |
| **Total**         | **43.78** | **78.00** | **+34.22** |

### Optimistic Estimate

| Category          | Current   | Target    | Delta      |
| ----------------- | --------- | --------- | ---------- |
| Tier 1 tasks (8)  | 14.69     | 16.00     | +1.31      |
| Tier 2 tasks (10) | 29.09     | 40.00     | +10.91     |
| Tier 3 tasks (12) | 0.00      | 42.00     | +42.00     |
| **Total**         | **43.78** | **98.00** | **+54.22** |

---

## 7. Known Bugs to Fix (from submission logs)

1. **JSON-as-query-param bug** — Employee search serializes full JSON object as firstName/lastName query params. Seen in ProjectHandler and TravelExpenseHandler. Root cause: LLM returns nested object but handler passes it directly to query string without extracting the string value.

2. **Payment pays wrong amount** — When prompt says "eksklusiv MVA", handler sometimes pays the ex-VAT amount instead of invoice total (which includes VAT). Must always GET `/invoice/{id}` to read the actual amount after invoice creation.

3. **Travel expense only creates 1 cost line** — French variant "Conférence Ålesund" only posted 1 cost line despite 3 items. The per diem cost line was missing.

4. **Customer searched by name instead of orgNumber** — `GET /customer?name=Estrela Lda` instead of `?organizationNumber=975389642`. Name search may fail with special characters.

5. **Payroll "success" but 0/4 checks** — Transaction created but in wrong structure. Need to verify payload format matches what competition expects.

6. **Token expiry mid-submission** — Some tasks get 403 "Invalid or expired token" partway through. This is likely the competition proxy timing out. Not much we can do except be faster.

7. **Product number collision in competition** — When creating invoices with product numbers, products from earlier tasks in the same submission may already exist. Handler hits 422 errors trying to create them again. Need to check-before-create or gracefully handle 409/422.

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
