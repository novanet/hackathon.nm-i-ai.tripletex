# Task 09 — Create Invoice (Multi-Line)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 09 |
| **Task Type** | `create_invoice` |
| **Variant** | Multi-line invoice with different VAT rates |
| **Tier** | 2 |
| **Our Score** | 2.67 |
| **Leader Score** | 3.00 |
| **Gap** | -0.33 |
| **Status** | ⚠️ Behind (efficiency) |
| **Handler** | `InvoiceHandler.cs` |
| **Priority** | #3 — LOW effort, small gain |

## What It Does

Create an invoice with multiple line items, potentially different VAT rates per line.

## API Flow

Same as Task 06 but with multiple order lines, each potentially with its own VAT type:

1. `POST /customer` — create customer
2. `GET /ledger/vatType?typeOfVat=OUTGOING&count=100` — resolve VAT types
3. `POST /order` — create order with multiple order lines + mixed VAT
4. `POST /invoice` — create invoice from order
5. `PUT /invoice/{id}/:send` — send invoice

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `invoice_found` | 2 | ✅ |
| `has_customer` | 1 | ✅ |
| `has_amount` | 1 | ✅ |
| `correct_amount` | 1 | ✅ |
| `has_order_lines` | 1 | ✅ |
| `invoice_sent` | 1 | ✅ |

## Why We're Behind

Same as Task 06 — efficiency gap from extra write calls. Correctness is 100%.

## How to Fix

Same fix as Task 06 — review and trim unnecessary write calls (`:send`, bank account POST, etc.).

## Effort

**TRIVIAL** — same fix as Task 06.

## Action Required

- [ ] Same optimization as Task 06
- [ ] Single submission verifies both tasks
