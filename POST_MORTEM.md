# Post-Mortem: NM i AI 2026 — Team Novanet

**Overall: 84.7 pts — 54th place out of 200+ teams (winner: 98.0)**
**Tripletex: 59.89 pts — 118th place (winner: 104.26) — this was our weakest task by far**
**Estimated per-task normalized: Tripletex ~57, Astar Island ~98, NorgesGruppen Data ~98**
**Competition window: March 19, 18:00 → March 22, 15:00 CET (69 hours)**

### Team context

- **Team size:** 2 developers (Hallstein + 1), out of a maximum of 4 allowed
- **AI tooling:** Single GitHub Copilot session (GPT-4o), no parallel agents
- **Prior experience:** First NM i AI competition; strong .NET/C# background, no prior Tripletex API experience
- **Effective hours:** Estimated ~40-45 hours (accounting for sleep, breaks, meals across 69-hour window)

### The overall picture

NM i AI 2026 had **three independent tasks**, each worth 33.33% of the overall score:

| Task | Type | Our normalized | Winner's approx. |
|------|------|:--------------:|:----------------:|
| **Tripletex** | AI accounting agent (HTTPS endpoint) | ~57 | ~100 |
| **Astar Island** | Norse world prediction (REST API) | ~98 | ~100 |
| **NorgesGruppen Data** | Grocery shelf object detection (code upload) | ~98 | ~100 |

We performed strongly on both Astar Island and NorgesGruppen Data. **Tripletex was our bottleneck** — it dragged our overall score from what could have been ~90+ down to 84.7. Improving our Tripletex normalized score from ~57 to ~85 (roughly matching our other tasks) would have pushed our overall from 84.7 to ~93.7 — good enough for top 5.

This post-mortem focuses on the Tripletex task because that's where nearly all of our lost points came from. The gap to the top 10 (89.7) was ~5 overall points, equivalent to ~15 normalized points on Tripletex — about 16 more Tripletex raw points. That was achievable.

---

## 1. The Challenge

NM i AI (Norwegian Championship in AI) 2026 was a 69-hour hackathon with 200+ teams competing across **three independent AI challenges**, each worth one-third of the overall score:

| Task | Challenge | Submission format | Scoring |
|------|-----------|-------------------|---------|
| **Tripletex** | AI accounting agent — parse natural language prompts and execute Tripletex API calls | HTTPS endpoint (`POST /solve`) | Field-by-field verification + efficiency bonus |
| **Astar Island** | Observe a black-box Norse civilization simulator and predict final world state probability distributions | REST API predictions (H×W×6 tensor per seed) | Entropy-weighted KL divergence, rounds every ~3 hours |
| **NorgesGruppen Data** | Detect grocery products on store shelf images | Code upload (`.zip` with `run.py` + model weights) | 70% detection mAP + 30% classification mAP |

**Overall scoring:** Each task's scores were normalized to 0–100 by dividing by the highest score in that task across all teams. The overall score was the **average** of the three normalized task scores. Tasks where a team didn't submit scored 0 — competing in all three was strongly advantageous.

**Available resources included:**
- Free GCP account (Cloud Run, Vertex AI + Gemini models, Compute Engine with GPU, Cloud Shell + Editor)
- MCP docs server for Claude Code: `claude mcp add --transport http nmiai https://mcp-docs.ainm.no/mcp`
- Competition Slack for announcements, rule clarifications, and support
- Tripletex sandbox account (persistent, for experimentation)
- Astar Island API with 50 observation queries per round
- NorgesGruppen COCO training dataset (248 images, ~22,700 annotations, 356 product categories)

### Tripletex task details (our weakest area)

This was the task we spent most of our time on. Each team exposed a single HTTPS endpoint that received:
- A **natural language prompt** describing an accounting task (in 7 languages: Norwegian bokmål, English, Spanish, Portuguese, Nynorsk, German, French)
- Optional **file attachments** (PDFs, CSVs)
- **Tripletex API credentials** (base URL + session token)

The agent had to understand the task, execute the correct Tripletex API calls, and return `{"status": "completed"}`. The platform then queried the Tripletex API directly to verify what was created, scoring each task field-by-field.

### Scoring system

- **30 task types** across 3 tiers (Tier 1 = basic CRUD, Tier 2 = multi-step workflows, Tier 3 = complex/PDF-based)
- **Score per task** = `correctness × tier_multiplier + efficiency_bonus`
- Tier multipliers: ×1 (Tier 1), ×2 (Tier 2), ×3 (Tier 3)
- Efficiency bonus could **double** the score — but only on perfect (100% correctness) runs
- GET requests were **free** — only write calls (POST/PUT/DELETE) counted for efficiency
- Every **4xx error** on a write call permanently reduced the efficiency bonus
- Total = sum of **best scores** across all 30 task types (you kept your best ever score per task)

This meant **breadth was king**: covering 25 tasks at 1.0 each (= 25 pts) beat 10 tasks at 2.0 each (= 20 pts). And Tier 3 tasks were worth up to 6.0 pts each, making them extremely valuable.

### Task examples

| Tier | Example tasks | Max score |
|------|---------------|-----------|
| 1 | Create employee, customer, product, supplier, department | 2.0 |
| 2 | Create invoice chain, register payment, run payroll, credit note | 4.0 |
| 3 | Bank reconciliation from CSV, PDF supplier invoice, FX payment, year-end closing, ledger corrections | 6.0 |

---

## 2. Strategy — How We Attacked the Challenge

This is the core of the post-mortem. Every other section flows from the strategic decisions made in the first few hours. We focus here on what *we* did and what we'd change — not on what the winning team did (though we reference their approach where it illustrates an alternative).

### 2.0 Cross-task strategy: We competed in 3 tasks but this post-mortem is about the weakest one

Our overall score of 84.7 (54th) was strong in 2 of 3 tasks and weak in 1. The math:

| Task | Our normalized | Points contributed | If we matched our other tasks |
|------|:--------------:|:------------------:|:----------------------------:|
| Astar Island | ~98 | ~32.7 | ~32.7 (no change) |
| NorgesGruppen Data | ~98 | ~32.7 | ~32.7 (no change) |
| **Tripletex** | **~57** | **~19.0** | ~32.7 (**+13.7**) |
| **Overall** | | **84.7 (54th)** | **~98.0 (1st-3rd)** |

**Tripletex was the only thing holding us back.** If we'd achieved even 80 normalized on Tripletex (not matching anyone — just being decent), our overall would have been ~92 — top 10. The other two tasks were already near-ceiling. Every hour we spent on Tripletex that didn't produce results was doubly wasteful because the marginal value of Tripletex improvement was so high for our overall score.

We didn't realize this dynamic clearly enough during the competition. The Tripletex task consumed most of our attention and effort because it was the most code-intensive, but we should have been even more aggressive about unblocking Tripletex progress — because it was the only lever we had left.

