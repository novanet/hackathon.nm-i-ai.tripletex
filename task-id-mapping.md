# Leaderboard Task ID → Submission Task Type Mapping

_Generated 2026-03-21 by correlating `last_attempt_at` timestamps from `leaderboard.jsonl` with `timestamp` from `submissions.jsonl`._

## Summary

- **30 task IDs** visible in leaderboard
- **13 distinct task types** in our submission logs
- Same task type appears under **multiple IDs** — these are different prompt variants (different complexity/language/requirements)
- **Current total best score: 38.24**

## Complete Mapping

| tx_task_id | task_type               | Variant Description                        | Best Score | Max Possible | Attempts |   Status   |
| :--------: | ----------------------- | ------------------------------------------ | :--------: | :----------: | :------: | :--------: |
|     01     | `create_employee`       | Basic employee creation                    |    1.50    |     ~3.0     |    11    | ⚠️ Partial |
|     02     | `create_customer`       | Standard customer with address             |    2.00    |     2.0      |    12    |  ✅ Full   |
|     03     | `create_product`        | Product with number + price + VAT          |    2.00    |     2.0      |    10    |  ✅ Full   |
|     04     | `create_supplier`       | Supplier with org number + email           |    2.00    |     2.0      |    9     |  ✅ Full   |
|     05     | `create_department`     | Multiple departments (3 at once)           |    1.33    |     2.0      |    10    | ⚠️ Partial |
|     06     | `create_invoice`        | Simple single-line invoice                 |    1.33    |     ~2.0     |    10    | ⚠️ Partial |
|     07     | `register_payment`      | Simple payment on existing invoice         |    2.00    |     2.0      |    11    |  ✅ Full   |
|     08     | `create_project`        | Basic project with customer + PM           |    1.50    |     ~3.0     |    10    | ⚠️ Partial |
|     09     | `create_invoice`        | Multi-line invoice (different VAT rates)   |    2.67    |     ~4.0     |    9     | ⚠️ Partial |
|     10     | `register_payment`      | Create order + invoice + pay               |    2.67    |     ~4.0     |    13    | ⚠️ Partial |
|     11     | `create_voucher`        | Supplier invoice voucher (incoming)        |  **0.00**  |     ~3.0     |    10    | ❌ Failing |
|     12     | `run_payroll`           | Standard payroll run                       |    1.00    |     ~3.0     |    17    | ⚠️ Partial |
|     13     | `create_travel_expense` | Travel expense with costs + per diem       |    2.50    |     ~3.0     |    11    | ⚠️ Partial |
|     14     | `create_credit_note`    | Credit note on existing invoice            |    2.67    |     ~3.0     |    14    | ⚠️ Partial |
|     15     | `create_project`        | Fixed-price project                        |    1.50    |     ~3.0     |    12    | ⚠️ Partial |
|     16     | `create_project`        | Timesheet hours → project invoice          |    2.67    |     ~4.0     |    9     | ⚠️ Partial |
|     17     | `create_voucher`        | Custom dimension + voucher posting         |    3.50    |     ~4.0     |    12    | ⚠️ Partial |
|     18     | `register_payment`      | Full payment flow (high scorer)            |  **4.00**  |     4.0      |    12    |  ✅ Full   |
|     19     | `create_employee`       | Employee from PDF contract (Tier 3)        |    1.77    |     ~4.0     |    1     | ⚠️ Partial |
|     20     | `create_voucher`        | Supplier invoice from PDF (Tier 3)         |    0.60    |     ~4.0     |    1     | ⚠️ Partial |
|     21     | `create_employee`       | Employee from PDF offer letter (Tier 3)    |  **0.00**  |     ~4.0     |    2     | ❌ Failing |
|     22     | `create_voucher`        | Voucher from PDF receipt (Tier 3)          |  **0.00**  |     ~4.0     |    1     | ❌ Failing |
|     23     | `bank_reconciliation`   | CSV bank statement matching (Tier 3)       |  **0.00**  |     ~4.0     |    3     | ❌ Failing |
|     24     | `create_voucher`        | Ledger correction (Tier 3)                 |  **0.00**  |     ~4.0     |    1     | ❌ Failing |
|     25     | `register_payment`      | Find overdue invoice + pay (Tier 3)        |  **0.00**  |     ~4.0     |    2     | ❌ Failing |
|     26     | `annual_accounts`       | Monthly close / month-end closing (Tier 3) |  **2.55**  |     ~6.0     |    4     | ⚠️ Partial |
|     27     | `register_payment`      | Foreign currency (EUR) invoice (Tier 3)    |    0.60    |     ~4.0     |    4     | ⚠️ Partial |
|     28     | `create_project`        | Cost analysis from ledger (Tier 3)         |  **0.00**  |     ~4.0     |    2     | ❌ Failing |
|     29     | `create_project`        | Full project lifecycle (Tier 3)            |    1.09    |     ~4.0     |    2     | ⚠️ Partial |
|     30     | `create_voucher`        | Simplified annual accounts (Tier 3)        |  **0.00**  |     ~4.0     |    1     | ❌ Failing |

