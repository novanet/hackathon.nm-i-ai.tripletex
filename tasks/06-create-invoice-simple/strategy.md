# Task 06 — Create Invoice (Simple)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 06 |
| **Task Type** | `create_invoice` |
| **Variant** | Simple single-line invoice |
| **Tier** | 2 |
| **Our Score** | 1.33 |
| **Leader Score** | 1.50 |
| **Gap** | -0.17 |
| **Status** | ⚠️ Behind (efficiency) |
| **Handler** | `InvoiceHandler.cs` |
| **Priority** | #3 — LOW effort, small gain |

## What It Does

Create a simple invoice with one line item for a customer.

## API Flow

1. `POST /customer` — create customer (POST-first strategy)
2. `GET /ledger/vatType?typeOfVat=OUTGOING&count=100` — resolve VAT type
3. `POST /order` — create order with order lines + VAT
4. `POST /invoice` — create invoice from order
5. `PUT /invoice/{id}/:send` — send invoice

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `invoice_found` | 2 | ✅ |
| `has_customer` | 1 | ✅ |
| `has_amount` | 1 | ✅ |
| `correct_amount` | 1 | ✅ |

## Why We're Behind

Tiny efficiency gap (-0.17). Correctness is 100%. The leader uses fewer write calls.

## How to Fix

1. Check if `:send` is strictly necessary — if competition only checks `invoice_found` + amounts, skipping send saves 1 write call
2. Check if bank account POST is still happening — `EnsureCompanyBankAccount` might be adding an unnecessary write
3. Review if VAT type resolution triggers any write calls

## Effort

**TRIVIAL** — same fix will apply to Task 09 as well.

## Action Required

- [ ] Review InvoiceHandler write call count
- [ ] Remove unnecessary `:send` if not checked by competition
- [ ] Submit to verify improvement
