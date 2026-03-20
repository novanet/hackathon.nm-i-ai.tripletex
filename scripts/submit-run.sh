#!/usr/bin/env bash
# Submit a competition run to the NM i AI platform.
# Usage:
#   AINM_TOKEN="<token>" ./scripts/submit-run.sh
#   ./scripts/submit-run.sh --token "<token>"
#   ./scripts/submit-run.sh --no-wait
#   ./scripts/submit-run.sh --no-replay

set -euo pipefail

command -v python3 >/dev/null 2>&1 || { echo "python3 is required"; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SRC_DIR="$SCRIPT_DIR/../src"

TASK_ID="cccccccc-cccc-cccc-cccc-cccccccccccc"
API_BASE="https://api.ainm.no"
LEADERBOARD_URL="$API_BASE/tripletex/leaderboard/996fca4f-53fc-4585-bc65-b7a632fe7478"

TOKEN=""
NO_WAIT=false
NO_REPLAY=false

# --- Parse arguments ---
while [[ $# -gt 0 ]]; do
    case "$1" in
        --token|-t)    TOKEN="$2"; shift 2 ;;
        --no-wait)     NO_WAIT=true; shift ;;
        --no-replay)   NO_REPLAY=true; shift ;;
        *)             echo "Unknown option: $1"; exit 1 ;;
    esac
done

# --- Resolve auth token ---
if [[ -z "$TOKEN" ]]; then
    TOKEN="${AINM_TOKEN:-}"
fi
if [[ -z "$TOKEN" ]]; then
    SECRETS_OUTPUT=$(dotnet user-secrets list --project "$SRC_DIR" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>/dev/null || true)
    if [[ -n "$SECRETS_OUTPUT" ]]; then
        TOKEN=$(echo "$SECRETS_OUTPUT" | grep -E '^AinmToken\s*=' | sed 's/^.*=\s*//' | xargs || true)
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

# Fallback to ngrok
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
    -X POST "$ENDPOINT_URL" \
    -H "Content-Type: application/json" \
    -d '{"prompt":"ping","files":[],"tripletex_credentials":{"base_url":"https://test","session_token":"test"}}' \
    --max-time 30 2>/dev/null || echo "000")

if [[ "$HEALTH_CODE" == "200" ]]; then
    echo -e "\033[32mHealth check: $HEALTH_CODE\033[0m"
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