### 2.1 Our architecture: The extraction pipeline

We built a three-stage pipeline:

```
Prompt (7 languages) → LLM Extractor (GPT-4o) → Structured JSON → Task Router → Deterministic C# Handler → Tripletex API
```

**Stage 1: LLM Extraction.** A single ~5,000-token system prompt instructed GPT-4o to parse any accounting prompt into a fixed JSON schema: `{task_type, entities, fields, relationships, action, dates, raw_amounts}`. This prompt covered all 30 task types, all 7 languages, and dozens of edge cases — from address parsing to receipt line item extraction to annual accounts depreciation.

**Stage 2: Task Router.** `TaskRouter.cs` mapped 25 task types to deterministic handlers. It also contained ~80 lines of inference logic: regex overrides in `Program.cs` for misclassified tasks, entity-based inference (has project entity → `create_project`), and special-case routing for reminder fees and cost analysis.

**Stage 3: Deterministic Handlers.** 20+ C# handler classes, each containing hard-coded API call sequences. `EmployeeHandler.cs` knew to POST to `/employee` then grant admin privileges. `InvoiceHandler.cs` knew to create Customer → Order → OrderLines → Invoice in sequence. Each handler extracted field values from the `ExtractionResult` JSON and built Tripletex API request bodies.

**Stage 4: Fallback Agent.** For unmapped task types, `FallbackAgentHandler.cs` ran a GPT-4o tool-use loop with 7 tools (`api_get`, `api_post`, `api_put`, `api_delete`, `search_help`, `search_vouchers`, `task_complete`) and a max of 12 iterations.

#### What worked about this architecture

- **Predictable for simple tasks.** Tier 1 CRUD tasks (employee, customer, product, supplier, department) worked reliably because the handlers were straightforward and the extraction was unambiguous.
- **Good observability.** Every handler logged its API calls, and the extraction JSON was logged, making diagnosis possible.
- **Efficient when correct.** A well-built handler made the minimum number of API calls with no wasted work.

#### Where it broke down

**The extraction prompt was doing too much.** Our system prompt grew to ~5,000 tokens covering:
- 25+ task type classifications with multilingual keyword examples
- Address parsing rules ("ALWAYS extract as separate fields: addressLine1, postalCode, city")
- Invoice VAT rules ("NEVER set vatIncluded unless prompt EXPLICITLY says prices include VAT")
- Receipt line item extraction ("extract ONLY that item's amount — NOT the receipt total")
- Ledger correction schemas ("MUST extract all 4 errors as separate numbered entities")
- Annual accounts with asset depreciation ("Each asset gets its OWN entity key — do NOT nest")
- Two pages of multilingual keyword lists for task classification

This was asking GPT-4o to do too many things reliably in a single pass. When it misclassified a task or extracted a field wrong, the downstream handler received bad data and the error surfaced as a Tripletex API failure — making root cause analysis slow. Was the 4xx error because the handler logic was wrong, or because the extraction was wrong?

**RegEx overrides in Program.cs were a code smell.** We ended up with ~50 lines of regex-based post-processing that patched LLM extraction failures:
- Override `create_invoice` → `register_payment` when payment keywords found
- Override `create_invoice` → `create_credit_note` when credit note keywords found
- Override anything → `create_employee` when strong employee-creation keywords detected
- Force-extract employee name, dateOfBirth, startDate, email via regex when LLM missed them

Each of these regexes existed because the LLM extraction failed for some prompt variant. Instead of fixing the root cause (the extraction prompt was overloaded), we patched around it. This is a classic sign of a system under strain.

**The extraction-handler split created two error surfaces.** Every task had to work correctly in *both* the extraction step AND the handler step. When a task failed, we had to check: did the LLM extract the right task_type? Did it extract all fields? Did the handler use those fields correctly? Did the handler build the right API body? This doubled the debugging effort.

### 2.2 Our LLM instructions: What should have been different

The extraction system prompt is where most of the "intelligence" of our system lived, and it had several structural problems:

**Problem 1: One prompt to rule them all.** All 30 task types lived in one system prompt. Every invocation sent ~5,000 tokens of context, even for a simple `create_employee` that needed maybe 200 tokens of instruction. This wasted context and increased the chance of the LLM confusing rules between task types.

*What we should have done:* Two-phase extraction. Phase 1: a short prompt that classifies the task type (just returns one string). Phase 2: a task-specific prompt with only the rules for that task type. This would have been ~300 tokens per invocation instead of ~5,000, and each task's rules wouldn't interfere with others.

**Problem 2: Implicit domain knowledge.** The prompt said things like "For vouchers with custom accounting dimensions, extract the dimension in a separate entity" — but didn't explain *why* or *what happens if you don't*. The LLM had no way to reason about the consequences of getting it wrong. When it made a judgment call (should this be a `create_voucher` or a `bank_reconciliation`?), it had no scoring context to prioritize correctly.

*What we should have done:* For ambiguous cases, include a brief rationale: "It is critical that X is extracted as Y because the downstream handler uses Y to call endpoint Z. Getting this wrong causes a 4xx error that permanently hurts the score."

**Problem 3: Negative rules instead of positive examples.** The prompt was full of "NEVER do X" and "do NOT use Y" — artifacts of bugs we'd encountered:
- "NEVER use a text description like 'HR expense account' — use a numeric account number"  
- "Do NOT set vatRate unless the prompt explicitly states a VAT percentage"
- "Do NOT fabricate placeholder entity names like 'Kostnadskonto 1'"

Negative rules are hard for LLMs to follow reliably. Each "NEVER" was a bug we'd fixed by adding a prohibition, rather than restructuring the prompt so the correct output was the natural one.

*What we should have done:* Provide concrete examples for each task type. "For a receipt voucher, output exactly: `{task_type: 'create_voucher', entities: {voucher: {account: '6800', amount: 349.00, ...}}}`. Note: account is always a numeric string." Examples teach better than prohibitions.

**Problem 4: No shot examples.** The prompt was entirely instructional — it described the schema and the rules, but never showed a complete input→output example. For complex tasks like `annual_accounts` or `correct_ledger`, an example extraction would have been worth 500 words of rules.

### 2.3 The fallback agent: Our safety net had gaps

The `FallbackAgentHandler` was our catch-all for tasks without deterministic handlers. It used GPT-4o in a tool-use loop with 7 tools and max 12 iterations.

**What worked:** It gave us *some* score on tasks we hadn't built handlers for, rather than 0.

**What didn't work:**

- **12-iteration cap was too low for complex tasks.** Bank reconciliation, for example, requires parsing a CSV, making 6+ GETs to find matching entities, then 5+ payment registrations, then supplier invoice POSTs, then a reconciliation POST. That's 15+ API calls — more than the 12-iteration limit.