## Task Types with Multiple IDs (Prompt Variants)

Each task type has multiple variants of increasing complexity:

| Task Type               | IDs                    | Count | Combined Score |
| ----------------------- | ---------------------- | :---: | :------------: |
| `create_employee`       | 01, 19, 21             |   3   |      3.27      |
| `create_customer`       | 02                     |   1   |      2.00      |
| `create_product`        | 03                     |   1   |      2.00      |
| `create_supplier`       | 04                     |   1   |      2.00      |
| `create_department`     | 05                     |   1   |      1.33      |
| `create_invoice`        | 06, 09                 |   2   |      4.00      |
| `register_payment`      | 07, 10, 18, 25, 27     |   5   |      9.27      |
| `create_project`        | 08, 15, 16, 28, 29     |   5   |      5.76      |
| `create_voucher`        | 11, 17, 20, 22, 24, 30 |   6   |      4.10      |
| `run_payroll`           | 12                     |   1   |      1.00      |
| `create_travel_expense` | 13                     |   1   |      2.50      |
| `create_credit_note`    | 14                     |   1   |      2.67      |
| `bank_reconciliation`   | 23                     |   1   |      0.00      |

## Zero-Score Tasks (Priority Fixes)

These 8 tasks score 0.00 and represent the biggest opportunity for improvement:

| ID  | Type                  | Variant            | Prompt Preview                                               | Issue                                                   |
| :-: | --------------------- | ------------------ | ------------------------------------------------------------ | ------------------------------------------------------- |
| 11  | `create_voucher`      | Supplier invoice   | "Nous avons reçu la facture INV-2026-5683 du fournisseur..." | Voucher postings failing — supplier invoice flow broken |
| 21  | `create_employee`     | PDF offer letter   | "Voce recebeu uma carta de oferta (ver PDF anexo)..."        | PDF extraction not working for this variant             |
| 22  | `create_voucher`      | PDF receipt        | "Wir benotigen die Headset-Ausgabe aus dieser Quittung..."   | PDF extraction not extracting voucher data              |
| 23  | `bank_reconciliation` | CSV bank statement | "Avstem bankutskrifta (vedlagt CSV) mot opne fakturaer..."   | Bank reconciliation handler not working                 |
| 24  | `create_voucher`      | Ledger correction  | "Wir haben Fehler im Hauptbuch für Januar und Februar..."    | Requires reading existing ledger + correcting           |
| 25  | `register_payment`    | Find overdue       | "L'un de vos clients a une facture en retard. Trouvez..."    | Needs to search for overdue invoice first               |
| 28  | `create_project`      | Cost analysis      | "Totalkostnadene auka monaleg frå januar til februar..."     | Requires ledger analysis, not simple creation           |
| 30  | `create_voucher`      | Annual accounts    | "Gjer forenkla årsoppgjer for 2025: 1) Rekn ut og..."        | Complex multi-step accounting workflow                  |

## Tier Analysis

| Tier                      | IDs   | Total Score | Max Possible (est.) | Coverage |
| ------------------------- | ----- | :---------: | :-----------------: | :------: |
| **Tier 1** (basic)        | 01–05 |    8.17     |        ~11.0        |   74%    |
| **Tier 2** (multi-step)   | 06–18 |    25.84    |        ~43.0        |   60%    |
| **Tier 3** (advanced/PDF) | 19–30 |    4.06     |        ~44.0        |    9%    |

## Methodology

Mapping was determined by correlating `last_attempt_at` timestamps in leaderboard snapshots (UTC+1) with `timestamp` values in submissions.jsonl (UTC). Matches within 30 seconds of each other are considered confirmed. Tasks 12, 14, and 18 were identified by tracking when their attempt counts changed across leaderboard snapshots and finding the corresponding submission at that exact time.

### Confidence Levels

- **Confirmed** (< 5s timestamp diff): 01, 02, 03, 04, 05, 07, 08, 09, 10, 11, 13, 15, 16, 17, 19, 20, 21, 22, 23, 24, 25, 27, 28, 29, 30
- **Confirmed by progression** (score changed at matching timestamp): 12 (run_payroll), 14 (create_credit_note)
- **Probable** (< 15s diff, matches context): 06 (create_invoice), 18 (register_payment)
- **Unknown**: 26 (not in leaderboard)
