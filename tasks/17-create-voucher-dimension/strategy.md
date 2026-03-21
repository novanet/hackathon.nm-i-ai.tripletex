# Task 17 — Create Voucher (Custom Dimension)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 17 |
| **Task Type** | `create_voucher` |
| **Variant** | Custom dimension + voucher posting |
| **Tier** | 2 |
| **Our Score** | 3.50 |
| **Leader Score** | 3.50 |
| **Gap** | 0 (tied) |
| **Status** | ✅ FIXED — Tied with leader |
| **Handler** | `VoucherHandler.cs` (custom dimension branch) |
| **Priority** | None — tied |

## What It Does

Create a voucher with postings and a custom dimension (e.g., "Region: Vestlandet").

## API Flow

1. `GET /dimension` — check if dimension exists
2. `POST /dimension` — create dimension if needed
3. `POST /dimension/{id}/dimensionValues` — create dimension value
4. `GET /ledger/account?number=X` — resolve accounts
5. `POST /ledger/voucher?sendToLedger=true` — create voucher with dimension on postings

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `voucher_found` | 2 | ✅ |
| `has_description` | 2 | ✅ |
| `has_postings` | 2 | ✅ |

## Current State

**FIXED.** Both at 3.50. Custom dimension extraction handles:
- Dimension in separate entity OR nested in voucher entity
- Singular `value` vs plural `values` key

## Action Required

None.
