# Task 13 — Create Travel Expense

## Overview

| Field | Value |
|---|---|
| **Task ID** | 13 |
| **Task Type** | `create_travel_expense` |
| **Variant** | Travel expense with costs + per diem |
| **Tier** | 2 |
| **Our Score** | 2.50 |
| **Leader Score** | 2.50 |
| **Gap** | 0 (tied) |
| **Status** | ✅ FIXED — Tied with leader |
| **Handler** | `TravelExpenseHandler.cs` |
| **Priority** | None — tied |

## What It Does

Create a travel expense for an employee with multiple cost lines (flights, hotel, meals, per diem).

## API Flow

1. `GET /employee?firstName=X&lastName=Y` — find employee
2. `POST /employee` — create if not found
3. `POST /travelExpense` — create expense with title, employee, dates
4. `POST /travelExpense/cost` × N — one per cost line (including per diem)

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `travel_expense_found` | — | ✅ |
| `has_title` | — | ✅ |
| `has_employee` | — | ✅ |
| `has_costs` | — | ✅ |

## Current State

**FIXED.** 6/6 checks, 8/8 points. Key fixes:
- Employee resolution from `entities["employee"]` not `relationships["employee"]`
- Per diem generation from `durationDays × dailyAllowanceRate`
- Cost item key aliases: `costs`, `costItems`, `cost_items`, `costLines`

## Action Required

None.
