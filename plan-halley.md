# Plan: Close the 38.68-Point Gap

## TL;DR
We're losing by 38.68 points. **85% of the gap (32.8 pts) is in Tier 3** where we score 8.9 vs leader's 41.7. We have 8 T3 tasks at zero or near-zero. The leader is solving nearly ALL T3 tasks at ~3.5 pts average while we average 0.74. The remaining 15% (6.2 pts) is T2 inefficiency and a couple zero-score T2 tasks. T1 is fine (we're ahead by 0.4). **The entire strategy must be T3-first.**

## Gap Decomposition

| Tier | Us | Leader | Gap | % of Total Gap |
|------|-----|--------|-----|---------------|
| T1 (×1) | 14.2 | 13.8 | -0.4 (ahead) | 0% |
| T2 (×2) | 23.3 | 29.5 | +6.2 | 16% |
| T3 (×3) | 8.9 | 41.7 | +32.8 | **84%** |
| **Total** | **46.33** | **85.01** | **38.68** | 100% |

## T3 Per-Task Breakdown (Tasks 19-30)

| Task | Type | Our Score | Est. Max | Gap | Root Cause | Fix Effort |
|------|------|-----------|----------|-----|------------|------------|
| 22 | voucher receipt PDF | **0.00** | ~6.0 | 6.0 | Account extraction failure, validator divergence | MEDIUM |
| 23 | bank reconciliation | **0.00** | ~6.0 | 6.0 | CSV format cycling, all 4 formats fail | HIGH |
| 24 | ledger correction | **0.00** | ~6.0 | 6.0 | Agent passes locally 4/4 but competition says 0 | MEDIUM |
| 25 | overdue payment | **0.00** | ~6.0 | 6.0 | Invoice search missing date params | LOW |
| 26 | ??? unknown | **0.00** | ~6.0 | 6.0 | Never attempted, must discover | UNKNOWN |
| 20 | supplier invoice PDF | 0.60 | ~6.0 | 5.4 | importDocument path broken | HIGH |
| 27 | FX payment | 0.60 | ~6.0 | 5.4 | Currency resolution silent fails | MEDIUM |
| 29 | project lifecycle | 1.09 | ~6.0 | 4.9 | 3/7 checks, missing lifecycle steps | MEDIUM |
| 28 | cost analysis | 1.50 | ~6.0 | 4.5 | 2/5 checks, ledger analysis incomplete | MEDIUM |
| 21 | employee PDF offer | 1.50 | ~6.0 | 4.5 | 5/10 checks, field extraction gaps | LOW |
| 19 | employee PDF contract | 1.77 | ~6.0 | 4.2 | 9/15 checks, missing PDF fields | LOW |
| 30 | annual accounts | 1.80 | ~6.0 | 4.2 | 4/6 checks, missing closing steps | MEDIUM |

**Total T3 potential gain: ~63 points** (theoretical max if all reach ~6.0)
**Realistic target: +25-35 points** if we get most T3 tasks to 3.0-4.0 each.

## T2 Per-Task Gaps (for reference)

| Task | Type | Our Score | Est. Max | Gap |
|------|------|-----------|----------|-----|
| 11 | supplier invoice voucher | 0.00 | ~4.0 | 4.0 |
| 12 | payroll | 1.00 | ~4.0 | 3.0 |
| 08 | project basic | 1.50 | ~4.0 | 2.5 |
| 15 | fixed-price project | 1.50 | ~4.0 | 2.5 |

---

## PHASE 1: Fix Zero-Score T3 Tasks (Target: +15-24 pts)

### Step 1.1: Task 25 — Overdue Invoice Payment (LOW effort, ~+4-6 pts)
- **Problem**: Invoice search missing `invoiceDateFrom`/`invoiceDateTo` params
- **Fix**: PaymentHandler — add date range params when searching invoices. Search for `amountOutstanding > 0` + past due date
- **Files**: `src/Handlers/PaymentHandler.cs` — the "find overdue" code path
- **Validation**: Must find invoice by due date, then pay `amountOutstanding`
- **Depends on**: Nothing. Independent fix.

### Step 1.2: Task 24 — Ledger Correction Voucher (MEDIUM effort, ~+4-6 pts)
- **Problem**: Agent passes locally 4/4 but competition gives 0. VALIDATOR DIVERGENCE.
- **Fix**: First investigate what competition actually checks vs our validator. Read the sandbox.jsonl for task 24 entries to understand what we're sending vs what competition expects.
- **Key**: Multi-voucher correction entries — ensure postings balance, correct accounts, `sendToLedger=true`
- **Files**: `src/Handlers/VoucherHandler.cs` multi-voucher path, `src/Services/SandboxValidator.cs`
- **Depends on**: Need a competition submission to compare scores

### Step 1.3: Task 22 — Voucher from PDF Receipt (MEDIUM effort, ~+4-6 pts)
- **Problem**: Account extraction from PDF receipt fails. Agent-side mixed results.
- **Fix**: VoucherHandler PDF path — ensure LLM extracts account number, amount, description from receipt image/PDF. May need to improve LLM prompt for receipt parsing.
- **Files**: `src/Handlers/VoucherHandler.cs`, `src/Services/LlmExtractor.cs`
- **Depends on**: Nothing. Independent fix.

### Step 1.4: Task 23 — Bank Reconciliation (HIGH effort, ~+4-6 pts)
- **Problem**: CSV format cycling — tries 4 bank formats (DNB, Nordea, Danske, SBanken), all fail with 422
- **Fix**: Completely rethink approach — perhaps skip format-based upload and instead:
  1. Parse CSV manually in handler
  2. Create adjustment postings via manual voucher entries
  3. Or figure out the actual CSV format the competition expects
- **Files**: `src/Handlers/BankReconciliationHandler.cs`
- **Depends on**: May need to study CSV file content to determine correct format

### Step 1.5: Task 26 — Unknown Task Discovery
- **Problem**: Never seen, 0 attempts
- **Fix**: Submit a run and analyze what task 26 is. Check FallbackAgent logs.
- **Depends on**: A submission run

## PHASE 2: Improve Low-Score T3 Tasks (Target: +10-18 pts)

### Step 2.1: Task 20 — Supplier Invoice PDF (0.60 → target 3.0+)
- **Problem**: importDocument path issues. `PUT /supplierInvoice/voucher/{id}/postings` returns 500.
- **Fix**: Knowledge.md says importDocument IS required for competition check 1. Must get importDocument working reliably, then find alternative to the broken PUT endpoint.
- **Files**: `src/Handlers/VoucherHandler.cs` HandleSupplierInvoice method
- **Parallel with**: Step 2.2

### Step 2.2: Task 27 — FX Payment (0.60 → target 3.0+)
- **Problem**: Currency resolution returns 0 silently → payment amount wrong
- **Fix**: Make currency resolution throw on failure, ensure exchange rate calcs are correct, verify `paidAmountCurrency` is set
- **Files**: `src/Handlers/PaymentHandler.cs` HandleFxPaymentAsync method
- **Parallel with**: Step 2.1

### Step 2.3: Task 29 — Full Project Lifecycle (1.09 → target 3.0+)
- **Problem**: Only 3/7 checks passing. Missing lifecycle steps.
- **Fix**: Analyze what 4 checks are failing. Likely needs: project → activity → timesheet → order → invoice → payment
- **Files**: `src/Handlers/ProjectHandler.cs`
- **Parallel with**: Step 2.4

### Step 2.4: Tasks 19, 21 — Employee from PDF (1.77, 1.50 → target 3.0+)
- **Problem**: PDF field extraction incomplete
- **Fix**: Improve LLM extraction prompt for PDF documents. Ensure dateOfBirth, startDate, email, all name fields extracted.
- **Files**: `src/Services/LlmExtractor.cs`, `src/Handlers/EmployeeHandler.cs`
- **Parallel with**: Step 2.3

### Step 2.5: Tasks 28, 30 — Cost Analysis + Annual Accounts (1.50, 1.80 → target 3.0+)
- **Problem**: Partially scoring, missing checks
- **Fix**: Analyze failing checks, likely ledger query completeness and correct posting structure
- **Files**: `src/Handlers/ProjectHandler.cs`, `src/Handlers/VoucherHandler.cs`

## PHASE 3: Fix T2 Gaps (Target: +4-6 pts)

### Step 3.1: Task 11 — Supplier Invoice Voucher T2 (0.00 → target 2.0+)
- Same underlying issue as T3 supplier invoice tasks
- Fix alongside Phase 2 Step 2.1

### Step 3.2: Task 12 — Payroll (1.00 → target 3.0+)  
- **Problem**: Only 1/4 checks passing. Division creation can fatally fail.
- **Fix**: Harden division creation, verify payslip structure, ensure has_employee_link check passes
- **Files**: `src/Handlers/PayrollHandler.cs`

### Step 3.3: Tasks 08, 15 — Projects (1.50 → target 3.0+)
- **Problem**: Efficiency issues, some check failures
- **Fix**: Clean up project PM resolution, reduce write errors
- **Files**: `src/Handlers/ProjectHandler.cs`, `src/Handlers/FixedPriceProjectHandler.cs`

## PHASE 4: Efficiency Sweep (Target: +2-4 pts across T1+T2)
- Only after correctness is fixed everywhere
- Eliminate 4xx write errors (each permanently hurts score)
- Reduce unnecessary write calls
- Current 77 write errors across recent runs → target <10

---

## Verification Strategy
1. After each fix, test locally with `Test-Solve.ps1` using saved prompts from submissions.jsonl
2. Submit competition run to verify score improvement
3. Compare local validator vs competition scores — fix any divergence immediately
4. Save learned checks to knowledge.md

## Score Projection

| Scenario | T1 | T2 | T3 | Total | Gap to Leader |
|----------|-----|-----|-----|-------|-------------|
| Current | 14.2 | 23.3 | 8.9 | 46.3 | 38.7 |
| Phase 1 done | 14.2 | 23.3 | 24.0 | 61.5 | 23.5 |
| Phase 1+2 done | 14.2 | 23.3 | 38.0 | 75.5 | 9.5 |
| Phase 1+2+3 done | 14.2 | 28.0 | 38.0 | 80.2 | 4.8 |
| All phases | 14.2 | 30.0 | 42.0 | 86.2 | AHEAD |

---

## Critical Observations

1. **The leader is clearly solving ALL 12 T3 tasks** at decent quality (~3.5 avg). We solve only 4 of 12 above zero. **Coverage is everything.**
2. **Validator divergence on tasks 24 and 25** — we think we pass locally but competition disagrees. This is the #1 trap. Must fix validator BEFORE fixing handler.
3. **5 zero-score T3 tasks = ~30 points left on the table.** Each one fixed to even 50% correctness = +3 points (×3 multiplier).
4. **FallbackAgentHandler's 12-iteration limit** is a silent killer for complex T3 tasks. Consider raising to 20.
5. **77 write errors per run** is hemorrhaging efficiency bonus points across all tiers.
6. **Task 26 is a complete blind spot** — we don't even know what it is yet.

## Execution Order (dependencies)
- Steps 1.1, 1.3, 1.5 can run **in parallel** (independent tasks)
- Step 1.2 requires a submission to compare validator scores
- Phase 2 steps are mostly independent and can be parallelized
- Phase 3.1 shares root cause with Phase 2.1 (supplier invoice)

## Decisions
- **T3 is the ONLY priority** until coverage reaches 10/12 tasks above zero
- **Never touch T1** — we're ahead, leave it alone
- **T2 fixes are secondary** — only fix if it shares code with a T3 fix (e.g., supplier invoice)

## Further Considerations
1. **Should we raise FallbackAgent max iterations from 12 → 20?** Recommendation: Yes — complex T3 tasks (annual accounts, cost analysis) need more API calls. Risk is timeout, but 20 is still safe.
2. **Should we invest in dynamic bank CSV format detection or skip bank recon?** Recommendation: Invest — bank recon is worth up to 6 points. Parse CSV ourselves instead of relying on Tripletex import.
3. **Task 26 discovery**: Submit a run immediately to find out what this task is before planning a handler.
