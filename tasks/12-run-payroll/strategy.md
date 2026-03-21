# Task 12 — Run Payroll

## Overview

| Field | Value |
|---|---|
| **Task ID** | 12 |
| **Task Type** | `run_payroll` |
| **Variant** | Standard payroll run |
| **Tier** | 2 |
| **Our Score** | 1.00 |
| **Leader Score** | 0.00 |
| **Gap** | +1.00 (we lead!) |
| **Status** | ✅ Leading — but room to improve |
| **Handler** | `PayrollHandler.cs` |
| **Priority** | #13 — MEDIUM effort, already ahead |

## What It Does

Create employee if needed, set up employment, create salary transaction with correct amounts.

## API Flow

1. `GET /employee?firstName=X&lastName=Y` — find or create employee
2. `POST /employee` — create if not found (with `userType = "NO_ACCESS"`)
3. `POST /employee/employment` — create employment (with division)
4. `POST /employee/employment/details` — set salary details
5. `POST /salary/transaction` — create salary transaction
6. `POST /salary/payslip` — generate payslip

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `salary_transaction_found` | — | ✅ |
| `has_employee_link` | — | ✅ |
| `payslip_generated` | — | ⚠️ Uncertain |
| `correct_amount` | — | ⚠️ Uncertain |

## Current State

Scoring 1.00, leader scores 0. Key fix was STJ serialization (anonymous types → Dictionary) which fixed `has_employee_link` and `payslip_generated` (0/4 → 8/8 locally). Competition result may not reflect latest fixes yet.

## How to Improve

1. Submit latest code — serialization fix may not be in competition yet
2. Verify payslip generation actually persists
3. Check if `correct_amount` is calculated properly (baseSalary + bonus)

## Effort

**MEDIUM** — needs a clean submission to verify. If still 1.00 after resubmit, investigate which checks fail.

## Action Required

- [ ] Submit competition run with latest code
- [ ] Compare 1.00 → hopefully higher
- [ ] If still 1.00, analyze which 3 checks fail via `Analyze-Run.ps1`
