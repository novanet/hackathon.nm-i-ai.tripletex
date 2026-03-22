# NM i AI 2026 — Tripletex: Opening Strategy

**Challenge:** Build an AI agent that executes accounting tasks via the Tripletex API.
**Window:** March 19 18:00 → March 22 15:00 CET (69 hours)
**Core loop:** Receive prompt → LLM parses → call Tripletex API → scored field-by-field.

---

## 0. The #1 Lesson: Domain Insight Beats Algorithm Sophistication

Last year's NM i AI top team: a single data insight (+90 points) outweighed all their algorithm work combined (+23 points). Apply this to Tripletex:

1. **What does scoring actually reward?** `correctness × tier_multiplier` + efficiency bonus (up to 2× for perfect runs). A Tier 3 perfect+efficient = **6.0 pts**. A Tier 1 imperfect = **~0.8 pts**. Correctness first, efficiency second.
2. **Map the 30 task types early.** Some are 1 API call, some are 5+. Know which tiers they're in.
3. **Understand the Tripletex data model viscerally.** Don't guess what `POST /invoice` needs — try it. Read the error. That error is your teacher.
4. **Question assumptions.** Prompts are in 7 languages (not just Norwegian). Each submission gets a completely fresh account (no state carries over). Rate limits: 5 per task per day, 3 concurrent.

**Rule:** Spend hour 1 on API exploration + architecture, not perfecting one task.

---

## 1. Scoring Deep Dive

### 1.1 Correctness (0.0 – 1.0)

After your agent responds `{"status": "completed"}`, the platform queries the Tripletex API to verify what was created. Each task has field-level checks worth points:

Example — "Create employee":
| Check | Points |
|---|---|
| Employee found | 2 |
| Correct first name | 1 |
| Correct last name | 1 |
| Correct email | 1 |
| Administrator role assigned | 5 |
| **Total** | **10** |

`correctness = points_earned / max_points` → e.g. 8/10 = 0.8

**Key insight:** The "administrator role" check is worth 5/10 points — half the score. Missing a single complex field can be devastating. Parse the prompt precisely.

### 1.2 Tier Multiplier

| Tier | Multiplier | Examples | Unlocks |
|---|---|---|---|
| 1 | ×1 | Create employee, create customer | Competition start (Thu 18:00) |
| 2 | ×2 | Create invoice, register payment, credit notes | Friday morning |
| 3 | ×3 | Bank reconciliation, ledger corrections, complex workflows | Saturday morning |

### 1.3 Efficiency Bonus (Only on Perfect Runs)

If `correctness = 1.0`, you get a bonus that can **double** your tier score:

| Scenario | Approximate Score |
|---|---|
| Failed all checks | 0.0 |
| 80% of checks passed (Tier 2) | 1.6 |
| Perfect, many errors and extra calls (Tier 2) | ~2.1 |
| Perfect, efficient, few errors (Tier 2) | ~2.6 |
| Perfect, best-in-class efficiency, zero errors (Tier 2) | **4.0** |

Two factors determine the bonus:
- **Call efficiency** — fewer API calls vs. best-known solution = higher bonus
- **Error cleanliness** — every 4xx error (400, 404, 422) reduces the bonus

Benchmarks recalculated every 12 hours as teams find more efficient solutions.

### 1.4 Total Score

`Total = sum of best scores across all 30 task types`

**This means breadth is king.** Covering 25 tasks at 1.0 each (= 25 pts) beats 10 tasks at 2.0 each (= 20 pts).

---

## 2. The Tripletex API — What You Must Know

Full OpenAPI spec: `https://kkpqfuj-amager.tripletex.dev/v2/openapi.json` (3.7 MB, 546 endpoints, 2167 schemas)

### 2.1 Authentication

```python
auth = ("0", session_token)  # Basic Auth — username always "0"
```

**Critical:** Always use the `base_url` from the request credentials (proxy URL), never the direct Tripletex URL.

### 2.2 API Conventions

| Convention | Example |
|---|---|
| Partial update via PUT (no PATCH) | `PUT /employee/{id}` with only changed fields + `version` |
| Actions use `:` prefix | `PUT /invoice/{id}/:payment`, `PUT /invoice/{id}/:createCreditNote` |
| Aggregates use `>` prefix | `GET /bank/reconciliation/>last` |
| Field selection | `?fields=id,firstName,lastName` or `?fields=*` for all |
| Pagination | `?from=0&count=100` — response: `{"fullResultSize": N, "values": [...]}` |
| Dates | `YYYY-MM-DD` |
| Sorting | `?sorting=date,-project.name` |
| Version field | Required for PUT updates — prevents concurrent overwrites |

### 2.3 Response Envelope

```json
// Multiple values
{"fullResultSize": 42, "from": 0, "count": 10, "values": [{...}, {...}]}

// Single value (POST creates, GET by ID)
{"value": {...}}
```

### 2.4 Error Envelope (Parse These!)

```json
{
  "status": 422,
  "code": 18000,
  "message": "Validation failed",
  "developerMessage": "Field 'invoiceDueDate' is required",
  "validationMessages": [
    {"field": "invoiceDueDate", "message": "Required field"}
  ]
}
```