- **The system prompt duplicated knowledge.** `FallbackAgentHandler` had its own ~60-line system prompt (`AgentSystemPrompt`) with API patterns that duplicated (and sometimes contradicted) what the extraction prompt and handlers already knew. Keeping these in sync was a maintenance burden.

- **No task-specific guidance.** The fallback agent got the same generic instructions regardless of whether it was handling a bank reconciliation or a timesheet entry. It had to figure out the API pattern from scratch every time, wasting iterations on discovery calls.

*What we should have done:* Given the fallback agent task-specific instructions. Even a few lines of guidance — "For bank reconciliation: (1) parse CSV attachment, (2) GET invoices by number, (3) POST payments for each matching row" — would have dramatically improved its success rate on complex tasks.

### 2.4 Our Copilot instructions: Mechanics over strategy

Our `copilot-instructions.md` was 250+ lines of detailed context for GitHub Copilot:
- How to build and run (`Start-Agent.ps1`)
- The C# architecture (extraction → router → handlers → fallback)
- API conventions (auth, response envelope, version field, date format)
- Testing and submission workflow
- Logging and diagnostics

This was excellent context for a **code-writing** assistant. But it was missing context for a **strategy** assistant:

- **No scoring context.** Copilot didn't know that Tier 3 tasks were worth 3× more, or that 4xx errors permanently hurt efficiency, or that GET requests were free. So when it suggested "add a safety GET to check if the entity exists first," it didn't know that was free and worth doing.

- **No task prioritization.** Copilot didn't know which tasks we were losing the most points on, or which ones were closest to passing. It treated every handler fix as equally important.

- **No process guardrails.** We never told Copilot "before implementing, estimate the points impact" or "stop and ask before refactoring shared code."  We let it dive into multi-hour refactors that didn't meaningfully move our score.

- **No parallel work plan.** We could have set up multiple Copilot sessions on different branches, each working on a different task's handler. Instead, we used one session serially — one handler at a time. With 30 tasks, this was a bottleneck.

### 2.5 Depth-first vs breadth-first: We knew better

Our `opening-strategy.md` explicitly said: breadth > depth. Cover all 30 tasks before optimizing any. A Tier 3 task scoring 50% = 3 pts. A Tier 1 task going from 80% → 100% = 0.4 pts gained.

**We didn't follow our own strategy.** When Tier 2 unlocked, we spent significant time polishing invoice VAT handling, refining employee extraction edge cases, and handling sandbox state collisions — all on tasks we already had partially working. When Tier 3 unlocked (6 pts each), we scrambled to build handlers and most scored 0 or near-0.

| Tier | Our total | Leader total | Gap |
|------|:---------:|:------------:|----:|
| Tier 1 (5 tasks, ×1) | 9.33 | 10.00 | -0.67 |
| Tier 2 (13 tasks, ×2) | 27.74 | 41.23 | -13.49 |
| Tier 3 (12 tasks, ×3) | 22.83 | 53.03 | **-30.20** |

Tier 3 was **68% of our total gap**. We under-invested in the highest-value tasks.

*What we should have done:* Enforce a strict "no polishing until all tasks have a skeleton handler" rule. Even a 5-minute fallback-agent configuration per Tier 3 task would have been better than nothing.

### 2.6 Iteration speed: The hidden tax

Our iteration cycle was:
1. Edit C# handler code
2. Run `Start-Agent.ps1` (kill process → build → start) — **2-5 minutes**
3. Test with `Test-Solve.ps1`
4. Check logs and validation

This doesn't sound bad for one iteration. But over 69 hours, we did hundreds of iterations. At 3-5 minutes per cycle, that's **5-8 hours** just waiting for builds. And each cycle had overhead beyond the build: reading through logs, finding the relevant error, understanding whether it was an extraction or handler issue.

*What we should have done:* Either (a) used a language with hot-reload (Python, Node.js), or (b) designed the architecture so the frequently-changing parts (task knowledge) lived in files that didn't require recompilation. We could have stored task instructions in JSON/YAML files loaded at runtime — still C#, but without the rebuild cycle for knowledge changes.

### 2.7 Pre-competition preparation: What we got right and wrong

**What we prepared (good):**
- `opening-strategy.md` — Scoring analysis, API mapping, all 30 task types
- `copilot-instructions.md` — Developer workflow guide
- `entity-model.md` — Entity relationships
- Scaffold project with endpoint, auth, logging

**What we didn't prepare (should have):**

1. **A competition result analyzer script.** We built `Analyze-Run.ps1` and `Refresh-Tasks.ps1` *during* the competition — useful tools, but hours of infrastructure work under time pressure.

2. **A dry-run replayer.** We had `Test-Solve.ps1` for manual testing, but we didn't systematically replay actual competition prompts. Every submission generates prompts we could reuse — we didn't capture and replay them until late.

3. **A daily review prompt.** Ready to paste into Copilot at the end of each session. Forces structured reflection on pace, ROI, and architecture friction (see Section 7).

4. **A plan for parallel work.** We could have split tasks across multiple Copilot/terminal sessions. We never planned for this.

### 2.8 The alternative: What we could have built in C#

It's worth noting that the skill-file architecture (instructions + LLM execution) could have been implemented in C# too. The language wasn't the constraint — the design pattern was.

Imagine: instead of 20+ handler classes, we had 30 markdown files in a `skills/` directory. Each file contained numbered API call instructions. A thin C# agent loop read the skill file, fed it to GPT-4o along with the prompt, and GPT-4o made API calls via two tools (`use_skill` + `api_call`). The rest of `Program.cs` — endpoint, auth, logging — stays identical.

The iteration cycle would have been: edit a text file → save → test. No rebuild. No type errors. No serialization bugs. The extraction prompt shrinks from 5,000 tokens to ~800 (just skill names). Each skill file is independently debuggable.

**We didn't need a different language or model to benefit from this pattern. We needed a different design, in our own stack.**

### 2.9 The unused Google Cloud account

Every verified team received a free GCP account with **no credit limits** — a dedicated project with full access to Cloud Run, Vertex AI, Compute Engine, Cloud Storage, and Gemini tools. We barely touched it.

**What was available:**

| Service | What it gave us | How we could have used it |
|---------|----------------|-------------------------|
| **Cloud Run** | Containerized HTTPS endpoint in `europe-north1` — same region as competition validators | Deploy the agent there instead of running locally + tunneling. Lower latency, no cloudflared DNS issues, no local CPU/memory pressure |
| **Vertex AI + Gemini** | Access to Gemini 2.0 Flash and other models, no billing limits | Use Gemini as a second LLM — either for extraction (freeing GPT-4o for execution) or as the primary model. Free to call from within GCP |
| **Compute Engine** | Full VMs, including GPU options | Offload heavy processing, run parallel agents |
| **Cloud Shell + Editor** | Free Linux VM with Python/Docker/gcloud pre-installed, VS Code in browser with Gemini Code Assist | A second development environment — one person works locally in VS Code, the other works in Cloud Shell Editor. Instant parallel dev |
| **Gemini Code Assist** | AI coding companion in Cloud Shell Editor | A second AI assistant alongside Copilot — effectively doubling our AI-assisted development bandwidth |
| **Gemini CLI** | `gemini` command in Cloud Shell terminal | Quick prompt prototyping, API exploration |
| **AI Studio** | Experiment with Gemini models directly in browser | Rapid prompt engineering — test extraction prompts interactively before embedding them in code |

