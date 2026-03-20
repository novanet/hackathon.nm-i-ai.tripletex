# Tripletex Developer Documentation Index

> Scraped from https://developer.tripletex.no/docs/documentation/ on 2026-03-20.
> Contains all sections relevant to API integration. Omits marketing/marketplace/Postman setup pages.

---

## Table of Contents

1. [Authentication and Tokens](#authentication-and-tokens)
2. [Integration Best Practices](#integration-best-practices)
3. [Using vatTypes / VAT-codes](#using-vattypes--vat-codes)
4. [FAQ: General](#faq-general)
5. [FAQ: Invoice/Order](#faq-invoiceorder)
6. [FAQ: Customer](#faq-customer)
7. [FAQ: Product/Inventory](#faq-productinventory)
8. [FAQ: SupplierInvoice / Attestation](#faq-supplierinvoice--attestation)
9. [FAQ: Ledger/Voucher](#faq-ledgervoucher)
10. [FAQ: Departments](#faq-departments)
11. [Webhooks](#webhooks)
12. [Checksum](#checksum)
13. [Debt Collection (Voucher Status)](#debt-collection-voucher-status)

---

## Authentication and Tokens

Authentication is done via **Basic access authentication**.

- **Username**: specifies which company to access.
  - `0` (zero) or blank = the company of the employee (owner of the employee token).
  - Any other value = accountant client. Use `GET /company/>withLoginAccess` to list clients.
- **Password**: the `sessionToken`.

### Token Creation Flow

Two tokens are needed to create a session token:

1. **Consumer token** — issued per developer/application (one for test, one for production)
2. **Employee token** — created by the end user in the Tripletex GUI

**Create session**: `PUT /token/session/:create` with consumer + employee tokens and an expiration date.

- Response contains `value.token` — this is your session token.
- Expiration: server time (CET) crosses midnight on the specified date.

**IMPORTANT**: Test credentials (`api-test.tripletex.tech`) do NOT work in production (`tripletex.no`), and vice versa.

### Employee Token Entitlements

- A token cannot have entitlements the owner user doesn't have.
- "All entitlements" on a token owned by a limited user → entitlements activated but greyed out.
- Check entitlements via:
  1. `GET /token/session/>whoAmI` → get `employeeId` (owner) and `actualEmployeeId` (token user)
  2. `GET /employee/entitlement?employeeId={id}` for each → compare results. Only shared entitlements are effective.

### Accountant Clients & Tokens

Accountant tokens work differently:

- After creating a session token, you can only use username `0` for `GET /company/>withLoginAccess` and `GET /token/session/>whoAmI`.
- To access a client account: use the client's company ID (from `withLoginAccess` response) as the **username**, session token as **password**.
- The accountant token has NO access to the accounting office's own account data.

---

## Integration Best Practices

### Minimize Requests

- Use query parameters to filter: `orderDateFrom`, `orderDateTo`, `isClosed`, `customerId`, etc.
- This reduces database load and response time.

### Minimize Response Size

- Use the `fields` parameter to fetch only needed data.
- Example: `fields=number,customer(displayName)` on `GET /order`.
- Saves bandwidth and processing on both sides.

### Error Handling

- Log request IDs and HTTP status codes.
- Display the error code and developer message to users.
- Parse `validationMessages` from error responses for specific field-level errors.

### Syncing Data

- Do an **initial full sync**, then use **webhooks** for ongoing changes.
- Alternative: **checksum** method for smaller/infrequently-changed datasets.
- Avoid polling with repeated GET requests.

---

## Using vatTypes / VAT-codes

When using VAT-codes (in orderLines or postings), ensure:

1. **Valid for endpoint type**: voucher = incoming (inbound), orderLines = outgoing (outbound)
2. **Active in the Tripletex account**: Enable via GUI: Accounts → Settings → VAT-settings
3. **Compatible with account**: Check `vatLocked` flag and allowed VAT types for the account
4. **Must exist in Tripletex**

### Validation Steps

- `GET /ledger/account` → check `legalVatTypes` for a given account
- `GET /ledger/vatType?typeOfVat=incoming|outgoing` → returns only active VAT-codes
- Verify the `accountId` allows the specified `vatType`

### VAT Type Reference Table (Key Entries)

| DB ID | Number | Name                                                          | Direction | Tax %  |
| ----- | ------ | ------------------------------------------------------------- | --------- | ------ |
| 1     | 1      | Deduction of input tax, high rate                             | Inbound   | 25%    |
| 11    | 11     | Deduction of input tax, medium rate                           | Inbound   | 15%    |
| 111   | 12     | Deduction of input tax, raw fish                              | Inbound   | 11.11% |
| 12    | 13     | Deduction of input tax, low rate                              | Inbound   | 12%    |
| 3     | 3      | Outbound tax, high rate                                       | Outbound  | 25%    |
| 31    | 31     | Outbound tax, medium rate                                     | Outbound  | 15%    |
| 311   | 32     | Outgoing fee, raw fish                                        | Outbound  | 11.11% |
| 32    | 33     | Outgoing fee, low rate                                        | Outbound  | 12%    |
| 5     | 5      | No output tax (within VAT Act)                                | Outbound  | 0%     |
| 51    | 51     | Tax-free inland turnover with reverse liability               | Outbound  | 0%     |
| 52    | 52     | Duty-free export of goods and services                        | Outbound  | 0%     |
| 6     | 6      | No output tax (outside VAT Act)                               | Outbound  | —      |
| 7     | 7      | No tax processing (revenues)                                  | Outbound  | 0%     |
| 230   | 20     | Basis, no input tax on importation                            | Inbound   | 0%     |
| 23    | 14     | Deduction of input tax on importation, high rate              | Inbound   | 100%   |
| 24    | 15     | Deduction of input tax on importation, medium rate            | Inbound   | 100%   |
| 13    | 21     | Basis input tax on importation, high rate                     | Inbound   | 0%     |
| 14    | 22     | Basis including import tax, medium rate                       | Inbound   | 0%     |
| 81    | 81     | Deduction of input tax on importation, high rate              | Outbound  | 25%    |
| 82    | 82     | Input tax without deduction on importation, high rate         | Inbound   | 25%    |
| 83    | 83     | Deduction of input tax on importation, medium rate            | Inbound   | 15%    |
| 84    | 84     | Input tax without deduction on importation, medium rate       | Inbound   | 15%    |
| 85    | 85     | Basic, duty-free importation                                  | Inbound   | 0%     |
| 34    | 86     | Deduction of input tax, services from abroad, high rate       | Inbound   | 25%    |
| 35    | 87     | Purchase of services from abroad without deduction, high rate | Inbound   | 25%    |
| 88    | 88     | Deduction of input tax, services from abroad, low rate        | Inbound   | 12%    |
| 89    | 89     | Purchase of services from abroad without deduction, low rate  | Inbound   | 12%    |
| 91    | 91     | Deduction of input tax, climate quotas/gold                   | Inbound   | 25%    |
| 92    | 92     | Purchase of climate quotas/gold without compensation          | Inbound   | 25%    |

**Special codes**: TAP-_ (loss of claim), JUST-_ (VAT adjustment for capital goods), TILB-_ (refund of VAT for capital goods), UTTAK-_ (withdrawal).

---

## FAQ: General

### Sub-object References in POST Requests

When referencing an existing object (e.g., account in a posting), use **only the ID**:

```json
"account": { "id": 53120749 },
"vatType": { "id": 3 }
```

Do NOT set `name`, `number`, etc. on referenced objects — that causes "not found" errors. The required fields shown in Swagger are only for creating new objects, not referencing existing ones.

### "Result set too large" Error

The `count` parameter only limits how many objects are returned in the response — it does NOT limit the database query. Use additional filters (date, account, etc.) to stay under the 10,000 object limit.

### "An ID cannot be set when creating a new object"

Never define `id` or `version` in POST requests. These are system-assigned on creation. `version` is only used in PUT (update) requests.

### "Object not found"

- You're likely using a wrong ID (not the internal Tripletex ID).
- Or the ID belongs to a different company than your authenticated context.
- Use GET on the relevant endpoint to find the correct ID.

### Organization Number Validation

A valid-looking org number can fail if the customer's `physicalAddress.country` doesn't match. For foreign customers, you MUST set the correct country in `physicalAddress`.

### Fields Parameter for Related Objects

Expand related objects in GET requests:

- `fields=*,orderLines(*)` — all order fields + all fields on each order line
- `fields=number,customer(displayName)` — just order number + customer display name

### Checking Token Entitlements

1. `GET /token/session/>whoAmI` → `employeeId` and `actualEmployeeId`
2. `GET /employee/entitlement?employeeId={id}` for each
3. Only entitlements present on BOTH users are effective for the token.

---

## FAQ: Invoice/Order

### Price Including/Excluding VAT Error

**Error**: "Enhetspris må være med mva" / "Enhetspris må være uten mva"

**Cause**: Mismatch between order's VAT display setting and which price field you set on order lines.

**Solution**: Use `isPrioritizeAmountsIncludingVat` on the order object:

- `isPrioritizeAmountsIncludingVat = true` → use `unitPriceIncludingVatCurrency` on order lines
- `isPrioritizeAmountsIncludingVat = false` → use `unitPriceExcludingVatCurrency` on order lines
- **Never define both** price fields simultaneously.

The default is determined by the account's invoice settings.

### Income Account for Order Lines

Determined in two ways:

1. **With Product**: Uses the product's income account IF the VAT type on product matches the VAT type on the order line. Otherwise falls back to default.
2. **Description Only**: Posts to the default income account for the VAT type.

You **cannot directly override** the income account on an order line. Defaults managed in: Accounts → Settings → Accounting Settings → Posting Rules.

### Invoice Send Method

Determined by the **customer object** on the order/invoice. Check via `GET /customer` → `invoiceSendMethod`.

### Checking if Invoice is Paid

`GET /invoice/{id}` → if `amountOutstanding == 0`, the invoice is fully paid/credited.

### Preventing Invoice from Being Sent

Set `sendToCustomer=FALSE` (default is TRUE) in:

- `PUT /order/{id}/:invoice`
- `POST /invoice/`

### Payment Information for Invoices

1. Find `customerId` and `invoice.number`
2. `GET /ledger/posting/openPost` with `customerId` and account 1500
3. Check for `closeGroupId`:
   - `null` → posting is open (unpaid)
   - Has value → use `GET /ledger/closeGroup` to find related postings

### Currency Values

Updated from Norges Bank at 06:10 and 18:10 CET daily.

---

## FAQ: Customer

### Customer Account Balance

Use `GET /ledger/posting/openPost` with:

- `date` (to and EXCLUDING — add +1 day for today's postings)
- `accountId` (default customer account is number 1500, get ID from `GET /ledger/account`)
- `customerId`

Check `closeGroup`:

- `null` → posting is open (unpaid)
- Has ID → use `GET /ledger/closeGroup` to find related closed postings

### PostalAddress Required for EHF

Triggered when:

- `isPrivateIndividual = false` (the default)
- `invoiceSendMethod = EHF` (also the default for non-private-individual)

**Solutions**:

- Provide a valid `postalAddress` (required by Peppol BIS Billing 3.0)
- Or set `invoiceSendMethod = EMAIL` to avoid the requirement

### Organization Number Validation Errors

Usually caused by `physicalAddress.country` defaulting to Norway. For foreign customers, set the correct country in `physicalAddress` for the org number to validate.

---

## FAQ: Product/Inventory

### discountGroup

Internal use only (wholesale integration). Not useful for external developers.

### inventoryLocationId on Stocktaking

The `location` field on productLine in stocktaking endpoints is read-only and for internal use. Changing inventory location is only supported via the Tripletex GUI.

---

## FAQ: SupplierInvoice / Attestation

### approvalListElement

Part of the attestation/approval workflow for vouchers. Ensures vouchers are approved before bookkeeping (unless the company allows bookkeeping before approval).

### organisationLevel Values

| Value | Level                |
| ----- | -------------------- |
| 0     | In voucher reception |
| 1     | Project              |
| 2     | Department           |
| 3     | Company              |
| 4     | Employee             |
| 5     | Bookkept             |

Users with "company attestant" privilege can complete the full attestation process.

### approvalListElement Status Values

| Value | Status                  |
| ----- | ----------------------- |
| 0     | NONE (not yet acted on) |
| 1     | APPROVED                |
| 2     | REJECTED                |
| 3     | NOT_RELEVANT            |

---

## FAQ: Ledger/Voucher

### "Kunde mangler" / "Leverandør mangler" / "Ansatt mangler" Errors

When posting to an account with `ledgerType` other than `GENERAL`, you must include the matching entity ID in the posting:

- `ledgerType = CUSTOMER` → include `"customer": { "id": <id> }`
- `ledgerType = SUPPLIER` → include `"supplier": { "id": <id> }`
- `ledgerType = EMPLOYEE` → include `"employee": { "id": <id> }`

**Example**: Account 1500 (Kundefordringer) has `ledgerType: "CUSTOMER"` and `vatLocked: true`.

```json
{
  "row": 3,
  "date": "2021-11-01",
  "amountGross": -500,
  "amountGrossCurrency": -500,
  "account": { "id": 53120823 },
  "customer": { "id": 17788588 }
}
```

---

## FAQ: Departments

### When is Department Required for Employees?

If department functionality has been activated **at any point** in the account, ALL new employees must be assigned a department. This persists even if the functionality is later deactivated.

### Checking if Department Functionality is Active

- `GET /department` → if `departmentManager` field is missing from response AND only default "Avdeling" is returned → department functionality has NOT been activated.
- Enable via GUI: Accounts → Settings → Accounting rules → Modules → "Department costing"

---

## Webhooks

### Available Events

| Event                                  | Description                                 |
| -------------------------------------- | ------------------------------------------- |
| `invoice.charged`                      | New invoice created and charged             |
| `supplier.create/update/delete`        | Supplier CUD                                |
| `employee.create/update/delete`        | Employee CUD                                |
| `order.create/update/delete`           | Order CUD                                   |
| `product.create/update/delete`         | Product CUD                                 |
| `account.create/update/delete`         | Booking account CUD                         |
| `project.create/update/delete`         | Project CUD                                 |
| `customer.create/update/delete`        | Customer CUD                                |
| `contact.create/update/delete`         | Contact CUD                                 |
| `archiverelation.create/update/delete` | Document archive relation CUD               |
| `notification.sent`                    | Notification sent to token owner            |
| `voucher.create/update/delete`         | Voucher CUD                                 |
| `voucherstatus.ready`                  | Voucher ready for processing from reception |

Each verb requires a separate subscription (e.g., `order.create`, `order.update`, `order.delete`).

### Subscribing

```
POST /event/subscription
{
  "event": "product.create",
  "targetUrl": "https://your.receiver",
  "fields": "*,currency(*)",
  "authHeaderName": "Authorization",
  "authHeaderValue": "Bearer abc123"
}
```

### Delivery Guarantees

- Guaranteed delivery with retry (increasing intervals over 30 hours).
- Auto-disable after persistent failures: status → `DISABLED_TOO_MANY_ERRORS`.
- Re-enable via `PUT /event/subscription/{id}`.
- Successful delivery resets the failure counter.

### Webhook Payload Format

```json
{
  "subscriptionId": 123,
  "event": "product.create",
  "id": 456,
  "value": {
    /* DTO filtered by fields */
  }
}
```

Delete events have `"value": null`.

---

## Checksum

Allows polling optimization using HTTP ETag-style caching:

1. Include `If-None-Match` header with any non-empty string on list endpoints.
2. Response includes `versionDigest` (the checksum).
3. On subsequent requests, set `If-None-Match` to the previous `versionDigest`.
4. If unchanged → HTTP 304 Not Modified (very fast).
5. If changed → normal response with new `versionDigest`.

### Limitations

- **False negatives**: Checksum may change even when your specific dataset hasn't.
- **False positives** (rare): Changes to user privileges or indirectly related objects may not update the checksum.
- Changes to direct sub-objects (e.g., orderLine → order) DO update the parent checksum.

---

## Debt Collection (Voucher Status)

For registered debt collection integrations:

1. Fetch invoices via `GET /invoice`
2. Post status on invoice voucher via `POST /voucherStatus`
3. Status types: `DEBT_COLLECTION` with statuses `PROCESSING`, `ERROR`, `DONE`
4. `ERROR` + comment → debt collection case deleted, customer notified
5. `DONE` → completes the debt collection
6. Use `externalObjectUrl` and `referenceNumber` to link to your system
7. VoucherStatuses are **immutable** (history tracking). Latest status is the active one.
