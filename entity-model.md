# Tripletex Entity Model — Quick Reference

**Use this before implementing handlers, debugging failures, or understanding entity relationships.**
Covers all entities relevant to the 30 competition task types.

---

## Entity Relationship Diagram

```
┌──────────────┐
│   Currency   │◄──────────────────────────────────────────────────────────────┐
└──────────────┘                                                               │
                                                                               │
┌──────────────┐      ┌──────────────┐                                         │
│   Country    │◄─────│   Address    │                                         │
└──────────────┘      └──────┬───────┘                                         │
                             │                                                 │
┌──────────────┐      ┌──────▼───────┐     ┌──────────────┐                    │
│  Department  │◄─────│   Employee   │────►│  Employment  │───►┌──────────┐    │
│              │      │              │     └──────────────┘    │ Division │    │
│  .manager ──►│      │  .department │                         │ .munic.  │    │
└──────┬───────┘      └──────┬───────┘                         └────┬─────┘    │
       │                     │                                      │          │
       │              ┌──────┴────────────────────────┐      ┌──────▼───────┐  │
       │              │                               │      │ Municipality │  │
       │       ┌──────▼───────┐              ┌────────▼─────┐└─────────────┘   │
       │       │   Customer   │              │   Supplier   │                  │
       │       └──────┬───────┘              └──────┬───────┘                  │
       │              │                             │                          │
       │      ┌───────┴──────────────┐              │                          │
       │      │                      │              │                          │
┌──────▼──────▼──┐           ┌───────▼──────┐       │                          │
│    Project     │           │   Contact    │       │                          │
│                │           └──────────────┘       │                          │
│ .customer      │                                  │                          │
│ .projectMgr    │           ┌──────────────┐      ┌▼─────────────┐            │
│ .department    │           │   VatType    │◄─────│   Product    │            │
└──┬─────┬───────┘           └──────┬───────┘      └──────────────┘            │
   │     │                         │                      │                    │
   │     │                  ┌──────▼───────┐              │                    │
   │     │                  │  OrderLine   │◄─────────────┘                    │
   │     │                  │  .vatType    │                                    │
   │     │                  │  .product    │                                    │
   │     │                  └──────┬───────┘                                   │
   │     │                         │                                           │
   │     │                  ┌──────▼───────┐      ┌──────────────┐             │
   │     ├─────────────────►│    Order     │─────►│   Invoice    │─────────────┘
   │     │                  │  .customer   │      │  .orders[]   │
   │     │                  │  .orderLines │      │  .currency   │
   │     │                  └──────────────┘      └──────┬───────┘
   │     │                                               │
   │     │                                        ┌──────▼───────┐
   │     │                                        │   Voucher    │◄── SupplierInvoice
   │     │                                        │  .postings[] │    (created via voucher)
   │     │                                        └──────┬───────┘
   │     │                                               │
   │     │                                        ┌──────▼───────┐     ┌────────────────────┐
   │     │                                        │   Posting    │────►│  DimensionValue    │
   │     │                                        │  .account    │     │  .dimensionIndex   │
   │     │                                        │  .customer   │     └────────┬───────────┘
   │     │                                        │  .supplier   │              │
   │     │                                        │  .project    │     ┌────────▼───────────┐
   │     │                                        │  .department │     │  DimensionName     │
   │     │                                        │  .vatType    │     │  .dimensionIndex   │
   │     │                                        └──────────────┘     └────────────────────┘
   │     │
   │  ┌──▼──────────────┐      ┌──────────────────┐
   │  │  TravelExpense  │─────►│ TravelExpenseCost│
   │  │  .employee      │      │  .travelExpense  │
   │  │  .project       │      │  .vatType        │
   │  │  .department    │      │  .paymentType    │
   │  └─────────────────┘      └──────────────────┘
   │
   │  ┌──────────────────┐
   ├─►│  TimesheetEntry  │
   │  │  .project        │
   │  │  .activity       │     ┌──────────────────┐
   │  │  .employee       │     │   SalesModule    │
   │  └──────────────────┘     │  .name (enum)    │
   │                           │  .costStartDate  │
   │                           └──────────────────┘
   │
 ┌─▼────────────────┐      ┌──────────────────┐
 │ SalaryTransaction│─────►│     Payslip      │     ┌──────────────────┐
 │  .payslips[]     │      │  .employee       │     │ BankReconciliation│
 └──────────────────┘      │  .specifications │     │  .account        │
                           └──────────────────┘     │  .accountingPeriod│
                                                    └──────────────────┘
```

---

## Per-Entity Reference Cards

### Customer

**Endpoint:** `POST /customer` · `GET /customer` · `PUT /customer/{id}` · `DELETE /customer/{id}`

| Field               | Type           | Required | Notes                                             |
| ------------------- | -------------- | -------- | ------------------------------------------------- |
| name                | string         | **YES**  | Only required field                               |
| organizationNumber  | string         |          | 9-digit org number                                |
| email               | string         |          |                                                   |
| invoiceEmail        | string         |          | Separate from email                               |
| phoneNumber         | string         |          |                                                   |
| phoneNumberMobile   | string         |          |                                                   |
| isCustomer          | boolean        |          | Always set `true` on creation                     |
| isPrivateIndividual | boolean        |          |                                                   |
| postalAddress       | Address        |          | `{addressLine1, postalCode, city}`                |
| physicalAddress     | Address        |          | `{addressLine1, postalCode, city, country: {id}}` |
| accountManager      | Employee ref   |          | `{id}`                                            |
| department          | Department ref |          | `{id}`                                            |
| currency            | Currency ref   |          | `{id}`                                            |
| language            | enum           |          | `NO` or `EN`                                      |

**References TO:** Employee (accountManager), Department, Currency, Address, Country
**Referenced BY:** Order, Invoice, Project, Contact, Posting

**Search params:** `?name=`, `?organizationNumber=`, `?customerNumber=`

---

### Employee

**Endpoint:** `POST /employee` · `GET /employee` · `PUT /employee/{id}` · `DELETE /employee/{id}`

