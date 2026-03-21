# Task 01 — Create Employee (Basic)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 01 |
| **Task Type** | `create_employee` |
| **Variant** | Basic employee creation |
| **Tier** | 1 |
| **Our Score** | 2.00 |
| **Leader Score** | 2.00 |
| **Gap** | 0 (tied) |
| **Status** | ✅ Tied — fully passing |
| **Handler** | `EmployeeHandler.cs` |
| **Priority** | None — at parity |

## What It Does

Create a basic employee from a text prompt. The prompt provides first name, last name, email, and sometimes date of birth. After creating the employee, grant admin role entitlement.

## Prompt Example

> "Create an employee named Ola Nordmann with email ola@example.com. Grant admin access."

Languages: Norwegian, English, Spanish, Portuguese, Nynorsk, German, French.

## API Flow

1. `GET /department?count=100` — resolve department (required when module active)
2. `GET /employee?email=X` — check if employee already exists
3. `POST /employee` — create with firstName, lastName, email, dateOfBirth, department
4. `PUT /employee/entitlement/:grantEntitlementsByTemplate?employeeId={id}&template=administrator` — grant admin role

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `employee_found` | 1 | ✅ |
| `firstName` | 1 | ✅ |
| `lastName` | 1 | ✅ |
| `email` | 1 | ✅ |
| `administrator_role` | 2 | ✅ |

**Local validation: 6/6 (100%)**

## Current State

**Fully working.** Competition and sandbox both pass all checks. Latest competition run (2026-03-21): 2 API calls, 0 errors. Latest sandbox: 6 calls, 0 errors, 6/6 validation.

Key fixes already applied:
- Synthetic email generation when not provided
- Department auto-resolution
- `dateOfBirth` defaults to `1990-01-01` if not in prompt
- Admin role always granted via `template=administrator`
- STJ serialization fix (Dictionary instead of anonymous types)

## Action Required

None. Maintain current implementation.
