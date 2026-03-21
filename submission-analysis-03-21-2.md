# Submission Analysis — Run 2026-03-21 14:35–14:49 UTC

## Overview

| Metric                        | Value                  |
| ----------------------------- | ---------------------- |
| Total agent tasks             | 30                     |
| Succeeded (agent-side)        | 27 (90%)               |
| Failed (agent-side)           | 3 (10%)                |
| Competition results           | 31 entries             |
| Score before run              | 40.897                 |
| **Score after run**           | **46.330**             |
| **Score gained this run**     | **+5.433**             |
| Total API calls               | ~295                   |
| Total write errors            | ~77                    |

### Score Improvements This Run

| tx_task_id | Type                 | Before | After | Delta    | Triggered By                          |
| ---------- | -------------------- | ------ | ----- | -------- | ------------------------------------- |
| 01         | employee (basic)     | 1.50   | 2.00  | **+0.50** | 7/7 checks passed (sub 2daa2675)     |
| 30         | annual accounts      | 0.00   | 1.80  | **+1.80** | 4/6 checks passed (sub 2a9e9b7d)     |
| 28         | cost analysis        | 0.00   | 1.50  | **+1.50** | 2/5 checks passed (sub 88d20b29)     |
| 21         | employee (offer PDF) | 0.00   | 1.50  | **+1.50** | 5/10 checks passed (sub 0722544f)    |
| 16         | project (timesheet)  | 2.667  | 2.80  | **+0.13** | 4/4 checks passed (sub 413aa298)     |

### Final Best Scores Per Task (Post-Run Leaderboard)

| tx_task_id | Best Score | Attempts |  | tx_task_id | Best Score | Attempts |
| ---------- | ---------- | -------- | --- | ---------- | ---------- | -------- |
| 01         | 2.00       | 12       |  | 16         | 2.80       | 10       |
| 02         | 2.00       | 13       |  | 17         | 3.50       | 12       |
| 03         | 2.00       | 11       |  | 18         | 4.00       | 12       |
| 04         | 2.00       | 9        |  | 19         | 1.77       | 3        |
| 05         | 1.33       | 10       |  | 20         | 0.60       | 3        |
| 06         | 1.33       | 11       |  | 21         | 1.50       | 3        |
| 07         | 2.00       | 11       |  | 22         | **0.00**   | 3        |
| 08         | 1.50       | 10       |  | 23         | **0.00**   | 6        |
| 09         | 2.67       | 9        |  | 24         | **0.00**   | 5        |
| 10         | 2.67       | 14       |  | 25         | **0.00**   | 4        |
| 11         | **0.00**   | 10       |  | 26         | **0.00**   | 2        |
| 12         | 1.00       | 18       |  | 27         | 0.60       | 5        |
| 13         | 2.50       | 11       |  | 28         | 1.50       | 4        |
| 14         | 2.67       | 14       |  | 29         | 1.09       | 4        |
| 15         | 1.50       | 13       |  | 30         | 1.80       | 3        |

**Still at 0.00 (6 tasks):** tx11 (supplier invoice voucher), tx22 (voucher receipt PDF), tx23 (bank recon), tx24 (ledger correction), tx25 (overdue invoice), tx26 (unknown)

## All 30 Entries

