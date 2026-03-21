# Task 29 — Create Project (Full Lifecycle, Tier 3)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 29 |
| **Task Type** | `create_project` |
| **Variant** | Full project lifecycle (create → hours → invoice) |
| **Tier** | 3 |
| **Our Score** | 1.09 |
| **Leader Score** | 4.91 |
| **Gap** | -3.82 |
| **Status** | ⚠️ Behind — large gap |
| **Handler** | `ProjectHandler.cs` → `HandleTimesheetAndInvoice` |
| **Priority** | #11 — HIGH effort, good gain |

## What It Does

Complex composite task: create a project, create an activity, assign employees, log timesheet hours, and create an invoice based on the logged hours. Essentially combines Tasks 08 + 16 into a single complex flow.

## API Flow (full chain)

1. `POST /customer` — create customer
2. `POST /employee` — create project manager
3. `PUT /employee/entitlement/:grantEntitlementsByTemplate` — grant entitlements
4. `POST /project` — create project with customer + PM
5. `POST /activity` — create activity (type: PROJECT_GENERAL_ACTIVITY)
6. `POST /project/projectActivity` — link activity to project
7. `POST /timesheet/entry` — log hours for employee on project activity
8. `POST /order` — create order from hours (hourly rate × hours)
9. `POST /invoice` — create invoice from order
10. `PUT /invoice/{id}/:send` (optional)

## Competition Checks (8 pts across 4 checks)

| Check | Points | Status |
|---|:---:|:---:|
| `project_found` | 2 | ✅ |
| `timesheet_logged` | 2 | ❌ |
| `invoice_found` | 2 | ❌ |
| `correct_amount` | 2 | ❌ |

## Why We Score 1.09

Only 1/4 checks pass (project_found). The timesheet + invoice portions fail:

1. **Employee resolution for timesheet** may fail — needs to find the employee created in step 2
2. **Activity creation** → `POST /activity` may fail or return wrong ID
3. **Project-activity linking** may not work — `POST /project/projectActivity` requires correct IDs
4. **Timesheet entry** needs `employee.id`, `project.id`, `activity.id`, `date`, `hours`
5. **Invoice from hours** — must calculate amount from hours × rate and create order

## Root Cause Investigation

`HandleTimesheetAndInvoice` method in `ProjectHandler.cs` handles this flow. Check:
- Does it correctly pass IDs between steps?
- Does activity creation succeed?
- Does timesheet entry POST succeed?
- Does invoice amount match hours × rate?

## How to Fix

1. **Read `HandleTimesheetAndInvoice` carefully** — trace each API call
2. **Check if employee ID persists** from project creation to timesheet entry
3. **Verify activity + project-activity flow** — these are the most fragile steps
4. **Test locally** with a composite project prompt
5. **Fix each failing step** in order

## Effort

**HIGH** — multi-step flow with several potential break points. Each sub-step must work + pass IDs correctly.

## Action Required

- [ ] Read `HandleTimesheetAndInvoice` method fully
- [ ] Analyze latest submission logs for task 29
- [ ] Identify which sub-step fails first
- [ ] Fix in order: activity → timesheet → invoice
- [ ] Test locally
- [ ] Submit to verify