| Field                  | Type           | Required | Notes                                                      |
| ---------------------- | -------------- | -------- | ---------------------------------------------------------- |
| firstName              | string         | **YES**  |                                                            |
| lastName               | string         | **YES**  |                                                            |
| email                  | string         | **YES**  | Required for Tripletex users                               |
| userType               | enum           | **YES**  | `STANDARD`, `EXTENDED`, `NO_ACCESS`. Never `"0"` or empty  |
| department             | Department ref | **YES**  | `{id}` — mandatory, fetch first available if not specified |
| dateOfBirth            | string         |          | `YYYY-MM-DD`. Must be set before creating employment       |
| employeeNumber         | string         |          | Auto-generated if omitted                                  |
| phoneNumberMobile      | string         |          |                                                            |
| nationalIdentityNumber | string         |          |                                                            |
| bankAccountNumber      | string         |          |                                                            |
| address                | Address ref    |          |                                                            |
| isContact              | boolean        |          |                                                            |

**⚠ startDate is NOT an Employee field** — create via separate `POST /employee/employment`

**References TO:** Department, Address
**Referenced BY:** Project (projectManager), Order (ourContactEmployee), TravelExpense, Posting, Employment, Payslip

**Search params:** `?firstName=`, `?lastName=`, `?email=`, `?employeeNumber=`

**Action endpoints:**

- `PUT /employee/entitlement/:grantEntitlementsByTemplate?employeeId={id}&template=ALL_PRIVILEGES` — grant admin role
- `PUT /employee/entitlement/:grantClientEntitlementsByTemplate` — grant client entitlements

---

### Employment

**Endpoint:** `POST /employee/employment` · `GET /employee/employment` · `PUT /employee/employment/{id}`

| Field                    | Type         | Required | Notes                             |
| ------------------------ | ------------ | -------- | --------------------------------- |
| employee                 | Employee ref | **YES**  | `{id}`                            |
| startDate                | string       | **YES**  | `YYYY-MM-DD`                      |
| endDate                  | string       |          |                                   |
| division                 | Division ref |          | **Required for payroll**          |
| isMainEmployer           | boolean      |          |                                   |
| taxDeductionCode         | enum         |          | `loennFraHovedarbeidsgiver`, etc. |
| noEmploymentRelationship | boolean      |          |                                   |

**⚠ Division must be set before running salary/payroll transactions**

**References TO:** Employee, Division
**Referenced BY:** (none directly — linked via Employee)

---

### Division

**Endpoint:** `POST /division` · `GET /division`

| Field              | Type             | Required | Notes                                          |
| ------------------ | ---------------- | -------- | ---------------------------------------------- |
| name               | string           | **YES**  | e.g., "Hovedvirksomhet"                        |
| organizationNumber | string           |          | From company                                   |
| startDate          | string           |          |                                                |
| municipalityDate   | string           |          |                                                |
| municipality       | Municipality ref |          | `{id}` — fetch via `GET /municipality?count=1` |

**⚠ Does not exist in fresh competition environments** — must create before payroll

---

### Department

**Endpoint:** `POST /department` · `GET /department` · `PUT /department/{id}` · `DELETE /department/{id}`

| Field             | Type         | Required | Notes                                                                |
| ----------------- | ------------ | -------- | -------------------------------------------------------------------- |
| name              | string       | **YES**  |                                                                      |
| departmentNumber  | string       |          | Auto-assigned if omitted. **Collisions common** — pre-check existing |
| departmentManager | Employee ref |          | `{id}`                                                               |
| isInactive        | boolean      |          |                                                                      |

**⚠ Always do `GET /department?fields=departmentNumber,name&count=1000` first to detect number collisions**

**References TO:** Employee (manager)
**Referenced BY:** Employee, Customer, Order, Project, Contact, Posting, TravelExpense

---

### Product

**Endpoint:** `POST /product` · `GET /product` · `PUT /product/{id}` · `DELETE /product/{id}`

| Field                     | Type           | Required | Notes                                      |
| ------------------------- | -------------- | -------- | ------------------------------------------ |
| name                      | string         | **YES**  |                                            |
| priceExcludingVatCurrency | number         | **YES**  |                                            |
| vatType                   | VatType ref    | **YES**  | `{id}` — resolve via `GET /ledger/vatType` |
| number                    | string         |          | Product number, verified by scoring        |
| priceIncludingVatCurrency | number         |          |                                            |
| costExcludingVatCurrency  | number         |          |                                            |
| isStockItem               | boolean        |          |                                            |
| isInactive                | boolean        |          |                                            |
| account                   | Account ref    |          | `{id}`                                     |
| department                | Department ref |          | `{id}`                                     |
| currency                  | Currency ref   |          | `{id}`                                     |
| supplier                  | Supplier ref   |          | `{id}`                                     |

**References TO:** VatType, Account, Department, Currency, Supplier
**Referenced BY:** OrderLine, Posting

---

### Supplier

**Endpoint:** `POST /supplier` · `GET /supplier` · `PUT /supplier/{id}` · `DELETE /supplier/{id}`

| Field              | Type         | Required | Notes               |
| ------------------ | ------------ | -------- | ------------------- |
| name               | string       | **YES**  | Only required field |
| organizationNumber | string       |          |                     |
| email              | string       |          |                     |
| phoneNumber        | string       |          |                     |
| postalAddress      | Address ref  |          |                     |
| physicalAddress    | Address ref  |          |                     |
| ledgerAccount      | Account ref  |          |                     |
| currency           | Currency ref |          |                     |

**References TO:** Address, Account, Currency, Employee (accountManager)
**Referenced BY:** Posting, Product

---

### Contact

**Endpoint:** `POST /contact` · `GET /contact` · `PUT /contact/{id}`

| Field             | Type           | Required | Notes                                                  |
| ----------------- | -------------- | -------- | ------------------------------------------------------ |
| firstName         | string         | \*       | At least one of firstName, lastName, or email required |
| lastName          | string         | \*       |                                                        |
| email             | string         | \*       |                                                        |
| customer          | Customer ref   |          | `{id}` — the customer this contact belongs to          |
| phoneNumberMobile | string         |          |                                                        |
| phoneNumberWork   | string         |          |                                                        |
| department        | Department ref |          |                                                        |
| isInactive        | boolean        |          |                                                        |

