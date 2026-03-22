# Task 22 — Create Voucher from PDF Receipt (Tier 3)

## Overview

| Field            | Value                                                    |
| ---------------- | -------------------------------------------------------- |
| **Task ID**      | 22                                                       |
| **Task Type**    | `create_voucher`                                         |
| **Variant**      | Voucher from PDF receipt                                 |
| **Tier**         | 3                                                        |
| **Our Score**    | 0.00                                                     |
| **Leader Score** | 0.00                                                     |
| **Gap**          | 0 (both fail)                                            |
| **Status**       | ⚠ Competition still unverified after latest local fix    |
| **Handler**      | `VoucherHandler.cs` (supplier-invoice path)              |
| **Priority**     | LOW for leaderboard gap, still worth verifying after fix |

## What It Does

Multi-language prompt (DE/EN/NN/PT): extract receipt data from an attached PDF and create a supplier-style voucher with expense posting, VAT handling, and optional department tagging.

Variants seen:

1. German: headset receipt → department Salg/Kvalitetskontroll
2. English: train ticket receipt → department Produksjon
3. Portuguese: coffee receipt → department HR
4. Nynorsk: various receipts → various departments

## API Flow

1. `POST /supplier` — create supplier from receipt
2. `GET /department?count=1000` — resolve department by name
3. `POST /department` — only when the requested department does not already exist
4. `GET /ledger/account?number=XXXX` — resolve expense account
5. `GET /ledger/vatType?number=1` — resolve input VAT type
6. `GET /ledger/voucherType?name=Leverandørfaktura` — resolve voucher type
7. `GET /ledger/account?number=2400` — resolve creditor account
8. `POST /ledger/voucher?sendToLedger=true` — create double-entry voucher
9. `GET /ledger/voucher/{id}` — sandbox-only validation fetch

## Competition Checks

| Check             | Points |     Status     |
| ----------------- | :----: | :------------: |
| `voucher_found`   |   —    | ❌ competition |
| `has_description` |   —    | ❌ competition |
| `has_postings`    |   —    | ❌ competition |

Observed local receipt-specific checks now mirrored by `SandboxValidator`:

- `postings_balanced`
- `correct_accounts`
- `has_supplier_reference`
- `has_invoice_reference`
- `has_department`
- `correct_amount`

## Current State (2026-03-22)

`Refresh-Tasks.ps1` now includes fresh 2026-03-22 sandbox replays that verify the current task-22 behavior, not just the older amount-normalization fix. The two most relevant replays are:

- Nynorsk `USB-hub` / `Kvalitetskontroll`: replayed with the saved `kvittering_nn_03.pdf`, created a supplier-style voucher with top-level description `USB-hub`, posting-level `description`, posting-level `invoiceNumber`, supplier references, department on the expense leg, and validated at `19/19`.
- Nynorsk `Oppbevaringsboks` / `Regnskap`: replayed with the saved `kvittering_nn_06.pdf`, created a supplier-style voucher with top-level description `Oppbevaringsboks`, posting-level `description`, posting-level `invoiceNumber`, supplier references, department on the expense leg, and validated at `19/19`.

This matters because the old local-success story was incomplete. The handler now covers both the exact receipt shape and the previously observed extraction-shape drift that could send a single receipt into the generic `Bilag` path.

Latest important runs:

- Competition 2026-03-21 21:05:47: success, 7 calls, 0 errors, extracted `6540.00`
- Sandbox 2026-03-21 22:27:36: success, 8 calls, 0 errors, exact prompt replay reached `19/19` but still used `6250.00`
- Sandbox 2026-03-21 22:39:09: success, 8 calls, 0 errors, extractor normalization first forced `6540.00` but exposed a locale bug that posted `654000`
- Sandbox 2026-03-21 22:41:55: success, 8 calls, 0 errors, exact prompt replay now extracts and posts `6540.00` correctly and validates at `19/19`
- Sandbox 2026-03-22 11:17:31: success, 9 calls, 1 expected 403 on `incomingInvoice/search`, `USB-hub` receipt replay validates at `19/19` with item-style description and posting invoice references
- Sandbox 2026-03-22 11:17:41: success, 9 calls, 1 expected 403 on `incomingInvoice/search`, `Oppbevaringsboks` receipt replay validates at `19/19` and no longer depends on the generic numbered-voucher path

This means the local implementation is no longer just "sandbox passing" in a loose sense. The exact competition-style prompt has been replayed with its saved PDF, the amount drift has been reproduced and fixed, and the validator has been tightened to check receipt-specific structure.