**What we actually used:** Nothing. We ran everything on a local Windows machine with a cloudflared tunnel.

**The concrete cost of not using it:**

1. **Local resource pressure.** Running the .NET agent, Copilot, a browser with Tripletex/Swagger open, log tailing, and occasionally tunneling — all on one machine — meant high CPU and memory consumption. The agent sometimes competed with the IDE for resources, slowing both development and testing. Cloud Run would have moved the agent to dedicated infrastructure, freeing the local machine for development only.

2. **Tunnel fragility.** We spent meaningful time debugging cloudflared DNS issues (the `Start-Cloudflared.ps1 -FixDns` flow, the Telenor DNS workaround, eventually switching to ngrok). Cloud Run gives you a stable HTTPS URL out of the box — `https://my-agent-xxxxx-lz.a.run.app` — no tunneling required.

3. **Missed model diversity.** Vertex AI gave us free access to Gemini models. Even if GPT-4o was our primary extractor, we could have used Gemini 2.0 Flash for the fallback agent (fast, cheap, good at instruction-following) or for a second-opinion extraction pass on ambiguous prompts.

4. **Missed parallel development environment.** Cloud Shell Editor is VS Code in a browser with Gemini Code Assist built in. One team member could have worked entirely in Cloud Shell while the other worked locally — two parallel development environments with two different AI assistants, no setup required.

5. **Missed prompt prototyping.** AI Studio lets you interactively test prompts against Gemini models. We could have rapidly iterated on extraction prompt designs there — trying two-phase classification, testing example-based prompts, comparing Gemini vs GPT-4o extraction quality — without touching the codebase.

**Why we didn't use it:** We defaulted to what we knew — local .NET development, GitHub Copilot, GPT-4o via GitHub Models. The GCP account arrived and we never stopped to systematically evaluate what it offered. This is the same "keep coding instead of stepping back" pattern that showed up in our breadth-first failure and our missing daily reviews. We treated the GCP account as "nice to have" rather than asking "what would change if we moved our agent to Cloud Run and used Vertex AI?"

**The minimum viable use of GCP** would have been: deploy to Cloud Run in `europe-north1` (15 minutes of setup, one `gcloud run deploy` command). That alone would have eliminated tunnel issues, reduced local resource pressure, and given us lower latency to the validators. We didn't even do that.

### 2.10 Other missed resources from the competition docs

Beyond GCP, the competition documentation provided several resources we didn't fully leverage:

**The MCP docs server.** The organizers provided a Model Context Protocol server: `claude mcp add --transport http nmiai https://mcp-docs.ainm.no/mcp`. This would have let Claude Code (or any MCP-compatible AI tool) directly query the competition documentation — task descriptions, scoring rules, API specs, examples — without us manually copy-pasting from the docs site. Having an AI assistant with real-time access to the competition rules and task specs could have caught issues like the wrong Tripletex endpoints (§4.5) much earlier.

**The cloudflared 120-second timeout warning.** The Tripletex examples page explicitly warned: "⚠️ Cloudflare Tunnel has a hard 120-second timeout. Tasks can take up to 300 seconds, so longer tasks WILL fail. Use ngrok if your agent needs more than 2 minutes per task." We spent time debugging cloudflared issues before switching to ngrok. Had we read the examples page more carefully, we'd have started with ngrok.

**Verified team rate limits.** Verified teams got significantly higher rate limits: 3 concurrent Tripletex submissions (vs 1 unverified), 10 per task per day (vs 3). We were Vipps-verified (🇳🇴 badge visible on leaderboard), so we had the higher limits — but understanding these limits earlier would have informed our submission strategy.

**Competition Slack.** The organizers ran an active Slack workspace for announcements, rule clarifications, and support. We don't know how much value other teams extracted from monitoring Slack for hints, clarifications about scoring edge cases, or API behavior announcements.

**The Tripletex sandbox account.** Every team got a persistent sandbox to explore the API and web interface. We used it, but we could have been more systematic — spending an hour manually creating entities in the Tripletex web UI to understand the data model before writing any handler code.

---

## 3. What We Did Well

It's easy to focus on the gap to the winner and miss the things that worked. These strengths are worth carrying forward.

### 3.1 Strong foundation on basic tasks (Tier 1)

We achieved parity with the leader on 11 out of 30 tasks, and led on 1 (travel expense). Our basic CRUD handlers — employee, customer, product, supplier — were solid and scored full marks. This means our core pipeline (request handling → LLM extraction → handler execution → Tripletex API) worked reliably for straightforward tasks.

### 3.2 Comprehensive documentation and knowledge management

Our `knowledge.md` file (200+ entries) was genuinely impressive — a living encyclopedia of API quirks, extraction pitfalls, and scoring insights discovered during the competition. Entries like "Employee `startDate` is NOT a field on `/employee`" and "Account 1500 is a system control account" saved multiple hours of debugging.

The pre-competition `opening-strategy.md` laid out scoring analysis, API mapping, and all 30 task types. This document alone represented significant domain understanding that many teams likely didn't have. The tragedy is that this knowledge was documentation *about* the system rather than documentation that *was* the system — had we used a skill-file architecture, this would have been directly executable.

### 3.3 Structured validation feedback loop

We built a local `SandboxValidator.cs` that mirrored competition checks, letting us test locally before burning submission quota. Combined with the scripts (`Start-Agent.ps1`, `Test-Solve.ps1`, `Submit-Run.ps1`, `Refresh-Tasks.ps1`, `Analyze-Run.ps1`), this created a tight dev loop that many teams probably lacked. Building this infrastructure during the competition cost time, but it paid for itself in avoided wasted submissions.

### 3.4 Deep understanding of scoring mechanics

We correctly identified early that breadth > depth, that GET requests are free, and that 4xx errors permanently hurt efficiency. Our `PRIORITY_EXECUTION_ORDER.md` tracked exact gaps vs the leader and computed the optimal work order by recoverable points per estimated hour. We had the right analytical framework — the failure was in executing the plan, not in understanding the game.

### 3.5 Travel expense handler

