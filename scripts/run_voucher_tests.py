#!/usr/bin/env python3
"""Run all create_voucher prompts from submissions.jsonl against the local sandbox."""
import json
import urllib.request
import sys
import time

SUBMISSIONS = "src/logs/submissions.jsonl"
BASE_URL = "https://kkpqfuj-amager.tripletex.dev/v2"
SESSION_TOKEN = "eyJ0b2tlbklkIjoyMTQ3NjM4Mjg4LCJ0b2tlbiI6IjRkNWUyZmI5LWQ3NDgtNGJjNC04MDQ5LWUyODAwODhjMWM1ZCJ9"
AGENT_URL = "http://localhost:5001/solve"

# Collect create_voucher prompts
voucher_prompts = []
with open(SUBMISSIONS) as f:
    for line in f:
        obj = json.loads(line)
        if obj.get("task_type") == "create_voucher":
            voucher_prompts.append({
                "prompt": obj["prompt"],
                "files": obj.get("files", []),
                "original_success": obj.get("success"),
            })

print(f"Found {len(voucher_prompts)} create_voucher prompts\n")

results = []
for i, vp in enumerate(voucher_prompts):
    prompt = vp["prompt"]
    print(f"[{i+1}/{len(voucher_prompts)}] Sending: {prompt[:100]}...")

    body = json.dumps({
        "prompt": prompt,
        "files": vp["files"],
        "tripletex_credentials": {
            "base_url": BASE_URL,
            "session_token": SESSION_TOKEN,
        }
    }).encode()

    req = urllib.request.Request(
        AGENT_URL,
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )

    start = time.time()
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            status = resp.status
            resp_body = resp.read().decode()
    except urllib.error.HTTPError as e:
        status = e.code
        resp_body = e.read().decode()
    except Exception as e:
        status = 0
        resp_body = str(e)
    elapsed = time.time() - start

    ok = 200 <= status < 300
    marker = "✅" if ok else "❌"
    results.append({"i": i+1, "ok": ok, "status": status, "elapsed": elapsed, "prompt": prompt[:80]})
    print(f"  {marker} HTTP {status} ({elapsed:.1f}s) — orig_success={vp['original_success']}")
    print(f"  Response: {resp_body[:200]}\n")

# Summary
print("=" * 60)
print("SUMMARY")
print("=" * 60)
passed = sum(1 for r in results if r["ok"])
print(f"Passed: {passed}/{len(results)}")
for r in results:
    marker = "✅" if r["ok"] else "❌"
    print(f"  {marker} [{r['i']}] HTTP {r['status']} ({r['elapsed']:.1f}s) — {r['prompt']}")