| #   | Line | tx_task_id (est.) | Task Type                  | Handler           | Success  | Calls | Errors | ms     | Lang |
| --- | ---- | ----------------- | -------------------------- | ----------------- | -------- | ----- | ------ | ------ | ---- |
| 1   | 194  | 29                | create_project (lifecycle) | ProjectHandler    | ✅       | 8     | 0      | 6320   | nb   |
| 2   | 195  | 01                | create_employee (basic)    | EmployeeHandler   | ✅       | 2     | 0      | 12346  | fr   |
| 3   | 196  | 27                | register_payment (EUR fx)  | PaymentHandler    | ✅       | 7     | 0      | 4087   | de   |
| 4   | 197  | 24                | ledger correction          | FallbackAgent     | ✅       | 18    | 0      | 30306  | nb   |
| 5   | 198  | 23                | bank_reconciliation        | BankReconHandler  | ✅       | 9     | 5      | 4634   | nn   |
| 6   | 199  | 24                | ledger correction          | FallbackAgent     | ✅       | 2     | 0      | 8447   | en   |
| 7   | 200  | 25                | overdue invoice + reminder | FallbackAgent     | ✅       | 6     | 4      | 23190  | de   |
| 8   | 201  | 15                | set_fixed_price            | FixedPriceProject | ✅       | 2     | 0      | 8775   | pt   |
| 9   | 202  | 20                | voucher (supplier PDF)     | VoucherHandler    | ✅       | 6     | 0      | 39060  | pt   |
| 10  | 203  | 23                | bank_reconciliation        | BankReconHandler  | **FAIL** | 2     | 1      | 2805   | de   |
| 11  | 204  | 28                | cost analysis + projects   | FallbackAgent     | ✅       | 15    | **9**  | 35296  | nb   |
| 12  | 205  | 30                | annual accounts            | FallbackAgent     | ✅       | 27    | **11** | 49505  | es   |
| 13  | 206  | 24                | ledger correction          | FallbackAgent     | ✅       | 2     | 0      | —      | en   |
| 14  | 207  | 22                | voucher (receipt PDF)      | VoucherHandler    | **FAIL** | 2     | 1      | 2410   | pt   |
| 15  | 208  | 30                | annual accounts            | FallbackAgent     | ✅       | 15    | 0      | 22024  | nb   |
| 16  | 209  | 19                | employee (PDF contract)    | EmployeeHandler   | ✅       | 2     | 0      | 9109   | es   |
| 17  | 210  | 19                | employee (PDF contract)    | EmployeeHandler   | ✅       | 2     | 0      | 85198  | es   |
| 18  | 211  | 28                | cost analysis + projects   | FallbackAgent     | ✅       | 10    | 2      | 19000  | fr   |
| 19  | 212  | 29                | create_project (lifecycle) | ProjectHandler    | ✅       | 8     | 0      | 7114   | es   |
| 20  | 213  | 12                | run_payroll                | PayrollHandler    | **FAIL** | 7     | 2      | 19237  | de   |
| 21  | 214  | 25                | overdue invoice + reminder | FallbackAgent     | ✅       | 6     | 5      | 20121  | es   |
| 22  | 215  | 22                | voucher (receipt PDF)      | VoucherHandler    | ✅       | 5     | 0      | 4267   | en   |
| 23  | 216  | 10/18             | register_payment (full)    | PaymentHandler    | ✅       | 8     | 0      | 14629  | de   |
| 24  | 217  | 21                | employee (PDF offer)       | EmployeeHandler   | ✅       | 2     | 0      | 5674   | nb   |
| 25  | 218  | 24                | ledger correction          | FallbackAgent     | ✅       | 13    | 2      | 20869  | en   |
| 26  | 219  | 03                | create_product             | ProductHandler    | ✅       | 1     | 0      | 3849   | pt   |
| 27  | 220  | 16                | create_project (timesheet) | ProjectHandler    | ✅       | 11    | 0      | 24873  | de   |
| 28  | 221  | 23                | bank_reconciliation        | BankReconHandler  | ✅       | 9     | 5      | 6617   | de   |
| 29  | 222  | 20                | voucher (supplier PDF)     | VoucherHandler    | ✅       | 6     | 0      | 72486  | de   |
| 30  | 223  | 30                | annual accounts            | FallbackAgent     | ✅       | 22    | **12** | 165287 | fr   |

## Competition Results (results.jsonl × leaderboard.jsonl)

Each row = one competition evaluation. `tx_task_ids` from leaderboard's `attempted_tasks`. Two submissions (tx26, tx02) returned 0 agent tasks (missed prompts).

