# Plan-02: Aggressive Fix for Zero-Score Tasks (06, 11, 12)

## TL;DR

Fix the three tasks where Novanet scored zero to recover **+6-8 points** (29.67 → 35-37+).
Task 11 (payroll) is the hardest but highest-value fix. Task 12 (payment reversal) needs a new handler path. Task 06 (enable_module) just needs testing — we never tried it properly.

---

## ✅ COMPLETED: create_employee (not originally in plan, discovered via cross-reference analysis)

**Problem:** 2 competition entries for create_employee had 0 scored results. Appeared misrouted to TravelExpenseHandler.

**Root causes found:**

1. **Handler logging race condition** — `TaskRouter.LastHandlerName` was shared singleton state, overwritten by concurrent requests. Both employees actually routed correctly to EmployeeHandler; only the logging was wrong.
2. **Narrow task_type overrides** — only caught `unknown`/`create_contact`, not other misclassifications.
3. **Missing Nynorsk keywords** — "fødd" (born) and "heiter" (named) absent from regex fallback.
4. **Employment division requirement** — sandbox requires `division.id`; competition environments don't.

**Fixes applied (commit a366009):**

- `TaskRouter.cs`: `RouteAsync` returns handler name in tuple (thread-safe). Added employee entity inference in `InferTaskType`.
- `Program.cs`: Broadened employee override with strong multi-language regex. Fixed handler name logging.
- `LlmExtractor.cs`: Added "fødd" and "heiter"/"heter" to regex fallback.
- `EmployeeHandler.cs`: Employment POST retries with division lookup on failure.

**Test results:** 6/6 (no admin) and 11/11 (with admin) — all languages pass.

---

## Task Analysis

### Task 06: enable_module (Tier 1, Max 2.0 pts, Current: 0, 1 try)

- **Handler:** EnableModuleHandler.cs — exists, routed, but NEVER appeared in submissions.jsonl
- **Root cause:** Only 1 competition try, scored 0 → either extraction failed or module API call failed
- **Risk:** Low — handler looks correct, just undertested

### Task 11: run_payroll (Tier 2, Max 4.0 pts, Current: 0, 2 tries)

- **Handler:** PayrollHandler.cs — handler reports success but ALL 4 checks fail
- **Competition checks:** salary_transaction_found, has_employee_link, payslip_generated, correct_amount
- **Observed failures:**
  - Run 1 (Laura Schneider, de): 422 "Ansatt nr. er ikke registrert med arbeidsforhold" (3 calls, 1 error)
  - Run 2 (Maria Almeida, pt): 422 "employee.dateOfBirth: Feltet må fylles ut" (7 calls, 1 error)
  - Run 3 (Hannah Fischer, de): success=true but 4/4 checks FAILED (10 calls, 0 errors)
  - Run 4 (Manon Durand, fr): success=true but 4/4 checks FAILED (10 calls, 0 errors)
- **Root cause:** Transaction created but structure fundamentally wrong OR not persisted correctly

### Task 12: register_payment (Tier 2, Max 4.0 pts, Current: 0, 4 tries)

- **Handler:** PaymentHandler.cs — works for CREATE but fails for REVERSE
- **Competition results pattern:**
  - Forward payments (Estrela, Montanha): success, some pass (scores 0.29–3.2)
  - Payment reversal (Bergwerk, Strandvik): success=true but creates new invoice instead of reversing
  - Token expired (Clearwater): 403
- **Root cause:** PaymentHandler always creates NEW invoice chain — doesn't handle "reverse existing payment"

## Implementation Plan

### Phase 1: Task 06 — enable_module (LOW effort, +2.0 pts potential)

**Step 1.1** Test locally with sandbox

- Run: `.\scripts\Test-Solve.ps1 "Aktiver prosjektmodulen for selskapet"`
- Run: `.\scripts\Test-Solve.ps1 "Enable the wage module for the company"`
- Run: `.\scripts\Test-Solve.ps1 "Aktivieren Sie das Zeiterfassungsmodul"`
- Verify handler is invoked (not fallback) and POST /company/salesmodules returns 201

**Step 1.2** Debug extraction

- Check if LLM extracts task_type=enable_module correctly
- Check if module name resolves to valid enum (PROJECT, SMART_WAGE, etc.)
- If extraction fails → add "enable_module" examples to LLM system prompt

**Step 1.3** Verify API endpoint

- Confirm POST /company/salesmodules is the right endpoint
- Check if it needs GET /company/salesmodules first to see what's available
- Check if already-enabled modules return error (409?) — handle gracefully

**Files to modify:**

- src/Handlers/EnableModuleHandler.cs — potentially fix module name mapping
- src/Services/LlmExtractor.cs — ensure enable_module extraction works
- src/Services/SandboxValidator.cs — add enable_module validation if missing

### Phase 2: Task 12 — register_payment REVERSE variant (MEDIUM effort, +2-4 pts potential)