**References TO:** Customer, Department
**Referenced BY:** Order (contact, attn, ourContact)

---

### VatType

**Endpoint:** `GET /ledger/vatType` (read-only, system reference data)

| Field      | Type   | Notes           |
| ---------- | ------ | --------------- |
| id         | int    |                 |
| name       | string | e.g., "MVA 25%" |
| number     | string | e.g., "3"       |
| percentage | number | e.g., 25.0      |

**Common VAT types:** 25% (standard), 15% (food), 12% (transport), 0% (exempt)

**⚠ Always resolve by percentage match first, then by number**

**Referenced BY:** Product, OrderLine, Posting, TravelExpenseCost, Project, Account

---

### Account

**Endpoint:** `GET /ledger/account` · `POST /ledger/account` · `PUT /ledger/account/{id}`

| Field              | Type    | Notes                                                         |
| ------------------ | ------- | ------------------------------------------------------------- |
| id                 | int     |                                                               |
| number             | string  | e.g., "1920" (bank), "3000" (sales), "6500" (office services) |
| name               | string  |                                                               |
| type               | enum    | `GENERAL`, `CUSTOMER`, `VENDOR`, `EMPLOYEE`, `ASSET`          |
| requiresDepartment | boolean |                                                               |
| requiresProject    | boolean |                                                               |
| isBankAccount      | boolean | read-only                                                     |

**Key accounts:** `1920` = Bank (used for counter-postings), `3000`+ = Sales revenue, `4000`+ = Cost of goods, `6000`+ = Operating expenses

**Referenced BY:** Posting (account), Product, Supplier, PaymentType

---

### Order

**Endpoint:** `POST /order` · `GET /order` · `PUT /order/{id}` · `DELETE /order/{id}`

| Field                           | Type           | Required | Notes                                      |
| ------------------------------- | -------------- | -------- | ------------------------------------------ |
| customer                        | Customer ref   | **YES**  | `{id}`                                     |
| orderLines                      | OrderLine[]    | **YES**  | At least one line required                 |
| orderDate                       | string         |          | `YYYY-MM-DD`, defaults to today            |
| deliveryDate                    | string         |          |                                            |
| reference                       | string         |          |                                            |
| receiverEmail                   | string         |          |                                            |
| invoiceComment                  | string         |          |                                            |
| department                      | Department ref |          | `{id}`                                     |
| project                         | Project ref    |          | `{id}`                                     |
| ourContactEmployee              | Employee ref   |          | `{id}`                                     |
| contact                         | Contact ref    |          | `{id}`                                     |
| currency                        | Currency ref   |          | `{id}`                                     |
| invoicesDueIn                   | int            |          | Days until due                             |
| invoicesDueInType               | enum           |          | `DAYS`, `MONTHS`, `RECURRING_DAY_OF_MONTH` |
| isPrioritizeAmountsIncludingVat | boolean        |          |                                            |

**Action endpoints:**

- `PUT /order/{id}/:invoice` — create invoice from order
- `PUT /order/:invoiceMultipleOrders` — create invoice from multiple orders

**References TO:** Customer, OrderLine, Project, Department, Employee, Contact, Currency
**Referenced BY:** Invoice (orders[])

---

### OrderLine

**Endpoint:** Created inline with Order (in `orderLines` array)

| Field                         | Type         | Required | Notes                                                        |
| ----------------------------- | ------------ | -------- | ------------------------------------------------------------ |
| count                         | number       | **YES**  | **Default to 1** — omitting gives 0, making invoice amount 0 |
| vatType                       | VatType ref  | **YES**  | `{id}`                                                       |
| unitPriceExcludingVatCurrency | number       | \*\*     | One of excl/incl price required                              |
| unitPriceIncludingVatCurrency | number       | \*\*     |                                                              |
| description                   | string       |          | Line description                                             |
| product                       | Product ref  |          | `{id}`                                                       |
| discount                      | number       |          | Percentage (%)                                               |
| markup                        | number       |          | Percentage (%)                                               |
| currency                      | Currency ref |          |                                                              |
| account                       | Account ref  |          | `{id}` — override default revenue account                    |

**⚠ count defaults to 0 if omitted** — always set explicitly

**References TO:** VatType, Product, Currency, Account, Order
**Referenced BY:** Order (orderLines[]), Invoice (orderLines[], read-only)

---

### Invoice

**Endpoint:** `POST /invoice` · `GET /invoice` · `PUT /invoice/{id}`

| Field          | Type         | Required | Notes                                               |
| -------------- | ------------ | -------- | --------------------------------------------------- |
| orders         | Order[]      | **YES**  | `[{id}]` — **only one order per invoice supported** |
| invoiceDate    | string       |          | `YYYY-MM-DD`                                        |
| invoiceDueDate | string       |          | Defaults to orderDate + invoicesDueIn               |
| comment        | string       |          |                                                     |
| currency       | Currency ref |          | `{id}`                                              |

**Read-only response fields:**

- `amount` (total with VAT in company currency)
- `amountExcludingVat`
- `amountCurrency` (total in invoice currency)
- `invoiceNumber`
- `isCredited`, `isCreditNote`, `isCharged`

**⚠ Cannot create invoice without an Order** — this is the #1 source of failures
**⚠ Company must have a bank account registered** — fails in clean competition environments

**Action endpoints:**

- `PUT /invoice/{id}/:send?sendType=EMAIL|MANUAL` — send to customer
- `PUT /invoice/{id}/:createCreditNote?date={date}` — create credit note
- `PUT /invoice/{id}/:payment?paymentDate={date}&paymentTypeId={id}&paidAmount={amount}` — register payment
- `PUT /invoice/{id}/:createReminder` — create payment reminder

**References TO:** Order (with OrderLines), Customer (via Order), Currency, Voucher
**Referenced BY:** TravelExpense, Project (invoicingPlan, read-only)

---

### Voucher

