# Improvement Analysis — Batch 3: Runs 109–118 (22:11–22:20)

Daily usage after batch: 118 / 180

## Summary Table

| Run | Task Type          | Score   | Checks | Calls | Errors | Lang | Norm | Duration |
| --- | ------------------ | ------- | ------ | ----- | ------ | ---- | ---- | -------- |
| 109 | create_invoice     | **7/7** | 5/5    | 7     | 0      | nb   | 1.50 | 57.5s    |
| 110 | run_payroll        | **0/8** | 0/4    | 10    | 0      | nb   | 0.00 | 116.3s   |
| 111 | create_product     | **7/7** | 5/5    | 2     | 0      | en   | 2.00 | 23.9s    |
| 112 | create_employee    | **8/8** | 7/7    | 3     | 0      | es   | 1.50 | 39.0s    |
| 113 | create_department  | **7/7** | 3/3    | 4     | 0      | nb   | 1.33 | 34.2s    |
| 114 | create_customer    | **8/8** | 7/7    | 1     | 0      | nb   | 2.00 | 11.1s    |
| 115 | create_credit_note | **8/8** | 5/5    | 6     | 0      | es   | 2.67 | 31.4s    |
| 116 | register_payment   | **8/8** | 3/3    | 4     | 0      | en   | 4.00 | 33.4s    |
| 117 | create_product     | **7/7** | 5/5    | 2     | 0      | nb   | 2.00 | 14.1s    |
| 118 | create_voucher     | **0/8** | 0/4    | 6     | 0      | fr   | 0.00 | 31.6s    |

**Perfect runs**: 109, 111, 112, 113, 114, 115, 116, 117 (8/10 = 80%)
**Full failures**: 110 (run_payroll), 118 (create_voucher)
**Total raw**: 60/77

---

## Prompt Details

| Run | Prompt (excerpt)                                                                                                                      |
| --- | ------------------------------------------------------------------------------------------------------------------------------------- |
| 109 | "Opprett og send en faktura til kunden Bergvik AS (org.nr 890733751) på 28900 kr eksklusiv MVA. Fakturaen gjelder Systemutvikling..." |
| 110 | "Kjør lønn for Marte Ødegård (marte.degard@example.org) for denne måneden. Grunnlønn er 48650 kr. Legg til en engangsbonu..."         |
| 111 | "Create the product 'Training Session' with product number 7908. The price is 26250 NOK excluding VAT, using the standard..."         |
| 112 | "Tenemos un nuevo empleado llamado Lucía Rodríguez, nacido el 30. April 1996. Créelo como empleado con el correo lucia.ro..."         |
| 113 | "Create three departments in Tripletex: 'Produksjon', 'Kundeservice', and 'Økonomi'."                                                 |
| 114 | "Opprett kunden Havbris AS med organisasjonsnummer 946293199. Adressen er Sjøgata 112, 4611 Kristiansand. E-post: post@ha..."         |
| 115 | "El cliente Viento SL (org. nº 857019199) ha reclamado sobre la factura por 'Informe de análisis' (27200 NOK sin IVA). Em..."         |
| 116 | "The payment from Brightstone Ltd (org no. 993125393) for the invoice 'Data Advisory' (27100 NOK excl. VAT) was returned..."          |
| 117 | "Opprett produktet 'Webdesign' med produktnummer 4823. Prisen er 32250 kr eksklusiv MVA, og standard MVA-sats på 25 % ska..."         |
| 118 | "Nous avons reçu la facture INV-2026-6571 du fournisseur Prairie SARL (nº org. 965215727) de 30100 NOK TTC. Le montant co..."         |

---

## Failure Analysis

### P0: run_payroll — 0/8, 0/4 checks (Run 110)

