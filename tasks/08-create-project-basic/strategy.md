# Task 08 — Create Project (Basic)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 08 |
| **Task Type** | `create_project` |
| **Variant** | Basic project with customer + PM |
| **Tier** | 2 |
| **Our Score** | 1.50 |
| **Leader Score** | 1.50 |
| **Gap** | 0 (tied) |
| **Status** | ✅ FIXED — Tied with leader |
| **Handler** | `ProjectHandler.cs` |
| **Priority** | None — tied |

## What It Does

Create a project, assign a customer, and assign a project manager (employee).

## API Flow

1. `POST /customer` — create/find customer
2. `POST /employee` — create project manager employee
3. `PUT /employee/entitlement/:grantEntitlementsByTemplate?template=ALL_PRIVILEGES` — grant PM entitlements
4. `POST /project` — create project with customer + projectManager

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `project_found` | — | ✅ |
| `name` | — | ✅ |
| `has_customer` | — | ✅ |
| `has_project_manager` | — | ✅ |

## Current State

**FIXED.** Both teams at 1.50. Key fix: PM needs entitlements granted before being assigned.

## Action Required

None.
