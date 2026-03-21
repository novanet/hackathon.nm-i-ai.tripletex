# Task 16 — Create Project (Timesheet Hours)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 16 |
| **Task Type** | `create_project` |
| **Variant** | Timesheet hours → project invoice |
| **Tier** | 2 |
| **Our Score** | 2.80 |
| **Leader Score** | 3.00 |
| **Gap** | -0.20 |
| **Status** | ⚠️ Behind (micro efficiency gap) |
| **Handler** | `ProjectHandler.cs` → `HandleTimesheetAndInvoice` |
| **Priority** | #5 — TRIVIAL effort, tiny gain |

## What It Does

Create a project, create an activity, log timesheet hours, then create an invoice from the hours.

## API Flow

1. `POST /customer` — create customer
2. `POST /employee` — create PM/employee
3. `PUT /employee/entitlement/:grantEntitlementsByTemplate` — grant entitlements
4. `POST /project` — create project
5. `POST /activity` — create activity
6. `POST /project/projectActivity` — link activity to project
7. `POST /timesheet/entry` — log hours
8. `POST /order` — create order from hours
9. `POST /invoice` — create invoice

## Competition Checks (8 pts across 4 checks)

| Check | Points | Status |
|---|:---:|:---:|
| `project_found` | — | ✅ |
| `timesheet_logged` | — | ✅ |
| `invoice_found` | — | ✅ |
| `correct_amount` | — | ✅ |

## Why We're Behind

Micro efficiency gap (-0.20). Correctness is 100%. Probably 1 extra write call.

## How to Fix

Review the API call chain for unnecessary writes — possibly the activity POST or module enable is redundant.

## Effort

**TRIVIAL** — trim 1 write call.

## Action Required

- [ ] Review write call count
- [ ] Check if module enable or entitlement grant is unnecessary for this variant
- [ ] Submit to verify