- **Prompt**: Norwegian, Marte Ødegård, base salary 48650 kr + one-time bonus
- **Handler**: PayrollHandler — reported `success=true`, 10 API calls, 0 errors
- **Competition checks**: salary_transaction_found, has_employee_link, payslip_generated, correct_amount — ALL failed
- **Pattern**: This is the 2nd time `run_payroll` scores 0/8. All 4 checks failing means either:
  1. No salary transaction was created/persisted at all, or
  2. The transaction structure is fundamentally wrong (wrong endpoint, wrong entity type)
- **Duration**: 116s — the longest run by far, suggesting the handler is doing many slow operations
- **Hypothesis**: The PayrollHandler may be creating salary specifications but never running the actual payroll processing step that generates the salary transaction and payslip. The competition likely checks for a finalized/processed payroll transaction, not just salary specs.
- **Fix needed**: Investigate PayrollHandler flow — ensure it calls the payroll processing/closing endpoint after creating salary specifications. Need to verify the full payroll lifecycle: create salary spec → process payroll → generate payslip.

### P1: create_voucher — 0/8, 0/4 checks (Run 118)

- **Prompt**: French, supplier invoice from Prairie SARL, 30100 NOK TTC (incl. VAT)
- **Handler**: VoucherHandler — reported `success=true`, 6 API calls, 0 errors
- **Competition checks**: voucher_found, has_description, has_postings (≥ 2) — ALL failed
- **Pattern**: This is the 2nd competition voucher failure. Previous failure was Batch 2 Run 104 (Spanish).
- **vatLocked fix** (commit d15bc1b) was deployed for this batch but **still failed** — the fix didn't solve the core problem.
- **Key observation**: Handler reports success with 6 calls and 0 errors. If the voucher was created successfully, the competition would at least pass `voucher_found`. All 4 checks failing suggests the voucher either:
  1. Was created with wrong `date` (competition may search by date range), or
  2. Was created with wrong `ledgerType` (wrong financial journal), or
  3. The `description` field is missing or in wrong location, or
  4. Postings are structurally wrong (missing required fields like amountCurrency)
- **Both failures were non-Norwegian languages** (es, fr) — possible LLM extraction issue with field mapping in these languages
- **Fix needed**: Deep investigation of VoucherHandler output — need to compare what's actually sent to the API vs what competition expects. Check if `date`, `description`, `ledgerType`, and posting `amountCurrency` values are correct.

---

## Reliably Passing Tasks (competition-confirmed)

| Task                 | Batch 3 Scores | Calls | Best Norm | Cumulative Record               |
| -------------------- | -------------- | ----- | --------- | ------------------------------- |
| `register_payment`   | 8/8            | 4     | 4.00      | 3/3 perfect                     |
| `create_credit_note` | 8/8            | 6     | 2.67      | **NEW** — 1st comp confirmation |
| `create_customer`    | 8/8            | 1     | 2.00      | 2/2 perfect                     |
| `create_employee`    | 8/8            | 3     | 1.50      | 3/3 perfect                     |
| `create_product`     | 7/7, 7/7       | 2     | 2.00      | 4/4 perfect                     |
| `create_invoice`     | 7/7            | 7     | 1.50      | 2/2 perfect (single-rate)       |
| `create_department`  | 7/7            | 4     | 1.33      | 2/2 perfect                     |

### New Confirmation: `create_credit_note` 8/8 ✅

First time tested in competition — **5/5 checks passed, 8/8 score, norm 2.67**. The CreditNoteHandler is working correctly. This adds 8 points to our max potential.

---

## Efficiency Observations

| Task                 | Calls | Theoretical Min | Gap | Notes                                   |
| -------------------- | ----- | --------------- | --- | --------------------------------------- |
| `create_customer`    | 1     | 1               | 0   | Optimal — single POST                   |
| `create_product`     | 2     | 1               | 1   | Likely: GET vatType + POST product      |
| `create_employee`    | 3     | 2               | 1   | POST employee + PUT entitlement + ?     |
| `register_payment`   | 4     | 4               | 0   | Optimal for payment chain               |
| `create_department`  | 4     | 3               | 1   | 3 depts = 3 POSTs + 1 extra             |
| `create_credit_note` | 6     | 5               | 1   | Customer→Order→Invoice→CreditNote chain |
| `create_invoice`     | 7     | 5               | 2   | Customer→VatType→Order→Invoice→Send     |
| `run_payroll`        | 10    | ?               | ?   | Broken — high call count for no result  |
| `create_voucher`     | 6     | 3               | 3   | Broken — calls wasted                   |