Our `TravelExpenseHandler.cs` was our only leading task (+0.1 over the winner), showing that our deterministic approach could be competitive when a handler was well-understood and well-built. This proves the architecture wasn't inherently doomed — it was just too slow to iterate across 30 tasks in the available time.

### 3.6 Tooling and developer experience

The development tooling we built was genuinely good:
- `Start-Agent.ps1` handled the full kill → build → start cycle cleanly
- `Test-Solve.ps1` automated local testing with credential management
- `Submit-Run.ps1` automated the entire submission flow including result polling and local replay
- `Refresh-Tasks.ps1` auto-generated task docs from run data
- `Analyze-Run.ps1` provided detailed diagnostics per submission

This infrastructure let us diagnose problems quickly — we just couldn't fix them fast enough given the C# rebuild cycle.

---

## 4. What We Missed — Our Specific Technical Failures

These are the concrete problems in *our* code that cost us points.

### 4.1 The monolithic extraction prompt grew like a weed

Our `LlmExtractor.cs` system prompt started small and grew to ~5,000 tokens over 69 hours. Here's how it happened:

**Day 1:** Simple extraction rules — task types, entity schema, a few language notes. ~1,000 tokens. Worked fine.

**Day 2:** Invoice VAT handling broke. Added: "NEVER set vatIncluded unless prompt EXPLICITLY says prices include VAT." Customer address parsing broke. Added: "ALWAYS extract as separate fields: addressLine1, postalCode, city." Employee admin role missed. Added: "For employee tasks, ALWAYS set admin=true." Receipt amounts wrong. Added entire section on receipt line item extraction with multilingual receipt keyword regex. ~3,000 tokens.

**Day 3:** Annual accounts broke. Added: "Each asset gets its OWN entity key." Ledger corrections broke. Added: "MUST extract all 4 errors as separate numbered entities." Cost analysis fabricated entities. Added: "Do NOT fabricate placeholder entity names like 'Kostnadskonto 1'." Travel expense amounts wrong. Added: multilingual `totalWithoutVAT` extraction rules. ~5,000 tokens.

Each addition was a reasonable fix for one bug. But the cumulative effect was a prompt that was trying to do everything and becoming less reliable at each individual thing. We were adding rules to fix symptoms instead of fixing the design.

**Concrete example of prompt bloat:**

```
For "register_supplier_invoice" / "pdf_supplier_invoice" / "import_supplier_invoice":
  - Extract vendor/supplier name from the file or prompt.
  - Extract total amount. 
  - Extract invoice number and invoice date.
  - Extract individual line items if present (description, amount, vatCode, account).
  - Set entities.supplier.name to the vendor name.
  - IMPORTANT: If the prompt says to book/register a purchase/supplier invoice, use task_type "register_supplier_invoice". 
  - If files are attached, set files_needed=true.
```

That's 7 rules for one task type, embedded in a prompt that has similar blocks for 24 other task types. Each rule interfered with the LLM's ability to focus.

### 4.2 RegEx overrides: Patching extraction failures at the wrong layer

`Program.cs` contained ~80 lines of post-extraction regex overrides. Here are the actual patterns from our code:

```csharp
// Override create_invoice → register_payment when payment keywords found
if (extracted.TaskType == "create_invoice" &&
    Regex.IsMatch(promptLower, @"\b(innbetaling|betaling|payment|pago|pagamento|zahlung|paiement)\b"))
    extracted.TaskType = "register_payment";

// Override anything → create_employee when employee keywords found
if (extracted.TaskType is "unknown" or "update_employee" &&
    Regex.IsMatch(promptLower, @"\b(ansatt|tilsett|employee|empleado|...)\b"))
    extracted.TaskType = "create_employee";

// Force-extract employee name when LLM missed it
if (!emp.ContainsKey("firstName") && !emp.ContainsKey("lastName"))
{
    var nameMatch = Regex.Match(request.Prompt, @"(?:navn|name|nombre|...)...");
    // ... split into firstName/lastName
}

// Force-extract dateOfBirth, startDate, email when LLM missed them
```

Every one of these regexes tells a story: the LLM extraction failed for some prompt variant, we debugged it, and instead of improving the extraction prompt, we added a regex workaround. This worked for the specific case but didn't address the underlying problem — the prompt was overloaded and unreliable.

**The cost:** Each regex was ~10 lines of code, required testing, and created maintenance burden. If we'd spent the same effort improving the extraction prompt (or better yet, splitting it into task-specific prompts), the fix would have been more durable.

### 4.3 The fallback agent was underpowered for complex tasks

`FallbackAgentHandler.cs` had several concrete problems:

**12-iteration limit too low.** Bank reconciliation requires: parse CSV → GET invoices (6×) → POST payments (5×) → POST supplier invoices (3×) → POST reconciliation. That's 15+ API calls in a single sequence. Our fallback agent gave up after 12 iterations regardless of progress.

**Generic system prompt.** The fallback agent's `AgentSystemPrompt` gave the same generic API guidance for every task type:
```
COMMON PATTERNS:
- Create customer: POST /customer { ... }
- Create order: POST /order { ... }
- Create invoice: POST /order/{id}/invoice
```

This was useless for bank reconciliation or year-end closing, where the task required specific accounting knowledge that isn't in the API docs.

**No task-specific context injection.** We never passed task-specific hints to the fallback agent. A simple switch statement giving bank reconciliation a few lines of guidance — "parse CSV, match invoice numbers, register payments" — would have dramatically improved success.

### 4.4 Handler dependency chains: Correct but slow to build

Our handlers correctly implemented dependency chains (e.g., `InvoiceHandler` creates Customer → Order → OrderLines → Invoice → Send in sequence). But each handler was a bespoke C# class averaging ~150 lines, requiring:

1. Read the `ExtractionResult` and map fields to API body keys
2. Handle nullable fields and default values
3. Parse the Tripletex response envelope  
4. Extract IDs for dependent calls
5. Handle version fields for PUT updates
6. Log each API call

For 25+ handlers, this was ~3,750 lines of C# code doing the same structural pattern with different field names and endpoints. The boilerplate was the same; only the domain knowledge differed. This was a signal that the knowledge should have been in data, not code.

### 4.5 Supplier invoices: Wrong endpoint entirely

Tasks 11 and 20 were our second-largest gap (-8.2 pts combined). We used generic voucher creation (`POST /ledger/voucher`) instead of the dedicated `POST /supplierInvoice` endpoint. The supplier invoice endpoint handles posting generation automatically; the voucher endpoint requires you to build the double-entry postings manually. We got the postings wrong and scored near-zero.

This was a case where reading the API docs more carefully — or having a skill file that documented the correct endpoint — would have saved 8+ points.

### 4.6 Payroll: Over-engineered the solution

For payroll (Task 12), we built an elaborate handler using `/salary/transaction` with employment linking, division resolution, municipality lookups, and payslip generation — 6+ API calls with multiple failure modes.