| #  | Sub ID   | tx_task_ids   | Agent Tasks                        | Checks    | Norm Score | Running Best |
| -- | -------- | ------------- | ---------------------------------- | --------- | ---------- | ------------ |
| 1  | 06558731 | 26            | *(0 tasks — missed)*               | 0/0       | 0.00       | 40.90        |
| 2  | 57ee8950 | 02            | *(0 tasks — missed)*               | 0/0       | 0.00       | 40.90        |
| 3  | c270fd0c | 29            | create_project                     | 3/7 `PPF FFFFP` | 1.09  | 40.90        |
| 4  | 2daa2675 | 01            | create_employee                    | **7/7** `PPPPPPP` | **2.00** | **41.40** ⬆ |
| 5  | 045c4dcd | 27            | register_payment                   | 1/4 `PFFF`  | 0.60     | 41.40        |
| 6  | a759ee51 | 01,24,27      | employee+payment+unknown           | 0/4 `FFFF`  | 0.00     | 41.40        |
| 7  | 6e70058b | 23,24         | unknown+bank_recon                 | 0/2 `FF`    | 0.00     | 41.40        |
| 8  | 32df48e4 | 23,24         | unknown                            | 0/4 `FFFF`  | 0.00     | 41.40        |
| 9  | f8d4f4c3 | 24,25         | unknown×2+fixed_price              | 0/6 `FFFFFF` | 0.00    | 41.40        |
| 10 | e29c69f6 | 15,25         | unknown+fixed_price                | 2/4 `PPFF`  | 1.00     | 41.40        |
| 11 | 90d44757 | 15,20,23      | voucher+bank_recon                 | 1/6 `PFFFFF` | 0.60    | 41.40        |
| 12 | e3c813f0 | 20,23         | voucher+bank_recon                 | 0/2 `FF`    | 0.00     | 41.40        |
| 13 | e43c8e19 | 28            | unknown                            | 0/5 `FFFFF` | 0.00     | 41.40        |
| 14 | 2a9e9b7d | 28,30         | unknown×2                          | 4/6 `PPPFFP` | **1.80** | **43.20** ⬆ |
| 15 | 6adb90fe | 24,30         | unknown×2                          | 0/4 `FFFF`  | 0.00     | 43.20        |
| 16 | de72f087 | 22            | create_voucher                     | 0/5 `FFFFF` | 0.00     | 43.20        |
| 17 | 7d062a47 | 22,24,30      | unknown+voucher+unknown            | 4/6 `PPPFFP` | 1.80    | 43.20        |
| 18 | 44a928a2 | 19            | create_employee                    | 9/15        | 1.77     | 43.20        |
| 19 | 91bf6b90 | 19,30         | unknown+employee×2                 | 9/15        | 1.77     | 43.20        |
| 20 | 88d20b29 | 19,28         | employee+unknown                   | 2/5 `FPFFP` | **1.50** | **44.70** ⬆ |
| 21 | f8338367 | 28,29         | unknown+project                    | 3/7         | 1.09     | 44.70        |
| 22 | 96765dd2 | 12,29         | project+payroll                    | 0/4 `FFFF`  | 0.00     | 44.70        |
| 23 | f7fe6266 | 12,25         | payroll+unknown                    | 0/6 `FFFFFF` | 0.00    | 44.70        |
| 24 | a5b2fa73 | 22,25         | unknown+voucher                    | 0/5 `FFFFF` | 0.00     | 44.70        |
| 25 | f76aaec1 | 10            | register_payment                   | 0/5 `FFFFF` | 0.00     | 44.70        |
| 26 | 2d0b8dcc | 10,21,24      | payment+employee+unknown           | 0/4 `FFFF`  | 0.00     | **46.20** ⬆ |
| 27 | 0722544f | 21,24         | employee+unknown                   | 5/10        | **1.50** | 46.20        |
| 28 | 06b38bbe | 03            | create_product                     | **5/5** `PPPPP` | **2.00** | 46.20     |
| 29 | 413aa298 | 03,16         | product+project                    | **4/4** `PPPP` | **2.80** | **46.33** ⬆ |
| 30 | d6f0455d | 23            | bank_reconciliation                | 0/2 `FF`    | 0.00     | 46.33        |
| 31 | 0085f951 | 20            | create_voucher                     | 1/6 `PFFFFF` | 0.60    | 46.33        |