---

## Cross-Batch Consistency (Batches 1-3)

| Task Type          | B1       | B2       | B3       | Status                                |
| ------------------ | -------- | -------- | -------- | ------------------------------------- |
| create_employee    | 8/8      | 8/8      | 8/8      | ✅ Solid                              |
| create_customer    | —        | —        | 8/8      | ✅ (1 sample)                         |
| create_supplier    | 0/7, 6/7 | 6/7      | —        | ⚠️ Fixed but untested                 |
| create_product     | 5/7      | 7/7, 7/7 | 7/7, 7/7 | ✅ Fixed & solid                      |
| create_department  | 7/7      | —        | 7/7      | ✅ Solid                              |
| create_project     | 2-8/8    | 8/8, 8/8 | —        | ✅ Fixed & solid                      |
| create_invoice     | 5/8      | 7/7      | 7/7      | ✅ (single-rate; multi-rate untested) |
| register_payment   | 7/7      | —        | 8/8      | ✅ Solid                              |
| create_credit_note | —        | —        | 8/8      | ✅ NEW                                |
| create_voucher     | —        | 0/8      | 0/8      | ❌ Fix didn't work                    |
| run_payroll        | —        | —        | 0/8      | ❌ Fundamentally broken               |
| create_travel_exp  | —        | 0/8      | —        | ❌ Not retested                       |

---

## Priority Fix Order

### 1. **PayrollHandler** (P0) — 0/8, potential +8 points

All 4 competition checks fail. The handler runs 10 API calls but nothing is found by the competition validator. This is likely a missing "process/close payroll" step. Needs deep investigation of:

- What endpoints the handler actually calls
- Whether salary transactions are being finalized
- Whether the payslip generation step exists

### 2. **VoucherHandler** (P0) — 0/8, potential +8 points

The vatLocked fix (d15bc1b) didn't solve it. Two consecutive 0/4 failures in competition despite handler reporting success. Need to:

- Check what the handler actually POSTs to the API (log the request body)
- Verify `ledgerType` value (must match journal type)
- Verify `date` format and value
- Verify posting structure (debit/credit accounts, amounts)
- Test with the exact French prompt locally

### 3. **Invoice multi-rate VAT** (P2) — last tested Batch 1 at 5/8

Not tested this batch, but still a known gap. When the prompt specifies multiple VAT rates (15% food, 0% exempt), the handler maps to wrong VAT type IDs.

### 4. **Travel expense** (P3) — last tested Batch 2 at 0/8

Previously attributed to token expiry (P6 in improvements.md), but only 1 data point. Needs a clean retest to determine if handler logic is actually broken.

### 5. **Supplier phoneNumber** (P4) — consistent 6/7

Low priority, consistent 1-point loss. Competition checks `phoneNumber` even when not in prompt.

---

## Score Potential

**Currently confirmed passing** (best scores):

- create_employee: 8
- create_customer: 8
- create_credit_note: 8
- register_payment: 8
- create_product: 7
- create_invoice: 7
- create_department: 7
- create_project: 8
- **Subtotal: 61 points**

**If fixed**:

- run_payroll: +8
- create_voucher: +8
- create_travel_expense: +8
- create_supplier: +7 (already mostly working)
- Invoice multi-rate: +1 (5→8, net +3 over current 7)
- **Potential total: ~88 points** from 12 task types

**Untested task types** (additional potential): create_contact, delete_entity, enable_module, update tasks — all routed to fallback or dedicated handlers but never confirmed in competition.
