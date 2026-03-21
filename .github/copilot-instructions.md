# Copilot Instructions — NM i AI 2026 Tripletex Agent

**Always read `opening-strategy.md` at the repo root before making architectural decisions or implementing new task handlers.** It contains the complete API mapping for all 30 task types, dependency chains, scoring rules, and the LLM system prompt. Treat it as the source of truth.

**Always read `knowledge.md` at the repo root before debugging, implementing handlers, or investigating failures.** It contains verified learnings from real submission runs — API quirks, extraction pitfalls, scoring details, and efficiency baselines. **After fixing a bug, discovering an API quirk, or learning something new about scoring/validation, update `knowledge.md`** by appending to the relevant category with a short entry and the date.

**Read `entity-model.md` at the repo root when working on entity relationships, dependency chains, or debugging complex multi-step tasks.** It maps all Tripletex entity schemas, required fields, cross-references, dependency chains per task type, action endpoints, and common pitfalls. Use it as a quick lookup before implementing or fixing handlers.

**Read `tripletex-docs.md` at the repo root for official Tripletex developer documentation.** It covers authentication, VAT code tables (with DB IDs and percentages), invoice/order pricing rules (`isPrioritizeAmountsIncludingVat`), customer EHF requirements, voucher posting ledgerType rules, department activation effects, webhooks, and more. Consult it when debugging API errors or unsure about field semantics.

**Keep `SandboxValidator.cs` (`src/Services/SandboxValidator.cs`) in sync with actual competition checks.** After every submission, compare local validation scores to competition scores. Any divergence means our validator is wrong — fix it immediately. See the "Validation Feedback Loop" section below for the process.

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
- **GET requests are FREE** — they are NOT counted towards efficiency. Read as much data as you need. Never skip a GET to "save calls".
- **Only write calls count**: POST, PUT, DELETE, PATCH are the only methods that affect the efficiency bonus. Fewer write calls = higher bonus.
- **Every 4xx error on a write call permanently reduces efficiency bonus** — validate before sending, don't retry blindly
- **Error cleanliness matters**: An agent that gets write calls right without trial-and-error is rewarded. Avoid speculative writes.

## Development Priority (Follow This Order)

1. **Correctness first** — Get all 30 task types passing all checks (correctness = 1.0). Extra GET requests are free and never hurt. An extra write call that prevents a 4xx error is worth it.
2. **Breadth second** — Cover all task types before optimizing any single one. A handler that scores 0.8 on a new task is worth more than squeezing 1.0→1.0+efficiency on an existing one.
3. **Efficiency last** — Only minimize write calls (POST/PUT/DELETE/PATCH) and eliminate 4xx errors AFTER all tasks pass at full correctness. Never sacrifice correctness for fewer calls. Never remove GET requests to "optimize" — they are free.

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
- Never remove or skip GET requests to "optimize" efficiency — GET requests are free and not counted
- Never translate field values extracted from prompts — copy them character-for-character

## Multi-Language Support

Prompts arrive in 7 languages: Norwegian bokmål, English, Spanish, Portuguese, Nynorsk, German, French. GPT-4o handles all natively. Extract field values VERBATIM — never translate names, emails, or organization numbers. See `opening-strategy.md` §5 for the complete LLM system prompt.

## Dev Workflow (MANDATORY)

**Starting / restarting the agent:** Always use the helper script — never manually `Get-Process | Kill` then `dotnet run`:

```powershell
.\scripts\Start-Agent.ps1              # foreground (blocks terminal)
.\scripts\Start-Agent.ps1 -Background  # background (returns immediately)
```

The script kills any existing `TripletexAgent` process, builds the project, waits for file locks to release, then starts fresh. **Never run `dotnet build` or `dotnet run` directly** — always use `Start-Agent.ps1`. It handles the full kill → build → start cycle and avoids file-lock errors from a running process.

**Testing prompts against the running agent:**

```powershell
.\scripts\Test-Solve.ps1 "Opprett en kunde med navn 'Test AS'"
```

Reads Tripletex credentials and API key from .NET user-secrets automatically. Prints the response and tails the latest log file.

