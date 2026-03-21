# Task 02 — Create Customer

## Overview

| Field | Value |
|---|---|
| **Task ID** | 02 |
| **Task Type** | `create_customer` |
| **Variant** | Standard customer with address |
| **Tier** | 1 |
| **Our Score** | 2.00 |
| **Leader Score** | 2.00 |
| **Gap** | 0 (tied) |
| **Status** | ✅ FIXED — Tied with leader |
| **Handler** | `CustomerHandler.cs` |
| **Priority** | None — already maxed |

## What It Does

Create a customer with name, email, organization number, phone number, and physical address.

## API Flow

1. `POST /customer` — create with all fields including `physicalAddress`
   - Address requires `addressLine1`, `postalCode`, `city`
   - Country defaults to Norway (id=161)

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `customer_found` | — | ✅ |
| `name` | — | ✅ |
| `email` | — | ✅ |
| `organizationNumber` | — | ✅ |
| `addr.addressLine1` | — | ✅ |
| `addr.postalCode` | — | ✅ |
| `addr.city` | — | ✅ |
| `phoneNumber` | — | ✅ |

## Current State

**FIXED.** Scoring 2.00 (maximum). No changes needed.

Key fixes applied:
- Address fields expanded individually (not just `has_address`)
- Country ID 161 (Norway) always included
- POST-first strategy (skip redundant GET in clean competition env)

## Action Required

None.
