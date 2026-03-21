# Task 15 — Create Project (Fixed-Price)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 15 |
| **Task Type** | `create_project` |
| **Variant** | Fixed-price project with milestone invoice |
| **Tier** | 2 |
| **Our Score** | 1.50 |
| **Leader Score** | 2.80 |
| **Gap** | -1.30 |
| **Status** | ⚠️ Behind |
| **Handler** | `ProjectHandler.cs` (handles fixed-price + milestone invoice) |
| **Priority** | #4 — LOW effort, good gain |

## What It Does

Create a project with a fixed price, then create a milestone invoice for a percentage of the fixed price.

## API Flow

1. `POST /customer` — create customer
2. `POST /employee` — create project manager
3. `PUT /employee/entitlement/:grantEntitlementsByTemplate` — grant PM entitlements
4. `POST /project` — create project with `isFixedPrice=true`, `fixedprice=X`
5. `POST /order` — create order for milestone invoice (amount = fixedPrice × percentage)
6. `POST /invoice` — create invoice from order

## Competition Checks (8 pts total)

| Check | Points | Status |
|---|:---:|:---:|
| `project_found` | — | ✅ |
| `has_customer` | — | ✅ |
| `has_project_manager` | — | ✅ |
| `invoice_created` | — | ⚠️ Sometimes fails |

## Why We're Behind

1. **PM resolution bug** (Fix 11 — `GetScalarString`) may have been causing PM assignment to fail
2. **Milestone invoice creation** was gated on LLM extracting an `invoice` entity — sometimes skipped when LLM didn't extract it
3. **Milestone amount calculation**: `Math.Round(fixedPrice * pct / 100, 2)` — must extract percentage from prompt via regex, never trust LLM math

### Key Knowledge

- Fix applied: always call `CreateProjectInvoice` if `isFixedPrice=true` AND prompt contains `\d+\s*%`
- Percentage extraction: deterministic regex, not LLM
- Previous runs showed 12-call runs (invoice created) → 8/8 pass vs 10-call runs (invoice skipped) → 6/8 fail

## How to Fix

1. **Submit** — Fix 11 (PM resolution) may already improve the score
2. If still failing, verify milestone invoice is always created for fixed-price projects
3. Check the percentage computation: `fixedPrice × pct / 100` rounded to 2 decimals

## Effort

**LOW** — submit first, then targeted fix if needed.

## Action Required

- [ ] Submit to verify Fix 11
- [ ] Check if milestone invoice is consistently created
- [ ] Analyze competition results for this task