**Endpoint:** `POST /ledger/voucher` · `GET /ledger/voucher` · `PUT /ledger/voucher/{id}` · `DELETE /ledger/voucher/{id}`

| Field                 | Type            | Required | Notes                         |
| --------------------- | --------------- | -------- | ----------------------------- |
| date                  | string          | **YES**  | `YYYY-MM-DD`                  |
| postings              | Posting[]       | **YES**  | At least one posting required |
| description           | string          |          |                               |
| voucherType           | VoucherType ref |          | `{id}`                        |
| externalVoucherNumber | string          |          | Max 70 chars                  |
| vendorInvoiceNumber   | string          |          |                               |

**Query parameter:** `?sendToLedger=true` — always use to post immediately

**Action endpoints:**

- `PUT /ledger/voucher/{id}/:reverse` — reverse posted voucher
- `PUT /ledger/voucher/{id}/:sendToInbox` — send to inbox
- `PUT /ledger/voucher/{id}/:sendToLedger` — post to ledger

**References TO:** Posting, VoucherType, Document
**Referenced BY:** Invoice (voucher, read-only)

---

### Posting

**Endpoint:** Created inline with Voucher (in `postings` array)

| Field                    | Type               | Required | Notes                               |
| ------------------------ | ------------------ | -------- | ----------------------------------- |
| account                  | Account ref        | **YES**  | `{id}` — resolve by account number  |
| amount                   | number             | **YES**  | Positive = debit, negative = credit |
| amountGross              | number             |          | Gross amount (incl. VAT)            |
| amountGrossCurrency      | number             |          |                                     |
| date                     | string             |          | `YYYY-MM-DD`                        |
| description              | string             |          |                                     |
| row                      | int                |          | 1-based row number                  |
| vatType                  | VatType ref        |          | `{id}`                              |
| customer                 | Customer ref       |          | `{id}`                              |
| supplier                 | Supplier ref       |          | `{id}`                              |
| employee                 | Employee ref       |          | `{id}`                              |
| project                  | Project ref        |          | `{id}`                              |
| product                  | Product ref        |          | `{id}`                              |
| department               | Department ref     |          | `{id}`                              |
| currency                 | Currency ref       |          | `{id}`                              |
| freeAccountingDimension1 | DimensionValue ref |          | `{id}` — custom dimension           |
| freeAccountingDimension2 | DimensionValue ref |          |                                     |
| freeAccountingDimension3 | DimensionValue ref |          |                                     |

**⚠ Postings must balance** — total debits must equal total credits
**⚠ Counter-postings to account 1920 (bank)** are common for auto-generated postings

**References TO:** Account, VatType, Customer, Supplier, Employee, Project, Product, Department, Currency, DimensionValue
**Referenced BY:** Voucher (postings[]), Invoice (postings[], read-only)

---

### Project

**Endpoint:** `POST /project` · `GET /project` · `PUT /project/{id}` · `DELETE /project/{id}`

| Field             | Type                 | Required | Notes                                              |
| ----------------- | -------------------- | -------- | -------------------------------------------------- |
| name              | string               | **YES**  |                                                    |
| customer          | Customer ref         | **YES**  | `{id}` — create if not found                       |
| startDate         | string               | **YES**  | `YYYY-MM-DD` — **required, default to today**      |
| projectManager    | Employee ref         |          | `{id}` — **must have ALL_PRIVILEGES entitlements** |
| department        | Department ref       |          | `{id}`                                             |
| number            | string               |          | Auto-generated if omitted                          |
| description       | string               |          |                                                    |
| endDate           | string               |          |                                                    |
| isFixedPrice      | boolean              |          |                                                    |
| fixedprice        | number               |          | Only if isFixedPrice=true                          |
| isInternal        | boolean              |          |                                                    |
| isOffer           | boolean              |          | true=offer, false=project                          |
| reference         | string               |          |                                                    |
| currency          | Currency ref         |          | `{id}`                                             |
| vatType           | VatType ref          |          | `{id}`                                             |
| contact           | Contact ref          |          |                                                    |
| participants      | ProjectParticipant[] |          |                                                    |
| projectActivities | ProjectActivity[]    |          |                                                    |
| invoiceDueDate    | int                  |          | Days until due                                     |

**⚠ Project manager must exist AND have entitlements granted before assignment**

**References TO:** Customer, Employee (projectManager), Department, Currency, VatType, Contact
**Referenced BY:** Order, Posting, TravelExpense, Activity (via ProjectActivity)

---

### Activity

**Endpoint:** `POST /activity` · `GET /activity`

| Field        | Type    | Required | Notes                                                                               |
| ------------ | ------- | -------- | ----------------------------------------------------------------------------------- |
| name         | string  |          |                                                                                     |
| activityType | enum    | **YES**  | `GENERAL_ACTIVITY`, `PROJECT_GENERAL_ACTIVITY`, `PROJECT_SPECIFIC_ACTIVITY`, `TASK` |
| rate         | number  |          | Hourly rate                                                                         |
| isChargeable | boolean |          |                                                                                     |

**Linked to project via:** `POST /project/projectActivity {project: {id}, activity: {id}}`

---

### TravelExpense

**Endpoint:** `POST /travelExpense` · `GET /travelExpense` · `PUT /travelExpense/{id}` · `DELETE /travelExpense/{id}`

| Field         | Type           | Required | Notes                     |
| ------------- | -------------- | -------- | ------------------------- |
| employee      | Employee ref   | **YES**  | `{id}`                    |
| title         | string         |          |                           |
| project       | Project ref    |          | `{id}`                    |
| department    | Department ref |          | `{id}`                    |
| travelDetails | TravelDetails  |          | Inline object (see below) |
| isChargeable  | boolean        |          |                           |

**TravelDetails sub-object:**
| Field | Type | Notes |
|-------|------|-------|
| departureDate | string | `YYYY-MM-DD` |
| returnDate | string | |
| departureFrom | string | |
| destination | string | |
| departureTime | string | |
| returnTime | string | |
| purpose | string | |
| isForeignTravel | boolean | |
| isDayTrip | boolean | true if returnDate == departureDate or no returnDate |

