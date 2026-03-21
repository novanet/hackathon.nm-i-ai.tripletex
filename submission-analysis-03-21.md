# Submission Analysis — 2026-03-21 (Latest 20 Entries)

## Overview

| Metric           | Value                                                                                                                                                    |
| ---------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Date range**   | 2026-03-21 02:12 – 09:55 UTC                                                                                                                             |
| **Environment**  | Competition                                                                                                                                              |
| **Success rate** | 6 / 20 (30%)                                                                                                                                             |
| **Nature**       | Mostly Tier 2/3 complex tasks — FX payments, PDF-based employee onboarding, bank reconciliation, ledger corrections, annual closing, full project cycles |

---

## Entry-by-Entry Breakdown

| #   | Line | Timestamp | Lang | Detected Type                                          | Handler                   | Result   | Calls/Err | Root Cause                                                     |
| --- | ---- | --------- | ---- | ------------------------------------------------------ | ------------------------- | -------- | --------- | -------------------------------------------------------------- |
| 1   | 174  | 02:12:57  | fr   | create_customer                                        | CustomerHandler           | **PASS** | 1/0       | —                                                              |
| 2   | 175  | 09:43:08  | de   | unknown (expense analysis → projects)                  | FallbackAgent             | **PASS** | 8/1       | 1× 422 on wrong `from` param for voucher date range            |
| 3   | 176  | 09:43:24  | de   | create_voucher (4 ledger corrections)                  | VoucherHandler            | **FAIL** | 1/1       | Empty postings array sent                                      |
| 4   | 177  | 09:43:57  | nb   | create_project (full cycle: budget+timesheets+invoice) | ProjectHandler            | **PASS** | 8/0       | Project+invoice created, but timesheets/supplier costs skipped |
| 5   | 178  | 09:44:31  | nb   | create_project (full cycle)                            | ProjectHandler            | **PASS** | 7/0       | Same — timesheets/supplier costs skipped                       |
| 6   | 179  | 09:45:19  | de   | create_voucher (headset receipt PDF)                   | VoucherHandler            | **PASS** | 8/1       | 500 on supplierInvoice PUT, fell back to manual voucher        |
| 7   | 180  | 09:45:47  | es   | create_voucher (supplier invoice PDF)                  | VoucherHandler            | **PASS** | 8/1       | Same 500 fallback pattern                                      |
| 8   | 181  | 09:46:00  | es   | bank_reconciliation (CSV)                              | BankReconciliationHandler | **FAIL** | 2/1       | `isApproved` field rejected                                    |
| 9   | 182  | 09:47:25  | en   | create_employee (offer letter PDF)                     | EmployeeHandler           | **FAIL** | 3/2       | Email missing from PDF, required for STANDARD userType         |
| 10  | 183  | 09:47:46  | fr   | register_payment (FX disagio)                          | PaymentHandler            | **FAIL** | 3/1       | 404 on `:payment` — stale paymentTypeId                        |
| 11  | 184  | 09:52:23  | nn   | create_voucher (annual closing)                        | VoucherHandler            | **FAIL** | 1/1       | Empty postings array sent                                      |
| 12  | 185  | 09:52:48  | fr   | register_payment (reminder fees)                       | PaymentHandler            | **FAIL** | 5/1       | Created customer "unknown" + 404 on payment                    |
| 13  | 186  | 09:53:08  | nn   | create_project (cost analysis → 3 projects)            | ProjectHandler            | **FAIL** | 2/1       | Invalid date: 2026-02-29 (not a leap year)                     |
| 14  | 187  | 09:53:22  | pt   | create_employee (contract PDF)                         | EmployeeHandler           | **PASS** | 2/0       | PDF had email, worked fine                                     |
| 15  | 188  | 09:53:40  | nb   | register_payment (order→invoice→pay)                   | PaymentHandler            | **FAIL** | 7/1       | 404 on `:payment` — stale paymentTypeId                        |
| 16  | 189  | 09:53:59  | pt   | create_employee (offer letter PDF)                     | EmployeeHandler           | **FAIL** | 3/2       | Email missing from PDF                                         |
| 17  | 190  | 09:54:09  | nn   | bank_reconciliation (CSV)                              | BankReconciliationHandler | **FAIL** | 2/1       | Same `isApproved` rejection                                    |
| 18  | 191  | 09:54:49  | es   | register_payment (FX agio)                             | PaymentHandler            | **FAIL** | 3/1       | 404 on `:payment` — stale paymentTypeId                        |
| 19  | 192  | 09:55:10  | nb   | register_payment (FX agio)                             | PaymentHandler            | **FAIL** | 3/1       | 404 on `:payment` — stale paymentTypeId                        |
| 20  | 193  | 09:55:37  | nn   | bank_reconciliation (CSV)                              | BankReconciliationHandler | **FAIL** | 2/1       | Same `isApproved` rejection                                    |

