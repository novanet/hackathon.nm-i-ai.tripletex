# Task 12 — Domain Breakdown: Payroll Run for Sigrid Brekke

## Original Request (Nynorsk)

> Køyr løn for Sigrid Brekke (sigrid.brekke@example.org) for denne månaden. Grunnløn er 34950 kr. Legg til ein eingongsbonus på 15350 kr i tillegg til grunnløna.

## Parsed Request

- **Employee:** Sigrid Brekke (sigrid.brekke@example.org)
- **Pay period:** March 2026 (current month)
- **Action:** Run payroll — single payslip with two pay lines

---

## Gross Pay Components

| Component | Type | Amount (NOK) |
|---|---|---|
| Base salary (grunnlønn) | Recurring, monthly | 34 950 |
| One-time bonus (eingongsbonus) | Non-recurring | 15 350 |
| **Total gross pay** | | **50 300** |

---

## Accounting Entries (Double-Entry Bookkeeping)

### 1. Record gross wages

| Account | Debit (NOK) | Credit (NOK) |
|---|---|---|
| 5000 – Lønn (Salary expense) | 34 950 | |
| 5020 – Bonus (Bonus expense) | 15 350 | |
| 2920 – Skyldig lønn (Wages payable) | | 50 300 |

### 2. Withholdings (statutory deductions from gross)

| Account | Debit (NOK) | Credit (NOK) |
|---|---|---|
| 2920 – Skyldig lønn | *calculated* | |
| 2600 – Skattetrekk (Tax withholding) | | *calculated* |
| 2770 – Skyldig trygdeavgift (Employee NI) | | *if applicable* |

### 3. Employer's contributions (arbeidsgiveravgift)

| Account | Debit (NOK) | Credit (NOK) |
|---|---|---|
| 5400 – Arbeidsgiveravgift (Employer NI expense) | *14.1% of 50 300 = 7 092* | |
| 2780 – Skyldig arbeidsgiveravgift | | *7 092* |

### 4. Net pay disbursement

| Account | Debit (NOK) | Credit (NOK) |
|---|---|---|
| 2920 – Skyldig lønn (net after withholdings) | *net amount* | |
| 1920 – Bank | | *net amount* |

---

## Debit/Credit Verification (per Accountingverse)

Source: https://www.accountingverse.com/accounting-basics/double-entry-accounting.html
Source: https://www.accountingverse.com/accounting-basics/debit-vs-credit.html

| Rule from Accountingverse | Applied in entries |
|---|---|
| *"To increase an expense, you debit it"* | Salary expense (5000) **debited** 34 950, Bonus expense (5020) **debited** 15 350 |
| *"To increase a liability, you credit it"* | Wages payable (2920) **credited** 50 300, Tax withholding (2600) **credited**, Employer NI payable (2780) **credited** |
| *"To decrease a liability, you debit it"* | Wages payable (2920) **debited** when reducing for withholdings and net pay |
| *"To decrease an asset, you credit it"* | Bank (1920) **credited** when disbursing net pay |

All journal entries satisfy: **debit = credit**.

---

## Key Observations

- **The bonus is taxable** — subject to the same withholding tax (skattetrekk) and employer NI (arbeidsgiveravgift) as base salary, but potentially at a higher marginal rate (tabellkort vs. prosentkort depending on employer setup).
- **Non-recurring classification** — the bonus should be booked on a separate expense account (e.g., 5020) to keep recurring vs. one-time costs distinguishable in reporting.
- **Exact deductions** depend on the employee's tax card (skattekort) — percentage method or table method — which determines the withholding amount.
- **Employer NI rate** assumed at 14.1% (standard zone 1 in Norway); varies by geographic zone (1–5).

---

## Confidence Assessment: 8.5 / 10

### Confirmed correct

- Double-entry structure and debit/credit logic — fully aligned with Accountingverse and general GAAP.
- Expense accounts debited, liability accounts credited, asset (bank) credited on disbursement.

### Remaining uncertainty (Norway-specific)