**The `validationMessages` array tells you exactly what's wrong.** This is gold for self-correction.

### 2.5 Key Status Codes

| Code | Meaning | Agent Action |
|---|---|---|
| 200/201 | Success | Continue |
| 400 | Bad request | Parse `validationMessages`, fix params |
| 401 | Auth failed | Check auth format: `("0", token)` |
| 404 | Not found | Check endpoint path / entity ID |
| 409 | Conflict (duplicate) | Entity already exists, GET it instead |
| 422 | Validation error | Read `developerMessage`, fix specific field |
| 429 | Rate limited | Wait `X-Rate-Limit-Reset` seconds |

---

## 3. The 30 Task Types — API Mapping

Based on the documented task categories and the OpenAPI spec analysis, here is the complete mapping:

### 3.1 Employees (Tier 1)

**Create employee:**
```
POST /employee
{
  "firstName": "Ola",
  "lastName": "Nordmann",
  "email": "ola@example.org",
  "userType": "EXTENDED"     // STANDARD | EXTENDED | NO_ACCESS
}
```
Key fields: `firstName`, `lastName`, `email`, `dateOfBirth`, `phoneNumberMobile`, `nationalIdentityNumber`, `bankAccountNumber`, `department` (ref), `employeeCategory` (ref), `userType`

**Assign administrator role:**
```
PUT /employee/entitlement/:grantEntitlementsByTemplate
  ?employeeId={id}&template=ALL_PRIVILEGES
```
This is a separate call after creating the employee. The `template` parameter determines the role. **This is likely the highest-value field check** (5/10 points in the example).

**Update employee (e.g., add phone, change email):**
```
PUT /employee/{id}
{ "version": N, "phoneNumberMobile": "+47 12345678" }
```
Must include `version` from the GET or POST response.

### 3.2 Customers (Tier 1)

**Create customer:**
```
POST /customer
{
  "name": "Acme AS",
  "email": "post@acme.no",
  "isCustomer": true,
  "organizationNumber": "123456789",
  "phoneNumber": "+47 22334455",
  "physicalAddress": {
    "addressLine1": "Storgata 1",
    "postalCode": "0150",
    "city": "Oslo",
    "country": {"id": 161}   // Norway = 161 (verify in sandbox)
  }
}
```
Key fields (46 total, most optional): `name`, `email`, `organizationNumber`, `phoneNumber`, `phoneNumberMobile`, `isCustomer`, `isPrivateIndividual`, `language`, `physicalAddress`, `postalAddress`, `invoiceEmail`, `invoiceSendMethod`, `invoicesDueIn`, `invoicesDueInType`, `currency` (ref), `category1` (ref)

### 3.3 Products (Tier 1)

**Create product:**
```
POST /product
{
  "name": "Consulting Service",
  "number": "1001",
  "priceExcludingVatCurrency": 1500.00,
  "vatType": {"id": N}    // Get from GET /ledger/vatType
}
```
Key fields: `name`, `number`, `priceExcludingVatCurrency`, `priceIncludingVatCurrency`, `costExcludingVatCurrency`, `vatType` (ref), `account` (ref), `department` (ref), `productUnit` (ref), `isStockItem`, `isInactive`

**VatType reference:** Query `GET /ledger/vatType` to find the correct ID for the VAT rate (e.g., 25% MVA, 0% MVA).

### 3.4 Departments (Tier 1)

**Create department:**
```
POST /department
{
  "name": "Salg",
  "departmentNumber": "100",
  "departmentManager": {"id": employee_id}   // optional ref
}
```
Only 10 properties — the simplest entity. Fields: `name`, `departmentNumber`, `departmentManager` (ref), `isInactive`

**Enable department module (if needed):**
```
POST /company/salesmodules
{"name": "MODULE_NAME", "costStartDate": "2026-03-19"}
```

### 3.5 Invoicing (Tier 2)

**The dependency chain is the critical insight:**

```
Customer → Order (with orderLines + product) → Invoice → (optional) Payment
```

**Step 1: Create customer** (if not existing)
```
POST /customer
{"name": "Customer AS", "isCustomer": true}
→ response.value.id = customer_id
```

**Step 2: Create order with inline order lines**
```
POST /order
{
  "customer": {"id": customer_id},
  "orderDate": "2026-03-19",
  "deliveryDate": "2026-03-19",
  "orderLines": [
    {
      "description": "Consulting",
      "count": 10,
      "unitPriceExcludingVatCurrency": 1500.00,
      "vatType": {"id": vat_type_id}
    }
  ]
}
→ response.value.id = order_id
```

Alternatively, create a product first and reference it:
```
"orderLines": [{"product": {"id": product_id}, "count": 10}]
```

**Step 3: Create invoice**
```
POST /invoice
{
  "invoiceDate": "2026-03-19",
  "invoiceDueDate": "2026-04-19",
  "orders": [{"id": order_id}]
}
→ response.value.id = invoice_id
```

**The invoice MUST reference an order.** You cannot create an invoice without orders. The order must have order lines. This is the #1 trap.

### 3.6 Register Payment (Tier 2)

