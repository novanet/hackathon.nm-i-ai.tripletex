# NM i AI 2026 — Tripletex: AI Accounting Agent

Challenge 1 of 4 in [NM i AI 2026](https://app.ainm.no/tasks) — Norway's National AI Championship.
**Competition window:** March 19 at 18:00 CET → March 22 at 15:00 CET (69 hours)
**Prize pool:** 1,000,000 NOK total

---

## Task Description

Build an AI agent that executes accounting tasks via the Tripletex API.

- **Metric:** Score per task (rolling average)
- **Response limit:** 300 seconds per request
- **Tasks to solve:** 30 unique tasks
- **Total score:** Sum of best score per task

**Submission page:** https://app.ainm.no/submit/tripletex
**Task docs:** https://app.ainm.no/docs/tripletex/overview

---

## How Submission Works

Deploy an HTTPS endpoint at `/solve`. The platform will send a random accounting task as a JSON payload and score the result.

To submit:
1. Go to https://app.ainm.no/submit/tripletex
2. Enter your **Endpoint URL** (e.g. `https://your-api.example.com/solve`)
3. Optionally provide an **API Key** (Bearer token / API key for your endpoint)
4. Click **Submit** — the platform sends a task and scores the response

---

## Tripletex Sandbox API

The contest uses a dedicated Tripletex sandbox environment.

| | |
|---|---|
| **API Docs (Swagger UI)** | https://kkpqfuj-amager.tripletex.dev/v2-docs/ |
| **Base URL** | `https://kkpqfuj-amager.tripletex.dev/v2` |
| **OpenAPI spec** | `https://kkpqfuj-amager.tripletex.dev/v2/openapi.json` |

A free sandbox account can be requested from the submission page ("Get Sandbox Account") to explore the API and web interface before submitting.

---

## Tripletex API Overview

### Authentication

The API uses **session token authentication** via Basic Auth:

1. Obtain a `consumerToken` (from Tripletex after API registration)
2. Create an `employeeToken` in Tripletex account settings under "API access"
3. Call `POST /token/session/:create` with consumerToken + employeeToken → returns a `sessionToken`
4. Authenticate requests with `Authorization: Basic <base64("0:<sessionToken>")>`
   - `0` = company of the employee; use company ID for accountant client access

### Key API Modules

| Module | Description |
|---|---|
| `ledger/voucher` | Vouchers / journal entries |
| `ledger/account` | Chart of accounts |
| `ledger/posting` | Ledger postings |
| `invoice` | Customer invoices |
| `supplierInvoice` | Supplier invoices |
| `customer` | Customers |
| `supplier` | Suppliers |
| `product` | Products |
| `order` | Sales orders |
| `project` | Projects |
| `employee` | Employees |
| `salary/payslip` | Payslips |
| `bank/reconciliation` | Bank reconciliation |
| `balanceSheet` | Balance sheet |
| `token/session` | Session token management |

### API Conventions

- **Partial updates:** Use `PUT` with optional fields (no `PATCH`)
- **Actions:** Prefixed with `:` — e.g. `POST /hours/123/:approve`
- **Aggregates:** Prefixed with `>` — e.g. `GET /hours/>thisWeeksBillables`
- **Dates:** ISO 8601 `YYYY-MM-DD`; DateTimes: `YYYY-MM-DDThh:mm:ss`
- **Versioning:** `version` field on resources prevents overwriting concurrent updates
- **Field selection:** Use `?fields=field1,field2` or `?fields=*` for all fields
- **Sorting:** `?sorting=date,-project.name`

### Response Envelope

```json
// Multiple values
{
  "from": 0,
  "count": 10,
  "values": [ { /* object */ } ]
}

// Single value
{
  "value": { /* object */ }
}
```

### Rate Limiting

Rate limit status is returned in response headers:
- `X-Rate-Limit-Limit` — allowed requests in current period
- `X-Rate-Limit-Remaining` — remaining requests
- `X-Rate-Limit-Reset` — seconds until reset

HTTP `429` is returned when the limit is exceeded.

---

## Overall Scoring & Ranking

1. Each task's scores are normalized 0–100 (divided by highest score across all teams)
2. Overall score = average of all four normalized task scores (25% each)
3. Teams ranked by overall score descending

**The four tasks:**

| Task | Type | Submission |
|---|---|---|
| **Tripletex** | AI accounting agent | HTTPS API endpoint |
| Grocery Bot | Real-time grocery store navigation | WebSocket agent |
| Astar Island | Norse world prediction | REST API predictions |
| NorgesGruppen Data | Grocery shelf object detection | Code upload (ZIP) |

---

## Links

| Resource | URL |
|---|---|
| Competition platform | https://app.ainm.no/tasks |
| Submit endpoint | https://app.ainm.no/submit/tripletex |
| Task docs | https://app.ainm.no/docs/tripletex/overview |
| Sandbox API Swagger | https://kkpqfuj-amager.tripletex.dev/v2-docs/ |
| Docs overview | https://app.ainm.no/docs |
| Rules | https://app.ainm.no/rules |
| Leaderboard | https://app.ainm.no/leaderboard |
| MCP docs server | `claude mcp add --transport http nmiai https://mcp-docs.ainm.no/mcp` |

