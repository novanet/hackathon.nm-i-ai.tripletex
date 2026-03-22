# Error Messages — Active Failures

Errors from `submissions.jsonl` and `sandbox.jsonl` after 2026-03-21 18:00 CET.
Tasks at max competition score (tied/leading) are excluded.

---

## Competition Errors (submissions.jsonl)

### API Validation Errors (422)

| Task Type | Handler | Error Message | Root Cause | Last Seen |
|---|---|---|---|---|
| `run_payroll` | PayrollHandler | `employee: Arbeidsforholdet er ikke knyttet mot en virksomhet.` | Employment not linked to a division (virksomhet) | 2026-03-21T23:54 |
| `create_employee` | EmployeeHandler | `email: Ugyldig format.` | LLM extracted malformed email address | 2026-03-22T01:53 |
| `create_employee` | EmployeeHandler | `lastName: Kan ikke være null.; firstName: Kan ikke være null.` | LLM failed to extract first/last name from prompt | 2026-03-21T23:23 |

### Token Expiration (403)

| Task Type | Handler | Error Message | Last Seen |
|---|---|---|---|
| `annual_accounts` | AnnualAccountsHandler | `Invalid or expired proxy token` | 2026-03-21T23:25 |

---

## Sandbox Errors (sandbox.jsonl)

| Task Type | Handler | Error Message | Root Cause | Last Seen |
|---|---|---|---|---|
| `bank_reconciliation` | BankReconciliationHandler | `Det eksisterer allerede en bankavstemming for denne kontoen i valgte periode.` | Duplicate bank reconciliation in sandbox (not a competition issue) | 2026-03-21T17:53 |
| `create_department` | DepartmentHandler | `departmentNumber: Nummeret er i bruk.` | Department number collision in sandbox | 2026-03-21T17:54 |
| `create_voucher` | VoucherHandler | `name: Feltet må fylles ut.` | Missing supplier name on voucher creation | 2026-03-21T17:59 |
| `create_employee` | EmployeeHandler | `firstName/lastName: Feltet har ikke korrekt lengde (1–100)` | LLM extraction produced empty or too-long names | 2026-03-21T17:56 |
| `create_employee` | EmployeeHandler | `dateOfBirth: Verdien er ikke av korrekt type for dette feltet.` | Wrong date format for dateOfBirth | 2026-03-21T17:57 |
| `create_employee` | EmployeeHandler | `date: Dato på lønnsraden er før arbeidsforholdets startdato.` | Salary date before employment start date | 2026-03-21T18:39 |
| `create_employee` | EmployeeHandler | `lastName: Cannot be null.; firstName: Cannot be null.` | LLM extraction failure (null names) | 2026-03-21T18:44 |
| `register_payment` | PaymentHandler | `orderLines.vatType.id: Invalid VAT code.` | Wrong VAT type ID used in order line | 2026-03-21T21:24 |
| `create_project` | ProjectHandler | `lastName: Length of field must be between 1 and 100.` | PM employee name extraction failure | 2026-03-22T01:31 |
| `create_project` | ProjectHandler | `orderLines.vatType.id: Invalid VAT code` (+ invoicing plan errors) | Wrong VAT type on project invoice plan | 2026-03-22T01:32 |
| `create_voucher` | VoucherHandler | `403: You do not have permission to access this feature.` | Permission issue on voucher endpoint | 2026-03-22T06:38 |

---

## Summary by Priority

1. **`run_payroll`** — Employment not linked to division. Recurring in every competition run (4× since cutoff). Blocks payroll scoring entirely.
2. **LLM extraction failures** — Null/invalid names and emails still happening in both competition and sandbox.
3. **VAT code mismatches** — Wrong VAT type IDs on order lines (register_payment, create_project).
4. **Token expiration** — annual_accounts lost to expired proxy token (unavoidable timing issue).
