# Improvements — Analysis of 10 Submission Runs (2026-03-20)

Batch run: 20:36:57 – 20:45:48 | Daily usage: 89 → 98 / 180

## Summary Table

| Run | Task Type         | Score   | Checks | Normalized | Calls | Errors | Lang |
| --- | ----------------- | ------- | ------ | ---------- | ----- | ------ | ---- |
| 1   | create_supplier   | 6/7     | 4/5    | 0.86       | 1     | 0      | en   |
| 2   | create_product    | 5/7     | 4/5    | 0.71       | 2     | 0      | pt   |
| 3   | register_payment  | **7/7** | 2/2    | 1.75       | 4     | 0      | nn   |
| 4   | create_supplier   | **0/7** | 0/5    | 0.00       | 1     | 0      | fr   |
| 5   | create_project    | 6/8     | 3/4    | 1.50       | 10    | 0      | nb   |
| 6   | create_project    | **8/8** | 4/4    | 2.76       | 14    | 2      | pt   |
| 7   | create_department | **7/7** | 3/3    | 1.25       | 4     | 0      | nn   |
| 8   | create_invoice    | 5/8     | 3/6    | 1.25       | 6     | 0      | fr   |
| 9   | create_project    | 2/8     | 1/4    | 0.50       | 10    | 0      | en   |
| 10  | create_employee   | **8/8** | 7/7    | 1.33       | 3     | 0      | nn   |

**Perfect runs**: 3, 6, 7, 10 (4/10)
**Total raw**: 54/75

---

## Failures Ranked by Severity

### ~~P0: SupplierHandler drops `organizationNumber` (Run 4 — 0/7)~~ ✅ FIXED

- **Root cause**: `SetIfPresent` looked for key `"organizationNumber"` but LLM extracts as `"orgNumber"`. Added fallback key lookup.
- **Local validation after fix**: 5/5 (100%) — POST body now includes `"organizationNumber":"975630749"`

### P1: ProductHandler doesn't send price (Run 2 — 5/7)

- **Prompt** (pt): "Crie o produto 'Manutenção' com número de produto 9664. O preço é 5500 NOK sem IVA, utilizando a taxa padrão de 25 %."
- **LLM extraction**: Correctly extracted `unitPrice: 5500`
- **POST body sent**: `{"name":"Manutenção","number":"9664","vatType":{"id":1}}` — **no price field!**
- **Response confirmed**: `"priceExcludingVatCurrency":0,"priceIncludingVatCurrency":0`
- **Impact**: The `price` competition check fails every time.
- **Fix**: In `ProductHandler.cs`, add `priceExcludingVatCurrency` (and/or `priceIncludingVatCurrency`) to the POST body from the extracted `unitPrice`.

### P2: Invoice VAT mapping for non-standard rates (Run 8 — 5/8)

- **Prompt** (fr): 3-line invoice with mixed VAT: 25% standard, 15% alimentaire (food), 0% exonéré (exempt)
- **API calls**: 6 calls, 0 errors — invoice was created
- **Impact**: 3/6 checks failed. Likely the 15% food VAT and 0% exempt VAT types were mapped to wrong IDs.
- **Competition checks**: `invoice_found` (pass), `has_customer` (pass), `has_amount` (pass), but amount-related checks likely failed due to wrong VAT calculations.
- **Fix**: Verify VAT type lookup logic in `InvoiceHandler.cs` for rates beyond the standard 25%. The 15% rate uses VAT code 31 (food/næringsmiddel), and 0% exempt uses code 6.

### P3: Project milestone invoicing — partial percentage (Runs 5 & 9)

**Run 9 (2/8):**

- **Prompt** (en): "Set a fixed price of 170500 NOK on project 'Infrastructure Upgrade' for Brightstone Ltd (org no. 850116091). The project manager is Charlotte Walker. Invoice the customer for 33% of the fixed price as a milestone payment."
- 10 API calls, 0 errors. Only 1/4 checks passed.
- **Likely issue**: The 33% milestone amount (170500 × 0.33 = 56265) was calculated wrong, or the fixed price wasn't set on the project entity correctly, or the invoice amount was wrong.

**Run 5 (6/8):**

- **Prompt** (nb): "Sett fastpris 181650 kr på prosjektet 'Nettbutikk-utvikling' for Tindra AS. Fakturer kunden for 50% av fastprisen som en delbetaling."
- 10 API calls, 0 errors. 3/4 checks passed — only 1 failed.
- Better than Run 9 (50% is a cleaner calculation than 33%), but still losing 1 check.

- **Fix**: Review `ProjectHandler.cs` fixed price setting and milestone invoice amount calculation. Ensure the order line amount matches the exact percentage of the fixed price.

### P4: Supplier missing phoneNumber (Run 1 — 6/7)

- **Prompt** (en): "Register the supplier Silveroak Ltd with organization number 811867500. Email: faktura@silveroakltd.no."
- POST body included name, email, orgNumber — all correct. 4/5 checks passed.
- **Known issue**: Competition checks `phoneNumber` even when not in the prompt (documented in copilot-instructions.md).
- **Impact**: Loses 1 check (2 points) consistently on supplier tasks when phone isn't in prompt.
- **Fix**: This may not be fixable if the prompt doesn't provide a phone number. Could try sending an empty string vs omitting the field, but likely a structural competition check.

---

## Perfectly Passing Tasks (no action needed)

| Task                             | Notes                                                   |
| -------------------------------- | ------------------------------------------------------- |
| `register_payment`               | 7/7, 4 calls, 0 errors — optimal                        |
| `create_project` (some variants) | 8/8 when no milestone invoicing required                |
| `create_department`              | 7/7, 4 calls for 3 departments — efficient              |
| `create_employee`                | 8/8, 7/7 checks, 3 calls — admin role granted correctly |

---

## Priority Fix Order

1. ~~**SupplierHandler**: Always include `organizationNumber` in POST body → fixes 0/7 → 7/7 potential~~ ✅ DONE
2. **ProductHandler**: Include `priceExcludingVatCurrency` in POST body → fixes 5/7 → 7/7 potential
3. **InvoiceHandler**: Fix VAT type mapping for 15% food and 0% exempt → fixes 5/8 → 8/8 potential
4. **ProjectHandler**: Fix milestone percentage invoice amount calculation → fixes 2/8 → 8/8 potential