**Summary:** 31 results, 7 scored > 0 (rows marked ⬆ = new best score achieved), 5 unique score improvements.

## Grouped by Task Type (tx_task_id)

| tx_task_id | Type                 | Count | Handler           | Success Rate | Avg Errors | Best Score (post-run) | Delta  |
| ---------- | -------------------- | ----- | ----------------- | ------------ | ---------- | --------------------- | ------ |
| 01         | employee (basic)     | 1     | EmployeeHandler   | 1/1          | 0          | 2.00                  | +0.50  |
| 03         | product              | 1     | ProductHandler    | 1/1          | 0          | 2.00                  | —      |
| 10/18      | payment (full)       | 1     | PaymentHandler    | 1/1          | 0          | 2.67 / 4.00           | —      |
| 12         | payroll              | 1     | PayrollHandler    | **0/1**      | 2          | 1.00                  | —      |
| 15         | fixed price          | 1     | FixedPriceProject | 1/1          | 0          | 1.50                  | —      |
| 16         | project (timesheet)  | 1     | ProjectHandler    | 1/1          | 0          | 2.80                  | +0.13  |
| 19         | employee (PDF)       | 2     | EmployeeHandler   | 2/2          | 0          | 1.77                  | —      |
| 20         | voucher (suppl. PDF) | 2     | VoucherHandler    | 2/2          | 0          | 0.60                  | —      |
| 21         | employee (offer PDF) | 1     | EmployeeHandler   | 1/1          | 0          | 1.50                  | **+1.50** |
| 22         | voucher (receipt)    | 2     | VoucherHandler    | **1/2**      | 0.5        | **0.00**              | —      |
| 23         | bank reconciliation  | 3     | BankReconHandler  | **2/3**      | 3.7        | **0.00**              | —      |
| 24         | ledger correction    | 4     | FallbackAgent     | 4/4          | 0.5        | **0.00**              | —      |
| 25         | overdue invoice      | 2     | FallbackAgent     | 2/2          | 4.5        | **0.00**              | —      |
| 27         | payment (EUR fx)     | 1     | PaymentHandler    | 1/1          | 0          | 0.60                  | —      |
| 28         | cost analysis        | 2     | FallbackAgent     | 2/2          | 5.5        | 1.50                  | **+1.50** |
| 29         | project (lifecycle)  | 2     | ProjectHandler    | 2/2          | 0          | 1.09                  | —      |
| 30         | annual accounts      | 3     | FallbackAgent     | 3/3          | 7.7        | 1.80                  | **+1.80** |

## 3 Hard Failures

1. **L203 — bank_reconciliation (de)**: Tripletex returned HTTP 500 ("Feilsituasjon") on `/ledger/accountingPeriod` — server-side error, not our fault
2. **L207 — voucher/receipt (pt)**: VoucherHandler extracted `"HR expense account"` as literal text instead of a valid account number. Department lookup also failed.
3. **L213 — run_payroll (de)**: PayrollHandler tried to create employee with `organizationNumber` field, hit validation error: "Juridisk enhet kan ikke registreres som virksomhet/underenhet"

## Root Cause Analysis of Recurring Errors

### 1. `amountGrossCurrency` = "NOK" string (annual accounts, tx_id 30)

The FallbackAgent sends `"amountGrossCurrency": "NOK"` (string) instead of a number. This field expects the numeric amount, not the currency code. Causes 3-12 retries per task. Seen in L205 (11 errors), L223 (12 errors).

### 2. Bank import format cycling (bank_recon, tx_id 23)

BankReconciliationHandler tries DNB_CSV → NORDEA_CSV → DANSKE_BANK_CSV → SBANKEN_PRIVAT_CSV — all fail with 422. The CSV doesn't match any predefined format. Each failed attempt costs an error. Seen in L198 (5 errors), L221 (5 errors).

### 3. Invoice search missing date params (overdue, tx_id 25)

FallbackAgent searches `/invoice?status=OVERDUE` without `invoiceDateFrom`/`invoiceDateTo` — Tripletex requires both. Costs 2-3 errors before it finds the right params. Seen in L200, L214.