**Action endpoints:**

- `PUT /travelExpense/:deliver` — submit for approval
- `PUT /travelExpense/:approve` — approve
- `PUT /travelExpense/:unapprove` — unapprove

**References TO:** Employee, Project, Department
**Referenced BY:** TravelExpenseCost

---

### TravelExpenseCost

**Endpoint:** `POST /travelExpense/cost` · `GET /travelExpense/cost`

| Field                | Type                   | Required | Notes                                                                     |
| -------------------- | ---------------------- | -------- | ------------------------------------------------------------------------- |
| travelExpense        | TravelExpense ref      | **YES**  | `{id}`                                                                    |
| amountCurrencyIncVat | number                 | **YES**  |                                                                           |
| paymentType          | TravelPaymentType ref  | **YES**  | `{id}` — from `GET /travelExpense/paymentType` (NOT invoice paymentType!) |
| date                 | string                 |          | `YYYY-MM-DD`                                                              |
| comments             | string                 |          | Mapped from "description"                                                 |
| category             | string                 |          |                                                                           |
| vatType              | VatType ref            |          | `{id}`                                                                    |
| currency             | Currency ref           |          | `{id}`                                                                    |
| costCategory         | TravelCostCategory ref |          | `{id}`                                                                    |
| isChargeable         | boolean                |          |                                                                           |

**⚠ Payment type endpoint is `/travelExpense/paymentType`** — different from invoice's `/invoice/paymentType`
**⚠ At least one cost line required** — scoring checks `has_costs > 0`

**References TO:** TravelExpense, VatType, Currency, TravelPaymentType, TravelCostCategory

---

### SalaryTransaction

**Endpoint:** `POST /salary/transaction`

| Field    | Type      | Required | Notes                         |
| -------- | --------- | -------- | ----------------------------- |
| date     | string    | **YES**  | Voucher date, `YYYY-MM-01`    |
| year     | int       | **YES**  |                               |
| month    | int       | **YES**  |                               |
| payslips | Payslip[] | **YES**  | At least one payslip required |

**Query parameter:** `?generateTaxDeduction=true`

**Payslip sub-object:**
| Field | Type | Required | Notes |
|-------|------|----------|-------|
| employee | Employee ref | **YES** | `{id}` |
| date | string | | |
| year | int | | |
| month | int | | |
| specifications | SalarySpecification[] | | Salary lines |

**SalarySpecification sub-object:**
| Field | Type | Notes |
|-------|------|-------|
| salaryType | SalaryType ref | `{id}` — resolve from `GET /salary/type` |
| rate | number | Amount per unit |
| count | number | Number of units (usually 1) |

**Common salary types:** `1000`=Fastlønn (base salary), `1350`=Bonus

**⚠ Employee must have active Employment with Division set before salary transaction**

**References TO:** Employee (via Payslip), SalaryType, Division (via Employment)

---

### PaymentType (Invoice)

**Endpoint:** `GET /invoice/paymentType` (read-only reference data)

| Field         | Type        | Notes                |
| ------------- | ----------- | -------------------- |
| id            | int         |                      |
| description   | string      | e.g., "Bankinnskudd" |
| debitAccount  | Account ref |                      |
| creditAccount | Account ref |                      |

**⚠ Prefer types with description containing "bank" or "overf"** (bank transfer)

### PaymentType (Travel)

**Endpoint:** `GET /travelExpense/paymentType` (read-only reference data)

Different endpoint from invoice payment types. Same structure.

---

### SupplierInvoice

**Endpoint:** `GET /supplierInvoice` · `GET /supplierInvoice/{id}` (read-only — created via voucher)

| Field                      | Type         | Required | Notes                                 |
| -------------------------- | ------------ | -------- | ------------------------------------- |
| invoiceNumber              | string       |          | Supplier's invoice reference          |
| invoiceDate                | string       |          | `YYYY-MM-DD`                          |
| invoiceDueDate             | string       |          | `YYYY-MM-DD`                          |
| supplier                   | Supplier ref |          | `{id}`                                |
| voucher                    | Voucher ref  |          | Read-only — linked automatically      |
| amount                     | number       |          | Read-only — in company currency (NOK) |
| amountCurrency             | number       |          | In specified currency                 |
| amountExcludingVat         | number       |          | Read-only                             |
| amountExcludingVatCurrency | number       |          | Read-only                             |
| currency                   | Currency ref |          | `{id}`                                |
| kidOrReceiverReference     | string       |          | KID or payment message                |
| isCreditNote               | boolean      |          | Read-only                             |
| outstandingAmount          | number       |          | Read-only — remaining amount to pay   |

**⚠ Not created via `POST /supplierInvoice`** — created via `POST /ledger/voucher?sendToLedger=true` with:

- Debit posting: expense account (e.g., 6500) with input VAT + `supplier: {id}`
- Credit posting: creditor account 2400 (leverandørgjeld) with negative amount + `supplier: {id}`
- Set `voucherType` to "Leverandørfaktura" and `vendorInvoiceNumber` on the voucher body

**Action endpoints:**

- `PUT /supplierInvoice/{id}/:approve` — approve for payment
- `PUT /supplierInvoice/{id}/:addPayment` — register payment
- `PUT /supplierInvoice/{id}/:addRecipient` — add approval recipient

**References TO:** Supplier, Voucher, Currency
**Referenced BY:** — (top-level entity)

---

### AccountingDimensionName

**Endpoint:** `POST /ledger/accountingDimensionName` · `GET /ledger/accountingDimensionName/search`

| Field          | Type    | Required | Notes                                  |
| -------------- | ------- | -------- | -------------------------------------- |
| dimensionName  | string  | **YES**  | Name of the custom dimension           |
| description    | string  |          | Optional description                   |
| dimensionIndex | int     |          | Read-only — auto-assigned (1, 2, or 3) |
| active         | boolean |          | Defaults to true                       |

**⚠ Max 3 custom dimensions** — `dimensionIndex` values: 1, 2, 3
**⚠ `dimensionIndex` is auto-assigned on creation** — read it from the POST response
**⚠ Search before creating** — use `GET /ledger/accountingDimensionName/search` to avoid duplicates

