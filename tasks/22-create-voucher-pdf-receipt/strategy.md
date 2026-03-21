# Task 22 — Create Voucher from PDF Receipt (Tier 3)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 22 |
| **Task Type** | `create_voucher` |
| **Variant** | Voucher from PDF receipt |
| **Tier** | 3 |
| **Our Score** | 0.00 |
| **Leader Score** | 0.00 |
| **Gap** | 0 (both fail) |
| **Status** | ❌ Both fail in competition — sandbox passing |
| **Handler** | `VoucherHandler.cs` (supplier-invoice path) |
| **Priority** | LOW — no competitive advantage, but free points if competition starts scoring |

## What It Does

Multi-language prompt (DE/EN/NN/PT): "Wir benötigen die Headset-Ausgabe aus dieser Quittung..." — extract receipt data from attached PDF and create a voucher with expense posting + department.

Variants seen:
1. German: headset receipt → department Salg/Kvalitetskontroll
2. English: train ticket receipt → department Produksjon
3. Portuguese: coffee receipt → department HR
4. Nynorsk: various receipts → various departments

## API Flow

1. `POST /supplier` — create supplier from receipt
2. `GET /department?count=1000` — resolve department by name
3. `GET /ledger/account?number=XXXX` — resolve expense account
4. `GET /ledger/vatType?number=1` — resolve input VAT type
5. `GET /ledger/voucherType?name=Leverandørfaktura` — resolve voucher type
6. `GET /ledger/account?number=2400` — resolve creditor account
7. `POST /ledger/voucher?sendToLedger=true` — create double-entry voucher

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `voucher_found` | — | ❌ competition |
| `has_description` | — | ❌ competition |
| `has_postings` | — | ❌ competition |

## Current State (2026-03-21)

**Sandbox fully passing.** Latest competition runs show handler succeeding with 0 errors:
- Competition 2026-03-21 21:05: 7 calls, 0 errors, success=True
- Competition 2026-03-21 17:35: 7 calls, 0 errors, success=True
- Earlier runs also succeeded

Handler routes receipt PDFs through the supplier-invoice voucher path with:
- `NormalizeAccountNumber()` coercing textual account labels to numeric expense accounts
- Department resolution from prompt text
- Correct VAT-split posting structure

**But competition leaderboard still shows 0.** This is puzzling since the handler completes without error. Possible causes:
1. Competition validator checks something our local validator doesn't
2. The receipt PDF extraction produces slightly wrong amounts/accounts
3. Both teams score 0, suggesting the competition checks may be unusually strict for this task

20 total runs (4 competition, 16 sandbox). Competition runs have been consistently succeeding since mid-day 2026-03-21.

## Action Required

- [ ] Submit fresh run to verify latest competition score
- [ ] If still 0, investigate what competition checks differ from local validator
- [ ] Low priority — no gap to close since leader also scores 0
