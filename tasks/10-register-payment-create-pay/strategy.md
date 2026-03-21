# Task 10 — Register Payment (Create + Pay)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 10 |
| **Task Type** | `register_payment` |
| **Variant** | Create order + invoice + pay |
| **Tier** | 2 |
| **Our Score** | 2.67 |
| **Leader Score** | 2.67 |
| **Gap** | 0 (tied) |
| **Status** | ✅ FIXED — Tied with leader |
| **Handler** | `PaymentHandler.cs` → `HandleFullChainPaymentAsync` |
| **Priority** | None — tied |

## What It Does

Full payment chain: create customer → order → invoice → register payment.

## API Flow

1. `POST /customer` — create customer
2. `GET /ledger/vatType` — resolve VAT
3. `POST /order` — create order with lines
4. `POST /invoice` — create invoice
5. `GET /invoice/paymentType` — resolve payment type
6. `PUT /invoice/{id}/:payment` — pay full amountOutstanding

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `invoice_found` | — | ✅ |
| `payment_registered` | — | ✅ |

## Current State

**FIXED.** Both at 2.67. Already uses concurrent payment type resolution.

## Action Required

None.