The simpler approach: post payroll as a manual ledger voucher on accounts 5000 (salary expense), 2600 (tax withholding), 2930 (net salary payable). Three API calls. Three debit/credit entries. Done.

We over-engineered because we assumed the "correct" approach was to use the dedicated salary API. But the competition checks only verified that the right accounting entries existed — not *how* they were created. We optimized for API purity when we should have optimized for points.

### 4.7 Year-end and month-end closing: Got the math wrong

Year-end closing (Task 30) and month-end closing (Task 26) required precise accounting calculations: depreciation, prepaid expense amortization, tax provisions. Our handlers attempted these but got formulas wrong:

- Depreciation: `annual_depreciation = acquisition_cost / useful_life_years` — we sometimes used wrong periods
- Tax provision: `tax = max(0, (Revenue - Expenses - Adjustments) × 0.22)` — we sometimes included wrong accounts
- Prepaid amortization: periods were off by one

These weren't code bugs — they were domain knowledge gaps. The formulas were simple; we just didn't verify them against accounting standards before implementing.

### 4.8 No skip-maxed optimization

We never implemented automatic skipping for tasks that were already at maximum score. Since each submission tested a single random task type (weighted toward less-attempted types), we couldn't control which task we'd get. But we could have returned `{"status": "completed"}` faster for maxed tasks — and more importantly, we could have tracked which task types were maxed and focused our development time accordingly.

### 4.9 PDF extraction was brittle

For PDF-attached tasks (receipt expense, PDF supplier invoice), we used PdfPig to extract text. The extraction worked for some layouts but failed for others — amounts were missed, line items were garbled, or the wrong total was extracted. Our `NormalizeFileBasedVoucherAmounts` method in `LlmExtractor.cs` had ~60 lines of complex logic trying to reconcile extracted amounts:

```
If receipt has labeled "Totalt"/"Total" line → use that
Else if single amount → use it
Else if multiple amounts → use the largest
```

This heuristic approach was fragile. A better approach would have been to pass the raw PDF text to GPT-4o and let it extract the structured data, rather than doing regex-based extraction in C# and hoping the heuristics covered all layouts.

### 4.10 Things we didn't understand the importance of

Several competition mechanics and resources were available to us but either misunderstood or never investigated. Each one would have changed how we operated.

**1. Each submission = 1 task, not 30.** Our internal documentation (`copilot-instructions.md`) stated: "All 30 tasks in a competition run share the same session_token, so they share the same `run_id`." This was wrong. The competition docs clearly stated: "Each submission receives one task, weighted toward tasks you've attempted less. Over many submissions, you'll encounter all task types." Every entry in our `results.jsonl` confirms `task_count: 1`. This misconception shaped our tooling (the entire `run_id` / `task_index` grouping concept was built around the 30-per-run assumption) and may have made us overly cautious about submission frequency early on. Over 3 days we did 409 submissions (122 Friday, 193 Saturday, 94 Sunday), averaging ~13.6 per task type — enough for coverage, but quicker early submissions would have given us faster feedback on all task types.

**2. Rate limits were much higher than we documented.** We had three contradictory numbers in our own docs:
- `opening-strategy.md`: "5 per task per day, 3 concurrent"
- `copilot-instructions.md`: "Max 32 submissions/day, max 3 concurrent"
- Actual competition docs: "10 per task per day (verified), 3 per task per day (unverified), 3 concurrent (verified)"

For a verified team with 30 task types, the real limit was up to **300 submissions per day**. We did 122-193/day so we weren't badly constrained in practice, but the perceived scarcity ("rate limits are tight — don't waste submissions on broken code") may have made us too cautious about submitting experimental handlers for diagnostic purposes. Submitting more aggressively early would have given us data faster.

**3. Efficiency benchmarks were dynamic — recalculated every 12 hours.** The scoring docs stated: "Efficiency benchmarks are recalculated periodically. As teams find more efficient solutions, the bar rises for everyone. Your best score per task is recalculated against current benchmarks every 12 hours. Normalization only affects the efficiency bonus — your correctness score never decreases." We never mentioned this in any of our strategy docs. The implications: (a) early efficiency optimization was unstable since benchmarks keep moving, (b) our "correctness first, efficiency last" strategy was even more correct than we realized, and (c) efficiency scores could DROP without us doing anything wrong — which may have confused us when reviewing scores.

**4. Tier 3 opened Saturday morning — a compressed 33-hour window for our highest-value tasks.** The scoring docs stated tiers were released progressively: "Tier 3 — opens early Saturday." The competition ran Thursday 18:00 → Sunday 15:00, meaning Tier 3 tasks (worth up to 6.0 pts each, 68% of our gap) were only available for ~33 hours. We should have had Tier 3 handler skeletons ready to submit the moment it opened. Instead, we were still polishing Tier 1/2 handlers on Saturday. Every hour of Saturday spent on a Tier 1 fix (+0.3 pts) instead of a Tier 3 skeleton (+2-3 pts) was a strategic error amplified by this timing constraint.

**5. We could log into competition Tripletex accounts in the browser.** The sandbox docs stated: "Once you've set up Visma Connect, the same credentials work for all Tripletex test accounts — including the ones created during competition submissions." This means after a failed submission, we could have opened the Tripletex web UI for that submission's sandbox and **manually inspected** exactly what our agent created — or failed to create. This would have been an incredibly powerful debugging tool for complex tasks like bank reconciliation, supplier invoices, and year-end closing. We never used it.

**6. "Verify your work" with free GET calls.** The competition examples page recommended: "Verify your work — After creating entities, query back to confirm they exist with correct values." Since GET requests were explicitly free (not counted for efficiency scoring), adding verification GETs at the end of each handler would have caught data issues at the cost of only a few seconds of the 5-minute timeout. We could have caught things like addresses not saving correctly, VAT types resolving to 0%, or employee roles not being assigned — all issues that cost us points and took multiple submission cycles to diagnose.

---

## 5. Score Breakdown

### Cross-task context

Our overall score (84.7, 54th) was dragged down almost entirely by Tripletex. Our ~98 normalized on both Astar Island and NorgesGruppen Data means the ONLY way to improve our overall score was to improve Tripletex. Every Tripletex raw point gained was worth ~0.32 normalized points, and every 3 normalized points moved the overall by 1 point.

### Tripletex by tier

| Tier | Tasks | Our total | Leader total | Gap | % of gap |
|------|:-----:|:---------:|:------------:|----:|:--------:|
| Tier 1 (basic CRUD) | 01-05 | 9.33 | 10.00 | -0.67 | 2% |
| Tier 2 (multi-step) | 06-18 | 27.74 | 41.23 | -13.49 | 30% |
| Tier 3 (advanced/PDF) | 19-30 | 22.83 | 53.03 | -30.20 | 68% |
| **Total** | **30** | **59.89** | **104.26** | **-44.37** | **100%** |

