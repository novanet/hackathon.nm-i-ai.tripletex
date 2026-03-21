# Task 20 — Create Voucher from PDF Supplier Invoice (Tier 3)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 20 |
| **Task Type** | `create_voucher` |
| **Variant** | Supplier invoice from PDF (Tier 3) |
| **Tier** | 3 |
| **Our Score** | 0.60 |
| **Leader Score** | 6.00 |
| **Gap** | -5.40 |
| **Status** | ❌ Failing — large gap |
| **Handler** | `PdfVoucherHandler.cs` → `VoucherHandler.cs` → `HandleSupplierInvoice` |
| **Priority** | #9 — MEDIUM effort, high potential gain |

## What It Does

Extract supplier invoice data from an attached PDF file, then create a voucher with supplier, expense account postings, and creditor counter-posting. Same as Task 11 but data comes from PDF instead of prompt text.

## API Flow

Same as Task 11 (once data is extracted):

1. `POST /supplier`
2. `GET /ledger/account` — resolve expense account
3. `GET /ledger/vatType` — resolve input VAT
4. `GET /ledger/voucherType`
5. `GET /ledger/account?number=2400` — creditor account
6. `POST /ledger/voucher?sendToLedger=true`

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `voucher_found` | 2 | ⚠️ Partial |
| `has_description` | 2 | ⚠️ |
| `has_postings` | 2 | ⚠️ |
| `correct_accounts` | 2 | ❌ |
| `correct_amount` | 3 | ❌ |

## Why We Score 0.60

Score 0.60/6.00 = ~10% correctness. The PDF extraction is producing data but likely:

1. **`supplierName`/`supplierOrgNumber` not extracted** from PDF → falls through to generic voucher path instead of `HandleSupplierInvoice`
2. **Amount extraction wrong** — PDF may have multiple amounts (subtotal, VAT, total) and LLM picks the wrong one
3. **Account number wrong** — expense account may not be extracted from PDF
4. **Postings structure wrong** — generic path vs supplier invoice path produces different posting structure

## Root Cause Investigation

The `PdfVoucherHandler` simply normalizes `task_type` to `create_voucher` and delegates. The critical question is whether the LLM, given PDF text content, extracts:
- `voucher.supplierName` → triggers `HandleSupplierInvoice`
- `voucher.supplierOrgNumber`
- `voucher.account` (expense account number)
- `voucher.amount` (gross amount)
- `voucher.invoiceNumber`

If any of these are missing, the handler falls through to the wrong branch.

## How to Fix

1. **Replay with saved PDF** — must find the file in `src/logs/files/`
2. **Check extraction** — does `supplierName` populate?
3. **Improve LLM prompt** for PDF supplier invoices — add specific instructions for extracting supplier name, org number, invoice number, amount, expense account from PDF invoices
4. **Verify the handler branch** — add logging to confirm `HandleSupplierInvoice` is reached
5. **Fix 10 (VAT lock detection)** should also help here

## Effort

**MEDIUM** — PDF extraction debugging, possibly LLM prompt improvements.

## Action Required

- [ ] Find saved PDF in `src/logs/files/`
- [ ] Replay locally with `-FilePaths`
- [ ] Check extraction output for supplier fields
- [ ] Improve LLM extraction for PDF invoices
- [ ] Verify `HandleSupplierInvoice` triggers
- [ ] Submit to verify improvement