### 4. Project creation without projectManager (cost analysis, tx_id 28)

FallbackAgent sends empty `{}` to `POST /project` — missing required `projectManager` field. Retries 5-7 times. Seen in L204 (9 errors out of 15 calls).

### 5. Account creation with wrong `type` enum (annual accounts, tx_id 30)

FallbackAgent tries `"type": "BALANCE"`, `"ASSET"`, `"RESULT"`, `"EXPENSE"` — none valid. The API doesn't accept string type enums in account creation. Seen in L205.

## Improvement Recommendations — By Expected Score Gain

| Priority | tx_task_id | Task                       | Current | Max  | Potential Gain | Fix Description                                                                                                                                                         |
| -------- | ---------- | -------------------------- | ------- | ---- | -------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **1**    | 24         | Ledger correction          | 0.00    | ~4.0 | **+4.0**       | Dedicated handler: read all vouchers+postings, match error patterns from prompt, create correction vouchers. Already has successful GETs, just needs proper write logic |
| **2**    | 25         | Overdue invoice + reminder | 0.00    | ~4.0 | **+4.0**       | Dedicated handler: search invoices with `invoiceDateFrom=2020-01-01&invoiceDateTo=today`, filter overdue, register payment + reminder voucher                           |
| **3**    | 23         | Bank reconciliation        | 0.00    | ~4.0 | **+4.0**       | Fix CSV import: detect/convert CSV format to match Tripletex expected columns, or use manual posting approach instead of bank import                                    |
| **4**    | 22         | Voucher (receipt)          | 0.00    | ~4.0 | **+4.0**       | Fix VoucherHandler: extract numeric account from PDF, handle department lookup properly. Check L215 (en) scored 4/6 vs L207 (pt) scored 0/5                              |
| **5**    | 11         | Supplier invoice           | 0.00    | ~3.0 | **+3.0**       | Not tested in this run — historically broken. Need to investigate                                                                                                       |
| **6**    | 26         | ???                        | 0.00    | ~4.0 | **+4.0**       | Unknown task type — agent returned 0 tasks (missed prompt). Need to capture and investigate the prompt                                                                  |
| **7**    | 30         | Annual accounts            | 1.80    | ~4.0 | **+2.2**       | ~~Was 0.00~~ now scores 1.80. Fix `amountGrossCurrency` (number not string), pre-create accounts properly                                                              |
| **8**    | 28         | Cost analysis              | 1.50    | ~4.0 | **+2.5**       | ~~Was 0.00~~ now scores 1.50. Fix FallbackAgent: always include `projectManager` in project creation                                                                   |
| **9**    | 21         | Employee (offer PDF)       | 1.50    | ~4.0 | **+2.5**       | ~~Was 0.00~~ now scores 1.50. Passed 5/10 checks. Need to identify which 5 checks failed                                                                               |
| **10**   | 12         | Payroll                    | 1.00    | ~3.0 | **+2.0**       | Fix org number handling — don't put org number in employee creation; fix "Juridisk enhet" error                                                                         |

## Quick Wins (FallbackAgent fixes)

These failures all stem from the FallbackAgent making basic mistakes. Adding tool-use constraints or dedicated handlers would eliminate most errors:

1. **Always include `amountGrossCurrency` as a number = `amountGross`** when posting vouchers (eliminates ~35 errors/run)
2. **Always include `projectManager` when creating projects** (eliminates ~9 errors/run)
3. **Always include `invoiceDateFrom`/`invoiceDateTo`** when searching invoices (eliminates ~6 errors/run)
4. **Never set account `type` as a string enum** — Tripletex creates accounts without the type field (eliminates ~4 errors/run)

## Theoretical Maximum Gain

If all 6 zero-score tasks were fixed to full correctness: **+23.0 points** (tx11 ~3.0 + tx22 ~4.0 + tx23 ~4.0 + tx24 ~4.0 + tx25 ~4.0 + tx26 ~4.0)
Current best: **38.24** → Potential: **~69.24**
