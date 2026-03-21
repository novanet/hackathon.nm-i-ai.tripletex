# Task 11 — Supplier Invoice (Leverandørfaktura)

**Status**: 0/4 on all attempts (10 submissions). T2 multiplier, max 4.0 pts.  
**Competition checks**: 4 checks, score_max=8.0. Validator looks for SupplierInvoice entities.  
**Last attempt**: 2026-03-21T03:05:51

---

## Root Cause

`/supplierInvoice` entities are **read-only** — they are only created as a side-effect of other operations; there is no `POST /supplierInvoice`. The entire `/incomingInvoice` endpoint (which is the primary creation path) is **403-gated** behind a paid Tripletex module ("Faktura inn") that is not enabled in the sandbox or competition environment.

---

## Phase A Results (2026-03-21)

Script used: `scripts/Phase-A-Test.ps1`

| Test                                                   | HTTP         | Finding                                                                                        |
| ------------------------------------------------------ | ------------ | ---------------------------------------------------------------------------------------------- |
| **BASELINE** `GET /supplierInvoice` (with date range)  | 200, count=0 | OK — date params `invoiceDateFrom`/`invoiceDateTo` are **required** (returns 422 without them) |
| **BASELINE** `GET /supplierInvoice/forApproval`        | 200, count=0 | Endpoint works                                                                                 |
| **A0** `POST /ledger/voucher/importDocument`           | **201** ✅   | Creates voucher ID 608896860 via `.values[0].id` (ListResponse, not `.value.id`)               |
| **A1** `GET /supplierInvoice?voucherId={id}`           | 200, count=0 | importDocument does **NOT** create a SupplierInvoice                                           |
| **A1b** Broad SI search after import                   | 200, count=0 | No new SI entities anywhere                                                                    |
| **A2** `GET /supplierInvoice/forApproval` after import | 200, count=0 | Nothing queued                                                                                 |
| **A3** `GET /incomingInvoice/{voucherId}`              | **403**      | Module not enabled                                                                             |
| **A3b** `GET /incomingInvoice` list                    | 400 (405)    | GET list not allowed                                                                           |
| **A4** Inspect voucher from importDocument             | 200          | `voucherType=null`, `postings=[]` — unprocessed document only                                  |
| **A5** `GET /ledger/voucherType`                       | 200          | `id=9912091 name='Leverandørfaktura'` exists                                                   |
| **A6** `POST /incomingInvoice`                         | **403**      | Module permission denied                                                                       |
| **A6b** `PUT /incomingInvoice/{voucherId}`             | **403**      | Module permission denied                                                                       |

### Key API Quirks Discovered

- `GET /supplierInvoice` requires `invoiceDateFrom` AND `invoiceDateTo` as **required** query params → returns 422 without them. Use `?invoiceDateFrom=2020-01-01&invoiceDateTo=2030-01-01` for broad searches.
- `POST /ledger/voucher/importDocument` response is `ListResponseVoucher` → use `.values[0].id`, NOT `.value.id`.
- importDocument creates a voucher with `voucherType=null`, zero postings — it is just a document attachment, NOT a supplier invoice.
- All `/incomingInvoice` endpoints are behind a module permission (code 9000 = feature not available).

---

## Voucher Types Available

From `GET /ledger/voucherType` in sandbox:

| id          | name                    |
| ----------- | ----------------------- |
| 9912090     | Utgående faktura        |
| **9912091** | **Leverandørfaktura**   |
| 9912092     | Purring                 |
| 9912093     | Betaling                |
| 9912094     | Lønnsbilag              |
| 9912095     | Terminoppgave           |
| 9912096     | Mva-melding             |
| 9912097     | Betaling med KID-nummer |
| 9912098     | Remittering             |
| 9912099     | Bankavstemming          |
| 9912100     | Reiseregning            |
| 9912101     | Ansattutlegg            |
| 9912102     | Åpningsbalanse          |
| 9912103     | Tolldeklarasjon         |
| 9912104     | Pensjon                 |
| 9912105     | Refusjon av sykepenger  |
| 9912106     | Øreavrunding            |

---

## Possible Further Testing (Prioritized)

### Phase B — Create voucher with explicit `Leverandørfaktura` type + postings