If the prompt originally came with file attachments (i.e. the `files` array in `submissions.jsonl` or `sandbox.jsonl` is non-empty), you **must** pass the saved files via `-FilePaths` — otherwise the handler will behave differently from the competition run. See **Replaying prompts with files** below for how to locate the saved files.

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
.\scripts\Submit-Run.ps1               # auto-starts agent + tunnel, submits, polls 2 min, replays locally
.\scripts\Submit-Run.ps1 -NoWait       # submit without polling or replay
.\scripts\Submit-Run.ps1 -NoReplay     # submit + poll but skip local replay
```

The script:

1. Checks agent is running — auto-starts via `Start-Agent.ps1 -Background` if not
2. Checks tunnel is running — auto-starts cloudflared if no tunnel found
3. Sends a health check ping, then submits to the competition API
4. Polls for results every 10s for up to 2 minutes
5. After completion, replays new requests from `submissions.jsonl` locally via `Test-Solve.ps1` and prints a summary

**Competition constraints:** Max 32 submissions/day, max 3 concurrent (429 if exceeded).

**Replaying prompts with files:** Whenever replaying a prompt — whether running `Test-Solve.ps1` directly or via the automated replay in `Submit-Run.ps1` — check if the original request had file attachments. If the `files` array in the `submissions.jsonl` (or `sandbox.jsonl`) entry is non-empty, the saved files must be passed to `Test-Solve.ps1` via `-FilePaths`. The agent saves received files to `src/logs/files/<yyyyMMdd-HHmmss>_<task_type>/` at request time. The `submissions.jsonl` entry logs a `files` array with `{Filename, MimeType}` (no content) and a `timestamp` field. To locate the saved files for a replay:

1. Parse the `timestamp` from the `submissions.jsonl` entry (UTC ISO-8601).
2. Convert to local time and format as `yyyyMMdd-HHmmss` (rounded to the nearest second).
3. Find the matching directory under `src/logs/files/` whose name starts with that timestamp prefix and ends with the task type — e.g. `20260321-104557_bank_reconciliation`.
4. Pass every file in that directory to `Test-Solve.ps1` with `-FilePaths`:

```powershell
$dir = "src/logs/files/20260321-104557_bank_reconciliation"
& "$PSScriptRoot\Test-Solve.ps1" -Prompt $entry.prompt -FilePaths (Get-ChildItem $dir | Select-Object -ExpandProperty FullName)
```

If no matching directory is found (e.g. timestamp drift > 2s), skip file attachment and log a warning — the replay will still run, just without the files.

**Competition vs local sandbox:** Competition submission runs use a **clean Tripletex environment** every time — no pre-existing customers, products, employees, etc. The local sandbox reuses state across test runs, so entities created in previous tests persist. Never assume entities exist in competition; always create them. Conversely, don't add "search before create" logic just because the local sandbox already has a duplicate — that wastes API calls in competition.

**Never do these:**

- Don't run `dotnet build`, `dotnet run`, or any manual build/run commands — always use `Start-Agent.ps1`
- Don't manually `Get-Process -Name TripletexAgent | Kill` followed by `dotnet run` — use `Start-Agent.ps1` instead
- Don't try to build while the agent is running — the binary will be locked. `Start-Agent.ps1` kills first, then builds.

## Validation Feedback Loop (MANDATORY)

`SandboxValidator.cs` is our local mirror of the competition validator. It runs after every local `Test-Solve.ps1` call and logs scores to `logs/validations.jsonl`. **The sole purpose is to predict competition scores accurately so we know when a fix is real before spending a submission.**

## Task Documentation (Auto-Updated)

**Read `tasks/PRIORITY_EXECUTION_ORDER.md` before deciding what to work on.** It shows current scores, gaps vs the leader, and the optimal execution order. It is auto-generated from leaderboard data.

Each task has a folder under `tasks/` with:

| File | Source | Purpose |
|---|---|---|
| `strategy.md` | **Hand-written** | Root cause analysis, fix plan, implementation notes. Edit manually. |
| `prompts.md` | Auto-generated | All unique prompts seen in competition + sandbox runs |
| `runs.md` | Auto-generated | Latest run: API calls, errors, extraction, validation checks |
| `history.md` | Auto-generated | Chronological run history across all submissions |

**When to read task files:**
- Before working on a task → read its `strategy.md` for context and plan
- When debugging a failure → read its `runs.md` for API calls and error details
- When checking prompt variations → read its `prompts.md`

**Auto-refresh:** `Refresh-Tasks.ps1` regenerates all auto-generated files + `PRIORITY_EXECUTION_ORDER.md`.
- **After `Submit-Run.ps1`** — called automatically at the end of every submission
- **After `Test-Solve.ps1`** — run manually: `.\scripts\Refresh-Tasks.ps1`
- **Anytime** — safe to run repeatedly, always reads from latest JSONL data

**After fixing a handler or adding a new one:**
1. Test locally with `Test-Solve.ps1`
2. Run `.\scripts\Refresh-Tasks.ps1` to update task docs
3. Check updated `runs.md` for the task to verify correctness
4. Check `PRIORITY_EXECUTION_ORDER.md` to confirm score improvement
5. Submit with `Submit-Run.ps1` (which auto-refreshes after completion)

## Logging & Diagnostics

### Log Files — What Each Contains

| File                     | Written by                      | Contains                                                                                                                |
| ------------------------ | ------------------------------- | ----------------------------------------------------------------------------------------------------------------------- |
| `logs/submissions.jsonl` | C# agent (competition requests) | One JSONL entry per `/solve` call: prompt, extraction, api_calls, handler result, run_id, task_index                    |
| `logs/sandbox.jsonl`     | C# agent (local sandbox tests)  | Same schema as submissions.jsonl, used for local `Test-Solve.ps1` runs                                                  |
| `logs/results.jsonl`     | `Submit-Run.ps1`                | Per-submission results: competition score, checks passed/failed, full task details with prompt + extraction + api_calls |
| `logs/leaderboard.jsonl` | `Submit-Run.ps1`                | Per-submission leaderboard snapshot: total best score, score deltas, attempted tasks with correlation                   |
| `logs/validations.jsonl` | C# agent (sandbox validator)    | Per-task local validation: check field/expected/actual/passed/points                                                    |

### run_id — How Tasks Are Grouped

Every `/solve` call logs a `run_id` = first 8 hex chars of `SHA256(session_token)`. All 30 tasks in a competition run share the same session_token, so they share the same `run_id`. Each task also gets a sequential `task_index` (1, 2, 3...) within the run. Use `run_id` to:

- Group all tasks belonging to a single competition submission
- Correlate `submissions.jsonl` entries with `results.jsonl` and `leaderboard.jsonl`
- Filter `Analyze-Run.ps1` output

### Analyzing a Run

```powershell
# Analyze the latest run (auto-detects run_id from last entry)
.\scripts\Analyze-Run.ps1

