# Task 07 — Register Payment (Simple Existing)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 07 |
| **Task Type** | `register_payment` |
| **Variant** | Simple payment on existing invoice |
| **Tier** | 2 |
| **Our Score** | 2.00 |
| **Leader Score** | 2.00 |
| **Gap** | 0 (tied) |
| **Status** | ✅ FIXED — Tied with leader |
| **Handler** | `PaymentHandler.cs` → `HandleSimplePayAsync` |
| **Priority** | None — already maxed |

## What It Does

Find a customer's existing unpaid invoice and register payment for the full outstanding amount.

## API Flow

1. `GET /customer?name=...` — find customer
2. `GET /invoice?customerId=X` — find unpaid invoice (amountOutstanding > 0)
3. `GET /invoice/paymentType` — resolve payment type
4. `PUT /invoice/{id}/:payment` — register payment with `paidAmount = amountOutstanding`

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `invoice_found` | — | ✅ |
| `payment_registered` | — | ✅ (amountOutstanding = 0) |

## Current State

**FIXED.** Scoring 2.00 (maximum). No changes needed.

## Action Required

None.
