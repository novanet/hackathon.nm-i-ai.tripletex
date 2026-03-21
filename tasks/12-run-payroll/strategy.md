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

## Current State (2026-03-22)

**Sandbox fully passing** at 8/8 (100%). Latest sandbox run after the handler fix: 15 calls, 0 errors, score 8/8.

**Latest verified sandbox replay:** William Taylor prompt succeeded end-to-end. The handler created a new employee, created employment with division attached, verified the employment link with a follow-up GET, posted the salary transaction, verified the payslip, and created the 5000-series payroll voucher.

**Competition is still unverified after the fix.** The last recorded competition failure is older than the patch and should be treated as stale evidence until a new submission is made.

**Updated root cause:** the original division logic had two failure modes in clean environments:
1. Omitting `organizationNumber` can fail with `organizationNumber: Feltet må fylles ut.`
2. Using the company's own organization number can fail with `Juridisk enhet kan ikke registreres som virksomhet/underenhet.`

**Current fix:** PayrollHandler now resolves an existing division if present; otherwise it creates `Hovedvirksomhet` with a synthetic 9-digit underenhet number, then verifies the employment actually comes back with `division.id` before allowing `POST /salary/transaction`.

66 total runs. Sandbox currently validates cleanly. Competition confirmation is still pending.

## Key Fix Applied

- STJ serialization fix (Dictionary instead of anonymous types) — fixed `has_employee_link` and `payslip_generated`
- Payroll voucher on 5000-series accounts for salary cost registration
- `generateTaxDeduction=false` to avoid tax calculation errors

## Remaining Issues

1. **Competition confirmation still missing** — sandbox passes, but the clean competition environment has not yet been rerun after the patch.
2. **Task docs still show the last stale competition result** — this will correct itself after the next submission replay and refresh.

## Action Required

- [x] Fix payroll division creation and employment linkage
- [x] Verify sandbox replay at 8/8
- [ ] Submit a fresh competition run when external submission is allowed
- [ ] Compare competition score vs local validator and adjust `SandboxValidator.cs` if they diverge
- [ ] Target: 4.00 (matching leader)