---

## Duplicate Type Analysis

| Task Type               | Count | Pass | Fail | Notes                                                                                          |
| ----------------------- | ----- | ---- | ---- | ---------------------------------------------------------------------------------------------- |
| **register_payment**    | 6     | 0    | 6    | ALL fail — 5× due to 404 on `:payment` (paymentTypeId stale), 1× misidentified task            |
| **create_voucher**      | 4     | 2    | 2    | Failures = empty postings for complex multi-voucher tasks; successes = PDF-based with fallback |
| **bank_reconciliation** | 3     | 0    | 3    | ALL fail — identical `isApproved` field rejection                                              |
| **create_employee**     | 3     | 1    | 2    | Failures = PDF missing email → 422 on STANDARD userType                                        |
| **create_project**      | 3     | 2    | 1    | Success but incomplete (no timesheets); 1 fail from invalid date                               |
| **create_customer**     | 1     | 1    | 0    | Tier 1 — working fine                                                                          |
| **unknown (Fallback)**  | 1     | 1    | 0    | Expense analysis via FallbackAgent — worked                                                    |

---

## Critical Failure Patterns

### 1. Payment 404 — `paymentTypeId` stale (5 failures)

- **Affected**: Entries #10, #15, #18, #19 (and partially #12)
- **Error**: `PUT /invoice/{id}/:payment?paymentTypeId=33295810` returns 404
- **Cause**: `paymentTypeId=33295810` is hardcoded or cached from a previous competition env. Each new competition run has fresh data with different IDs.
- **Fix**: Query `/ledger/paymentType` to get the correct ID dynamically per-request

### 2. Bank Reconciliation `isApproved` rejection (3 failures)

- **Affected**: Entries #8, #17, #20
- **Error**: `isApproved: Feltet eksisterer ikke i objektet.` (422)
- **Cause**: BankReconciliationHandler sends `isApproved: false` but the API doesn't accept that field
- **Fix**: Remove `isApproved` from the POST body to `/bank/reconciliation`

### 3. VoucherHandler empty postings for complex tasks (2 failures)

- **Affected**: Entries #3 (ledger error corrections), #11 (annual closing)
- **Error**: `postings: Et bilag kan ikke registreres uten posteringer.` (422)
- **Cause**: When LLM extracts multi-voucher structures (voucher1, voucher2, etc.), VoucherHandler doesn't convert these into actual posting objects — sends `postings: []`
- **Fix**: VoucherHandler needs to handle multi-voucher extraction format — iterate over voucher1/2/3/etc entities, build postings from account+amount pairs

### 4. Employee PDF onboarding — missing email (2 failures)

- **Affected**: Entries #9, #16 (both offer letter PDFs)
- **Error**: `email: Må angis for Tripletex-brukere.` (422, retried once = 2 errors each)
- **Cause**: Offer letter PDFs don't contain email addresses, but handler creates employee with `userType: STANDARD` which requires email
- **Fix**: When email is missing, either generate synthetic email (`firstname.lastname@example.org`) or use `userType: NO_ACCESS`
- **Note**: Entry #14 (contract PDF) succeeded because the PDF/extraction included an email

