# Task 26 — Unknown Task

## Overview

| Field | Value |
|---|---|
| **Task ID** | 26 |
| **Task Type** | ??? |
| **Variant** | Unknown |
| **Tier** | 3 (presumed) |
| **Our Score** | 0.00 |
| **Leader Score** | 3.75 |
| **Gap** | -3.75 |
| **Status** | ❓ Unknown — never identified |
| **Handler** | None (FallbackAgentHandler) |
| **Priority** | #12 — UNKNOWN effort |

## What It Does

We have never identified what Task 26 asks for. It has 0 attempts in our leaderboard, meaning either:
1. We've never received this task (unlikely — all 30 tasks should be sent per competition run)
2. The request came but was handled by FallbackAgentHandler and scored 0
3. The task ID mapping is wrong for this slot

## Investigation Steps

1. **Check `submissions.jsonl`** for task_index 26 in any competition run
2. **Check `results.jsonl`** for task ID 26 competition results — may have prompt text
3. **Check the competition dashboard** for task 26 description
4. Look at FallbackAgentHandler logs for any unrecognized task types

## Possible Task Types (speculation)

Given the competition covers 30 tasks and we've identified 29, the missing one could be:
- `create_contact` — create a contact person for a customer (ContactHandler exists)
- `delete_entity` — delete an entity (DeleteEntityHandler exists)
- `enable_module` — enable a Tripletex module (EnableModuleHandler exists)
- `update_customer` / `update_supplier` — update an existing entity
- Something entirely new (e.g., currency exchange, budget, recurring invoice)

## How to Fix

1. **Identify the task** — must check logs first
2. **If handler already exists** (contact, delete, enable_module) — just needs correct routing
3. **If new task type** — implement a handler

## Effort

**UNKNOWN** — depends entirely on what the task is.

## Action Required

- [ ] Search `submissions.jsonl` for task_index 26
- [ ] Search `results.jsonl` for task_id 26
- [ ] Identify the prompt and task type
- [ ] Implement or route accordingly