**Hypothesis**: `POST /ledger/voucher` with `voucherType.id=9912091` and proper debit/credit postings may cause the competition validator to treat it as a SupplierInvoice, even if `GET /supplierInvoice` doesn't list it.

```json
{
  "date": "2026-03-21",
  "description": "Leverandørfaktura from Supplier AS",
  "voucherType": { "id": 9912091 },
  "postings": [
    { "date": "2026-03-21", "description": "...", "account": { "id": <6500 acct id> }, "amountGross": 12500.00, "vatType": { "id": <25% input VAT id> } },
    { "date": "2026-03-21", "description": "...", "account": { "id": <2400 acct id> }, "amountGross": -12500.00 }
  ]
}
```

If this creates a SI record accessible via `GET /supplierInvoice`, it solves the problem cleanly.

**Test**: Create voucher with type=9912091, then search `/supplierInvoice?invoiceDateFrom=...&invoiceDateTo=...` to see if it appears.

---

### Phase C — Enable incoming invoice module first

**Hypothesis**: The sandbox account can enable the "Faktura inn" module via API, after which `/incomingInvoice` becomes available.

**Test**:

```
GET /module
PUT /module/{id} with isActive=true  (find the incomingInvoice module ID)
```

Or via company settings:

```
GET /company/settings
PUT /company/settings  (toggle relevant boolean)
```

If the module can be activated, then the standard `POST /incomingInvoice` flow becomes available.

---

### Phase D — `PUT /ledger/voucher/{id}/:sendToLedger` on imported voucher

**Hypothesis**: The imported voucher (from importDocument) can be "sent to ledger" via an action endpoint, which may create a SupplierInvoice as a side effect.

**Test**:

```
POST /ledger/voucher/importDocument  → get voucherId
PUT /ledger/voucher/{voucherId}/:sendToLedger
GET /supplierInvoice?voucherId={voucherId}
```

---

### Phase E — `POST /supplierInvoice` (does it exist undocumented?)

**Hypothesis**: The API may have an undocumented `POST /supplierInvoice` endpoint not in the OpenAPI spec.

**Test**: Try `POST /supplierInvoice` with a minimal body and observe the response code.

- 405 Method Not Allowed = definitely doesn't exist
- 400/422 = exists but body is wrong → iterate
- 403 = exists but module-gated

---

### Phase F — `POST /ledger/voucher` with vendor link fields

**Hypothesis**: The voucher POST accepts fields like `vendor`, `vendorId`, or `invoiceNumber` that cause it to be indexed as a supplier invoice.

Check OpenAPI schema for `Voucher` model: look for `vendor`, `vendorInvoiceNumber`, `supplierInvoiceType` fields.

---

### Phase G — `PUT /ledger/voucher/{id}` with voucherType change

**Hypothesis**: Change an existing plain voucher's `voucherType` to `{id: 9912091}` via PUT update — may trigger SI creation.

**Test**:

```
POST /ledger/voucher (plain)  → voucherId
GET /ledger/voucher/{id}      → get version
PUT /ledger/voucher/{id}  { ...existing fields, voucherType: { id: 9912091 }, version: N }
GET /supplierInvoice?voucherId={id}
```

---

### Phase H — Competition environment module check

The competition environment may have different modules enabled vs the local sandbox. A blind submission with the current VoucherHandler Path B (plain voucher) currently scores 0/4, but maybe after enabling the module in the handler it would score higher.

Try activating `incomingInvoice` module in the handler before creating the SI:

1. `GET /module` → find incoming invoice module
2. `PUT /module/{id}/:activate` or patch settings
3. `POST /incomingInvoice`

---

## Current Handler Behavior (VoucherHandler.cs)

**Path A** (attempted first): `importDocument` → `PUT /supplierInvoice/voucher/{id}/postings` → HTTP 500 in sandbox  
**Path B** (fallback): Plain `POST /ledger/voucher` with type inference → scores 0/4 in competition (creates a voucher but not a SupplierInvoice)

---

## Recommendation

Test in order: **B → C → E → D → F**

Phase B (voucher with explicit Leverandørfaktura type) is cheapest and most likely to work if the competition validator is checking `voucherType.name` rather than `GET /supplierInvoice`. Phase C (module activation) would unlock the "proper" path but requires figuring out the module ID.
