# Task 03 — Create Product

## Overview

| Field | Value |
|---|---|
| **Task ID** | 03 |
| **Task Type** | `create_product` |
| **Variant** | Product with number + price |
| **Tier** | 1 |
| **Our Score** | 2.00 |
| **Leader Score** | 2.00 |
| **Gap** | 0 (tied) |
| **Status** | ✅ FIXED — Tied with leader |
| **Handler** | `ProductHandler.cs` |
| **Priority** | None — already maxed |

## What It Does

Create a product with name, product number, and price.

## API Flow

1. `POST /product` — create with `name`, `number`, `priceExcludingVatCurrency`
   - Do NOT include `vatType` (sandbox rejects it)
   - Alias `productNumber` → `number`, `price`/`unitPrice` → `priceExcludingVatCurrency`

## Competition Checks

| Check | Points | Status |
|---|:---:|:---:|
| `product_found` | — | ✅ |
| `name` | — | ✅ |
| `number` | — | ✅ |
| `price` | — | ✅ |

## Current State

**FIXED.** Scoring 2.00 (maximum). No changes needed.

## Action Required

None.
