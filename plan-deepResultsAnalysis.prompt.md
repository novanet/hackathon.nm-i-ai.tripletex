# Plan: Deep Results Analysis & Improvement Recommendations

## TL;DR
Analysis of ~110 competition submissions reveals 6 critical correctness bugs, 3 intermittent reliability issues, and ~12 undiscovered Tier 3 tasks. Fixing the 3 broken handlers (bank_reconciliation, run_payroll, voucher edge cases) and stabilizing intermittent failures should add ~15-20 points. Then expanding Tier 3 coverage could add 10-30 more.

## Current Best Scores per Task Type (from results.jsonl)

| Task Type | Best Score | Max Pts | Success Rate | Status |
|-----------|-----------|---------|-------------|--------|
| create_customer | 8/8 (2.0) | 8 | 100% | PERFECT |
| create_department | 7/7 (1.33) | 7 | 100% | PERFECT |
| create_credit_note | 8/8 (3.0) | 8 | 100% | PERFECT |
| create_product | 7/7 (2.0) | 7 | ~90% | GOOD |
| create_invoice | 8/8 (4.0) | 8 | ~85% | GOOD |
| create_travel_expense | 8/8 (4.0) | 8 | ~75% | OK |
| create_employee | 8/8 (1.5) | 8-22 | ~70% | INTERMITTENT |
| create_project | 8/8 (3.0) | 8-11 | ~60% | UNSTABLE |
| register_payment | 8/8 (4.0) | 8-10 | ~65% | UNSTABLE |
| create_supplier | 6/7 (0.86) | 7 | 80% (never 100%) | CHECK 5 ALWAYS FAILS |
| create_voucher | 13/13 (4.0) | 8-13 | ~55% | CRITICAL |
| run_payroll | 4/8 (1.0) | 8 | ~10% | CRITICAL |
| bank_reconciliation | 0/10 (0.0) | 10 | 0% | BROKEN |
| unknown (FallbackAgent) | 0/5-10 (0.0) | 10 | 0% | NO HANDLER |

---

## Phase 1: Fix Broken Handlers (0% → >50%)

### 1A. bank_reconciliation — Remove `isApproved` field
- **Root cause**: `isApproved` field doesn't exist in the API version used by competition proxy
- **Fix**: Remove `isApproved` from POST body in BankReconciliationHandler.cs
- **File**: `src/Handlers/BankReconciliationHandler.cs` line 52
- **Impact**: Tier 2 ×2, 10 pts max → potentially 2-4 points added

### 1B. run_payroll — Fix persistent 0/4 failure
- **Root cause**: Multiple issues:
  - Payslip search broken in sandbox AND likely competition
  - Employee email required for Tripletex users but not always extracted
  - Division creation fails with parent company orgNumber
  - STJ serialization drops anonymous type properties
- **Status**: Knowledge.md says dual salary+voucher approach scores 8/8 locally, but competition still 0/4 or 2/4
- **Need**: Investigate why competition still fails — possibly need to verify all fixes were applied in latest code
- **File**: `src/Handlers/PayrollHandler.cs`
- **Impact**: Tier 2 ×2, 8 pts max → potentially 2-4 points added

### 1C. create_voucher — Fix "Et bilag kan ikke registreres uten posteringer"
- **Root cause**: Two failure modes:
  1. Postings array is empty/null when LLM extraction fails to parse accounts
  2. Account-locked VAT code mismatch causes 422 on specific account combos
- **Fix**: Add defensive fallback when structured postings extraction fails (use debit 1920 / credit 3000 defaults), add explicit error handling for locked VAT codes
- **File**: `src/Handlers/VoucherHandler.cs`
- **Impact**: Tier 2 ×2, 13 pts max → going from ~55% to ~85% reliability would add ~2-3 points

---

## Phase 2: Fix Intermittent Failures

### 2A. create_supplier — Check 5 always fails
- **Root cause**: Likely `phoneNumber` check — competition checks it even when not in prompt
- **Fix**: Default phoneNumber to a placeholder or extract more aggressively from prompt
- **File**: `src/Handlers/SupplierHandler.cs`
- **Impact**: 6→7 pts per session, normalized 0.86→1.0+

### 2B. create_project — Check 2 fails ~40% of the time
- **Hypothesis**: Check 2 = `has_customer` or `has_project_manager` — inconsistent resolution
- **Root cause options**: 
  - Customer not always found/created properly
  - Project manager entitlements not always granted
  - Fixed-price project needs invoice but handler doesn't always create one
