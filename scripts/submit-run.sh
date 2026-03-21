#!/usr/bin/env bash
# Submit a competition run to the NM i AI platform.
# Usage:
#   AINM_TOKEN="<token>" ./scripts/submit-run.sh
#   ./scripts/submit-run.sh --token <token>
#   ./scripts/submit-run.sh --no-wait
#   ./scripts/submit-run.sh --no-replay

set -euo pipefail

command -v python3 >/dev/null 2>&1 || { echo "ERROR: python3 is required but not found."; exit 1; }
command -v curl >/dev/null 2>&1 || { echo "ERROR: curl is required but not found."; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SRC_DIR="$SCRIPT_DIR/../src"

TASK_ID="cccccccc-cccc-cccc-cccc-cccccccccccc"
API_BASE="https://api.ainm.no"
LEADERBOARD_URL="$API_BASE/tripletex/leaderboard/996fca4f-53fc-4585-bc65-b7a632fe7478"

TOKEN=""
NO_WAIT=false
NO_REPLAY=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --token|-t)     TOKEN="$2"; shift 2 ;;
        --no-wait)      NO_WAIT=true; shift ;;
        --no-replay)    NO_REPLAY=true; shift ;;
        *)              echo "Unknown option: $1"; exit 1 ;;
    esac
done

# --- Resolve auth token ---
if [[ -z "$TOKEN" ]]; then
    TOKEN="${AINM_TOKEN:-}"
fi
if [[ -z "$TOKEN" ]]; then
    SECRETS_OUTPUT=$(dotnet user-secrets list --project "$SRC_DIR" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>/dev/null || true)
    if [[ -n "$SECRETS_OUTPUT" ]]; then
        TOKEN=$(echo "$SECRETS_OUTPUT" | grep -E '^AinmToken\s*=' | sed 's/^.*=\s*//' | xargs)
    fi
fi
if [[ -z "$TOKEN" ]]; then
    echo -e "\033[31mNo auth token provided. Set AINM_TOKEN, pass --token, or configure AinmToken in user-secrets.\033[0m"
    echo -e "\033[90m  dotnet user-secrets set AinmToken '<value>' --project src --id 54b40cce-1f78-4e18-ab1b-c1501ef7f7da\033[0m"
    exit 1
fi

# --- Check agent is running (auto-start if not) ---
if ! pgrep -f "TripletexAgent" > /dev/null 2>&1; then
    echo -e "\033[33mTripletexAgent not running — starting...\033[0m"
    "$SCRIPT_DIR/start-agent.sh" --background
    sleep 2
    if ! pgrep -f "TripletexAgent" > /dev/null 2>&1; then
        echo -e "\033[31mERROR: Failed to start TripletexAgent.\033[0m"
        exit 1
    fi
fi
AGENT_PID=$(pgrep -f "TripletexAgent" | head -1)
echo -e "\033[32mAgent running (PID $AGENT_PID)\033[0m"

# --- Get tunnel URL (try cloudflared first, then ngrok) ---
TUNNEL_URL=""

# Try cloudflared log
CF_LOG="$SRC_DIR/logs/cloudflared.log"
if [[ -f "$CF_LOG" ]] && pgrep -f "cloudflared" > /dev/null 2>&1; then
    TUNNEL_URL=$(grep -oE 'https://[a-z0-9-]+\.trycloudflare\.com' "$CF_LOG" 2>/dev/null | head -1 || true)
    if [[ -n "$TUNNEL_URL" ]]; then
        echo -e "\033[32mUsing cloudflared tunnel\033[0m"
    fi
fi

# Try localtunnel log
if [[ -z "$TUNNEL_URL" ]]; then
    LT_LOG="$SRC_DIR/logs/localtunnel.log"
    if [[ -f "$LT_LOG" ]]; then
        TUNNEL_URL=$(grep -oE 'https://[a-z0-9-]+\.loca\.lt' "$LT_LOG" 2>/dev/null | head -1 || true)
        if [[ -n "$TUNNEL_URL" ]]; then
            echo -e "\033[32mUsing localtunnel\033[0m"
        fi
    fi
fi