**References TO:** — (standalone)
**Referenced BY:** AccountingDimensionValue (via dimensionIndex)

---

### AccountingDimensionValue

**Endpoint:** `POST /ledger/accountingDimensionValue` · `GET /ledger/accountingDimensionValue/search`

| Field                     | Type    | Required | Notes                                             |
| ------------------------- | ------- | -------- | ------------------------------------------------- |
| displayName               | string  | **YES**  | Name of the dimension value                       |
| dimensionIndex            | int     | **YES**  | Must match parent AccountingDimensionName's index |
| active                    | boolean |          | Defaults to true                                  |
| number                    | string  |          | Can contain letters and numbers                   |
| showInVoucherRegistration | boolean |          | Set `true` to show in voucher UI                  |
| position                  | int     |          | Sort order in value list                          |

**⚠ `dimensionIndex` must come from parent AccountingDimensionName** — not arbitrary
**⚠ Search before creating** — use `GET /ledger/accountingDimensionValue/search?dimensionIndex={n}`

**Linked to postings via:** `freeAccountingDimension1`, `freeAccountingDimension2`, or `freeAccountingDimension3` on the Posting entity (field number matches dimensionIndex)

**References TO:** AccountingDimensionName (via dimensionIndex)
**Referenced BY:** Posting (via freeAccountingDimension1/2/3)

---

### TimesheetEntry

**Endpoint:** `POST /timesheet/entry` · `GET /timesheet/entry/{id}` · `PUT /timesheet/entry/{id}` · `DELETE /timesheet/entry/{id}`

| Field                  | Type         | Required | Notes                                      |
| ---------------------- | ------------ | -------- | ------------------------------------------ |
| project                | Project ref  | **YES**  | `{id}`                                     |
| activity               | Activity ref | **YES**  | `{id}` — must be linked to the project     |
| date                   | string       | **YES**  | `YYYY-MM-DD`                               |
| hours                  | number       | **YES**  | Decimal hours (e.g., 7.5)                  |
| employee               | Employee ref |          | `{id}` — defaults to authenticated user    |
| comment                | string       |          |                                            |
| projectChargeableHours | number       |          | 0–24, chargeable hours on project activity |

**Read-only fields:** `chargeableHours`, `locked`, `chargeable`, `hourlyRate`, `hourlyCost`, `invoice`

**Batch endpoint:** `POST /timesheet/entry/list` — create multiple entries at once

**⚠ Activity must be linked to the project** — create via `POST /project/projectActivity` first
**⚠ SMART_TIME_TRACKING module must be enabled** — `POST /company/salesmodules {name: "SMART_TIME_TRACKING"}`

**References TO:** Project, Activity, Employee, Invoice
**Referenced BY:** — (standalone time registration)

---

### BankReconciliation

**Endpoint:** `POST /bank/reconciliation` · `GET /bank/reconciliation` · `PUT /bank/reconciliation/{id}` · `DELETE /bank/reconciliation/{id}`

| Field                             | Type             | Required | Notes                                |
| --------------------------------- | ---------------- | -------- | ------------------------------------ |
| account                           | Account ref      | **YES**  | `{id}` — bank account (e.g., 1920)   |
| accountingPeriod                  | AccountingPeriod |          | `{id}` — the period being reconciled |
| voucher                           | Voucher ref      |          | Read-only                            |
| isClosed                          | boolean          |          |                                      |
| type                              | enum             |          | `MANUAL` or `AUTOMATIC`              |
| bankAccountClosingBalanceCurrency | number           |          | Closing balance in account currency  |

**Read-only fields:** `transactions`, `closedDate`, `closedByContact`, `closedByEmployee`, `approvable`, `autoPayReconciliation`

**Action endpoints:**

- `PUT /bank/reconciliation/{id}/:adjustment` — create adjustment posting

**⚠ Banks are referenced by register number** (4-digit, e.g., 1103=DNB) — no standalone BankAccount entity
**⚠ Account ref is a ledger Account** (e.g., account 1920 = bank), not a separate bank account object

**References TO:** Account, AccountingPeriod, Voucher, Employee, Contact
**Referenced BY:** BankTransaction

---

### Municipality

**Endpoint:** `GET /municipality` (read-only reference data)

| Field          | Type   | Notes                          |
| -------------- | ------ | ------------------------------ |
| number         | string | Read-only — municipality code  |
| name           | string | Read-only                      |
| county         | string | Read-only — county name        |
| payrollTaxZone | string | Read-only — arbeidsgiveravgift |
| displayName    | string | Read-only                      |

**⚠ Read-only reference** — cannot create or modify municipalities
**⚠ Used by Division entity** — `division.municipality` references this

**References TO:** — (standalone reference)
**Referenced BY:** Division (municipality)

---

### SalesModule

**Endpoint:** `POST /company/salesmodules` · `GET /company/salesmodules`

| Field         | Type   | Required | Notes                              |
| ------------- | ------ | -------- | ---------------------------------- |
| name          | enum   | **YES**  | Module enum value (see list below) |
| costStartDate | string | **YES**  | `YYYY-MM-DD` — billing start date  |

**Common module enum values:**

| Enum Value              | Description                     |
| ----------------------- | ------------------------------- |
| `PROJECT`               | Department & project management |
| `SMART_WAGE`            | Smart wage/salary               |
| `SMART_TIME_TRACKING`   | Time tracking                   |
| `OCR`                   | OCR scanning                    |
| `LOGISTICS`             | Logistics                       |
| `ELECTRONIC_VOUCHERS`   | Electronic voucher handling     |
| `API_V2`                | API access                      |
| `BASIS`                 | Basic package                   |
| `SMART`                 | Smart package                   |
| `KOMPLETT`              | Complete package                |
| `PRO`                   | Professional package            |
| `FIXED_ASSETS_REGISTER` | Fixed assets register           |

