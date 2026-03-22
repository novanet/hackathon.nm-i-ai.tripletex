# Task 28 — Create Project (Cost Analysis, Tier 3)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 28 |
| **Task Type** | `create_project` / `cost_analysis` |
| **Variant** | Cost analysis from ledger data |
| **Tier** | 3 |
| **Our Score** | 0 (was failing 5/5 checks) |
| **Leader Score** | 1.50 |
| **Gap** | 1.50 |
| **Status** | 🔧 FIXED (2026-03-22) — awaiting competition verification |
| **Handler** | `CostAnalysisHandler.cs` |
| **Priority** | High — was scoring 0 due to missing account fields |

## What It Does

Nynorsk prompt: "Totalkostnadene auka monaleg frå januar til februar..." — compare two months of ledger data, find the top 3 expense account increases, and create a project + activity for each.

## Root Cause (2026-03-22)

The `/ledger` endpoint does NOT return `account.number` or `account.name` unless explicitly requested via `fields` parameter. `GetLedgerData()` was calling `/ledger?dateFrom=X&dateTo=Y` without specifying fields, so `account.number` defaulted to 0, causing ALL accounts to be filtered out by the 4000-7999 expense range check. Result: zero projects created, handler returned "success" with no entity IDs.

The stale "FIXED/TIED" status came from auto-generated docs that showed the latest run from a *different* execution path (ProjectHandler lifecycle variant, not CostAnalysisHandler ledger-analysis).

## Fix Applied

Added `fields=account(id,number,name),sumAmount` to the `/ledger` GET calls. Also hardened the handler to log an error instead of silently succeeding when top3 is empty.

## API Flow

1. `GET /ledger?dateFrom=Jan&dateTo=Jan&fields=account(id,number,name),sumAmount` — get January expenses
2. `GET /ledger?dateFrom=Feb&dateTo=Feb&fields=account(id,number,name),sumAmount` — get February expenses
3. Compare and find top 3 increases (expense accounts 4000-7999)
4. `GET /employee?count=1&fields=id` — resolve project manager
5. `POST /project` × 3 — create one internal project per top increase
6. `POST /activity` × 3 — create one activity per project
7. `POST /project/projectActivity` × 3 — link activities to projects

## Competition Checks (expected)

| Check | Points | Status |
|---|:---:|:---:|
| `project_found` | — | Pending |
| `has_activities` | — | Pending |
| `correct_analysis` | — | Pending |

## Verification Notes

- Sandbox smoketest confirms handler now creates projects with correct account names (sandbox only had 1 expense account increase; competition has 3+)
- SandboxValidator updated with `cost_analysis` case
- Local validator scores are NOT authoritative for this task — verify via competition submission