### 5. SupplierInvoice voucher PUT 500 (2 cases, recovered)

- **Affected**: Entries #6, #7 (both succeeded despite error)
- **Error**: `PUT /supplierInvoice/voucher/{id}/postings` returns 500
- **Recovery**: Handler fell back to manual `POST /ledger/voucher?sendToLedger=true`, which worked
- **Impact**: Extra API calls (8 instead of ~5), wastes efficiency but achieves correctness

### 6. Misc Issues

- **Entry #12**: PaymentHandler created customer named "unknown" — LLM extracted `customer: "unknown"` because prompt says "find the overdue invoice" without naming the customer. Handler should search for overdue invoices rather than creating a dummy customer.
- **Entry #13**: Invalid date 2026-02-29 (not a leap year) in `endDate` field causes 422.
- **Entries #4, #5**: Full project cycle tasks partially succeeded (project+invoice created) but skipped timesheets and supplier costs — competition likely checks for those.

---

## New Tier 2/3 Task Types Observed

| Task Type                           | Description                                                        | Current Support                                    |
| ----------------------------------- | ------------------------------------------------------------------ | -------------------------------------------------- |
| **FX Payment (agio/disagio)**       | Register payment with exchange rate differences, book FX gain/loss | Not supported                                      |
| **Bank Reconciliation**             | Match bank statement CSV against open invoices                     | Handler exists but broken (`isApproved` bug)       |
| **Ledger Error Correction**         | Find and fix posting errors with correction vouchers               | VoucherHandler can't build multi-voucher postings  |
| **Annual Closing**                  | Depreciation, prepaid expenses, tax calculation                    | VoucherHandler can't build multi-voucher postings  |
| **Full Project Cycle**              | Budget, timesheets, supplier costs, project invoice                | ProjectHandler does basic create+invoice only      |
| **Employee Onboarding from PDF**    | Extract employment details from offer letters/contracts            | Works when email present, fails otherwise          |
| **Reminder Fees + Partial Payment** | Find overdue invoices, post reminder fees, partial payment         | PaymentHandler doesn't search for overdue invoices |
| **Expense Analysis**                | Analyze ledger for cost increases, create internal projects        | FallbackAgent handled it — worked                  |

---

## Recommended Next Steps (Priority Order)

### Phase 1: Fix Critical Regressions (3 bugs → fixes 10/14 failures)

1. **Fix `paymentTypeId` lookup** — Query `/ledger/paymentType` dynamically instead of using cached ID. Fixes 5 failures.
2. **Fix BankReconciliationHandler** — Remove `isApproved` from POST body. Fixes 3 failures.
3. **Fix EmployeeHandler for missing email** — Generate synthetic email from name when PDF doesn't provide one. Fixes 2 failures.

### Phase 2: Fix VoucherHandler for Complex Tasks (fixes 2 failures)

4. **Multi-voucher support in VoucherHandler** — Parse `voucher1`/`voucher2`/etc structures from extraction. Build proper postings arrays with account ID lookups.

### Phase 3: Tier 2/3 Handler Improvements

5. **ProjectHandler full cycle** — Add timesheet registration and supplier cost booking when extracted.
6. **FX Payment handling** — Extend PaymentHandler for multi-currency invoices and exchange rate postings.
7. **Reminder fees flow** — Extend PaymentHandler to search for overdue invoices instead of creating dummy customer.
8. **Fix invalid date validation** — Add leap year check or let the API handle it more gracefully.

### Phase 4: Future Consideration

9. **Annual closing / depreciation handler** — New dedicated handler or extend VoucherHandler for multi-step accounting procedures.
10. **Bank reconciliation full flow** — CSV parsing + invoice matching logic beyond just creating the reconciliation record.

---

## Impact Estimate

| Fix             | Failures Fixed | Projected Success Rate |
| --------------- | -------------- | ---------------------- |
| Phase 1 only    | 10             | ~80% (16/20)           |
| Phase 1 + 2     | 12             | ~90% (18/20)           |
| Phase 1 + 2 + 3 | 14             | ~95%+                  |
