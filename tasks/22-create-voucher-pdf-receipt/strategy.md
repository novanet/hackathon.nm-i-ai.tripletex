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
| **Status**       | ❌ Both fail in competition — local fix verified         |
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

## Current State (2026-03-21)

`Refresh-Tasks.ps1` now shows 25 total runs for this task: 4 competition and 21 sandbox. The latest sandbox replay is the exact Nynorsk `Kvalitetskontroll` receipt prompt at 2026-03-21 22:41:55, and it succeeds with 8 calls, 0 errors, extracted amount `6540.00`, and a local validation score of `19/19`.

Latest important runs:

- Competition 2026-03-21 21:05:47: success, 7 calls, 0 errors, extracted `6540.00`
- Sandbox 2026-03-21 22:27:36: success, 8 calls, 0 errors, exact prompt replay reached `19/19` but still used `6250.00`
- Sandbox 2026-03-21 22:39:09: success, 8 calls, 0 errors, extractor normalization first forced `6540.00` but exposed a locale bug that posted `654000`
- Sandbox 2026-03-21 22:41:55: success, 8 calls, 0 errors, exact prompt replay now extracts and posts `6540.00` correctly and validates at `19/19`

This means the local implementation is no longer just "sandbox passing" in a loose sense. The exact competition-style prompt has been replayed with its saved PDF, the amount drift has been reproduced and fixed, and the validator has been tightened to check receipt-specific structure.

Competition still shows 0.00 for both teams, so the remaining unknown is not the local fix itself but whether the competition validator now accepts the corrected voucher shape and amount consistently.

## Root Cause

The main bug was not the voucher POST path. `VoucherHandler` was already able to create the voucher without API errors.

The real issue was extraction instability on receipt PDFs:

- `PdfPig` text extraction was deterministic for the exact file `kvittering_nn_03.pdf`
- the extracted text included both line items and a labeled total: `USB-hub6250.00`, `Kontorstoler290.00`, `Totalt:6540.00`, `herav MVA 25%:1635.00`
- GPT-4o sometimes chose the line-item amount `6250.00` and sometimes the labeled total `6540.00`, even with `Temperature = 0`

That mismatch was the most likely reason a competition run could succeed operationally while still failing validator checks.

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

## Current Handler Behavior

The operative path is still `VoucherHandler.HandleSupplierInvoice()`:

- normalizes text-like expense accounts via `NormalizeAccountNumber()`
- resolves or creates missing departments via `ResolveDepartmentIdAsync()`
- posts a classic `Leverandørfaktura` voucher with supplier reference and `externalVoucherNumber`
- applies the department to the expense posting

The handler itself is stable. The recent work was about making the extracted input deterministic and the validator stricter.

## Action Required

- [ ] Submit a fresh competition run now that the exact prompt replays locally at `19/19` with `6540.00`
- [ ] Compare competition task-22 checks against the stricter local validator after that submission
- [ ] If competition still scores 0, inspect whether the competition rejects the voucher for a field we still do not mirror locally
- [ ] Keep this task lower priority than positive-gap tasks, since both teams are still at 0
