# Task 22 — Create Voucher from PDF Receipt (Tier 3)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 22 |
| **Task Type** | `create_voucher` |
| **Variant** | Voucher from PDF receipt (German) |
| **Tier** | 3 |
| **Our Score** | 0.00 |
| **Sandbox Score** | 15/15 (verified 2026-03-21) |
| **Leader Score** | 0.00 |
| **Gap** | 0 (both fail) |
| **Status** | Sandbox fixed, competition still unverified |
| **Handler** | `PdfVoucherHandler.cs` → `VoucherHandler.cs` |
| **Priority** | LOW — no competitive advantage |

## What It Does

German prompt: "Wir benötigen die Headset-Ausgabe aus dieser Quittung..." — extract receipt data from PDF and create a voucher for the expense.

## API Flow

Similar to Task 11/20 — extract supplier/expense/amount from PDF receipt, create voucher with double-entry postings.

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `voucher_found` | — | ❌ |
| `has_description` | — | ❌ |
| `has_postings` | — | ❌ |

## Current State

Sandbox replay is now passing at full correctness. On 2026-03-21 the current handler scored 15/15 locally on all three real receipt variants:

1. German headset receipt → department `Salg`
2. English train ticket receipt → department `Produksjon`
3. Portuguese coffee receipt → department `HR`

The remaining issue is competition verification, not sandbox correctness. The task folder is stale because the auto-generated task docs were not refreshed after the later local replay runs.

## How to Fix

The sandbox path is already fixed. The working ingredients are:

1. Receipt PDF extraction must populate supplier-style voucher fields so the request routes into `HandleSupplierInvoice`
2. `NormalizeAccountNumber()` must coerce textual account labels to numeric expense accounts
3. Receipt departments must be resolved or created and attached to the expense posting
4. Voucher validation must sum `postings.amount` before `amountGross` so VAT-split vouchers balance correctly

If competition still scores 0, the next step is to compare the competition validator's checks against the local validator rather than changing the handler again.

## Effort

**MEDIUM-HIGH** — receipt extraction is harder, and no competitive gain since leader also 0.

## Action Required

- [x] Verify sandbox correctness with the saved receipt PDFs
- [ ] Refresh auto-generated task docs when PowerShell tooling is available in the environment
- [ ] Submit a competition replay only if Tier 3 voucher gaps above this task are already under control