# Analyze a specific run_id
.\scripts\Analyze-Run.ps1 -RunId "a1b2c3d4"

# Analyze by competition submission_id
.\scripts\Analyze-Run.ps1 -SubmissionId "sub_abc123"

# Show full details (prompts, LLM extraction, API calls)
.\scripts\Analyze-Run.ps1 -ShowPrompt -ShowExtraction -ShowApiCalls

# Analyze sandbox runs
.\scripts\Analyze-Run.ps1 -Sandbox -Last 5
```

The script answers all diagnostic questions:

1. **Which prompt was run?** → `-ShowPrompt` flag, or look at `prompt` field in entries
2. **What ExtractionResult did the LLM produce?** → `-ShowExtraction` flag, or `extraction` field
3. **Which Tripletex API requests/responses?** → `-ShowApiCalls` flag shows method, path, status, request body, response snippet. Error responses include up to 2000 chars.
4. **How many tasks passed or failed?** → Summary header shows succeeded/failed counts
5. **What checks passed or failed?** → Competition checks section with ✓/✗ marks

### Answering "Why did task X fail?"

1. Run `.\scripts\Analyze-Run.ps1 -ShowApiCalls -ShowExtraction`
2. Find the task by type or index
3. Check: Was extraction correct? (task_type, entities, relationships)
4. Check: Did any API call return 4xx? (red-highlighted in output)
5. Check: Was the response error message clear? (shown under failed calls)
6. Cross-reference with competition checks in the Competition Result section

### Process — After Every Competition Submission

1. **Compare scores**: For each task type in the submission replay, compare `local_score / local_max` vs `competition_score / competition_max`.
2. **Identify divergence**: If local says pass but competition says fail (false positive), our validator is missing a check. If local says fail but competition says pass (false negative), our check is too strict or wrong.
3. **Update `SandboxValidator.cs`** to match — add missing checks, fix wrong checks, adjust point weights.
4. **Update `knowledge.md`** with what the competition actually checks for that task type.

### Known Competition Check Mapping (update as you learn more)

| Task type               | Competition checks (as observed)                                                                            | Notes                                                              |
| ----------------------- | ----------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------ |
| `create_employee`       | employee_found, firstName, lastName, email, admin_role                                                      | admin_role worth ~50% of total                                     |
| `create_customer`       | customer_found, name, email, organizationNumber, addr.addressLine1, addr.postalCode, addr.city, phoneNumber | Competition checks individual address fields, NOT just has_address |
| `create_supplier`       | supplier_found, name, email, organizationNumber, phoneNumber                                                | phoneNumber check present even when not in prompt                  |
| `create_product`        | product_found, name, number, price                                                                          |                                                                    |
| `create_department`     | department_found, name, departmentNumber                                                                    |                                                                    |
| `create_project`        | project_found, name, has_customer, has_project_manager                                                      |                                                                    |
| `create_invoice`        | invoice_found, has_customer, has_amount, correct_amount                                                     | Amount = invoice total incl. VAT at correct rate                   |
| `register_payment`      | invoice_found, payment_registered (amountOutstanding = 0)                                                   |                                                                    |
| `run_payroll`           | salary_transaction_found, has_employee_link, payslip_generated, correct_amount                              | All 4 fail = transaction not persisted or wrong structure          |
| `create_travel_expense` | travel_expense_found, has_title, has_employee, has_costs                                                    |                                                                    |
| `create_credit_note`    | credit_note_created                                                                                         |                                                                    |
| `create_voucher`        | voucher_found, has_description, has_postings (≥ 2)                                                          |                                                                    |

### Golden Rule

**A local score of 100% means nothing if competition disagrees.** When competition fails a check we pass locally, stop and fix the validator BEFORE fixing the handler. The validator is the ground truth for local development.

Start-Agent.ps1 — Kill + restart agent (supports -Background)
Start-Tunnel.ps1 — Start ngrok HTTPS tunnel
Start-Cloudflared.ps1 — Start cloudflare quick tunnel (supports -Kill)
Test-Solve.ps1 — Send test prompt to agent, tail logs
Submit-Run.ps1 — Full submission flow: auto-start, submit, poll, replay, refresh tasks
Analyze-Run.ps1 — Analyze a run: prompts, extraction, API calls, checks (see Logging & Diagnostics)
Refresh-Tasks.ps1 — Regenerate task docs (prompts.md, runs.md, history.md) + PRIORITY_EXECUTION_ORDER.md
src/
Program.cs — Minimal API setup, /solve endpoint, ping fast-path
Models/
SolveRequest.cs — Request DTOs
ExtractionResult.cs — LLM extraction output model
Services/
TripletexApiClient.cs — HTTP client wrapper (auth, logging, errors)
LlmExtractor.cs — GPT-4o structured extraction (3-retry + regex fallback)
TaskRouter.cs — Routes task_type to handler
ReferenceDataService.cs — Lazy-loaded VAT types, payment types, accounts
Handlers/
ITaskHandler.cs — Handler interface
EmployeeHandler.cs — Create/update employee + role assignment
CustomerHandler.cs — Create customer (with address parsing)
SupplierHandler.cs — Create supplier
ProductHandler.cs — Create product
DepartmentHandler.cs — Create department (with collision retry)
ProjectHandler.cs — Create project (customer + PM resolution)
InvoiceHandler.cs — Full invoice chain (customer → order → invoice → send)
PaymentHandler.cs — Register payment (optimized: 7 calls)
CreditNoteHandler.cs — Create credit note on existing invoice
ContactHandler.cs — Create contact person for customer
TravelExpenseHandler.cs — Create travel expense with cost lines
FallbackAgentHandler.cs — LLM tool-use loop for unmapped tasks
... — One handler per task type
logs/
sandbox.jsonl — Sandbox test submission logs
submissions.jsonl — Competition submission logs
agent-\*.log — Serilog application logs

```

```
