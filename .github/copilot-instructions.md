# Copilot Instructions — NM i AI 2026 Tripletex Agent

**Always read `opening-strategy.md` at the repo root before making architectural decisions or implementing new task handlers.** It contains the complete API mapping for all 30 task types, dependency chains, scoring rules, and the LLM system prompt. Treat it as the source of truth.

**Always read `knowledge.md` at the repo root before debugging, implementing handlers, or investigating failures.** It contains verified learnings from real submission runs — API quirks, extraction pitfalls, scoring details, and efficiency baselines. **After fixing a bug, discovering an API quirk, or learning something new about scoring/validation, update `knowledge.md`** by appending to the relevant category with a short entry and the date.

## Project Overview

This is a hackathon competition agent for NM i AI 2026. It exposes a single `POST /solve` HTTPS endpoint that receives accounting task prompts (in 7 languages), uses an LLM to parse them, executes Tripletex API calls, and returns `{"status": "completed"}`.

## Tech Stack

- **C# / .NET 10** with ASP.NET Core Minimal APIs
- **LLM**: GPT-4o via GitHub Models (`https://models.github.ai/inference`, model: `openai/gpt-4o`)
- **NuGet**: `OpenAI` package with custom endpoint pointed at GitHub Models
- **Tunnel**: cloudflared for HTTPS exposure
- **Logging**: Serilog, structured JSON to file

## Architecture — Hybrid: Deterministic + Fallback

The architecture is defined in `opening-strategy.md` §4. Follow it precisely:

1. **LLM Extractor** — GPT-4o with structured output parses the prompt into `{task_type, entities, fields}`
2. **Task Router** — routes to a deterministic handler if the task type is mapped
3. **Deterministic Handlers** — each handler knows the exact API calls needed (see §3 and §8.2 of opening-strategy.md for minimum call counts)
4. **Fallback Agent** — for unmapped task types, use a tool-use loop with the LLM
5. **TripletexApiClient** — thin wrapper handling auth, logging, and error parsing

## Tripletex API Rules (CRITICAL)

- **Auth**: Basic Auth, username is always `"0"`, password is the `session_token` from the request body
- **Base URL**: ALWAYS use `credentials.base_url` from the request. NEVER hardcode a Tripletex URL
- **Response envelope**: Single entity = `response.value`, list = `response.values`
- **Error envelope**: Parse `validationMessages` array — it tells you exactly what's wrong
- **PUT updates**: MUST include `version` field from prior GET/POST response
- **Actions**: Use `:` prefix (e.g., `PUT /invoice/{id}/:payment`)
- **Dates**: Always `YYYY-MM-DD` format
- **Pagination**: `?from=0&count=100`

## Request/Response Format

### Input (`POST /solve`)

```json
{
  "prompt": "Create an employee named Ola Nordmann...",
  "files": [
    {
      "filename": "invoice.pdf",
      "content_base64": "...",
      "mime_type": "application/pdf"
    }
  ],
  "tripletex_credentials": {
    "base_url": "https://tx-proxy.ainm.no/v2",
    "session_token": "abc123..."
  }
}
```

### Output

```json
{ "status": "completed" }
```

Always return `{"status": "completed"}` after executing the task. The platform verifies results by querying the Tripletex API directly.

## Scoring Rules (Guide All Decisions)

- `total = sum of best scores across all 30 task types`
- `task_score = correctness × tier_multiplier + efficiency_bonus`
- **Breadth > depth**: covering 25 tasks at 1.0 (=25 pts) beats 10 tasks at 2.0 (=20 pts)
- **Correctness first**: efficiency bonus only applies when correctness = 1.0
- **Every 4xx error permanently reduces efficiency score** — validate before sending, don't retry blindly
- **Fewer API calls = higher efficiency bonus** — see §8.2 in opening-strategy.md for minimum call counts per task

## Key Dependency Chains

These are the #1 source of failures. See `opening-strategy.md` §8 for the full graph.

- **Invoice**: Customer → Order (with OrderLines + VatType) → Invoice. You CANNOT create an invoice without an order.
- **Employee admin role**: `POST /employee` then `PUT /employee/entitlement/:grantEntitlementsByTemplate?employeeId={id}&template=administrator`. Worth 50% of employee scoring points.
- **Payment**: Full invoice chain + `PUT /invoice/{id}/:payment` (query params, not body)
- **Travel expense**: `POST /travelExpense` then `POST /travelExpense/cost` for each cost line

## Anti-Patterns (NEVER Do These)

