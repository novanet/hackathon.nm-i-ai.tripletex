# Task 18 — Register Payment (Full Chain)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 18 |
| **Task Type** | `register_payment` |
| **Variant** | Full payment flow (high scorer) |
| **Tier** | 2 |
| **Our Score** | 4.00 |
| **Leader Score** | 4.00 |
| **Gap** | 0 (tied) |
| **Status** | ✅ FIXED — Maximum score |
| **Handler** | `PaymentHandler.cs` → `HandleFullChainPaymentAsync` |
| **Priority** | None — already maxed |

## What It Does

Full payment chain: create customer → order with lines → invoice → register payment for full amount.

## API Flow

1. `POST /customer`
2. `GET /ledger/vatType`
3. `POST /order` (with order lines)
4. `POST /invoice`
5. `GET /invoice/paymentType` (concurrent with invoice chain)
6. `PUT /invoice/{id}/:payment`

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `invoice_found` | — | ✅ |
| `payment_registered` | — | ✅ |

## Current State

**FIXED.** Scoring 4.00 (maximum). Concurrent paymentType resolution already optimized.

## Action Required

None.