# Try ngrok
if [[ -z "$TUNNEL_URL" ]]; then
    TUNNEL_URL=$(curl -s http://localhost:4040/api/tunnels 2>/dev/null \
        | python3 -c "
import sys, json
try:
    data = json.load(sys.stdin)
    for t in data.get('tunnels', []):
        if t.get('proto') == 'https':
            print(t['public_url'])
            break
except: pass
" 2>/dev/null || true)
    if [[ -n "$TUNNEL_URL" ]]; then
        echo -e "\033[32mUsing ngrok tunnel\033[0m"
    fi
fi

# Auto-start cloudflared if no tunnel found
if [[ -z "$TUNNEL_URL" ]]; then
    echo -e "\033[33mNo tunnel found — starting cloudflared...\033[0m"
    "$SCRIPT_DIR/start-cloudflared.sh"
    sleep 2
    if [[ -f "$CF_LOG" ]]; then
        TUNNEL_URL=$(grep -oE 'https://[a-z0-9-]+\.trycloudflare\.com' "$CF_LOG" 2>/dev/null | head -1 || true)
        if [[ -n "$TUNNEL_URL" ]]; then
            echo -e "\033[32mUsing cloudflared tunnel\033[0m"
        fi
    fi
fi

if [[ -z "$TUNNEL_URL" ]]; then
    echo -e "\033[31mERROR: Could not start tunnel. Try manually:\033[0m"
    echo -e "\033[90m  ./scripts/start-cloudflared.sh\033[0m"
    exit 1
fi

ENDPOINT_URL="$TUNNEL_URL/solve"
echo -e "\033[36mEndpoint: $ENDPOINT_URL\033[0m"

# --- Quick health check ---
HEALTH_CODE=$(curl -s -o /dev/null -w "%{http_code}" \
    -X POST "$TUNNEL_URL/solve" \
    -H "Content-Type: application/json" \
    -d '{"prompt":"ping","files":[],"tripletex_credentials":{"base_url":"https://test","session_token":"test"}}' \
    --max-time 30 2>/dev/null || echo "000")
if [[ "$HEALTH_CODE" == "200" ]]; then
    echo -e "\033[32mHealth check: OK\033[0m"
else
    echo -e "\033[33mWARNING: Health check returned $HEALTH_CODE. Submitting anyway...\033[0m"
fi

# --- Snapshot submissions.jsonl line count for replay ---
SUBMISSIONS_FILE="$SRC_DIR/logs/submissions.jsonl"
PRE_LINE_COUNT=0
if [[ -f "$SUBMISSIONS_FILE" ]]; then
    PRE_LINE_COUNT=$(wc -l < "$SUBMISSIONS_FILE" | xargs)
fi

# --- Submit ---
echo ""
echo -e "\033[36mSubmitting to competition...\033[0m"

SUBMIT_BODY=$(python3 -c "
import json
print(json.dumps({'endpoint_url': '$ENDPOINT_URL', 'endpoint_api_key': None}))
")

SUBMIT_TMPFILE=$(mktemp)
trap 'rm -f "$SUBMIT_TMPFILE"' EXIT

SUBMIT_CODE=$(curl -s -o "$SUBMIT_TMPFILE" -w "%{http_code}" \
    -X POST "$API_BASE/tasks/$TASK_ID/submissions" \
    -H "Cookie: access_token=$TOKEN" \
    -H "Content-Type: application/json" \
    -H "Origin: https://app.ainm.no" \
    -H "Referer: https://app.ainm.no/" \
    -d "$SUBMIT_BODY" \
    --max-time 30)

SUBMIT_RESPONSE=$(cat "$SUBMIT_TMPFILE")

if [[ "$SUBMIT_CODE" == "429" ]]; then
    echo -e "\033[31mERROR: Max 3 in-flight submissions. Wait for current runs to complete.\033[0m"
    exit 1
elif [[ "$SUBMIT_CODE" -lt 200 || "$SUBMIT_CODE" -ge 300 ]]; then
    echo -e "\033[31mERROR: Submission failed ($SUBMIT_CODE): $SUBMIT_RESPONSE\033[0m"
    exit 1
fi

SUBMISSION_ID=$(echo "$SUBMIT_RESPONSE" | python3 -c "import sys,json; print(json.load(sys.stdin)['id'])" 2>/dev/null || echo "unknown")
SUB_STATUS=$(echo "$SUBMIT_RESPONSE" | python3 -c "import sys,json; print(json.load(sys.stdin).get('status','?'))" 2>/dev/null || echo "?")
DAILY_USED=$(echo "$SUBMIT_RESPONSE" | python3 -c "import sys,json; print(json.load(sys.stdin).get('daily_submissions_used','?'))" 2>/dev/null || echo "?")
DAILY_MAX=$(echo "$SUBMIT_RESPONSE" | python3 -c "import sys,json; print(json.load(sys.stdin).get('daily_submissions_max','?'))" 2>/dev/null || echo "?")

echo ""
echo -e "\033[32m========================================\033[0m"
echo -e "\033[32m Submission queued!\033[0m"
echo -e "\033[32m========================================\033[0m"
echo "  ID:     $SUBMISSION_ID"
echo "  Status: $SUB_STATUS"
echo "  Daily:  $DAILY_USED / $DAILY_MAX"
echo ""

if $NO_WAIT; then exit 0; fi

# --- Poll for completion (2 minutes) ---
echo -e "\033[90mPolling for results (2 min, Ctrl+C to stop)...\033[0m"
POLL_INTERVAL=10
MAX_POLLS=12

FINAL_STATE=""
FINAL_RESPONSE=""
for i in $(seq 1 $MAX_POLLS); do
    sleep $POLL_INTERVAL

    POLL_TMPFILE=$(mktemp)
    POLL_CODE=$(curl -s -o "$POLL_TMPFILE" -w "%{http_code}" \
        -X GET "$API_BASE/tripletex/my/submissions" \
        -H "Cookie: access_token=$TOKEN" \
        --max-time 15 2>/dev/null || echo "000")

    if [[ "$POLL_CODE" != "200" ]]; then
        NOW=$(date +%H:%M:%S)
        echo -e "  [$NOW] Poll error: HTTP $POLL_CODE"
        rm -f "$POLL_TMPFILE"
        continue
    fi

    POLL_RESPONSE=$(cat "$POLL_TMPFILE")
    rm -f "$POLL_TMPFILE"

    # Find our submission in the list
    MY_SUB=$(echo "$POLL_RESPONSE" | python3 -c "
import sys, json
data = json.load(sys.stdin)
subs = data if isinstance(data, list) else data.get('submissions', data.get('values', []))
for s in subs:
    if s.get('id') == '$SUBMISSION_ID':
        print(json.dumps(s))
        break
" 2>/dev/null || true)

    if [[ -z "$MY_SUB" ]]; then
        NOW=$(date +%H:%M:%S)
        echo -e "  [$NOW] Submission not in list yet..."
        continue
    fi

    STATE=$(echo "$MY_SUB" | python3 -c "import sys,json; print(json.load(sys.stdin).get('status','?'))" 2>/dev/null || echo "?")
    SCORE=$(echo "$MY_SUB" | python3 -c "import sys,json; s=json.load(sys.stdin); print(s.get('score',''))" 2>/dev/null || echo "")
    NOW=$(date +%H:%M:%S)

    if [[ -n "$SCORE" && "$SCORE" != "None" && "$SCORE" != "null" ]]; then
        echo -e "  [$NOW] Status: $STATE | \033[33mScore: $SCORE\033[0m"
    else
        echo "  [$NOW] Status: $STATE"
    fi

    if [[ "$STATE" == "completed" || "$STATE" == "failed" || "$STATE" == "error" ]]; then
        echo ""
        echo -e "\033[36mFinal result:\033[0m"
        echo "$MY_SUB" | python3 -m json.tool 2>/dev/null || echo "$MY_SUB"
        FINAL_STATE="$STATE"
        FINAL_RESPONSE="$MY_SUB"
        break
    fi
done

if [[ -z "$FINAL_STATE" ]]; then
    echo ""
    echo -e "\033[33mPolling timed out after 2 minutes. Check status manually.\033[0m"
fi

# --- Persist results to results.jsonl ---
if [[ -n "$FINAL_STATE" && -n "$FINAL_RESPONSE" ]]; then
    RESULTS_FILE="$SRC_DIR/logs/results.jsonl"
    mkdir -p "$(dirname "$RESULTS_FILE")"

    echo "$FINAL_RESPONSE" | python3 -c "
import sys, json
from datetime import datetime, timezone

sub = json.load(sys.stdin)
feedback = sub.get('feedback', {}) or {}
checks = feedback.get('checks', []) or []

passed = sum(1 for c in checks if ': passed' in c)
failed = len(checks) - passed

entry = {
    'submission_id': sub.get('id'),
    'timestamp': datetime.now(timezone.utc).isoformat(),
    'status': sub.get('status'),
    'score_raw': sub.get('score_raw'),
    'score_max': sub.get('score_max'),
    'normalized_score': sub.get('normalized_score'),
    'duration_ms': sub.get('duration_ms'),
    'feedback_comment': feedback.get('comment'),
    'total_checks': len(checks),
    'passed_checks': passed,
    'failed_checks': failed,
    'checks': [{'text': c, 'passed': ': passed' in c} for c in checks]
}
print(json.dumps(entry))
" >> "$RESULTS_FILE" 2>/dev/null || true

    echo ""
    echo -e "\033[32mResults saved to results.jsonl\033[0m"
fi

# --- Fetch leaderboard snapshot ---
LB_TMPFILE=$(mktemp)
LB_CODE=$(curl -s -o "$LB_TMPFILE" -w "%{http_code}" \
    -X GET "$LEADERBOARD_URL" \
    --max-time 15 2>/dev/null || echo "000")

if [[ "$LB_CODE" == "200" ]]; then
    LB_DATA=$(cat "$LB_TMPFILE")
    rm -f "$LB_TMPFILE"

    LEADERBOARD_FILE="$SRC_DIR/logs/leaderboard.jsonl"
    mkdir -p "$(dirname "$LEADERBOARD_FILE")"

    echo "$LB_DATA" | python3 -c "
import sys, json
from datetime import datetime, timezone

data = json.load(sys.stdin)
tasks = data if isinstance(data, list) else []

total = sum(t.get('best_score', 0) for t in tasks)
zero_tasks = [t.get('tx_task_id', '?') for t in tasks if t.get('best_score', 0) == 0]

entry = {
    'timestamp': datetime.now(timezone.utc).isoformat(),
    'submission_id': '$SUBMISSION_ID',
    'total_best_score': round(total, 4),
    'task_count': len(tasks),
    'tasks': tasks
}
print(json.dumps(entry))
" >> "$LEADERBOARD_FILE" 2>/dev/null || true

    # Print summary
    TOTAL_SCORE=$(echo "$LB_DATA" | python3 -c "
import sys, json
data = json.load(sys.stdin)
tasks = data if isinstance(data, list) else []
print(round(sum(t.get('best_score', 0) for t in tasks), 2))
" 2>/dev/null || echo "?")

    TASK_COUNT=$(echo "$LB_DATA" | python3 -c "
import sys, json
data = json.load(sys.stdin)
tasks = data if isinstance(data, list) else []
print(len(tasks))
" 2>/dev/null || echo "?")

    ZERO_TASKS=$(echo "$LB_DATA" | python3 -c "
import sys, json
data = json.load(sys.stdin)
tasks = data if isinstance(data, list) else []
zeros = [t.get('tx_task_id', '?') for t in tasks if t.get('best_score', 0) == 0]
print(', '.join(zeros) if zeros else '')
" 2>/dev/null || echo "")

    echo ""
    echo -e "\033[32mLeaderboard snapshot saved to leaderboard.jsonl\033[0m"
    echo -e "\033[33m  Total: $TOTAL_SCORE across $TASK_COUNT tasks\033[0m"
    if [[ -n "$ZERO_TASKS" ]]; then
        echo -e "\033[33m  Zero-score tasks: $ZERO_TASKS\033[0m"
    fi

    # Print full leaderboard
    echo ""
    echo -e "\033[36mLeaderboard (all tasks):\033[0m"
    echo "$LB_DATA" | python3 -c "
import sys, json

data = json.load(sys.stdin)
tasks = data if isinstance(data, list) else []
tasks.sort(key=lambda t: t.get('tx_task_id', ''))

for t in tasks:
    tid = t.get('tx_task_id', '?')
    score = t.get('best_score', 0)
    attempts = t.get('total_attempts', 0)
    color = '\033[90m' if score == 0 else '\033[0m'
    print(f'{color}  tx:{tid}  best:{score:>7}  attempts:{attempts}\033[0m')
" 2>/dev/null || true
else
    rm -f "$LB_TMPFILE"
    echo -e "\033[33mWARNING: Failed to fetch leaderboard (HTTP $LB_CODE)\033[0m"
fi

# --- Replay new competition requests locally ---
if $NO_REPLAY || [[ ! -f "$SUBMISSIONS_FILE" ]]; then
    exit 0
fi

CURRENT_LINE_COUNT=$(wc -l < "$SUBMISSIONS_FILE" | xargs)
NEW_COUNT=$((CURRENT_LINE_COUNT - PRE_LINE_COUNT))

if [[ $NEW_COUNT -le 0 ]]; then
    echo ""
    echo -e "\033[90mNo new entries in submissions.jsonl to replay.\033[0m"
    exit 0
fi

echo ""
echo -e "\033[36m========================================\033[0m"
echo -e "\033[36m Replaying $NEW_COUNT competition request(s) locally\033[0m"
echo -e "\033[36m========================================\033[0m"

tail -n "$NEW_COUNT" "$SUBMISSIONS_FILE" | while IFS= read -r line; do
    PROMPT=$(echo "$line" | python3 -c "
import sys, json
try:
    e = json.load(sys.stdin)
    p = e.get('prompt', '')
    if p == 'ping': sys.exit(0)
    print(p)
except: sys.exit(1)
" 2>/dev/null || true)

    if [[ -z "$PROMPT" ]]; then continue; fi

    TASK_TYPE=$(echo "$line" | python3 -c "import sys,json; print(json.load(sys.stdin).get('task_type','?'))" 2>/dev/null || echo "?")
    LANGUAGE=$(echo "$line" | python3 -c "import sys,json; print(json.load(sys.stdin).get('language','?'))" 2>/dev/null || echo "?")
    HANDLER=$(echo "$line" | python3 -c "import sys,json; print(json.load(sys.stdin).get('handler','?'))" 2>/dev/null || echo "?")
    SUCCESS=$(echo "$line" | python3 -c "import sys,json; print(json.load(sys.stdin).get('success','?'))" 2>/dev/null || echo "?")
    CALL_COUNT=$(echo "$line" | python3 -c "import sys,json; print(json.load(sys.stdin).get('call_count','?'))" 2>/dev/null || echo "?")
    ERROR_COUNT=$(echo "$line" | python3 -c "import sys,json; print(json.load(sys.stdin).get('error_count','?'))" 2>/dev/null || echo "?")
    ERROR_MSG=$(echo "$line" | python3 -c "import sys,json; print(json.load(sys.stdin).get('error','') or '')" 2>/dev/null || echo "")

    PROMPT_SHORT="${PROMPT:0:80}..."

    echo ""
    echo -e "\033[33m--- Task: $TASK_TYPE ($LANGUAGE) ---\033[0m"
    echo -e "\033[90mPrompt: $PROMPT_SHORT\033[0m"

    # Replay via test-solve.sh
    "$SCRIPT_DIR/test-solve.sh" "$PROMPT" || true

    # Summary of competition result
    echo ""
    echo -e "\033[36mCompetition result:\033[0m"
    echo "  Handler:    $HANDLER"
    echo "  Success:    $SUCCESS"
    echo "  API calls:  $CALL_COUNT"
    echo "  Errors:     $ERROR_COUNT"
    if [[ -n "$ERROR_MSG" ]]; then
        echo -e "  \033[31mError:      $ERROR_MSG\033[0m"
    fi
done
