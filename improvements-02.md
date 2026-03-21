# Improvements Phase 2 — Gap Analysis & Strategy (2026-03-21)

## Current Standing

**Novanet: 40.17 pts** vs **Leader (Ave Christus Rex): 48.98 pts** — Gap: ~8.8 pts

## Critical Discovery: Dynamic Efficiency Benchmarks

Efficiency bonuses are **relative to other teams' best solutions** and recalculated every 12 hours. Our leaderboard data shows scores DROPPING between submissions without new attempts (Task 09: 4.0→3.33→4.0, Task 17: 4.0→3.5→4.0). This means efficiency gains degrade over time as competitors improve.

**Implication:** Correctness fixes have DURABLE value; efficiency fixes are TRANSIENT. Fix correctness first.

---

## Gap Decomposition

| Task | Us   | Leader | Gap       | Tier | Inferred Type                             | Root Cause                                                                                                   | Fix Complexity |
| ---- | ---- | ------ | --------- | ---- | ----------------------------------------- | ------------------------------------------------------------------------------------------------------------ | -------------- |
| 12   | 1.00 | 4.00   | **-3.00** | T2   | `run_payroll`                             | Checks 2&3 fail (has_employee_link, payslip_generated). System.Text.Json serialization bug may still linger. | Medium         |
| 15   | 1.50 | 3.33   | **-1.83** | T2   | `create_project` (fixed-price variant)    | Check 2 consistently fails. Fixed-price flag + milestone invoicing not implemented.                          | Medium         |
| 11   | 0.00 | 1.00   | **-1.00** | T2   | Unknown — _needs investigation_           | 0/9 attempts score any points. Leader also only gets 1.0.                                                    | High (unknown) |
| 10   | 3.00 | 4.00   | **-1.00** | T2   | Efficiency gap — correctness OK           | Too many API calls vs benchmark                                                                              | Low            |
| 14   | 3.00 | 4.00   | **-1.00** | T2   | Efficiency gap — correctness OK           | Too many API calls vs benchmark                                                                              | Low            |
| 05   | 1.33 | 2.00   | **-0.67** | T1   | Efficiency gap — likely `create_employee` | Extra call vs leader's 2 calls                                                                               | Low            |
| 08   | 1.50 | 2.00   | **-0.50** | T1   | Efficiency gap on Tier 1 task             | Too many API calls                                                                                           | Low            |
| 01   | 1.50 | 2.00   | **-0.50** | T1   | Efficiency gap on Tier 1 task             | Too many API calls                                                                                           | Low            |
| 16   | 3.00 | 3.50   | **-0.50** | T2   | Efficiency gap                            | Minor call overhead                                                                                          | Low            |

**Tasks where we LEAD:**

- Task 06: Us 1.67 > Leader 1.25 (+0.42)
- Task 13: Us 2.67 > Leader 2.40 (+0.27)
- Task 17: Us 4.00 > Leader 3.50 (+0.50) — fluctuates with benchmarks

**Perfect scores (no action needed):** Tasks 02, 03, 04, 07, 09, 17, 18

**Breakdown:** ~5.8 pts from correctness failures (Tasks 11, 12, 15) + ~3.0 pts from efficiency shortfalls (Tasks 01, 05, 08, 10, 14, 16)

---

## Phase 1: Fix Complete Failures (+4–5 pts, HIGH priority)

### 1A: Fix `run_payroll` (Task 12) — Expected gain: +2.0 to +3.0

_Depends on: nothing_

1. Read `PayrollHandler.cs` end-to-end, verify all payloads use `Dictionary<string,object>` (not anonymous types in `object[]` — the System.Text.Json bug from knowledge.md)
2. Verify salary transaction body includes proper `employee: {id}` reference on payslip stubs
3. After `POST /salary/transaction`, fetch individual payslips via `GET /salary/payslip/{id}` to verify they're accessible (competition validator likely does this)
4. Ensure voucher posting on account 5000 includes `employee: {id}` ref (Check 2 = has_employee_link)
5. Verify salary type mapping: #2000 = "Fastlønn" for base salary, #1350 for bonus
6. Test locally with `Test-Solve.ps1` using a German/Portuguese payroll prompt

### 1B: Identify and Fix Task 11 — Expected gain: +1.0 to +2.0

_Depends on: nothing (parallel with 1A)_

1. Search `submissions.jsonl` for the specific prompts that become Task 11 (correlate timing with leaderboard attempt timestamps)
2. Check which task types appear in results that get 0/N on ALL attempts — cross-reference with task count
3. Likely candidates: composite timesheet+invoice task, supplier invoice variant, or a task the LLM misclassifies
4. Once identified, implement or fix the handler
5. If it's a task the FallbackAgentHandler fumbles, add dedicated handler logic

### 1C: Fix `create_project` Check 2 (Task 15) — Expected gain: +1.5 to +2.0

