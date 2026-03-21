# Gap Analysis & Suggestions — Hallstein #01

**Date:** 2026-03-21
**Our score:** 46.33 | **Leader:** 85.01 | **Gap:** 38.68

---

## Scoreboard Truth

| Tier | Leader | Us | Gap | % of total gap |
|---|---|---|---|---|
| **T1** | 13.8 | **14.2** | -0.4 (ahead!) | — |
| **T2** | 29.5 | 23.3 | **6.2** | 16% |
| **T3** | 41.7 | 8.9 | **32.8** | **84%** |

**The entire problem is Tier 3.** T1 is fine. T2 has modest upside. T3 is a catastrophe — we're capturing 21% of the leader's T3 score.

---

## T3 Task-by-Task Autopsy (tasks 19–30, ×3 multiplier, ~6.0 max each)

| Task | What it is | Our Score | Leader (est.) | Status |
|---|---|---|---|---|
| **19** | Employee from PDF contract | 1.77 | ~4–5 | Partial — PDF fields missing |
| **20** | Supplier voucher from PDF | 0.60 | ~3–4 | Almost zero — postings wrong |
| **21** | Employee from PDF offer letter | **0.00** | ~4–5 | **COMPLETE FAILURE** |
| **22** | Voucher from PDF receipt | **0.00** | ~3–4 | **COMPLETE FAILURE** |
| **23** | Bank reconciliation (CSV) | **0.00** | ~3–4 | **COMPLETE FAILURE** |
| **24** | Ledger correction (find 4 errors) | **0.00** | ~3–4 | **COMPLETE FAILURE** |
| **25** | Find overdue invoice + reminder fees | **0.00** | ~3–4 | **COMPLETE FAILURE** |
| **26** | ??? Unknown task | **0.00** | ~3–4 | **NOT DISCOVERED** |
| **27** | FX currency payment | 0.60 | ~3–4 | Partial — currency chain broken |
| **28** | Cost analysis → create projects | **0.00** | ~3–4 | **COMPLETE FAILURE** |
| **29** | Full project lifecycle | 1.09 | ~4–5 | Partial — missing steps |
| **30** | Simplified annual accounts | **0.00** | ~3–4 | **COMPLETE FAILURE** |
| | **TOTAL** | **4.06** | **~41.7** | |

**8 out of 12 T3 tasks score ZERO.** The leader is solving most of these.

---

## Root Cause Analysis — Why T3 Is Failing

### 1. PDF Extraction Is Broken (Tasks 19, 20, 21, 22)
These tasks send base64-encoded PDF files. Our `FallbackAgentHandler` uses PdfPig for text extraction, but:
- **EmployeeHandler never receives PDF tasks** — they fall to FallbackAgent which is unreliable
- **VoucherHandler's PDF path got ripped out** (importDocument removed, classic path doesn't extract PDF fields properly)
- Task 19 scored 1.77 (some fields extracted) but 21 scored 0 (different PDF format)
- **FIX**: Route PDF employee tasks to EmployeeHandler, add PDF text extraction, map fields from contract/offer letter text

### 2. Bank Reconciliation Is Completely Broken (Task 23)
3 attempts, 0 score. The handler exists but:
- **CSV parsing likely fails** on the actual competition CSV format
- **Period/account resolution** may not match what competition expects
- **No post-reconciliation state validation**
- **FIX**: Test with actual competition CSV (we have it in logs/files), debug the entire flow

### 3. Complex Ledger Analysis Tasks Not Implemented (Tasks 24, 28, 30)
These require **reading existing ledger data**, analyzing it, then creating corrective entries:
- **Task 24** (Ledger correction): "Find 4 errors in Jan/Feb vouchers, correct them" — requires GET /ledger/voucher, detect wrong account/amount, POST correction vouchers
- **Task 28** (Cost analysis): "Find 3 expense accounts with biggest increase Jan→Feb, create internal projects" — requires ledger aggregation + project creation
- **Task 30** (Annual accounts): "Calculate depreciation for 3 assets, post year-end entries" — pure calculation + voucher creation
- **FIX**: These are algorithmic. Read existing data, compute, create entries. No exotic API calls needed — just GET + POST /ledger/voucher

### 4. Overdue Invoice Search Broken (Task 25)
"Find the overdue invoice and register reminder fees of 50 NOK"
- Our handler tries but scores 0 — likely can't find the overdue invoice
- **FIX**: `GET /invoice?invoiceDateFrom=2020-01-01&invoiceDateTo={today}&from=0&count=100` → filter where `amountOutstanding > 0` AND `invoiceDueDate < today`

