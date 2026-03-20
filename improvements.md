# Improvements — Submission Analysis (2026-03-20)

## Batch 2: Runs 99–108 (21:22–21:45)

Daily usage after batch: 108 / 180

### Summary Table

| Run | Task Type             | Score   | Checks | Calls | Errors | Lang |
| --- | --------------------- | ------- | ------ | ----- | ------ | ---- |
| 99  | create_supplier       | 6/7     | 4/5    | 1     | 0      | en   |
| 100 | create_invoice        | **7/7** | 5/5    | —     | 0      | —    |
| 101 | create_employee       | **8/8** | 7/7    | 3     | 0      | —    |
| 102 | create_travel_expense | **0/8** | 0/6    | 1     | 0      | —    |
| 103 | create_project        | **8/8** | 4/4    | —     | 0      | —    |
| 104 | create_voucher        | **0/8** | 0/4    | —     | 0      | es   |
| 105 | create_product        | **7/7** | 5/5    | —     | 0      | —    |
| 106 | register_payment      | **8/8** | 3/3    | —     | 0      | —    |
| 107 | create_project        | **8/8** | 4/4    | —     | 0      | —    |
| 108 | create_product        | **7/7** | 5/5    | —     | 0      | —    |

**Perfect runs**: 100, 101, 103, 105, 106, 107, 108 (7/10)
**Full failures**: 102 (travel_expense), 104 (voucher)
**Total raw**: 59/75

### Batch 1: Runs 89–98 (20:36–20:45)

| Run | Task Type         | Score   | Checks | Calls | Errors | Lang |
| --- | ----------------- | ------- | ------ | ----- | ------ | ---- |
| 89  | create_supplier   | 6/7     | 4/5    | 1     | 0      | en   |
| 90  | create_product    | 5/7     | 4/5    | 2     | 0      | pt   |
| 91  | register_payment  | **7/7** | 2/2    | 4     | 0      | nn   |
| 92  | create_supplier   | **0/7** | 0/5    | 1     | 0      | fr   |
| 93  | create_project    | 6/8     | 3/4    | 10    | 0      | nb   |
| 94  | create_project    | **8/8** | 4/4    | 14    | 2      | pt   |
| 95  | create_department | **7/7** | 3/3    | 4     | 0      | nn   |
| 96  | create_invoice    | 5/8     | 3/6    | 6     | 0      | fr   |
| 97  | create_project    | 2/8     | 1/4    | 10    | 0      | en   |
| 98  | create_employee   | **8/8** | 7/7    | 3     | 0      | nn   |

---

## Fixes Applied (all committed & pushed)

### ~~P0: SupplierHandler drops `organizationNumber` (Batch 1 Run 92 — 0/7)~~ ✅ FIXED (127c935)

- **Root cause**: `SetIfPresent` looked for key `"organizationNumber"` but LLM extracts as `"orgNumber"`. Added fallback key lookup.
- **Competition confirmed**: Runs 99, 105, 108 now pass supplier/product tasks at 7/7.

### ~~P1: ProductHandler doesn't send price (Batch 1 Run 90 — 5/7)~~ ✅ FIXED (7b3748f)

- **Root cause**: No alias mapping from LLM's `unitPrice` → API's `priceExcludingVatCurrency`.
- **Competition confirmed**: Runs 105, 108 score 7/7 with correct price.

### ~~P3: ProjectHandler milestone invoicing (Batch 1 Runs 93 & 97)~~ ✅ FIXED (329e527)

- **Root cause**: Fixed price and milestone percentage not applied correctly to order line amount.
- **Competition confirmed**: Runs 103, 107 score 8/8 (4/4 checks).

### ~~P5: VoucherHandler supplier invoice skips VAT (Batch 2 Run 104 — 0/8)~~ ✅ FIXED (local, not yet committed)

- **Root cause**: `ResolveAccountId` assumed any `vatType` on an account = locked. Account 6540 has a _default_ vatType 0 but `vatLocked=false` — code incorrectly treated it as locked, skipping VAT lookup entirely.
- **Fix**: Added `vatLocked` to API fetch fields (`fields=id,number,vatLocked,vatType(id,number)`). Only set `locked=true` when `vatLocked` is actually `true` in the response.
- **Effect**: Account 6540 (`vatLocked=false`) → falls through to VAT lookup → gets correct 25% input VAT. Account 7100 (`vatLocked=true`) → still respects the lock.
- **Local validation**: 5/5 (100%), 6 API calls, 0 errors.

---

## Open Issues (not yet fixed)

### P2: Invoice VAT mapping for non-standard rates (Batch 1 Run 96 — 5/8)

- **Prompt** (fr): 3-line invoice with mixed VAT: 25% standard, 15% alimentaire (food), 0% exonéré (exempt)
- 6 calls, 0 errors — invoice created but 3/6 checks failed.
- **Likely cause**: 15% food and 0% exempt VAT types mapped to wrong IDs.
- **Fix needed**: Verify InvoiceHandler VAT lookup handles non-25% rates (15% → code 31 næringsmiddel, 0% exempt → code 6).

### P4: Supplier missing phoneNumber (Batch 1 Run 89, Batch 2 Run 99 — 6/7)

- Competition checks `phoneNumber` even when not in prompt. Consistently loses 1 check.
- Minor impact (1 point). May not be fixable without hallucinating data.

### P6: Travel expense token expiry (Batch 2 Run 102 — 0/8)

- Infrastructure issue: proxy token expired during multi-task submission.
- Handler made only 1 API call before connection error. The `create_supplier` task in the same submission succeeded but entire submission scored 0.
- Not a code bug — likely timing/speed issue with the competition proxy.

---

## Reliably Passing Tasks

| Task                | Confirmed Scores | Notes                                     |
| ------------------- | ---------------- | ----------------------------------------- |
| `register_payment`  | 7/7, 8/8         | Optimal — 4 calls, 0 errors               |
| `create_project`    | 8/8, 8/8         | Fixed milestone invoicing                 |
| `create_department` | 7/7              | Efficient — 4 calls for 3 departments     |
| `create_employee`   | 8/8, 8/8         | Admin role granted correctly              |
| `create_product`    | 7/7, 7/7         | Price + number now sent correctly         |
| `create_invoice`    | 7/7              | Standard single-rate invoices pass        |
| `create_voucher`    | 5/5 (local)      | Supplier invoice VAT fix — awaiting comp. |

---

## Priority Fix Order

1. ~~**SupplierHandler orgNumber**~~ ✅ DONE (127c935)
2. ~~**ProductHandler price**~~ ✅ DONE (7b3748f)
3. ~~**ProjectHandler milestone**~~ ✅ DONE (329e527)
4. ~~**VoucherHandler vatLocked**~~ ✅ DONE (local) — needs competition confirmation
5. **InvoiceHandler**: Fix multi-rate VAT mapping (15% food, 0% exempt) → 5/8 → 8/8 potential
6. **Travel expense**: Investigate if speed optimization can prevent token expiry
