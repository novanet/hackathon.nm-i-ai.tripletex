# Task 28 — Create Project (Cost Analysis, Tier 3)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 28 |
| **Task Type** | `create_project` / `cost_analysis` |
| **Variant** | Cost analysis from ledger data |
| **Tier** | 3 |
| **Our Score** | 1.50 |
| **Leader Score** | 1.50 |
| **Gap** | 0 (tied) |
| **Status** | ✅ FIXED — Tied with leader |
| **Handler** | `CostAnalysisHandler.cs` |
| **Priority** | None — tied |

## What It Does

Nynorsk prompt: "Totalkostnadene auka monaleg frå januar til februar..." — compare two months of ledger data, find the top 3 expense account increases, and create a project for each.

## API Flow

1. `GET /ledger/posting?dateFrom=Jan&dateTo=Jan` — get January expenses
2. `GET /ledger/posting?dateFrom=Feb&dateTo=Feb` — get February expenses
3. Compare and find top 3 increases
4. `POST /project` × 3 — create one project per top increase

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `project_found` | — | ✅ |
| `correct_analysis` | — | ✅ |

## Current State

**FIXED.** Both at 1.50. Handler compares ledger data, finds top 3 increases, creates projects.

## Action Required

None — tied with leader.
