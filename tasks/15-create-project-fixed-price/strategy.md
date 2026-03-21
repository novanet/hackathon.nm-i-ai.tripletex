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

## Why We Were Behind

1. **Routing bug (FIXED 2026-03-21)**: LLM extracted `set_fixed_price` → routed to `FixedPriceProjectHandler` which only updates existing projects. In competition (clean env), no project exists → handler returned empty → task failed. **Fix**: Rerouted `set_fixed_price` and `update_project` to `ProjectHandler` which handles full creation chain.
2. **PM resolution bug** (Fix 11 — `GetScalarString`) was causing PM assignment to fail — fixed earlier.
3. **Milestone invoice creation** was gated on LLM extracting an `invoice` entity — fixed to always create if `isFixedPrice=true` AND prompt contains `\d+\s*%`.

### Key Knowledge

- `set_fixed_price` and `update_project` now route to `ProjectHandler` (not `FixedPriceProjectHandler`)
- `ProjectHandler` creates: customer → PM employee → grant entitlements → project (isFixedPrice=true) → order → milestone invoice
- Milestone amount: deterministic regex extracts percentage from prompt, calculates `fixedPrice × pct / 100`
- Previous runs showed 12-call runs (invoice created) → 8/8 pass vs 10-call runs (invoice skipped) → 6/8 fail
- Tested with Norwegian (50%) and French (33%) prompts — both pass with 0 errors

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