- Never let the LLM generate raw HTTP calls — it hallucinates endpoints and field names
- Never hardcode the Tripletex base URL — always use `credentials.base_url`
- Never create an invoice without first creating an order with order lines
- Never retry on 4xx errors in a loop — each error permanently hurts the efficiency score
- Never skip the `version` field on PUT updates
- Never fetch all reference data upfront for simple tasks — lazy-load only what's needed
- Never translate field values extracted from prompts — copy them character-for-character

## Multi-Language Support

Prompts arrive in 7 languages: Norwegian bokmål, English, Spanish, Portuguese, Nynorsk, German, French. GPT-4o handles all natively. Extract field values VERBATIM — never translate names, emails, or organization numbers. See `opening-strategy.md` §5 for the complete LLM system prompt.

## Dev Workflow (MANDATORY)

**Starting / restarting the agent:** Always use the helper script — never manually `Get-Process | Kill` then `dotnet run`:

```powershell
.\scripts\Start-Agent.ps1              # foreground (blocks terminal)
.\scripts\Start-Agent.ps1 -Background  # background (returns immediately)
```

The script kills any existing `TripletexAgent` process, waits for file locks to release, then starts fresh.

**Testing prompts against the running agent:**

```powershell
.\scripts\Test-Solve.ps1 "Opprett en kunde med navn 'Test AS'"
```

Reads Tripletex credentials and API key from .NET user-secrets automatically. Prints the response and tails the latest log file.

**Starting a tunnel for competition submissions:**

Two tunnel options are available. Prefer ngrok (cloudflared has TLS issues on some ISPs like Telenor):

```powershell
.\scripts\Start-Tunnel.ps1             # ngrok tunnel (requires ngrok installed)
.\scripts\Start-Cloudflared.ps1        # cloudflare quick tunnel (no account needed)
.\scripts\Start-Cloudflared.ps1 -Kill  # stop cloudflared
```

**Submitting a competition run:**

```powershell
$env:AINM_TOKEN = "<access_token from browser cookies>"
.\scripts\Submit-Run.ps1               # auto-detects tunnel, submits, polls for results
.\scripts\Submit-Run.ps1 -NoWait       # submit without polling
```

The script checks that the agent is running, finds the active tunnel URL (tries cloudflared first, falls back to ngrok), sends a health check ping, then submits to the competition API. After submission it polls for results every 10s for up to 10 minutes.

**Competition constraints:** Max 32 submissions/day, max 3 concurrent (429 if exceeded).

**Never do these:**

- Don't run `dotnet run` without first stopping the old process — it will fail with a port/file lock
- Don't manually `Get-Process -Name TripletexAgent | Kill` followed by `dotnet run` — use `Start-Agent.ps1` instead

## File Structure Convention

```
scripts/
  Start-Agent.ps1               — Kill + restart agent (supports -Background)
  Start-Tunnel.ps1              — Start ngrok HTTPS tunnel
  Start-Cloudflared.ps1         — Start cloudflare quick tunnel (supports -Kill)
  Test-Solve.ps1                — Send test prompt to agent, tail logs
  Submit-Run.ps1                — Submit competition run, poll for results
src/
  Program.cs                    — Minimal API setup, /solve endpoint, ping fast-path
  Models/
    SolveRequest.cs             — Request DTOs
    ExtractionResult.cs         — LLM extraction output model
  Services/
    TripletexApiClient.cs       — HTTP client wrapper (auth, logging, errors)
    LlmExtractor.cs             — GPT-4o structured extraction (3-retry + regex fallback)
    TaskRouter.cs               — Routes task_type to handler
    ReferenceDataService.cs     — Lazy-loaded VAT types, payment types, accounts
  Handlers/
    ITaskHandler.cs             — Handler interface
    EmployeeHandler.cs          — Create/update employee + role assignment
    CustomerHandler.cs          — Create customer (with address parsing)
    SupplierHandler.cs          — Create supplier
    ProductHandler.cs           — Create product
    DepartmentHandler.cs        — Create department (with collision retry)
    ProjectHandler.cs           — Create project (customer + PM resolution)
    InvoiceHandler.cs           — Full invoice chain (customer → order → invoice → send)
    PaymentHandler.cs           — Register payment (optimized: 7 calls)
    CreditNoteHandler.cs        — Create credit note on existing invoice
    ContactHandler.cs           — Create contact person for customer
    TravelExpenseHandler.cs     — Create travel expense with cost lines
    FallbackAgentHandler.cs     — LLM tool-use loop for unmapped tasks
    ...                         — One handler per task type
  logs/
    sandbox.jsonl               — Sandbox test submission logs
    submissions.jsonl           — Competition submission logs
    agent-*.log                 — Serilog application logs
```