BODY=$(python3 -c "
import json
print(json.dumps({'endpoint_url': '$ENDPOINT_URL', 'endpoint_api_key': None}))
")

SUBMIT_RESULT=$(curl -s -w "\n%{http_code}" \
    -X POST "$API_BASE/tasks/$TASK_ID/submissions" \
    -H "Cookie: access_token=$TOKEN" \
    -H "Content-Type: application/json" \
    -H "Origin: https://app.ainm.no" \
    -H "Referer: https://app.ainm.no/" \
    -d "$BODY" \
    --max-time 30)

HTTP_CODE=$(echo "$SUBMIT_RESULT" | tail -1)
RESPONSE_BODY=$(echo "$SUBMIT_RESULT" | sed '$d')

if [[ "$HTTP_CODE" == "429" ]]; then
    echo -e "\033[31mERROR: Max 3 in-flight submissions. Wait for current runs to complete.\033[0m"
    exit 1
elif [[ "$HTTP_CODE" != "200" && "$HTTP_CODE" != "201" ]]; then
    echo -e "\033[31mERROR: Submission failed ($HTTP_CODE): $RESPONSE_BODY\033[0m"
    exit 1
fi

SUBMISSION_ID=$(echo "$RESPONSE_BODY" | python3 -c "import sys,json; print(json.load(sys.stdin).get('id',''))" 2>/dev/null || true)
STATUS=$(echo "$RESPONSE_BODY" | python3 -c "import sys,json; print(json.load(sys.stdin).get('status',''))" 2>/dev/null || true)
DAILY_USED=$(echo "$RESPONSE_BODY" | python3 -c "import sys,json; print(json.load(sys.stdin).get('daily_submissions_used','?'))" 2>/dev/null || true)
DAILY_MAX=$(echo "$RESPONSE_BODY" | python3 -c "import sys,json; print(json.load(sys.stdin).get('daily_submissions_max','?'))" 2>/dev/null || true)

echo ""
echo -e "\033[32m========================================\033[0m"
echo -e "\033[32m Submission queued!\033[0m"
echo -e "\033[32m========================================\033[0m"
echo "  ID:     $SUBMISSION_ID"
echo "  Status: $STATUS"
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

    POLL_RESULT=$(curl -s \
        -H "Cookie: access_token=$TOKEN" \
        "$API_BASE/tripletex/my/submissions" \
        --max-time 15 2>/dev/null || true)

    if [[ -z "$POLL_RESULT" ]]; then
        echo "  [$(date +%H:%M:%S)] Poll error: empty response"
        continue
    fi

    MY_SUB=$(echo "$POLL_RESULT" | python3 -c "
import sys, json
try:
    data = json.load(sys.stdin)
    for s in data:
        if s.get('id') == '$SUBMISSION_ID':
            print(json.dumps(s))
            break
except: pass
" 2>/dev/null || true)

    if [[ -z "$MY_SUB" ]]; then
        echo "  [$(date +%H:%M:%S)] Submission not in list yet..."
        continue
    fi

    STATE=$(echo "$MY_SUB" | python3 -c "import sys,json; print(json.load(sys.stdin).get('status',''))" 2>/dev/null || true)
    SCORE=$(echo "$MY_SUB" | python3 -c "import sys,json; s=json.load(sys.stdin).get('score'); print(s if s is not None else '')" 2>/dev/null || true)

    if [[ -n "$SCORE" ]]; then
        echo -e "  [$(date +%H:%M:%S)] Status: $STATE | \033[33mScore: $SCORE\033[0m"
    else
        echo "  [$(date +%H:%M:%S)] Status: $STATE"
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
import json, sys
from datetime import datetime, timezone

sub = json.load(sys.stdin)
feedback = sub.get('feedback', {}) or {}
checks_raw = feedback.get('checks', []) or []
checks = []
passed = 0
failed = 0
for c in checks_raw:
    p = ': passed' in str(c)
    checks.append({'text': c, 'passed': p})
    if p: passed += 1
    else: failed += 1

entry = {
    'submission_id': sub.get('id'),
    'timestamp': datetime.now(timezone.utc).isoformat(),
    'status': sub.get('status', ''),
    'score_raw': sub.get('score_raw'),
    'score_max': sub.get('score_max'),
    'normalized_score': sub.get('normalized_score'),
    'duration_ms': sub.get('duration_ms'),
    'feedback_comment': feedback.get('comment'),
    'total_checks': passed + failed,
    'passed_checks': passed,
    'failed_checks': failed,
    'checks': checks,
}
print(json.dumps(entry))
" >> "$RESULTS_FILE" 2>/dev/null || echo -e "\033[33mWARNING: Failed to save results.\033[0m"

    echo ""
    echo -e "\033[32mResults saved to results.jsonl\033[0m"
fi

# --- Fetch leaderboard snapshot ---
LEADERBOARD_FILE="$SRC_DIR/logs/leaderboard.jsonl"
mkdir -p "$(dirname "$LEADERBOARD_FILE")"

LEADERBOARD_RAW=$(curl -s "$LEADERBOARD_URL" --max-time 15 2>/dev/null || true)
if [[ -n "$LEADERBOARD_RAW" && "$LEADERBOARD_RAW" != "null" ]]; then
    echo "$LEADERBOARD_RAW" | python3 -c "
import json, sys
from datetime import datetime, timezone

try:
    tasks = json.load(sys.stdin)
    total_score = sum(t.get('best_score', 0) for t in tasks)
    entry = {
        'timestamp': datetime.now(timezone.utc).isoformat(),
        'submission_id': '$SUBMISSION_ID',
        'total_best_score': round(total_score, 4),
        'task_count': len(tasks),
        'tasks': tasks,
    }
    print(json.dumps(entry))
except Exception as e:
    print(json.dumps({'error': str(e)}), file=sys.stderr)
" >> "$LEADERBOARD_FILE" 2>/dev/null

    TOTAL=$(echo "$LEADERBOARD_RAW" | python3 -c "
import json, sys
tasks = json.load(sys.stdin)
total = sum(t.get('best_score', 0) for t in tasks)
zeros = [t['tx_task_id'] for t in tasks if t.get('best_score', 0) == 0]
print(f'Total: {total:.2f} across {len(tasks)} tasks')
if zeros: print(f\"  Zero-score tasks: {', '.join(zeros)}\")
" 2>/dev/null || true)

    echo ""
    echo -e "\033[32mLeaderboard snapshot saved to leaderboard.jsonl\033[0m"
    if [[ -n "$TOTAL" ]]; then
        echo -e "\033[33m$TOTAL\033[0m"
    fi
else
    echo -e "\033[33mWARNING: Failed to fetch leaderboard.\033[0m"
fi

# --- Replay new competition requests locally ---
if $NO_REPLAY; then exit 0; fi
if [[ ! -f "$SUBMISSIONS_FILE" ]]; then exit 0; fi

NEW_LINES=$(tail -n +"$((PRE_LINE_COUNT + 1))" "$SUBMISSIONS_FILE" 2>/dev/null || true)
NEW_COUNT=$(echo "$NEW_LINES" | grep -c . 2>/dev/null || echo "0")

if [[ "$NEW_COUNT" -eq 0 || -z "$NEW_LINES" ]]; then
    echo ""
    echo -e "\033[90mNo new entries in submissions.jsonl to replay.\033[0m"
    exit 0
fi

echo ""
echo -e "\033[36m========================================\033[0m"
echo -e "\033[36m Replaying $NEW_COUNT competition request(s) locally\033[0m"
echo -e "\033[36m========================================\033[0m"

echo "$NEW_LINES" | while IFS= read -r line; do
    [[ -z "$line" ]] && continue

    PROMPT=$(echo "$line" | python3 -c "import sys,json; print(json.load(sys.stdin).get('prompt',''))" 2>/dev/null || true)
    TASK_TYPE=$(echo "$line" | python3 -c "import sys,json; print(json.load(sys.stdin).get('task_type',''))" 2>/dev/null || true)
    LANGUAGE=$(echo "$line" | python3 -c "import sys,json; print(json.load(sys.stdin).get('language',''))" 2>/dev/null || true)
    HANDLER=$(echo "$line" | python3 -c "import sys,json; print(json.load(sys.stdin).get('handler',''))" 2>/dev/null || true)
    SUCCESS=$(echo "$line" | python3 -c "import sys,json; print(json.load(sys.stdin).get('success',''))" 2>/dev/null || true)
    CALL_COUNT=$(echo "$line" | python3 -c "import sys,json; print(json.load(sys.stdin).get('call_count',''))" 2>/dev/null || true)
    ERROR_COUNT=$(echo "$line" | python3 -c "import sys,json; print(json.load(sys.stdin).get('error_count',''))" 2>/dev/null || true)
    ERROR_MSG=$(echo "$line" | python3 -c "import sys,json; print(json.load(sys.stdin).get('error','') or '')" 2>/dev/null || true)

    if [[ "$PROMPT" == "ping" || -z "$PROMPT" ]]; then continue; fi

    PROMPT_PREVIEW="${PROMPT:0:80}..."

    echo ""
    echo -e "\033[33m--- Task: $TASK_TYPE ($LANGUAGE) ---\033[0m"
    echo -e "\033[90mPrompt: $PROMPT_PREVIEW\033[0m"

    # Replay via test-solve.sh
    "$SCRIPT_DIR/test-solve.sh" "$PROMPT" || true

    # Summary of competition vs local
    echo ""
    echo -e "\033[36mCompetition result:\033[0m"
    echo "  Handler:    $HANDLER"
    echo "  Success:    $SUCCESS"
    echo "  API calls:  $CALL_COUNT"
    echo "  Errors:     $ERROR_COUNT"
    if [[ -n "$ERROR_MSG" ]]; then
        echo -e "  Error:      \033[31m$ERROR_MSG\033[0m"
    fi
done