```
PUT /invoice/{invoice_id}/:payment
  ?paymentDate=2026-03-19
  &paymentTypeId={payment_type_id}
  &paidAmount=15000.00
```

**paymentTypeId:** Query `GET /invoice/paymentType` to find the right ID (bank transfer, cash, etc.)

All parameters are query params, not body. Required: `paymentDate`, `paymentTypeId`, `paidAmount`.

### 3.7 Credit Notes (Tier 2)

```
PUT /invoice/{invoice_id}/:createCreditNote
  ?date=2026-03-19
  &comment=Feilaktig faktura
  &sendToCustomer=false
```

Creates a new invoice that nullifies the original. Parameters: `date` (required), `comment`, `creditNoteEmail`, `sendToCustomer`, `sendType`.

### 3.8 Travel Expenses (Tier 1–2)

**Create travel expense:**
```
POST /travelExpense
{
  "employee": {"id": employee_id},
  "title": "Kundebesøk Oslo",
  "travelDetails": {
    "departureDate": "2026-03-15",
    "returnDate": "2026-03-16",
    "departureFrom": "Bergen",
    "destination": "Oslo",
    "departureTime": "08:00",
    "returnTime": "18:00",
    "purpose": "Kundemøte",
    "isForeignTravel": false,
    "isDayTrip": false
  }
}
→ response.value.id = travel_id
```

**Add cost to travel expense:**
```
POST /travelExpense/cost
{
  "travelExpense": {"id": travel_id},
  "paymentType": {"id": payment_type_id},
  "category": {"id": cost_category_id},
  "amount": 500.00,
  "date": "2026-03-15",
  "description": "Taxi"
}
```

**Delete travel expense:**
```
DELETE /travelExpense/{id}
```

**Get cost categories:** `GET /travelExpense/costCategory`
**Get payment types:** `GET /travelExpense/paymentType`

### 3.9 Projects (Tier 2)

**Create project linked to customer:**
```
POST /project
{
  "name": "Website redesign",
  "number": "P001",
  "customer": {"id": customer_id},
  "projectManager": {"id": employee_id},
  "startDate": "2026-03-19",
  "endDate": "2026-12-31",
  "isInternal": false,
  "isFixedPrice": false
}
→ response.value.id = project_id
```

Key fields: `name`, `number`, `customer` (ref), `projectManager` (ref), `department` (ref), `startDate`, `endDate`, `projectCategory` (ref), `isInternal`, `isFixedPrice`, `isOffer`, `isClosed`

### 3.10 Corrections / Deletions (Tier 2–3)

**Delete entity — pattern is the same for all:**
```
1. GET /entity?search_params → find the entity
2. DELETE /entity/{id}
```

