# Task 19 — Create Employee from PDF Contract (Tier 3)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 19 |
| **Task Type** | `create_employee` |
| **Variant** | Employee from PDF employment contract |
| **Tier** | 3 |
| **Our Score** | 1.77 |
| **Leader Score** | 2.45 |
| **Gap** | -0.68 |
| **Status** | ⚠️ Behind |
| **Handler** | `PdfEmployeeHandler.cs` → `EmployeeHandler.cs` |
| **Priority** | #10 — MEDIUM effort, moderate gain |

## What It Does

Extract employee details from a PDF employment contract and create the employee with full onboarding (employment, salary, department, occupation code).

## API Flow

Same as Task 01 plus additional employment details:

1. `GET /department?count=1` — resolve department
2. `POST /employee` — create employee
3. `PUT /employee/entitlement/:grantEntitlementsByTemplate` — admin role
4. `POST /employee/employment` — create employment (with division, start date)
5. `POST /employee/employment/details` — salary, type/form, remuneration, occupation code

## Competition Checks (extended for PDF variants)

| Check | Points | Status |
|---|:---:|:---:|
| `employee_found` | — | ✅ |
| `firstName` | — | ✅ |
| `lastName` | — | ✅ |
| `email` | — | ✅ |
| `admin_role` | — | ✅ |
| `department` | — | ⚠️ May fail |
| `employment_start_date` | — | ⚠️ May fail |
| `salary` | — | ⚠️ May fail |
| `employment_type/form` | — | ⚠️ May fail |
| `occupation_code` | — | ⚠️ May fail |

## Why We're Behind

PDF extraction misses some employment detail fields. The LLM sees the PDF text but doesn't always extract:
- `startDate` for employment
- `salary` / `annualSalary`
- `employmentType` / `employmentForm`
- `occupationCode`
- `remunerationType`
- `percentageOfFullTimeEquivalent`

## How to Fix

1. **Replay with saved PDF** — find the file in `src/logs/files/` for this task
2. **Improve LLM system prompt** to explicitly ask for employment contract fields
3. **EmployeeHandler** already handles onboarding if fields are extracted — the bottleneck is extraction
4. Same fix helps Task 21 (PDF offer letter)

## Known Issues

- Occupation code lookup: `GET /employee/employment/occupationCode?code=3323` returns 0 results. Use `query=3323` instead.
- Occupation code validation mismatch: `expected 3323`, `actual 1142109`

## Effort

**MEDIUM** — LLM prompt tuning + occupation code resolution fix.

## Action Required

- [ ] Find saved PDF in `src/logs/files/`
- [ ] Replay locally, check what LLM extracts
- [ ] Add employment detail fields to extraction prompt
- [ ] Fix occupation code lookup
- [ ] Test + submit (same fix helps Task 21)
