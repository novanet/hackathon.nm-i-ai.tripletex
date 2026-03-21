# Task 24 — Create Voucher (Ledger Correction, Tier 3)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 24 |
| **Task Type** | `create_voucher` / `correct_ledger` |
| **Variant** | Ledger correction (German) |
| **Tier** | 3 |
| **Our Score** | 2.25 |
| **Leader Score** | 6.00 |
| **Gap** | -3.75 |
| **Status** | ⚠️ Behind — partial correctness |
| **Handler** | `LedgerCorrectionHandler.cs` |
| **Priority** | #8 — MEDIUM effort, good gain |

## What It Does

German prompt: "Wir haben Fehler im Hauptbuch für Januar und Februar..." — detect and correct ledger errors by creating correction vouchers.

Error types handled:
- **wrong_account** — posting on wrong account, move to correct one
- **duplicate** — reverse duplicated posting
- **missing_vat** — add missing VAT to a posting
- **wrong_amount** — correct an incorrect amount

## API Flow

1. `GET /ledger/posting?dateFrom=X&dateTo=Y` — fetch all postings in the period
2. `GET /ledger/account?number=X` — resolve accounts for corrections
3. For each error:
   - Find the original posting via description/account/amount matching
   - Find the counter-posting via `FindAndGetCounter`
   - `POST /ledger/voucher?sendToLedger=true` — create correction voucher (reverse + re-post)

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `voucher_found` | 2 | ✅ (at least some found) |
| `has_description` | 2 | ✅ |
| `has_postings` | 2 | ✅ |
| `postings_balanced` | 2 | ⚠️ |
| `correct_accounts` | 2 | ⚠️ |
| `correct_amount` | 3 | ⚠️ |

## Why We Score 2.25 (37%)

Scoring 2.25/6.00 means some correction vouchers are created but not all correctly:

1. **LLM extraction may miss some errors** — `ParseCorrections()` in the handler relies on LLM extracting all error entities
2. **Counter-posting lookup fails** — `FindAndGetCounter` may not find the right matching posting, causing fallback to account 1920
3. **Wrong account resolution** — the "correct" account number may not be extracted properly
4. **Amount calculation wrong** for some error types
5. **Duplicate detection** needs exact amount + account matching to find the duplicated posting

## How to Fix

1. **Analyze submission logs** — what errors did the LLM extract? How many correction vouchers were created?
2. **Improve `ParseCorrections`** — ensure all error types from the German prompt are extracted
3. **Fix `FindAndGetCounter`** — improve matching logic for counter-postings
4. **Verify correction voucher amounts** — must exactly reverse + re-post
5. **Test with the actual German prompt** from competition

## Root Cause Deep Dive

The `LedgerCorrectionHandler.PostCorrection` method handles each error type:
- `wrong_account`: reverses original on wrong account, re-posts on correct account
- `duplicate`: creates reverse posting only
- `missing_vat`: posts the VAT difference
- `wrong_amount`: reverses wrong amount, posts correct amount

Each depends on finding the original posting in the ledger — if `GetOriginalPosting` fails, the entire correction is skipped.

## Effort

**MEDIUM** — needs submission log analysis + extraction/posting fixes.

## Action Required

- [ ] Run `Analyze-Run.ps1 -ShowApiCalls -ShowExtraction` for this task
- [ ] Check how many corrections LLM extracted vs how many were in the prompt
- [ ] Verify counter-posting matching logic
- [ ] Test locally with the German prompt
- [ ] Submit to verify improvement