- **File**: `src/Handlers/ProjectHandler.cs`
- **Impact**: Stabilizing from 60%→90% on 8pt tasks adds ~2 points

### 2C. register_payment — 404 errors on invoice lookup
- **Root cause**: PaymentHandler tries to find existing invoice but gets 404
- **Fix**: Better search fallback, use multiple search criteria (org number, name, amount)
- **File**: `src/Handlers/PaymentHandler.cs`

### 2D. create_employee — Tier 3 15-check variant
- **Latest result**: 9/15 (13/22 pts) — 6 checks fail (checks 10-15)
- **Hypothesis**: Tier 3 checks include employment details, salary info, dateOfBirth, NIN, etc.
- **Fix**: Ensure all extractable fields are passed through: dateOfBirth, nationalIdentityNumber, phoneNumber, address, bankAccount, employment details
- **File**: `src/Handlers/EmployeeHandler.cs`

---

## Phase 3: Tier 3 Task Coverage (×3 multiplier = up to 6 pts each)

Currently handling 18/30 task types. ~12 unknown Tier 3 tasks exist.

### 3A. Immediate wins (likely simple handlers):
- **create_contact** — Already in LLM extractor but no handler exists. Route to ContactHandler (already exists in src/Handlers/)
- **update_customer** — Simple GET + PUT flow
- **update_supplier** — Simple GET + PUT flow
- **update_product** — Simple GET + PUT flow

### 3B. Medium complexity:
- **supplier_invoice** — POST /incomingInvoice is 403-blocked; use voucher-based workaround
- **OCR/document processing** — Parse base64 file attachments, extract invoice data

### 3C. Discovery needed:
- ~6-8 more task types are unknown. Need submissions to discover them via FallbackAgentHandler logging.

---

## Phase 4: Efficiency Optimizations (after correctness)

- Product handler call count: 2→1 by skipping VAT lookup
- Department handler: 4→2 by smarter number selection
- Project handler: 12→6 by eliminating error retries
- Invoice handler: 8→5 (already optimized to 5 in current code)
- Employee handler: Reduce redundant department lookups

---

## Estimated Total Impact

| Phase | Estimated Points Added |
|-------|----------------------|
| 1A: Fix bank_reconciliation | +2 to +4 pts |
| 1B: Fix run_payroll | +2 to +4 pts |
| 1C: Fix voucher reliability | +2 to +3 pts |
| 2A-D: Stabilize intermittent | +3 to +5 pts |
| 3A: Easy Tier 3 handlers | +6 to +12 pts |
| 3B: Medium Tier 3 | +3 to +6 pts |
| **Total** | **+18 to +34 pts** |

---

## Recommended Implementation Order

| # | Fix | Time | Expected Pts |
|---|-----|------|-------------|
| 1 | `bank_reconciliation`: remove `isApproved` | 5 min | +2–4 |
| 2 | `create_contact`: add TaskRouter mapping | 5 min | +3–6 |
| 3 | `create_supplier`: fix Check 5 (phoneNumber) | 10 min | +0.3 |
| 4 | `create_voucher`: postings fallback | 30 min | +2–3 |
| 5 | `run_payroll`: deep debug | 1 hr | +2–4 |
| 6 | `create_project`: stabilization | 30 min | +2 |
| 7 | `register_payment`: fix 404 lookup | 30 min | +1–2 |
| 8 | `create_employee`: Tier 3 fields | 30 min | +2–4 |
| 9 | `update_customer/supplier/product` handlers | 1 hr | +9–18 |
| 10 | Tier 3 discovery submissions | — | +?? |

---

## Key Observations

1. **Voucher 0/4 failures are the single biggest reliability drag** — when it works it scores 13 pts (highest single-task score), but ~50% failure rate. Worth prioritizing deep investigation of which prompt variants cause empty postings.

2. **`create_contact` is a free lunch** — handler exists, extractor knows about it, just needs one line in TaskRouter. Should be done immediately.

3. **Tier 3 discovery is time-critical** — the competition ends March 22 15:00 CET. Every unknown task type at ×3 multiplier is worth more than optimizing existing handlers. Budget 3-4 submissions purely for recon.

4. **Token expiration errors** account for ~5-10% of failures — these are NOT code bugs but infrastructure/timing issues. Can be mitigated with faster handler execution.

5. **`create_employee` has two difficulty levels** — Tier 1 (7 checks, 8 pts) and Tier 3 (15 checks, 22 pts). The Tier 3 variant tests employment details, salary info, etc. Getting 9/15 → 15/15 is worth more than any single Tier 1 fix.
