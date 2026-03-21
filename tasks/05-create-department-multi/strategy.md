# Task 05 — Create Department (Multi)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 05 |
| **Task Type** | `create_department` |
| **Variant** | Multiple departments (3 at once) |
| **Tier** | 1 |
| **Our Score** | 1.33 |
| **Leader Score** | 1.33 |
| **Gap** | 0 (tied) |
| **Status** | ✅ FIXED — Tied with leader |
| **Handler** | `DepartmentHandler.cs` |
| **Priority** | None — tied |

## What It Does

Create multiple departments in one prompt (e.g., "Create departments: Sales, Marketing, Engineering").

## API Flow

1. `GET /department?fields=departmentNumber,name&count=100` — get existing department numbers (avoid collisions)
2. `POST /department` × N — create each department with unique `departmentNumber`

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `department_found` | 2 | ✅ (per department) |

## Current State

**FIXED.** Both teams at 1.33. Department number collision avoidance implemented (pre-query existing, pick next available).

## Possible Improvement

Could be efficiency-related — if we can reduce write calls or avoid the GET. But gap is 0 so no priority.

## Action Required

None — tied with leader.