**Full enum (60+ values):** `MAMUT`, `AGRO_*`, `NO1TS*`, `DIYPACKAGE`, `VVS`, `ELECTRO`, `ACCOUNTING_OFFICE`, `WAGE`, `TIME_TRACKING`, `UP_TO_*_VOUCHERS`, `UP_TO_*_VOUCHERS_AUTOMATION`, `MIKRO`, `AUTOPLUS_*`, `INTEGRATION_PARTNER`, `SMART_PROJECT`, `YEAR_END_REPORTING_*`, `PRIMARY_INDUSTRY`, `STICOS`, `ZTL`

**Multi-language mapping** (EnableModuleHandler resolves these):

| Prompt term (any language)                            | API enum              |
| ----------------------------------------------------- | --------------------- |
| avdeling, department, abteilung, departamento         | `PROJECT`             |
| prosjekt, project, proyecto, projeto, projekt, projet | `PROJECT`             |
| tidregistrering, tidsregistrering, zeiterfassung      | `SMART_TIME_TRACKING` |
| lønn, wage, salary, gehalt, salaire, salario          | `SMART_WAGE`          |
| logistikk, logistics                                  | `LOGISTICS`           |
| elektroniske_bilag, electronic_vouchers               | `ELECTRONIC_VOUCHERS` |

**⚠ Module name must be an exact enum match** — use the mapping table above
**⚠ Some modules have extra costs** — the API will activate regardless

**References TO:** — (standalone)
**Referenced BY:** — (enables features in the application)

---

## Dependency Chains by Task

### create_customer (1 call)

```
POST /customer
```

### create_supplier (1 call)

```
POST /supplier
```

### create_department (2+ calls)

```
GET /department (collision check) → POST /department
```

### create_product (2 calls)

```
GET /ledger/vatType → POST /product
```

### create_employee (3–5 calls)

```
GET /department (for required dept ref)
  → POST /employee
    → [POST /employee/employment] (if startDate)
      → [PUT /employee/entitlement/:grantEntitlementsByTemplate] (if admin role)
```

### create_invoice (4–6 calls)

```
┌─ EnsureBankAccount() ─┐
│  GET /ledger/vatType   │  (parallel)
│  ResolveCustomer()     │
└────────────────────────┘
  → POST /order {customer, orderLines[{count, vatType, price}]}
    → POST /invoice {orders: [{id}]}
      → [PUT /invoice/{id}/:send] (optional)
```

**⚠ Order MUST have orderLines with vatType and count≥1**
**⚠ Company bank account must exist**

### register_payment (7–8 calls)

```
┌─ GET /invoice/paymentType ─┐
│  Full invoice chain (4-6)  │  (parallel start)
└────────────────────────────┘
  → PUT /invoice/{id}/:payment?paymentDate={d}&paymentTypeId={id}&paidAmount={amt}
```

**⚠ paidAmount must include VAT** — use invoice's `amount` field, not ex-VAT from prompt
**⚠ Payment params go in URL query string, NOT body**

### create_credit_note (5–6 calls)

```
Full invoice chain (4-6)
  → PUT /invoice/{id}/:createCreditNote?date={d}&sendToCustomer=false
```

### create_project (4–8 calls)

```
ResolveCustomer() (search or create)
  → ResolveProjectManager() (search or create employee)
    → PUT /employee/entitlement/:grantEntitlementsByTemplate (if new manager)
      → [GET /department]
        → POST /project {name, customer, projectManager, startDate, ...}
```

**⚠ Manager must have entitlements BEFORE being assigned to project**

### create_travel_expense (4–7 calls)

```
GET /employee (resolve employee)
  → POST /travelExpense {employee, title, travelDetails}
    → GET /travelExpense/paymentType
      → POST /travelExpense/cost × N (one per cost line)
```

**⚠ Use `/travelExpense/paymentType` not `/invoice/paymentType`**

### create_voucher (3–9 calls)

```
[Create dimensions if needed:]
  GET /ledger/accountingDimensionName/search
  → POST /ledger/accountingDimensionName
  → POST /ledger/accountingDimensionValue × N

[Resolve accounts:]
  GET /ledger/account?number={n} × N

→ POST /ledger/voucher?sendToLedger=true {date, description, postings[]}
```

**⚠ Postings must balance (debits = credits)**
**⚠ Counter-posting to account 1920 (bank) when needed**

### run_payroll (5–8 calls)

```
GET /employee (resolve)
  → [POST /employee if not found]
    → GET /division (or POST /division if clean env)
      → GET /employee/employment → [POST or PUT employment with division]
        → GET /salary/type (resolve salary type IDs)
          → POST /salary/transaction?generateTaxDeduction=true
```

**⚠ Division must exist (create in clean environments)**
**⚠ Employment must link to Division**

### delete_entity (2 calls)

```
GET /{entityPath}?name={name} (search) → DELETE /{entityPath}/{id}
```

### enable_module (1–2 calls)

```
POST /company/salesmodules {name: "MODULE_ENUM", costStartDate}
```

### update_employee (2–3 calls)

```
GET /employee?firstName={first}&lastName={last} (search)
  → PUT /employee/{id} {version, ...changed fields}
    → [PUT /employee/entitlement/:grantEntitlementsByTemplate] (if role change)
```

**⚠ Must include `version` from GET response** — or 409 conflict
**⚠ Cannot update `startDate` on Employee** — that's on Employment entity

### delete_travel_expense (2 calls)

```
GET /travelExpense?employeeId={id} (search)
  → DELETE /travelExpense/{id}
```

### create_supplier_invoice (4–6 calls)

```
POST /supplier {name, organizationNumber}
  → GET /ledger/voucherType?name=Leverandørfaktura
    → GET /ledger/account?number=6500 (expense account)
      → GET /ledger/account?number=2400 (creditor account)
        → POST /ledger/voucher?sendToLedger=true {date, voucherType, vendorInvoiceNumber,
            postings: [debit(expense+VAT, supplier), credit(2400, -amount, supplier)]}
```

**⚠ Not created via `/supplierInvoice` endpoint** — that's read-only
**⚠ Both debit and credit postings must include `supplier: {id}`**
**⚠ Set `vendorInvoiceNumber` on the voucher body for tracking**

