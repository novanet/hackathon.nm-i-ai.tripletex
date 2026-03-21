# Task 04 — Create Supplier

## Overview

| Field | Value |
|---|---|
| **Task ID** | 04 |
| **Task Type** | `create_supplier` |
| **Variant** | Supplier with org number + email |
| **Tier** | 1 |
| **Our Score** | 2.00 |
| **Leader Score** | 2.00 |
| **Gap** | 0 (tied) |
| **Status** | ✅ FIXED — Tied with leader |
| **Handler** | `SupplierHandler.cs` |
| **Priority** | None — already maxed |

## What It Does

Create a supplier with name, email, organization number, and phone number.

## API Flow

1. `POST /supplier` — create with `name`, `email`, `organizationNumber`, `phoneNumber`

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `supplier_found` | — | ✅ |
| `name` | — | ✅ |
| `email` | — | ✅ |
| `organizationNumber` | — | ✅ |
| `phoneNumber` | — | ✅ |

## Current State

**FIXED.** Scoring 2.00 (maximum). No changes needed.

## Action Required

None.
