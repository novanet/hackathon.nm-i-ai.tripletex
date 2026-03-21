# Task 01 — Create Employee (Basic)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 01 |
| **Task Type** | `create_employee` |
| **Variant** | Basic employee creation |
| **Tier** | 1 |
| **Our Score** | 2.00 |
| **Leader Score** | 1.50 |
| **Gap** | +0.50 (we lead!) |
| **Status** | ✅ FIXED — Leading |
| **Handler** | `EmployeeHandler.cs` |
| **Priority** | None — already ahead |

## What It Does

Create a basic employee from a text prompt. The prompt provides first name, last name, email, and sometimes date of birth. After creating the employee, grant admin role entitlement.

## Prompt Example

> "Create an employee named Ola Nordmann with email ola@example.com. Grant admin access."

Languages: Norwegian, English, Spanish, Portuguese, Nynorsk, German, French.

## API Flow

1. `GET /department?count=1` — resolve department (required when module active)
2. `POST /employee` — create with firstName, lastName, email, dateOfBirth, department
3. `PUT /employee/entitlement/:grantEntitlementsByTemplate?employeeId={id}&template=administrator` — grant admin role

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `employee_found` | 2 | ✅ |
| `firstName` | 1 | ✅ |
| `lastName` | 1 | ✅ |
| `email` | 1 | ✅ |
| `admin_role` | 5 | ✅ |

## Current State

**FIXED.** We score 2.00 against the leader's 1.50. No changes needed.

Key fixes already applied:
- Synthetic email generation when not provided
- Department auto-resolution
- `dateOfBirth` defaults to `1990-01-01` if not in prompt
- Admin role always granted via `template=administrator`
- STJ serialization fix (Dictionary instead of anonymous types)

## Action Required

None. Maintain current implementation.