### create_timesheet_entry (3–5 calls)

```
GET /employee (resolve)
  → GET /activity or POST /activity (resolve/create activity)
    → [POST /project/projectActivity] (link activity to project if needed)
      → POST /timesheet/entry {project, activity, employee, date, hours}
```

**⚠ SMART_TIME_TRACKING module must be enabled first**
**⚠ Activity must be linked to project via projectActivity**

### create_dimension (2–4 calls)

```
GET /ledger/accountingDimensionName/search (check existing)
  → POST /ledger/accountingDimensionName {dimensionName}
    → POST /ledger/accountingDimensionValue × N {displayName, dimensionIndex, active}
```

**⚠ Max 3 custom dimensions** — dimensionIndex auto-assigned (1, 2, or 3)
**⚠ Get dimensionIndex from POST response** — needed to create values

---

## Action Endpoints (Complete List)

| Method | Path                                                       | Purpose              | Params                                     |
| ------ | ---------------------------------------------------------- | -------------------- | ------------------------------------------ |
| PUT    | `/invoice/{id}/:send`                                      | Send invoice         | `?sendType=EMAIL\|MANUAL`                  |
| PUT    | `/invoice/{id}/:payment`                                   | Register payment     | `?paymentDate=&paymentTypeId=&paidAmount=` |
| PUT    | `/invoice/{id}/:createCreditNote`                          | Create credit note   | `?date=&comment=&sendToCustomer=`          |
| PUT    | `/invoice/{id}/:createReminder`                            | Payment reminder     |                                            |
| PUT    | `/order/{id}/:invoice`                                     | Invoice from order   |                                            |
| PUT    | `/order/:invoiceMultipleOrders`                            | Multi-order invoice  |                                            |
| PUT    | `/employee/entitlement/:grantEntitlementsByTemplate`       | Grant role           | `?employeeId=&template=`                   |
| PUT    | `/employee/entitlement/:grantClientEntitlementsByTemplate` | Client entitlements  |                                            |
| PUT    | `/employee/preferences/:changeLanguage`                    | Change language      |                                            |
| PUT    | `/travelExpense/:deliver`                                  | Submit for approval  |                                            |
| PUT    | `/travelExpense/:approve`                                  | Approve              |                                            |
| PUT    | `/travelExpense/:unapprove`                                | Unapprove            |                                            |
| PUT    | `/ledger/voucher/{id}/:reverse`                            | Reverse voucher      |                                            |
| PUT    | `/ledger/voucher/{id}/:sendToInbox`                        | Send to inbox        |                                            |
| PUT    | `/ledger/voucher/{id}/:sendToLedger`                       | Post to ledger       |                                            |
| PUT    | `/supplierInvoice/{id}/:approve`                           | Approve supplier inv |                                            |
| PUT    | `/supplierInvoice/{id}/:addPayment`                        | Pay supplier inv     |                                            |
| PUT    | `/purchaseOrder/{id}/:send`                                | Send PO              |                                            |
| PUT    | `/purchaseOrder/{id}/:sendByEmail`                         | Email PO             |                                            |
| PUT    | `/purchaseOrder/goodsReceipt/{id}/:confirm`                | Confirm receipt      |                                            |
| PUT    | `/bank/reconciliation/{id}/:adjustment`                    | Bank adjustment      |                                            |
| PUT    | `/ledger/posting/:closePostings`                           | Close postings       |                                            |
| PUT    | `/timesheet/month/:approve`                                | Approve month        |                                            |
| PUT    | `/timesheet/month/:complete`                               | Complete month       |                                            |
| PUT    | `/timesheet/week/:approve`                                 | Approve week         |                                            |

---

## Common Pitfalls & Gotchas

1. **`startDate` is NOT an Employee field** — use `POST /employee/employment` separately
2. **`count` on OrderLine defaults to 0** — always set explicitly, or invoice amount = 0
3. **Company bank account required for invoices** — not pre-configured in competition environments
4. **`version` field required on all PUT updates** — fetch via GET first
5. **Payment amount must include VAT** — use `invoice.amount`, not ex-VAT from prompt
6. **Payment params are URL query params**, not request body
7. **Two different PaymentType endpoints** — `/invoice/paymentType` vs `/travelExpense/paymentType`
8. **Department numbers collide** — pre-check with `GET /department`
9. **Project manager needs entitlements BEFORE assignment** — or 422 error
10. **Division required for payroll** — doesn't exist in fresh environments
11. **Employment must have division** for salary transactions to work
12. **Voucher postings must balance** — debits = credits
13. **Voucher always uses `?sendToLedger=true`** — posts immediately
14. **Counter-postings default to account 1920** (bank)
15. **Customer search: use `?organizationNumber=`** not `?name=` with org number value
16. **Employee `userType` must be `STANDARD` or `EXTENDED`** — never `"0"` or empty string
17. **Employee `email` is required** for Tripletex users — 422 if missing
18. **Activity `activityType` is required** — use `PROJECT_GENERAL_ACTIVITY` for project activities
19. **Timesheet entries** need `activity`, `date`, `employee` as minimum fields
20. **Module names must resolve to known enums** — `PROJECT`, `SMART_WAGE`, etc.
21. **Supplier invoices are NOT created via `/supplierInvoice`** — use `POST /ledger/voucher?sendToLedger=true` with debit (expense) + credit (2400) postings
22. **Max 3 custom dimensions** — `dimensionIndex` (1, 2, 3) is auto-assigned on creation; read it from POST response
23. **Dimension values require parent's `dimensionIndex`** — must create AccountingDimensionName first to get the index
24. **Posting.freeAccountingDimension{N}** — N must match the dimensionIndex of the dimension name (1, 2, or 3)
25. **TimesheetEntry requires SMART_TIME_TRACKING module** — enable before creating entries
26. **TimesheetEntry activity must be linked to project** — create ProjectActivity first via `POST /project/projectActivity`
27. **BankReconciliation `account` is a ledger Account ref** (e.g., 1920) — no separate bank account entity
28. **Municipality is read-only** — cannot create, only referenced by Division