1. **Account numbers** (5000, 5020, 2920, 2600, 2780, 1920) — based on NS 4102 (Norwegian standard chart of accounts). Employer may have variations.
2. **Arbeidsgiveravgift rate** (14.1%) — correct for zone 1; ranges 0%–14.1% by municipality.
3. **Tax withholding calculation** — requires the employee's actual tax card. Bonus may be taxed at a flat percentage rate (~50%) rather than the table rate.
4. **Separation of salary vs. bonus accounts** — using 5020 for bonus is best practice, not strictly required.

---

## Strategy Verification Against All Prompts

Verified against 40 payroll prompts in 6 languages (NO Bokmål, NO Nynorsk, DE, PT, FR, EN).

### Core pattern (covered — no changes needed)

~35 of 40 prompts follow the exact same structure:
- Employee (name + email) + pay period + base salary + one-time bonus
- Double-entry mechanics, account numbers, debit/credit rules all apply identically
- Only the amounts and employee details change

### Variants requiring strategy additions

#### 1. Salary-only (no bonus) — ~12 prompts

Examples: Ola Nordmann 45000 kr, Hans Hansen 50000 kr, Kari Larsen 40000 kr, etc.

**Change:** Step 1 (record gross wages) simplifies — only one debit line to 5000, no 5020 entry.
The remaining steps (withholdings, employer NI, disbursement) are structurally identical.

| Account | Debit (NOK) | Credit (NOK) |
|---|---|---|
| 5000 – Lønn (Salary expense) | *base salary* | |
| 2920 – Skyldig lønn (Wages payable) | | *base salary* |

#### 2. Batch payroll run (no specific employee) — 1 prompt

> "Kjør lønnskjøring for mars 2026"

**Change:** This is a batch operation — run payroll for ALL employees for the period, not a single payslip.
The accounting entries are the same per employee but aggregated. Strategy should note:
- Iterate over all active employees in the payroll register
- Apply the same per-employee logic (salary + optional bonus)
- Journal entries can be posted individually or as a consolidated batch voucher

#### 3. Fallback to manual vouchers — 2 prompts

> "Dersom lønns-API-et ikkje fungerer, kan du bruke manuelle bilag på lønnskontoer (5000-serien)"
> "If the salary API is unavailable, you can use manual vouchers on salary accounts (5000-series)"

**Change:** This is an operational fallback, not a structural accounting change. The journal entries
are identical — the difference is the *method of entry* (API call vs. manual voucher posting).
Strategy should note: if the payroll API is down, the same debits/credits are recorded
as manual journal entries (bilag) against the 5000-series expense accounts.

#### 4. Extra employee data (birth date) — 1 prompt

> "Emilie Dahl (fødselsdato 15.06.1990, e-post emilie.dahl@bedrift.no)"

**Change:** None to accounting strategy. Birth date is HR/employee-master data, not relevant
to the journal entries. Parse and pass to employee lookup but ignore for accounting purposes.

#### 5. Different pay period (April 2026) — 1 prompt

> "Ola Hansen ... for april 2026"

**Change:** None to accounting strategy. The pay period is a parameter — the journal entries
are dated to the applicable period. All debit/credit logic is period-agnostic.

#### 6. New/unknown employee — 1 prompt

> "Run payroll for Brand New Person with salary 50000 NOK"

**Change:** Operational concern — employee may not exist in the system. Strategy should note:
if employee is not found in the payroll register, employee master data must be created first
(name, tax card, bank account, NI zone) before payroll can be processed. The accounting
entries themselves remain unchanged once the employee exists.

### Conclusion

**No change to the accounting strategy is required.** The double-entry structure, account
numbers (NS 4102), and debit/credit rules documented above are correct for all 40 prompts.
The only variations are:
- Whether the bonus line (5020) is present or absent
- Whether execution is single-employee or batch
- Whether entry method is API or manual voucher
- Whether employee setup is needed first

All of these are operational/parameter differences, not accounting logic differences.

---

## Payroll Execution Summary

Run a single payslip for March 2026 with two pay lines:

1. **Lønnsart for grunnlønn** — 34 950 kr
2. **Lønnsart for eingongsbonus** — 15 350 kr (flagged as non-recurring/one-time)