### 5. FX Payment Chain Incomplete (Task 27)
Score 0.60 = partial. The payment part works but currency resolution or invoice creation fails.
- **FIX**: Debug the actual FX prompt, ensure currency resolved correctly, amounts calculated properly

### 6. Task 26 Not Even Discovered
We don't know what this task is. **Need at least 1 more submission to discover it.**

---

## T2 Gap Analysis (6.2 points)

| Task | Type | Our Score | Max (~4.0) | Gap |
|---|---|---|---|---|
| **11** | Supplier invoice voucher | **0.00** | ~4.0 | Full — postings pattern wrong |
| **12** | Payroll | 1.00 | ~4.0 | Big — needs payslip fix |
| **06** | Simple invoice | 1.33 | ~2.0 | Small — efficiency? |
| **05** | Multi-department | 1.33 | ~2.0 | Small — maybe only 2/3 created |
| **08** | Basic project | 1.50 | ~2.0+ | Small — missing a check |
| **15** | Fixed-price project | 1.50 | ~3.0 | Medium — invoice missing? |

**Biggest T2 wins**: Task 11 (supplier invoice, +4.0 potential) and Task 12 (payroll, +3.0 potential).

---

## The Aggressive Plan — Prioritized by Points/Effort

### TIER A: High Impact, Achievable (~20–25 pts potential)

| Priority | Task(s) | Points | What to do |
|---|---|---|---|
| **A1** | 25 (overdue invoice) | +3–5 pts | Fix invoice search filter. Simple GET + filter + payment. |
| **A2** | 24 (ledger correction) | +3–5 pts | GET vouchers for Jan+Feb, find errors (wrong account, wrong amount, missing entry, duplicate), POST correction vouchers |
| **A3** | 28 (cost analysis) | +3–5 pts | GET ledger/voucher by period, aggregate by account, find top-3 increases, create 3 internal projects |
| **A4** | 30 (annual accounts) | +3–5 pts | Parse depreciation amounts from prompt (pure math), POST vouchers with debit depreciation/credit asset |
| **A5** | 11 (supplier invoice) | +3–4 pts | Fix postings — debit expense account, credit 2400 (supplier). Get the VAT split right. |
| **A6** | 23 (bank reconciliation) | +3–5 pts | Debug with actual CSV from competition, fix parsing + matching |

### TIER B: Medium Impact (~10–15 pts potential)

| Priority | Task(s) | Points | What to do |
|---|---|---|---|
| **B1** | 19, 21 (employee from PDF) | +4–8 pts | Route to EmployeeHandler, extract fields from PDF text (name, email, DOB, role, salary) |
| **B2** | 20, 22 (voucher from PDF) | +4–8 pts | Extract supplier/amount/account from PDF text, create proper voucher |
| **B3** | 27 (FX payment) | +2–3 pts | Debug currency chain, fix exchange rate math |
| **B4** | 12 (payroll) | +2–3 pts | Fix payslip generation/linking |
| **B5** | 29 (full project lifecycle) | +2–3 pts | Add missing steps (timesheet? invoice? closure?) |

### TIER C: Polish (~5 pts potential)

| Priority | Task(s) | Points | What to do |
|---|---|---|---|
| **C1** | 15 (fixed-price project) | +1–2 pts | Ensure milestone invoice fires |
| **C2** | 06 (simple invoice) | +0.5–1 pt | Fix correctness issue |
| **C3** | 05 (multi-department) | +0.5–1 pt | Ensure all 3 departments created |
| **C4** | 26 (unknown) | +3–5 pts | Submit to discover it first |

---

## Critical Strategic Call

The leader has **41.7 in T3 alone**. That's ~7 T3 tasks scoring ~6.0 each. They clearly:
1. Handle PDF extraction well
2. Implemented ledger analysis (tasks 24, 28, 30)
3. Got bank reconciliation working (task 23)
4. Solved the hard payment variants (tasks 25, 27)

**To close the gap, we need to go from 8 T3 tasks scoring 0 to at least 8 T3 tasks scoring ~4.0 each.** That's +24 points from T3 alone, closing the gap to ~15 points.

Combined with T2 fixes (task 11 supplier invoice +4, payroll +3), that's potentially **+30 points**, putting us at **~76** vs leader's 85.

### The single highest-ROI action
Implementing the three "analyze ledger + create entities" tasks (24, 28, 30) — they're pure computation, no exotic API calls needed, and together worth up to **15 points**. The leader almost certainly has these.

### Recommended implementation order
1. Tasks 25, 24, 28, 30 (computation T3 — no PDF needed)
2. Task 11 (supplier invoice fix — T2 quick win)
3. Tasks 19, 21, 20, 22 (PDF extraction improvements)
4. Task 23 (bank reconciliation debug)
5. Task 27 (FX payment fix)
6. Discover task 26