_Depends on: nothing (parallel with 1A, 1B)_

1. Read `ProjectHandler.cs` — focus on fixed-price/milestone variant handling
2. Look at submissions for "Sett fastpris" prompts — these trigger the variant
3. Check 2 likely validates: `isFixedPrice=true` + `fixedPrice` field set on project
4. May also need milestone/partial invoicing logic ("Fakturer kunden for 25% av fastprisen som ei delbetaling")
5. Ensure project is created with `isFixedPrice=true`, `fixedPrice=amount`, then create order for partial amount

---

## Phase 2: Efficiency Optimization (+2–3 pts, MEDIUM priority)

### 2A: Reduce API calls for Task 05 (`create_employee`)

- Currently 3 calls: GET department + POST employee + POST employment
- Leader achieves 2.0 with likely 2 calls
- Can we skip GET department? Only needed when department module is active
- Alternative: combine department check into employee POST body

### 2B: Reduce API calls for Tasks 01, 08 (T1 tasks)

- Need to identify exact task_type mapping first
- Possible: `create_supplier` (1 call already optimal), `create_product` (2 calls)
- If `create_product`: cache vatType lookups, or skip vatType entirely if not required

### 2C: Reduce API calls for Tasks 10, 14 (T2 tasks)

- These are Tier 2 tasks with 100% correctness but 3.0/4.0 efficiency
- Key optimizations:
  - Cache `GET /ledger/vatType` across tasks in same submission
  - Cache `GET /ledger/account?number=1920` (bank account check)
  - Skip `GET /product?number=X` for products that don't exist yet (just inline in order line)
  - Skip sending invoice if not asked to send

### 2D: Reduce API calls for Task 16

- Minor improvement (3.0→3.5)
- Likely just needs 1 fewer API call

---

## Phase 3: Tier 3 Preparation (FUTURE priority)

1. Tier 3 unlocks Saturday morning with ×3 multiplier (max 6.0 per task)
2. 12 new tasks will appear (Tasks 19–30)
3. Expected types: bank reconciliation, complex ledger corrections, multi-step workflows, supplier invoices
4. Ensure handlers exist for: `BankReconciliationHandler`, `DeleteEntityHandler`, `EnableModuleHandler`, `TimesheetHandler`
5. FallbackAgentHandler should be robust enough to handle novel tasks

---

## Execution Strategy

### Submission Budget

- 32 submissions/day, ~10 left today
- **Reserve 2 for quick wins** (efficiency fixes after verifying locally)
- **Reserve 3 for Phase 1 verification** (one per correctness fix)
- **Save remainder for Tier 3** on Saturday

### Workflow

1. Fix locally → test with `Test-Solve.ps1` → verify SandboxValidator matches expectations
2. Submit only when local testing confirms improvement
3. After submission: compare local vs competition scores, fix validator divergences
4. Update `knowledge.md` after each fix

### Priority Order

1. **Task 12 (payroll)** — Largest gap (-3.00), known root cause, clear fix path
2. **Task 15 (project fixed-price)** — Second largest gap (-1.83), likely straightforward
3. **Task 11 (unknown)** — Requires investigation first, but +1.0 available
4. **Efficiency batch** — All T1 tasks together, then T2 tasks

### Risk Assessment

- **Payroll fix**: Medium risk. Multiple interacting systems (salary types, voucher posting, payslip generation). May need 2 iterations.
- **Project fixed-price**: Low risk. Likely just missing fields on POST body. Single iteration expected.
- **Task 11 investigation**: High risk. Unknown identity means unknown fix path. May require trial-and-error submissions.
- **Efficiency fixes**: Low risk. Just removing unnecessary API calls. But gains are transient (competitors also improve).

---

## Relevant Files

| File                              | Purpose                                                            |
| --------------------------------- | ------------------------------------------------------------------ |
| `src/Handlers/PayrollHandler.cs`  | Fix Checks 2&3 for run_payroll (Phase 1A)                          |
| `src/Handlers/ProjectHandler.cs`  | Fix fixed-price variant Check 2 (Phase 1C)                         |
| `src/Services/TaskRouter.cs`      | Verify no misrouting for Task 11 (Phase 1B)                        |
| `src/Services/LlmExtractor.cs`    | Check extraction prompts for missing task_type mappings (Phase 1B) |
| `src/Handlers/EmployeeHandler.cs` | T1 efficiency optimization (Phase 2A)                              |
| `src/Handlers/InvoiceHandler.cs`  | T2 efficiency optimization (Phase 2C)                              |
| `src/Handlers/PaymentHandler.cs`  | T2 efficiency optimization (Phase 2C)                              |
| `src/logs/submissions.jsonl`      | Cross-reference Task 11 identity (Phase 1B)                        |
| `src/logs/results.jsonl`          | Validate fix effectiveness                                         |
| `knowledge.md`                    | Update after each fix                                              |
