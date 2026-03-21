# Task 21 — Create Employee from PDF Offer Letter (Tier 3)

## Overview

| Field | Value |
|---|---|
| **Task ID** | 21 |
| **Task Type** | `create_employee` |
| **Variant** | Employee from PDF offer/job letter (Portuguese) |
| **Tier** | 3 |
| **Our Score** | 1.50 |
| **Leader Score** | 2.36 |
| **Gap** | -0.86 |
| **Status** | ⚠️ Behind |
| **Handler** | `PdfEmployeeHandler.cs` → `EmployeeHandler.cs` |
| **Priority** | #10 — MEDIUM effort (same fix as Task 19) |

## What It Does

Portuguese prompt: "Voce recebeu uma carta de oferta (ver PDF anexo)..." — extract employee details from a PDF offer/job letter and create the employee with full onboarding.

## API Flow

Same as Task 19 — employee creation + employment + details.

## Competition Checks

Same extended checks as Task 19 (employee fields + employment details).

## Why We're Behind

Same root cause as Task 19 — PDF extraction doesn't capture all employment detail fields from the offer letter PDF. The Portuguese language may add extra extraction difficulty.

## How to Fix

**Same fix as Task 19.** Improving the LLM extraction prompt for PDF employment documents will help both tasks simultaneously.

Additional considerations:
- Portuguese PDF may have different field labels
- Offer letter format differs from employment contract — salary, start date, role may be labeled differently
- Occupation code may not be present in offer letters

## Effort

**MEDIUM** — same fix as Task 19, no extra work.

## Action Required

- [ ] Same as Task 19 — fix is shared
- [ ] Verify Portuguese PDF extraction specifically
- [ ] Test + submit
