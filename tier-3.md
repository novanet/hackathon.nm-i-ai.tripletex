# Tier 3 Discovery Strategy

With Tier 3 unlocked (×3 multiplier, up to **6 pts per task**), discovering new task types is the highest-ROI activity. We currently handle ~19/30 task types — each missing Tier 3 task is potentially worth 3× a Tier 1 task.

---

## Phase 1: Rapid Reconnaissance (3-4 submissions)

1. **Submit in quick succession** — each submission tests all 30 task types with random prompts/languages. The competition picks 1 prompt per task type, so every submission reveals what's out there.
2. **Enhance FallbackAgent logging** — temporarily add extra logging to dump the full raw prompt + extracted entities for every task that hits `FallbackAgentHandler` (task_type = `unknown`). This is your main discovery channel.
3. **After each submission**, run `Analyze-Results.ps1` and diff the leaderboard to see which _new_ task type names appear in scoring — especially ones with 0 score.

## Phase 2: Catalog & Categorize (~30 min)

4. Mine `submissions.jsonl` for all distinct `task_type` values and FallbackAgent hits.
5. Cross-reference with the competition leaderboard breakdown to find task types scoring 0 (= tasks we're not covering).
6. Build a discovery matrix: task name → example prompt → API endpoints → competition checks → estimated complexity.

## Phase 3: Expand Coverage (per new task type)

7. Add new `task_type` values to the LLM extractor enum in `src/Services/LlmExtractor.cs`.
8. Add routing in `src/Services/TaskRouter.cs`.
9. Implement **lightweight handler** targeting 0.5-0.8 correctness (don't over-engineer).
10. Submit to verify it scores >0 before moving to next task type.

---

## Likely Undiscovered Tier 3 Tasks

Based on Tripletex API capabilities and competition structure:

- **Supplier invoice** — known 0/4, `/incomingInvoice` is 403-blocked, needs creative workaround
- **OCR/document processing** — prompts with `files[]` base64 attachments
- **Update customer/supplier/product** — simple PUT operations, likely easy wins
- **Contact person** — `create_contact` is in extractor already but has no handler
- **Reminder/purring** creation
- **Bank statement import** or reconciliation variants
- **Period close / year-end** operations
- **Remittance / batch payment**

## Submission Budget Strategy

- **6-8** of 32 daily submissions for discovery
- **8-10** for verifying new handlers
- **14-18** reserved for later optimization

## Key Principles

- **FallbackAgentHandler is the safety net** — improve its system prompt with more Tripletex patterns to catch more tasks even without dedicated handlers.
- **Breadth > depth** — a handler scoring 0.5 on a new Tier 3 task (= 1.5 pts) is worth more than optimizing efficiency on a Tier 1 task (max +1.0 pt).
- Don't spend time on supplier invoice deep-dive yet — the 403 block may be unsolvable.

## Relevant Files

- `src/Services/LlmExtractor.cs` — add new task_type values to extraction enum
- `src/Services/TaskRouter.cs` — route new types to handlers
- `src/Handlers/FallbackAgentHandler.cs` — improve system prompt for better unknown-task handling
- `scripts/Submit-Run.ps1` — submission workflow
- `scripts/Analyze-Results.ps1` — post-submission analysis

## Verification Checklist

1. After each submission, compare leaderboard diff for new task type names.
2. Check `submissions.jsonl` for `FallbackAgentHandler` entries — these are undiscovered tasks.
3. For each new handler: confirm it scores >0 in the next submission before moving on.

## Decision

Start Phase 1 immediately. The first 3-4 submissions are purely diagnostic — don't fix anything between them, just collect data. Then spend time analyzing before implementing.