Tier 3 was 68% of our gap. These were the complex tasks worth up to 6 pts each — bank reconciliation, ledger corrections, FX payments, year-end closing — and we had fundamental issues on most of them.

### Largest individual gaps

| Task | Type | Us | Leader | Gap | Root cause |
|------|------|:--:|:------:|----:|------------|
| T23 | Bank reconciliation (CSV) | 0 | 6.0 | -6.0 | Handler completely broken |
| T20 | PDF supplier invoice | 0.6 | 4.8 | -4.2 | Wrong endpoint/structure |
| T30 | Year-end closing | 1.8 | 6.0 | -4.2 | Missing/wrong vouchers |
| T11 | Supplier invoice | 0 | 4.0 | -4.0 | Used generic voucher, not `/supplierInvoice` |
| T27 | FX/EUR payment | 2.1 | 6.0 | -3.9 | Exchange rate calculation errors |
| T24 | Ledger correction | 2.25 | 6.0 | -3.75 | VAT-aware correction logic missing |
| T12 | Payroll | 1.0 | 4.0 | -3.0 | Over-engineered with salary/transaction API |
| T22 | PDF receipt expense | 0 | 2.7 | -2.7 | PDF extraction + travel expense flow broken |
| T26 | Monthly close | 3.6 | 6.0 | -2.4 | Calculation errors in accruals |

These 9 tasks account for 34.15 of the 44.37 point gap (77%).

---

## 6. Our Potential

How much of the gap was closable given our constraints? Since Astar Island and NorgesGruppen Data were near-ceiling, the entire leverage was in Tripletex:

| Scenario | Tripletex raw | Tripletex norm | Overall | Placement |
|----------|:------------:|:--------------:|:-------:|:---------:|
| Actual final | 59.89 | ~57 | 84.7 | 54th |
| Fix top 3 gaps (T23, T20, T30) | ~74 | ~71 | ~89 | Top 10-15 |
| Fix top 5 gaps (add T11, T27) | ~82 | ~79 | ~91.5 | Top 5-8 |
| Fix all 9 major gaps to ~60% | ~85 | ~82 | ~92.5 | Top 5 |
| Match Tripletex leader (104.26) | 104.26 | 100 | ~99 | Top 1-2 |

The honest assessment: breaking into the **top 10 overall** (~89.7 pts) required about +16 raw Tripletex points — fixing 3-4 major broken handlers. This was very achievable with better time allocation and the right endpoint choices. The gap wasn't about capability — it was about iteration speed and strategic focus on the wrong things within Tripletex.

**The knowledge was there** — our `knowledge.md`, `opening-strategy.md`, and `entity-model.md` contained most of the insights needed. The issue wasn't understanding the domain — it was translating that understanding into working API calls reliably, and iterating fast enough to cover all 30 task types.

---

## 7. The Missing Practice: In-Competition Post-Mortems

### Would daily post-mortems during the competition have helped?

**Yes — significantly.** A structured review at the end of each day (or each phase) would have surfaced our biggest strategic mistake by the end of Day 1.

Consider the timeline:
- **Day 1 (Thu evening → Fri morning):** Tier 1 tasks unlocked. We got basic CRUD working. Everything felt fine.
- **Day 2 (Fri → Sat morning):** Tier 2 tasks unlocked. Invoice chains, payment flows, payroll. We started hitting API quirks and spending hours debugging C# handler code. Red flag: iteration cycles were slow, and we were fixing serialization bugs instead of learning the API.
- **Day 3 (Sat → Sun afternoon):** Tier 3 tasks unlocked — the highest-value tasks (6 pts each). We scrambled to build handlers for bank reconciliation, year-end closing, FX payments, and PDF processing. Most of these scored 0 or near-0. By the time we realized the approach wasn't scaling, there weren't enough hours left.

A Day 1 post-mortem asking "Is our architecture letting us move fast enough?" would have forced an honest conversation about iteration speed. A Day 2 post-mortem asking "Are we on track to cover all 30 tasks?" would have revealed that we were depth-first on Tier 2 while Tier 3 (worth 3× more) was approaching.

### What should the prompt look like?

This prompt should be **prepared before the competition starts** — ready to paste into your AI assistant at the end of each session. It forces structured reflection instead of just "keep coding."

---

**Pre-built Competition Review Prompt (paste at end of each day/session):**

```
You are a competition strategy advisor. We are in a 69-hour hackathon.
Analyze our current position and recommend course corrections.

**Current state**
- Hours elapsed: [X] / 69
- Hours remaining: [Y]
- Current score: [X] pts
- Leader score: [X] pts (if known)
- Tasks passing (>0 score): [N] / 30
- Tasks at full correctness: [N] / 30
- Tasks not yet attempted: [list]

**Today's work**
- What we worked on: [brief summary]
- What we shipped: [tasks improved/added]
- What we struggled with: [blockers, time sinks]
- How long did a typical fix take? (minutes from idea → tested → working)

**Analyze and recommend:**

1. **Pace check**: At our current velocity (tasks/hour), will we cover all 30 tasks?
   If not, what needs to change?

2. **ROI analysis**: For each unfinished task, estimate (points recoverable) vs
   (hours required). Rank by points-per-hour. What's the optimal allocation of
   remaining hours?

3. **Architecture friction**: Is our current architecture helping or hurting
   iteration speed? Are we spending more time on infrastructure/debugging than on
   domain understanding? Should we consider a different approach?

4. **Biggest blind spots**: What are we assuming that might be wrong?
   What patterns are we not seeing?

5. **Concrete plan**: Given the remaining hours, produce a prioritized task list
   with time estimates. Include "stop doing" items — what should we abandon?

Be direct. Challenge our assumptions. Time is the scarcest resource.
```

---

### Why this matters beyond competitions

The practice of structured in-progress reflection applies to any time-boxed project: hackathons, sprints, incident response. The tendency under pressure is to keep coding — to "fix forward" without stepping back to evaluate whether the approach itself is right.

For us, 30 minutes of structured review on Day 1 evening could have surfaced: "We're spending 45 minutes per handler fix, the winning path requires 30 handlers, that's 22+ hours just on fixes — and Tier 3 hasn't even started. Something needs to change." That observation alone might have prompted an architecture pivot that saved 15-20 points.

**Pre-building the review prompt** is the key insight. During the competition, you won't have the mental bandwidth to think about what to ask. Having it ready removes the friction and makes the review happen even when you're exhausted and just want to keep shipping.

---

## 8. Top 12 Things We Should Have Done Differently

**1. Split the extraction prompt into two phases: classify, then extract.**
Our monolithic ~5,000-token extraction prompt was our biggest single engineering mistake. A short classification prompt (200 tokens) followed by a task-specific extraction prompt (300 tokens) would have been more reliable, faster, and easier to debug. When extraction broke, we couldn't tell which rule was interfering with which — because all 30 task types lived in one prompt.

