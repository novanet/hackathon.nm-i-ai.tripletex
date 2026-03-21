# Task 12 — Run Payroll

## Overview

| Field | Value |
|---|---|
| **Task ID** | 12 |
| **Task Type** | `run_payroll` |
| **Variant** | Standard payroll run |
| **Tier** | 2 |
| **Our Score** | 1.00 |
| **Leader Score** | 4.00 |
| **Gap** | -3.00 |
| **Status** | ❌ Failing — sandbox passes 8/8 but competition only 1.00 |
| **Handler** | `PayrollHandler.cs` |
| **Priority** | #5 — HIGH priority, large gap |

## What It Does

Create employee if needed, set up employment + division, create salary transaction with base salary + bonus, generate payslip, then create a payroll voucher on 5000-series accounts.

## API Flow (current — 14 calls sandbox)

1. `GET /employee?email=X` — find existing employee
2. `GET /department?count=1` — resolve department
3. `POST /employee` — create employee (if not found)
4. `GET /division?count=1` — check for existing division
5. `POST /employee/employment` — create employment with division
6. `GET /salary/type?count=100` — get salary types
7. `POST /salary/transaction?generateTaxDeduction=false` — create salary transaction
8. `GET /salary/payslip/{id}` — verify payslip
9. `GET /ledger/account?number=5000` — resolve salary expense account
10. `GET /ledger/account?number=1920` — resolve bank account
11. `GET /ledger/voucherType?name=Lønnsbilag` — resolve voucher type
12. `POST /ledger/voucher?sendToLedger=true` — create payroll voucher
13. `GET /salary/transaction/{id}` — verify transaction (validation)
14. `GET /salary/payslip/{id}` — verify payslip (validation)

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `salary_transaction_found` | 2 | ✅ |
| `has_employee_link` | 2 | ✅ |
| `payslip_generated` | 2 | ✅ |
| `correct_amount` | 2 | ✅ |

**Local validation: 8/8 (100%)**

## Current State (2026-03-21)

**Sandbox fully passing** at 8/8 (100%). Latest sandbox run: 14 calls, 0 errors, score 8/8.

**Competition failing.** Latest competition run (2026-03-21 14:44): 7 calls, 2 errors. Error was `"organizationNumber: Juridisk enhet kan ikke registreres som virksomhet/underenhet."` — the division creation failed because the company's org number can't be used as a sub-entity. Handler only made 7 of 14 calls before failing.

**Root cause:** Division creation uses the company's own org number, which fails in competition's clean environment. In sandbox this works because a division may already exist. The handler needs to:
1. Skip org number when creating divisions, OR
2. Use a different org number for the division, OR  
3. Look up the company's existing division structure first

66 total runs. Sandbox consistently passes (0 errors). Competition has intermittent division creation failures.

## Key Fix Applied

- STJ serialization fix (Dictionary instead of anonymous types) — fixed `has_employee_link` and `payslip_generated`
- Payroll voucher on 5000-series accounts for salary cost registration
- `generateTaxDeduction=false` to avoid tax calculation errors

## Remaining Issues

1. **Division creation fails in competition** — org number conflict. This is the #1 blocker preventing competition score improvement.
2. Competition score stuck at 1.00 despite local 8/8 — the division error aborts the handler before salary transaction is created.

## Action Required

- [ ] Fix division creation to not use company org number (or find existing division)
- [ ] Test with competition-like clean environment
- [ ] Submit and verify score improves from 1.00
- [ ] Target: 4.00 (matching leader)