Competition still shows 0.00 for both teams, so the remaining unknown is not the local fix itself but whether the competition validator now accepts the corrected voucher shape and amount consistently.

## Root Cause

The task-22 failure mode turned out to be two separate issues, not just amount drift.

### 1. Receipt amount drift

The first confirmed issue was extraction instability on receipt PDFs:

- `PdfPig` text extraction was deterministic for the exact file `kvittering_nn_03.pdf`
- the extracted text included both line items and a labeled total: `USB-hub6250.00`, `Kontorstoler290.00`, `Totalt:6540.00`, `herav MVA 25%:1635.00`
- GPT-4o sometimes chose the line-item amount `6250.00` and sometimes the labeled total `6540.00`, even with `Temperature = 0`

### 2. Receipt-shape drift inside `create_voucher`

The later and more concrete issue was that some single receipts were extracted as `voucher1` instead of `voucher`. When that happened, `VoucherHandler` used the generic multi-voucher logic and created a plain `Bilag` with no supplier-style metadata.

Even when the receipt stayed on the supplier path, the posted voucher description had drifted back to the receipt number (`KVITTERING ...` or `01.01.2026`) instead of the requested expense item from the prompt (`USB-hub`, `Oppbevaringsboks`, `Togbillett`, etc.). That made the local voucher look structurally correct while still being a weak match for any competition search keyed on the actual requested expense.

## Fixes Implemented

### 1. Receipt amount normalization in `LlmExtractor`

`LlmExtractor.NormalizeFileBasedVoucherAmounts()` now runs after structured extraction for file-based vouchers. It scans the extracted PDF text for labeled totals such as `Totalt`, `Total`, `Sum`, `Gesamt`, and normalizes the voucher amount to that total before handler execution.

This makes the receipt amount deterministic even when the model initially picks a line item instead of the final total.

### 2. Invariant formatting fix for normalized amounts

The first normalization attempt wrote a decimal-like object into the extraction dictionary. That later flowed through generic string parsing and produced locale-formatted `6540,00`, which `ExtractAmount()` interpreted as `654000` under invariant parsing.

The final fix stores the normalized amount as the invariant string `"6540.00"`.

### 3. Receipt-specific validator tightening

`SandboxValidator.ValidateVoucher()` was expanded beyond generic voucher checks. For receipt-style vouchers it now fetches and validates:

- `externalVoucherNumber`
- posting `invoiceNumber`
- posting `supplier`
- department on the expense posting specifically
- gross or net amount depending on how VAT-split postings are returned

This brings the local validator much closer to what the competition is likely checking for task 22.

### 4. Numbered receipt routing + item descriptions

`VoucherHandler.HandleAsync()` now detects the case where there is exactly one numbered voucher entity and it contains supplier-style receipt fields. That case is routed to `HandleSupplierInvoice()` instead of `HandleMultiVoucher()`.

`HandleSupplierInvoice()` now also writes posting-level `description` and posting-level `invoiceNumber` on the created supplier voucher postings.

`LlmExtractor.NormalizeFileBasedVoucherAmounts()` now also derives a `receiptDescription` for task-22-style receipts from the prompt and file text, so the posted voucher description becomes the requested item (`USB-hub`, `Oppbevaringsboks`) rather than the receipt number.

## Current Handler Behavior

The operative path is still `VoucherHandler.HandleSupplierInvoice()`:

- normalizes text-like expense accounts via `NormalizeAccountNumber()`
- resolves or creates missing departments via `ResolveDepartmentIdAsync()`
- posts a classic `Leverandørfaktura` voucher with supplier reference, `vendorInvoiceNumber`, and `externalVoucherNumber`
- writes posting-level `description` and `invoiceNumber` on the created voucher lines
- applies the department to the expense posting

The current remaining unknown is only competition behavior. Locally, the handler now covers both amount normalization and receipt-shape drift for the observed task-22 variants.

## Action Required

- [ ] Submit a fresh competition run now that both the exact `USB-hub` prompt and the previously misrouted `Oppbevaringsboks` prompt replay locally at `19/19` with item-style descriptions
- [ ] Compare competition task-22 checks against the stricter local validator after that submission
- [ ] If competition still scores 0, inspect whether the competition rejects the voucher for a field we still do not mirror locally
- [ ] Keep this task lower priority than positive-gap tasks, since both teams are still at 0