**Step 2.1** Understand the reverse payment task

- Prompt pattern: "Zahlung wurde zurückgebucht / Reverser betalinga / pagamento foi devolvido"
- Action extracted: "reverse" (LLM correctly identifies this)
- Competition expects: find existing invoice, remove payment, so amountOutstanding > 0 again

**Step 2.2** Implement reverse payment path in PaymentHandler

- When action="reverse":
  1. Search for existing customer by name/orgNumber
  2. GET /invoice?customerId={id} to find the invoice matching description
  3. Find existing payment on invoice (check amountOutstanding vs amount)
  4. Use the payment reversal API: likely `PUT /invoice/{id}/:payment` with paidAmount=0 or negative
  5. OR: may need to find the payment voucher and reverse it

**Step 2.3** Also fix forward payment amount bugs

- Check 2 fails ("payment_registered"): amountOutstanding != 0
- Root cause: paying ex-VAT amount instead of incl-VAT invoice total
- Fix: ALREADY reads from GET /invoice amount, but verify this is the incl-VAT amount
- Verify: `amount` field on invoice is incl. VAT (from tripletex-docs: yes, amount is always incl. VAT)

**Step 2.4** Handle token expiry gracefully

- Clearwater run: 403 "Invalid or expired token" after 4 calls
- Nothing we can do about expired tokens — but ensure we don't waste calls before first API check

**Files to modify:**

- src/Handlers/PaymentHandler.cs — add reverse payment logic (branch on extracted action)
- src/Models/ExtractionResult.cs — verify Action field is populated
- src/Services/LlmExtractor.cs — verify "reverse" action extraction

### Phase 3: Task 11 — run_payroll (HIGH effort, +4.0 pts potential)

**Step 3.1** Diagnose why transactions pass but validation fails

- The handler successfully creates a salary transaction (201 response)
- But competition says salary_transaction_found=FAIL
- Hypothesis A: Transaction is created but NOT in correct state (draft vs. posted?)
- Hypothesis B: Payslip specifications format is wrong → no actual payslip generated
- Hypothesis C: Competition searches by different criteria (employee email? date range?)

**Step 3.2** Add verification GET after POST

- After POST /salary/transaction, immediately GET /salary/transaction/{id}
- Log the FULL response to see what Tripletex actually stored
- Check if payslips array is populated with employee and specifications

**Step 3.3** Test payslip structure variations

- Current: `{ salaryType: {id}, rate: amount, count: 1 }`
- Try: `{ salaryType: {id}, amount: totalAmount }` (no rate/count split)
- Try: `{ salaryType: {id}, rate: amount, count: 1, amount: amount }` (explicit amount)
- Reference: Check entity-model.md for SalarySpecification schema

**Step 3.4** Fix employee search in clean environment

- Current: searches by email with fields=id,dateOfBirth,version
- Problem: in clean environment, employee doesn't exist → must create
- Current handler DOES handle creation (line 66-87 of PayrollHandler.cs)
- BUT: when employee IS found but has no DOB → PUT dateOfBirth patch
- BUT: the `fields` param was only "id" initially (line 60) — fixed to include dateOfBirth,version
  - verify this fix is actually deployed

**Step 3.5** Verify employment/division setup

- Division: handler creates if none exists ✅
- Employment: handler creates with taxDeductionCode + employmentDetails ✅
- BUT: does employment need to be ACTIVE/APPROVED before payroll?
- BUT: does division need organizationNumber matching company's org number?

**Step 3.6** Check if transaction needs to be "processed" (closed/posted)

- Maybe POST creates a DRAFT transaction
- Competition may look for a POSTED/CLOSED transaction
- Check API for: POST /salary/transaction/{id}/:process or similar action endpoint
- Check: PUT /salary/transaction/{id} with status change?

**Files to modify:**

- src/Handlers/PayrollHandler.cs — fix transaction structure, add verification, potentially process/post
- src/Services/SandboxValidator.cs — update payroll validation to match competition
- knowledge.md — document findings

## Verification Plan

1. After each fix, test locally: `.\scripts\Test-Solve.ps1 "<prompt>"`
2. Check logs/validations.jsonl for local validation scores
3. When confident, submit: `.\scripts\Submit-Run.ps1`
4. Compare competition scores vs local predictions
5. Update SandboxValidator.cs if scores diverge

## Priority Order

1. Task 06 (enable_module) — quickest win, just needs testing
2. Task 12 (register_payment reverse) — medium effort, clear fix path
3. Task 11 (run_payroll) — hardest, needs investigation, but worth 4 pts

## Expected Impact

- Task 06: 0 → 1.5-2.0 = **+1.5-2.0 pts**
- Task 12: 0 → 2.0-3.2 = **+2.0-3.2 pts** (forward already works sometimes)
- Task 11: 0 → 2.0-4.0 = **+2.0-4.0 pts** (if we crack the structure)
- **Total: +5.5-9.2 pts** (29.67 → 35-39 range)