**2. Stored task knowledge in editable text/data files, not compiled C# handlers.**
Each handler was ~150 lines of C#, and each edit required rebuild → restart → retest (2-5 minutes). Over 69 hours with hundreds of iterations, we lost hours to build cycles. If task knowledge lived in JSON/YAML/Markdown files loaded at runtime, even within C#, iteration would have been 10× faster. The domain knowledge was the bottleneck — not the code patterns.

**3. Actually followed our own breadth-first strategy.**
Our `opening-strategy.md` explicitly said "breadth > depth." We wrote this before the competition. Then in practice, we spent hours polishing Tier 1/2 handlers (+0.4 pts each) while Tier 3 tasks (6 pts each, 68% of our gap) sat unstarted or broken. We should have enforced a hard rule: no handler gets more than 30 minutes of polish until ALL 30 tasks have a skeleton.

**4. Given the fallback agent task-specific instructions.**
The fallback agent had a generic system prompt. Adding even 3-5 lines of task-specific guidance per unmapped task type — "for bank reconciliation: parse CSV, match invoice numbers by subtracting 1000, register payments" — would have turned 0-point tasks into 2-3 point tasks with minimal effort.

**5. Used concrete examples in the extraction prompt instead of "NEVER do X" rules.**
Our prompt accumulated 15+ negative rules ("NEVER use text descriptions," "Do NOT fabricate placeholders," "Do NOT set vatRate unless..."). These were debugging-driven additions that treated symptoms. A single input→output example per task type would have taught the LLM the correct shape more reliably than pages of prohibitions.

**6. Run multiple Copilot/terminal sessions in parallel on different tasks.**
We worked serially: one handler at a time, one Copilot session, one terminal. With 30 tasks in 69 hours, this was ~2.3 hours per task — barely enough for simple ones, impossibly tight for complex ones. Even 2-3 parallel sessions would have doubled our throughput.

**7. Used the right Tripletex endpoints from the start.**
Supplier invoices (Tasks 11, 20) cost us -8.2 pts because we used generic voucher creation instead of `POST /supplierInvoice`. Payroll (Task 12) cost us -3 pts because we used the complex salary/transaction API instead of a simple ledger voucher. Both were "read the docs more carefully" problems — 15 minutes of research would have saved 11+ points.

**8. Built an automatic competition prompt replayer before the competition.**
We had `Test-Solve.ps1` for manual testing, but we didn't systematically replay actual competition prompts from previous submissions. Each submission generated a real prompt with real data that we could test against locally — and with 409 submissions over 3 days, we accumulated a rich test corpus. We only built replay capability late in the competition.

**9. Removed the regex post-processing workarounds and fixed the root cause.**
The ~80 lines of regex overrides in `Program.cs` were a clear code smell — each one meant the extraction prompt was failing for some case. Instead of adding override patches at the wrong layer, we should have invested that time in improving the prompt itself (or splitting it into per-task prompts, per item #1).

**10. Deployed to Google Cloud Run instead of running locally with tunnels.**
We had a free GCP account with no credit limits — Cloud Run, Vertex AI, Gemini models, the works. We used none of it. Deploying to Cloud Run (`gcloud run deploy` — 15 minutes of setup) would have eliminated our tunnel fragility, freed local CPU/memory for development, and given us lower latency to validators. Using Vertex AI would have given us free Gemini models as a complement to GPT-4o. Using Cloud Shell Editor would have given us a second parallel dev environment with its own AI assistant. We defaulted to what we knew instead of evaluating what was provided.

**11. Done a structured 15-minute review at the end of each day.**
We had the analytical tools (`Analyze-Run.ps1`, `PRIORITY_EXECUTION_ORDER.md`) but didn't force ourselves to stop and use them systematically. A daily review asking "Is our architecture letting us move fast enough?" on Day 1 would have caught the core problem — we were going depth-first in a breadth-first game, with an iteration cycle too slow for the task count.

**12. Used the competition's MCP docs server.**
The organizers provided `claude mcp add --transport http nmiai https://mcp-docs.ainm.no/mcp` — a Model Context Protocol server that let AI coding tools query the competition documentation directly. This could have accelerated API endpoint discovery and caught issues like using the wrong endpoint for supplier invoices. We either didn't know about it or didn't think to set it up.

---

## 9. Final Reflection

We placed **54th overall out of 200+ teams** with an overall score of 84.7. On the Tripletex-specific leaderboard, we were 118th with 59.89 pts.

The striking thing about our result is the imbalance: ~98 normalized on Astar Island and NorgesGruppen Data, but only ~57 on Tripletex. Tripletex was the single thing holding us back from the top 10. Fixing just 3-4 major broken handlers would have pushed us to ~89 overall — top 15. The gap to 1st place (98.0) was almost entirely a Tripletex gap.

**Our core failure on Tripletex was not architectural — it was process.** We had the right strategy document, the right scoring analysis, and enough domain knowledge. We failed to:
- Follow our own breadth-first plan
- Recognize when the extraction prompt was becoming a liability  
- Stop polishing passing tasks and focus on zero-scoring ones
- Set up parallel work streams
- Deploy to Cloud Run and use the GCP resources we were given
- Use the competition's MCP docs server for faster API discovery
- Read the competition docs thoroughly (the cloudflared timeout warning, the Tier 3 release schedule, the 1-task-per-submission model, the Visma Connect debugging capability — all documented, all missed or misunderstood)
- Do daily structured reviews

Beyond the process failure, there was a **comprehension failure**: we didn't fully understand the competition mechanics we were operating under. Our own documentation had three contradictory rate limit numbers. We thought each submission tested 30 tasks (it tested 1). We didn't know efficiency benchmarks moved every 12 hours. We didn't know we could log into competition Tripletex accounts for debugging. These aren't obscure details — they were in the competition docs, waiting for someone to read carefully and build strategy around them.

The extraction prompt growing from 1K to 5K tokens was the clearest symptom. Each addition was a local fix for a local bug. None of them were wrong individually. But we never stepped back and asked: "Is this prompt architecture scaling? Should we split it? Should we use examples instead of rules?" That question, asked on Day 2, could have changed the trajectory.

**The most actionable takeaway:** In any time-boxed competition or project, build in mandatory review checkpoints. Not when you feel like it — when the clock says so. 15 minutes every 8 hours to ask: "Am I working on the highest-leverage thing? Is my approach scaling? What am I avoiding?" A pre-built review prompt removes the willpower barrier and makes it mechanical.

Next time, we'll prototype the architecture in the first 2 hours and ask: "Can we go from bug report to fix to tested in under 5 minutes?" If the answer is no, the architecture needs to change — regardless of what language or tools we're comfortable with.