Available DELETE endpoints:
- `DELETE /employee` — NOT AVAILABLE (employees can't be deleted via API)
- `DELETE /customer/{id}`
- `DELETE /product/{id}`
- `DELETE /order/{id}`
- `DELETE /department/{id}`
- `DELETE /travelExpense/{id}`
- `DELETE /ledger/voucher/{id}`
- `DELETE /project/{id}`
- `DELETE /supplier/{id}`

**Reverse a voucher (journal entry):**
```
The voucher schema has a reverseVoucher field. For reversing incorrect entries,
you likely need to create a new voucher with opposing debit/credit amounts.
```

### 3.11 Supplier Management (Tier 1–2)

**Create supplier:**
```
POST /supplier
{
  "name": "Leverandør AS",
  "email": "faktura@leverandor.no",
  "organizationNumber": "987654321",
  "phoneNumber": "+47 22334455"
}
```

Supplier fields are nearly identical to Customer (35 properties). Note: `isSupplier` is readOnly (auto-set to true on this endpoint).

### 3.12 Ledger Vouchers / Journal Entries (Tier 2–3)

**Create voucher with postings:**
```
POST /ledger/voucher
{
  "date": "2026-03-19",
  "description": "Kontantsalg",
  "voucherType": {"id": voucher_type_id},
  "postings": [
    {
      "debit": {"id": debit_account_id},
      "credit": {"id": credit_account_id},
      "amountGross": 12500.00,
      "date": "2026-03-19",
      "description": "Kontantsalg"
    }
  ]
}
```

**Get chart of accounts:** `GET /ledger/account?fields=id,number,name&count=1000`

---

## 4. Architecture: LLM Parser + Deterministic Executor (Recommended)

### 4.1 Why This Architecture Wins

| Approach | Efficiency Score | Predictability | Error Rate |
|---|---|---|---|
| Full LLM Agent (ReAct loop) | Low (many calls) | Low | High (hallucinations) |
| **LLM Parser + Deterministic Code** | **High (minimal calls)** | **High** | **Low** |
| Hybrid (deterministic + fallback agent) | Medium-High | Medium | Medium |

The efficiency bonus **doubles** your tier score on perfect runs. Every unnecessary API call and every 4xx error reduces it. A deterministic executor that knows exactly which calls to make scores 4.0–6.0 on tasks it handles, while a ReAct loop scores 2.0–3.0.

**Go hybrid:** Deterministic for mapped task types, full agent loop as a fallback for unmapped ones.

### 4.2 Architecture Diagram

```
┌─────────────────────────────────────────────────────────────┐
│  POST /solve                                                 │
│  ┌─────────────────┐                                         │
│  │ Request Parser   │ ← body["prompt"], body["files"],       │
│  │                  │   body["tripletex_credentials"]         │
│  └────────┬─────────┘                                        │
│           ▼                                                  │
│  ┌─────────────────┐                                         │
│  │ LLM Extractor    │ ← GPT-4o / Claude 3.5 Sonnet          │
│  │ (structured out)  │ → {task_type, entities, fields}        │
│  └────────┬─────────┘                                        │
│           ▼                                                  │
│  ┌─────────────────┐     ┌───────────────────┐               │
│  │ Task Router      │────►│ Deterministic     │ (mapped)      │
│  │                  │     │ Handler Registry  │               │
│  │                  │     └───────────────────┘               │
│  │                  │     ┌───────────────────┐               │
│  │                  │────►│ Fallback Agent    │ (unmapped)     │
│  │                  │     │ (tool-use loop)   │               │
│  └────────┬─────────┘     └───────────────────┘               │
│           ▼                                                  │
│  ┌─────────────────┐                                         │
│  │ API Client       │ ← auth=("0", token), base_url          │
│  │ (requests/httpx) │ → logs every call + response            │
│  └────────┬─────────┘                                        │
│           ▼                                                  │
│  return {"status": "completed"}                              │
└─────────────────────────────────────────────────────────────┘
```

### 4.3 The Task Handler Registry

```python
TASK_HANDLERS = {
    "create_employee": handle_create_employee,
    "update_employee": handle_update_employee,
    "create_customer": handle_create_customer,
    "create_product": handle_create_product,
    "create_department": handle_create_department,
    "create_invoice": handle_create_invoice,
    "register_payment": handle_register_payment,
    "create_credit_note": handle_create_credit_note,
    "create_travel_expense": handle_create_travel_expense,
    "delete_travel_expense": handle_delete_travel_expense,
    "create_project": handle_create_project,
    "create_supplier": handle_create_supplier,
    "create_voucher": handle_create_voucher,
    "delete_entity": handle_delete_entity,
    # ... add as you discover task types
}
```

### 4.4 Skeleton Handler Example

```python
def handle_create_employee(api, extracted):
    """Deterministic handler: create employee + assign role."""
    emp = extracted["entities"]["employee"]
    
    # Step 1: Create employee (1 API call)
    body = {
        "firstName": emp["firstName"],
        "lastName": emp["lastName"],
        "email": emp.get("email"),
        "userType": "EXTENDED" if emp.get("roles") else "STANDARD",
    }
    # Add optional fields only if present
    for field in ["dateOfBirth", "phoneNumberMobile", "bankAccountNumber",
                  "nationalIdentityNumber"]:
        if field in emp:
            body[field] = emp[field]
    
    result = api.post("/employee", json=body)
    employee_id = result["value"]["id"]
    
    # Step 2: Assign role if requested (1 API call)
    if emp.get("roles"):
        role = emp["roles"][0]  # e.g., "administrator"
        api.put(f"/employee/entitlement/:grantEntitlementsByTemplate",
                params={"employeeId": employee_id, "template": role})
    
    # Total: 1-2 API calls, zero errors
```

---

## 5. Prompt Parsing Strategy

### 5.1 Multi-Language Handling

Prompts come in 7 languages: Norwegian bokmål (nb), English (en), Spanish (es), Portuguese (pt), Nynorsk (nn), German (de), French (fr).

**Don't build 7 parsers.** GPT-4o and Claude handle all these natively. System prompt:

```
The prompt may be in Norwegian, English, Spanish, Portuguese, Nynorsk, German,
or French. Extract all structured data regardless of language. Return field
values EXACTLY as they appear in the prompt (names, emails, amounts, dates).
Never translate proper nouns, email addresses, or organization numbers.
```

### 5.2 Structured Extraction Schema

```json
{
  "task_type": "create_employee",
  "entities": {
    "employee": {
      "firstName": "Ola",
      "lastName": "Nordmann",
      "email": "ola@example.org",
      "roles": ["administrator"],
      "dateOfBirth": "1990-05-15",
      "phoneNumberMobile": "+47 99887766"
    }
  },
  "relationships": {
    "department": "Salg"
  },
  "action": "create"
}
```

**Critical:** Field values must be extracted **character-for-character** from the prompt. The scoring checks exact matches. "Ola" ≠ "Ole" ≠ "OLA".

### 5.3 LLM System Prompt (Complete)

```
You are an accounting task parser for the Tripletex API. Given a task
prompt (in any of 7 languages), extract structured data for execution.

Respond ONLY with valid JSON matching this schema:
{
  "task_type": one of ["create_employee", "update_employee",
    "create_customer", "create_product", "create_department",
    "create_invoice", "register_payment", "create_credit_note",
    "create_travel_expense", "delete_travel_expense",
    "create_project", "create_supplier", "create_voucher",
    "delete_entity", "enable_module", "unknown"],
  "entities": {
    "<entity_type>": {
      "<field>": "<value>"  // Extract exactly as written in prompt
    }
  },
  "relationships": {
    "<target_entity>": "<identifier>"  // e.g. "department": "Salg"
  },
  "action": "create" | "update" | "delete" | "reverse",
  "raw_amounts": ["1500.00"],  // All monetary amounts found
  "dates": ["2026-03-19"],     // All dates found
  "files_needed": true | false  // Whether attached files contain data needed
}

Rules:
- Copy field values VERBATIM from the prompt (names, emails, org numbers)
- Parse monetary amounts as numbers (strip currency symbols)
- Convert all dates to YYYY-MM-DD format
- If the task type is ambiguous, use "unknown"
- Norwegian "kontoadministrator" = role "administrator"
- Spanish "administrador" = role "administrator"
```

### 5.4 File Handling

Some tasks include PDF/image attachments containing invoice data, contracts, or receipts.

```python
import base64
from pathlib import Path

for f in files:
    data = base64.b64decode(f["content_base64"])
    if f["mime_type"] == "application/pdf":
        # PyMuPDF extraction
        import fitz
        doc = fitz.open(stream=data, filetype="pdf")
        text = "\n".join(page.get_text() for page in doc)
        # Feed text to LLM along with prompt
    elif f["mime_type"].startswith("image/"):
        # Use GPT-4o vision or Claude vision
        # base64 encode and send as image in LLM call
        pass
```

**Priority:** Get text-only tasks working first. Add file handling in phase 2.

---

## 6. API Client Wrapper

Build a thin wrapper that handles auth, logging, and error parsing:

```python
import requests
import logging
import json

class TripletexAPI:
    def __init__(self, base_url, session_token):
        self.base_url = base_url.rstrip("/")
        self.auth = ("0", session_token)
        self.call_log = []
    
    def _request(self, method, path, **kwargs):
        url = f"{self.base_url}{path}"
        resp = requests.request(method, url, auth=self.auth, **kwargs)
        
        # Log every call
        entry = {
            "method": method, "path": path,
            "status": resp.status_code,
            "error": None
        }
        
        if resp.status_code >= 400:
            try:
                err = resp.json()
                entry["error"] = err.get("developerMessage", err.get("message", ""))
            except:
                entry["error"] = resp.text[:200]
            logging.warning(f"API {method} {path} → {resp.status_code}: {entry['error']}")
        
        self.call_log.append(entry)
        resp.raise_for_status()
        return resp.json()
    
    def get(self, path, **kwargs):
        return self._request("GET", path, **kwargs)
    
    def post(self, path, **kwargs):
        return self._request("POST", path, **kwargs)
    
    def put(self, path, **kwargs):
        return self._request("PUT", path, **kwargs)
    
    def delete(self, path, **kwargs):
        return self._request("DELETE", path, **kwargs)
    
    def get_call_count(self):
        return len(self.call_log)
    
    def get_error_count(self):
        return sum(1 for c in self.call_log if c["error"])
```

---

## 7. Reference Data Lookups (Cache These)

Several task types need reference IDs. On a fresh account, query these once at the start:

```python
def load_reference_data(api):
    """Call once per submission — costs 3-4 API calls but saves many later."""
    refs = {}
    
    # VAT types (needed for products, order lines, invoices)
    vat_resp = api.get("/ledger/vatType", params={"count": 100, "fields": "id,name,number,percentage"})
    refs["vat_types"] = {v["number"]: v for v in vat_resp["values"]}
    # Common: "3" = 25% MVA, "6" = 0% MVA (verify in sandbox!)
    
    # Payment types (needed for invoice payments)
    pay_resp = api.get("/invoice/paymentType", params={"count": 100, "fields": "id,description"})
    refs["payment_types"] = {v["description"]: v for v in pay_resp["values"]}
    
    # Travel expense cost categories
    cat_resp = api.get("/travelExpense/costCategory", params={"count": 100, "fields": "id,name"})
    refs["cost_categories"] = {v["name"]: v for v in cat_resp["values"]}
    
    # Chart of accounts (for voucher postings)
    acc_resp = api.get("/ledger/account", params={"count": 2000, "fields": "id,number,name"})
    refs["accounts"] = {v["number"]: v for v in acc_resp["values"]}
    
    return refs
```

**Trade-off:** These 3-4 GET calls cost efficiency points but prevent 422 errors from wrong IDs. On a fresh account, you need these. Consider lazy-loading only what the task needs.

---

## 8. Dependency Chains — The #1 Trap

### 8.1 The Complete Dependency Graph

```
VatType ──────────┐
                  ▼
Product ─────► OrderLine ─────► Order ─────► Invoice ─────► Payment
                  ▲                            ▲
Customer ─────────┘                            │
                                               ▼
                                         CreditNote

Employee ─────► TravelExpense ─────► TravelCost
    │
    ▼
Project ──── Customer (link)
    │
    ▼
Department

Supplier ─────► SupplierInvoice ─────► Payment

Account ─────► Voucher/Posting
```

### 8.2 Minimum API Calls Per Task Type

| Task | Min Calls | Sequence |
|---|---|---|
| Create employee | 1 | `POST /employee` |
| Create employee + admin role | 2 | `POST /employee` → `PUT /employee/entitlement/:grantEntitlementsByTemplate` |
| Create customer | 1 | `POST /customer` |
| Create product | 1 (or 2) | `(GET /ledger/vatType)` → `POST /product` |
| Create department | 1 | `POST /department` |
| Create invoice (new customer) | 3 | `POST /customer` → `POST /order` (with inline orderLines) → `POST /invoice` |
| Create invoice (existing product) | 4 | `POST /customer` → `POST /product` → `POST /order` → `POST /invoice` |
| Register payment | 4–5 | Create invoice chain + `PUT /invoice/{id}/:payment` |
| Create credit note | 4+ | Create invoice chain + `PUT /invoice/{id}/:createCreditNote` |
| Create travel expense | 2 | `POST /travelExpense` → `POST /travelExpense/cost` (if costs) |
| Delete travel expense | 2 | `GET /travelExpense` → `DELETE /travelExpense/{id}` |
| Create project | 1–2 | `(POST /customer)` → `POST /project` |
| Create voucher | 1–2 | `(GET /ledger/account)` → `POST /ledger/voucher` |

### 8.3 The Order→Invoice Trap (Most Common Failure)

You **cannot** create an invoice without an order. You **cannot** create a useful order without order lines. Order lines need either a product reference or inline description + price + vatType.

```python
def handle_create_invoice(api, extracted, refs):
    cust = extracted["entities"]["customer"]
    invoice = extracted["entities"]["invoice"]
    items = extracted["entities"].get("orderLines", [])
    
    # 1. Create customer
    cust_result = api.post("/customer", json={
        "name": cust["name"],
        "email": cust.get("email"),
        "isCustomer": True
    })
    customer_id = cust_result["value"]["id"]
    
    # 2. Create order with inline order lines
    order_lines = []
    for item in items:
        line = {
            "description": item.get("description", "Vare"),
            "count": item.get("count", 1),
            "unitPriceExcludingVatCurrency": item.get("unitPrice", 0),
            "vatType": {"id": refs["vat_types"]["3"]["id"]}  # 25% MVA
        }
        order_lines.append(line)
    
    order_result = api.post("/order", json={
        "customer": {"id": customer_id},
        "orderDate": invoice.get("invoiceDate", "2026-03-19"),
        "deliveryDate": invoice.get("invoiceDate", "2026-03-19"),
        "orderLines": order_lines
    })
    order_id = order_result["value"]["id"]
    
    # 3. Create invoice
    inv_result = api.post("/invoice", json={
        "invoiceDate": invoice.get("invoiceDate", "2026-03-19"),
        "invoiceDueDate": invoice.get("invoiceDueDate", "2026-04-19"),
        "orders": [{"id": order_id}]
    })
    return inv_result["value"]["id"]
```

---

## 9. Employee Role Assignment — Critical Detail

The scoring example shows **administrator role = 5 out of 10 points** for employee creation. Here's exactly how roles work:

### 9.1 userType Field (on Employee)

```
"userType": "STANDARD"  — Reduced access, limited entitlements
"userType": "EXTENDED"   — Can be given all system entitlements  
"userType": "NO_ACCESS"  — No login access
```

If the task says "kontoadministrator" (account administrator), set `userType: "EXTENDED"` on create, **then** assign entitlements.

### 9.2 Entitlement Templates

```
PUT /employee/entitlement/:grantEntitlementsByTemplate
  ?employeeId=123&template=administrator
```

The `template` parameter value is likely one of the entitlement templates available. **Discover available templates in the sandbox:**

```
GET /employee/entitlement?employeeId={your_test_employee_id}&fields=*
```

### 9.3 Possible Role Mappings (Verify in Sandbox)

| Prompt Keyword (multi-lang) | Template Value (likely) |
|---|---|
| kontoadministrator, account administrator, administrador | `administrator` |
| lønnsansvarlig, salary administrator | TBD — check sandbox |
| prosjektleder, project manager | TBD — may be project role, not entitlement |
| regnskapsfører, accountant | TBD — check sandbox |

**Action item:** In the sandbox, create an employee with `userType: "EXTENDED"`, then try `PUT /employee/entitlement/:grantEntitlementsByTemplate` with different template values and see what sticks.

---

## 10. Efficiency Optimization Playbook

Only pursue this after correctness reaches 1.0 on a task type.

### 10.1 Rules for Maximum Efficiency

1. **Never GET what you just POSTed.** The POST response contains the created entity with its ID.
2. **Don't search for entities you created.** Track IDs in memory during the request.
3. **Use `?fields=id` on GETs.** Don't fetch fields you don't need.
4. **Chain dependencies.** Use IDs from each response to feed the next call.
5. **Inline where possible.** Order lines can be inlined in the order POST — no need for separate `POST /order/orderline` calls.
6. **Zero 4xx errors.** Validate before sending. Use the OpenAPI spec as your source of truth.

### 10.2 Reference Data Trade-off

Every GET for reference data costs efficiency points. Strategies:

| Strategy | Calls | Risk |
|---|---|---|
| Pre-fetch all ref data at start | +3-4 calls always | Over-fetching for simple tasks |
| Lazy-load only what's needed | +0-2 calls per type | Minimal overhead |
| Hardcode common IDs | +0 calls | Breaks if IDs differ per fresh account |

**Recommendation:** Lazy-load. Only fetch `vatType` if the task involves products/invoices. Only fetch `paymentType` if registering payments.

---

## 11. Logging & Data Collection

### 11.1 Log Every Submission (JSON)

```python
import json, datetime

def log_submission(prompt, files, task_type, api_calls, score, notes):
    entry = {
        "timestamp": datetime.datetime.now().isoformat(),
        "prompt": prompt[:500],  # truncate for storage
        "language": detect_language(prompt),  # simple heuristic
        "files": [f["filename"] for f in files],
        "task_type_detected": task_type,
        "api_calls": api_calls,  # from api.call_log
        "call_count": len(api_calls),
        "error_count": sum(1 for c in api_calls if c["error"]),
        "notes": notes
    }
    with open("submissions.jsonl", "a") as f:
        f.write(json.dumps(entry, ensure_ascii=False) + "\n")
```

### 11.2 Build a Prompt Library

After 10-15 submissions you'll have templates for each task type in each language. This lets you:
- Fine-tune LLM parsing with real few-shot examples
- Build regression tests
- Identify which task types you haven't seen
- Spot patterns across language variants

### 11.3 RUNS.md

| # | Time | Task Type | Lang | Score | Calls | Errors | Notes |
|---|---|---|---|---|---|---|---|
| 1 | 19:30 | create_employee | nb | 0.0 | 0 | - | Pipeline test |
| 2 | 19:45 | create_employee | nb | 0.5 | 1 | 0 | Missing role |
| 3 | 20:00 | create_employee | nb | 1.0 | 2 | 0 | Added entitlement template |
| 4 | 20:30 | create_invoice | en | 0.8 | 5 | 2 | Missing product ref |

---

## 12. Phased Execution Plan

### Phase 1: Foundation (Hours 0–2) — Thursday Evening

| Priority | Task | Target |
|---|---|---|
| 1 | Claim sandbox, explore UI, make manual API calls | Visceral understanding |
| 2 | Deploy `/solve` skeleton (FastAPI + cloudflared) | End-to-end pipeline |
| 3 | Wire up LLM extractor (GPT-4o structured output) | Task classification |
| 4 | First handler: `create_employee` + admin role | Score > 0 on real submission |
| 5 | First handler: `create_customer` | Second task type covered |

**Gate:** You have a working agent that scores > 0 on at least one task type.

### Phase 2: Tier 1 Breadth (Hours 2–6) — Thursday Night

| Priority | Task | Target |
|---|---|---|
| 6 | Handler: `create_product` | 3rd task type |
| 7 | Handler: `create_department` | 4th task type — simplest entity |
| 8 | Handler: `create_supplier` | 5th task type |
| 9 | Handler: `update_employee` (PUT with version) | 6th task type |
| 10 | Broad submission campaign | Discover all Tier 1 task types |
| 11 | Log all prompts, analyze patterns | Build prompt library |

**Gate:** All Tier 1 task types covered with >0 scores.

### Phase 3: Tier 2 Depth (Hours 6–18) — Friday

| Priority | Task | Target |
|---|---|---|
| 12 | Handler: `create_invoice` (full dependency chain) | Highest-value Tier 2 |
| 13 | Handler: `register_payment` | Extends invoice handler |
| 14 | Handler: `create_credit_note` | Extends invoice handler |
| 15 | Handler: `create_travel_expense` + costs | Travel flows |
| 16 | Handler: `delete_travel_expense` | Deletion pattern |
| 17 | Handler: `create_project` linked to customer | Project flows |
| 18 | File handling (PDF text extraction) | Tasks with attachments |
| 19 | Analyze all submission logs, fix failing patterns | Correctness → 1.0 |

**Gate:** Tier 2 tasks scoring 1.0+ consistently.

### Phase 4: Efficiency Push (Hours 18–30) — Friday Night/Saturday

| Priority | Task | Target |
|---|---|---|
| 20 | Minimize API calls per task type | Efficiency bonus |
| 21 | Eliminate all 4xx errors | Zero-error runs |
| 22 | Lazy-load reference data | Reduce unnecessary GETs |
| 23 | Regression test suite from logged prompts | Prevent regressions |

**Gate:** Perfect tasks scoring 2.0+ (Tier 1) or 4.0+ (Tier 2).

### Phase 5: Tier 3 + Polish (Hours 30–69) — Saturday/Sunday

| Priority | Task | Target |
|---|---|---|
| 24 | Tier 3 tasks open — analyze new task types | New handlers |
| 25 | Handler: complex voucher/posting workflows | Tier 3 coverage |
| 26 | Handler: bank reconciliation | Tier 3 coverage |
| 27 | Image OCR pipeline (scanned invoices) | File-based tasks |
| 28 | Coverage sweep — find uncovered task types | Max breadth |
| 29 | Final efficiency pass | Max scores |

---

## 13. Critical Success Factors (Ranked)

1. **Breadth over depth.** Total score = sum of 30 task bests. Cover 25 tasks at 1.0 (= 25) beats 10 tasks at 2.0 (= 20). Build handlers for every task type before optimizing any single one.

2. **The LLM parser must be flawless.** If it reads "Ola" as "Ole", you lose points. Use structured output mode. Copy field values verbatim.

3. **Understand the dependency chains.** Invoice requires order requires order lines requires (product or inline description) + vatType. This is where most teams will fumble.

4. **Employee entitlements are the hidden points.** The "administrator role" check is 5/10 points in the create employee example. The `PUT /employee/entitlement/:grantEntitlementsByTemplate` endpoint is critical and easy to miss.

5. **Efficiency only matters on perfect runs.** Don't optimize call count until correctness = 1.0. An imperfect-but-fast run gets NO efficiency bonus.

6. **Rate limits are tight.** 5 per task per day, 3 concurrent. Don't waste submissions on broken code. Test against the sandbox. Log everything from real submissions.

7. **The OpenAPI spec is your bible.** Feed relevant endpoint schemas to your LLM. This eliminates 422 validation errors better than any retry logic.

---

## 14. Anti-Patterns to Avoid

| Anti-Pattern | Impact | Do This Instead |
|---|---|---|
| LLM generates raw HTTP calls | Hallucinated endpoints, wrong field names, 4xx storm | LLM extracts data → deterministic code makes calls |
| Retry loops on errors | Each 4xx hurts efficiency score permanently | Validate before sending, fix once |
| Ignoring proxy `base_url` | All calls fail → 0 score | Always use `creds["base_url"]` |
| Hardcoding Norwegian only | 6/7 languages = 0 score | Multi-language LLM extraction from day one |
| Skipping sandbox exploration | Guessing at required fields = 422 errors | Hands-on API testing before coding |
| Optimizing one task for hours | 29 other tasks unscored | Breadth first |
| Not logging submissions | Can't analyze patterns or build regression tests | Log every prompt, call, and score |
| Creating invoice without order | 422 error, wasted submission | Always: customer → order (with lines) → invoice |
| Fetching all reference data upfront | 4+ unnecessary GETs on simple tasks | Lazy-load only what the specific task needs |
| Ignoring version field on PUTs | 409 Conflict error | Always include `version` from prior GET/POST response |

---

## 15. Tech Stack

```
Agent endpoint:     Python 3.12 + FastAPI + uvicorn
LLM:                GPT-4o (structured output mode) or Claude 3.5 Sonnet
HTTP client:        requests or httpx (for async)
PDF extraction:     PyMuPDF (fitz)
Image OCR:          GPT-4o vision (pass base64 image to LLM)
Deployment:         Cloud VM (Azure/GCP) or cloudflared tunnel for local dev
Monitoring:         Structured JSON logging to file (submissions.jsonl)
```

```bash
pip install fastapi uvicorn requests openai anthropic pymupdf python-multipart
```

---

## 16. Quick Reference Card

### Endpoints You'll Use Most

| Endpoint | Method | Purpose |
|---|---|---|
| `/employee` | POST | Create employee |
| `/employee/{id}` | PUT | Update employee |
| `/employee/entitlement/:grantEntitlementsByTemplate` | PUT | Assign role |
| `/customer` | POST | Create customer |
| `/product` | POST | Create product |
| `/department` | POST | Create department |
| `/order` | POST | Create order (with inline orderLines) |
| `/invoice` | POST | Create invoice (refs order) |
| `/invoice/{id}/:payment` | PUT | Register payment (query params!) |
| `/invoice/{id}/:createCreditNote` | PUT | Issue credit note |
| `/travelExpense` | POST | Create travel expense |
| `/travelExpense/cost` | POST | Add cost to travel expense |
| `/travelExpense/{id}` | DELETE | Delete travel expense |
| `/project` | POST | Create project |
| `/supplier` | POST | Create supplier |
| `/ledger/voucher` | POST | Create voucher/journal entry |
| `/ledger/vatType` | GET | List VAT types (ref data) |
| `/invoice/paymentType` | GET | List payment types (ref data) |
| `/ledger/account` | GET | Chart of accounts (ref data) |

### Auth Template

```python
auth = ("0", creds["session_token"])
base_url = creds["base_url"]  # ALWAYS use this, never hardcode
```

### Response Parsing Template

```python
# Single entity (POST, GET by ID)
entity_id = response.json()["value"]["id"]

# List (GET search)
entities = response.json()["values"]  # may be empty []
```

---

## 17. Where to Start RIGHT NOW

**Minute 0:** Claim sandbox. Open Tripletex UI. Open Swagger docs at `https://kkpqfuj-amager.tripletex.dev/v2-docs/`

**Minute 5:** `POST /employee` with `{"firstName":"Test","lastName":"User","email":"test@test.no"}`. Note the response shape. Try `POST /customer`. Try `POST /invoice` — it WILL fail with a 422. **Read that error message.** It tells you that you need orders.

**Minute 15:** Write the FastAPI skeleton. Deploy with `npx cloudflared tunnel --url http://localhost:8000`. Submit once. Log the request.

**Minute 30:** Wire up GPT-4o. Parse the prompt you received. Build handler for whatever task type you got.

**Minute 45:** Submit again. You should score > 0.

**Hour 1+:** Breadth. Add handlers. Cover task types. Log everything.

**The competition rewards breadth × correctness × efficiency, in that order.**
