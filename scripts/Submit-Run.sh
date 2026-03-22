#!/usr/bin/env bash
# Submit-Run.sh — Bash equivalent of Submit-Run.ps1
#
# Ensures the agent and tunnel are running (auto-starts if needed), submits the
# endpoint URL to the competition API, polls for completion (3 min), then replays
# new competition requests locally via Test-Solve.sh for analysis.
#
# Usage:
#   ./scripts/Submit-Run.sh                          # default: 1 run, poll, replay
#   ./scripts/Submit-Run.sh --token <TOKEN>          # explicit auth token
#   ./scripts/Submit-Run.sh --no-wait                # submit without polling
#   ./scripts/Submit-Run.sh --no-replay              # poll but skip local replay
#   ./scripts/Submit-Run.sh --runs 3                 # 3 sequential submissions
#
# Requires: curl, jq, dotnet

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_DIR="$SCRIPT_DIR/../src"
LOGS_DIR="$SRC_DIR/logs"

TASK_ID="cccccccc-cccc-cccc-cccc-cccccccccccc"
API_BASE="https://api.ainm.no"
LEADERBOARD_URL="$API_BASE/tripletex/leaderboard/996fca4f-53fc-4585-bc65-b7a632fe7478"

# --- Colors ---
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
CYAN='\033[0;36m'
MAGENTA='\033[0;35m'
GRAY='\033[0;90m'
NC='\033[0m' # No Color

# --- Defaults ---
TOKEN=""
NO_WAIT=false
NO_REPLAY=false
RUNS=1

# --- Argument parsing ---
while [[ $# -gt 0 ]]; do
  case "$1" in
    --token|-t)
      TOKEN="$2"; shift 2 ;;
    --no-wait)
      NO_WAIT=true; shift ;;
    --no-replay)
      NO_REPLAY=true; shift ;;
    --runs)
      RUNS="$2"; shift 2 ;;
    -*)
      echo -e "${RED}Unknown option: $1${NC}" >&2; exit 1 ;;
    *)
      echo -e "${RED}Unexpected argument: $1${NC}" >&2; exit 1 ;;
  esac
done

# --- Validate parameters ---
if [[ "$RUNS" -lt 1 ]]; then
  echo -e "${RED}ERROR: --runs must be >= 1.${NC}"
  exit 1
fi
if $NO_WAIT && [[ "$RUNS" -gt 1 ]]; then
  echo -e "${RED}ERROR: --no-wait cannot be used with --runs > 1 (sequential runs require polling).${NC}"
  exit 1
fi

# --- Resolve auth token ---
if [[ -z "$TOKEN" ]]; then
  TOKEN="${AINM_TOKEN:-}"
fi
if [[ -z "$TOKEN" ]]; then
  # Try user-secrets
  secrets=$(dotnet user-secrets list --project "$SRC_DIR" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>/dev/null || true)
  if [[ -n "$secrets" ]]; then
    while IFS= read -r line; do
      if [[ "$line" =~ ^AinmToken[[:space:]]*=[[:space:]]*(.+)$ ]]; then
        TOKEN="${BASH_REMATCH[1]}"
        break
      fi
    done <<< "$secrets"
  fi
fi
if [[ -z "$TOKEN" ]]; then
  echo -e "${RED}No auth token provided. Set \$AINM_TOKEN, pass --token, or configure AinmToken in user-secrets.${NC}"
  echo -e "${GRAY}  dotnet user-secrets set AinmToken '<value>' --project src --id 54b40cce-1f78-4e18-ab1b-c1501ef7f7da${NC}"
  exit 1
fi

# --- Check agent is running (auto-start if not) ---
AGENT_PID=$(pgrep -f 'TripletexAgent' 2>/dev/null | head -1 || true)
if [[ -z "$AGENT_PID" ]]; then
  echo -e "${YELLOW}TripletexAgent not running — starting...${NC}"
  bash "$SCRIPT_DIR/Start-Agent.sh" --background
  sleep 2
  AGENT_PID=$(pgrep -f 'TripletexAgent' 2>/dev/null | head -1 || true)
  if [[ -z "$AGENT_PID" ]]; then
    echo -e "${RED}ERROR: Failed to start TripletexAgent.${NC}"
    exit 1
  fi
fi
echo -e "${GREEN}Agent running (PID $AGENT_PID)${NC}"

# --- Get tunnel URL (try cloudflared first, then localtunnel, then ngrok) ---
TUNNEL_URL=""

# Try cloudflared log
CF_LOG="$LOGS_DIR/cloudflared.log"
if [[ -f "$CF_LOG" ]]; then
  CF_URL=$(grep -oE 'https://[a-z0-9-]+\.trycloudflare\.com' "$CF_LOG" | tail -1 || true)
  if [[ -n "$CF_URL" ]]; then
    CF_PROC=$(pgrep -f 'cloudflared' 2>/dev/null || true)
    if [[ -n "$CF_PROC" ]]; then
      TUNNEL_URL="$CF_URL"
      echo -e "${GREEN}Using cloudflared tunnel${NC}"
    fi
  fi
fi

# Try localtunnel log
if [[ -z "$TUNNEL_URL" ]]; then
  LT_LOG="$LOGS_DIR/localtunnel.log"
  if [[ -f "$LT_LOG" ]]; then
    LT_URL=$(grep -oE 'https://[a-z0-9-]+\.loca\.lt' "$LT_LOG" | tail -1 || true)
    if [[ -n "$LT_URL" ]]; then
      TUNNEL_URL="$LT_URL"
      echo -e "${GREEN}Using localtunnel${NC}"
    fi
  fi
fi

# Fallback to ngrok
if [[ -z "$TUNNEL_URL" ]]; then
  NGROK_RESPONSE=$(curl -s "http://localhost:4040/api/tunnels" 2>/dev/null || true)
  if [[ -n "$NGROK_RESPONSE" ]]; then
    NGROK_URL=$(echo "$NGROK_RESPONSE" | jq -r '.tunnels[] | select(.proto == "https") | .public_url' 2>/dev/null | head -1 || true)
    if [[ -n "$NGROK_URL" && "$NGROK_URL" != "null" ]]; then
      TUNNEL_URL="$NGROK_URL"
      echo -e "${GREEN}Using ngrok tunnel${NC}"
    fi
  fi
fi

# Auto-start cloudflared if nothing found
if [[ -z "$TUNNEL_URL" ]]; then
  echo -e "${YELLOW}No tunnel found — starting cloudflared...${NC}"
  bash "$SCRIPT_DIR/Start-Cloudflared.sh" 2>/dev/null || true
  sleep 2
  if [[ -f "$CF_LOG" ]]; then
    CF_URL=$(grep -oE 'https://[a-z0-9-]+\.trycloudflare\.com' "$CF_LOG" | tail -1 || true)
    if [[ -n "$CF_URL" ]]; then
      TUNNEL_URL="$CF_URL"
      echo -e "${GREEN}Using cloudflared tunnel${NC}"
    fi
  fi
fi

if [[ -z "$TUNNEL_URL" ]]; then
  echo -e "${RED}ERROR: Could not start tunnel. Try manually:${NC}"
  echo -e "${GRAY}  ./scripts/Start-Cloudflared.sh${NC}"
  exit 1
fi

ENDPOINT_URL="$TUNNEL_URL/solve"
echo -e "${CYAN}Endpoint: $ENDPOINT_URL${NC}"

if [[ "$RUNS" -gt 1 ]]; then
  echo ""
  echo -e "${MAGENTA}========================================${NC}"
  echo -e "${MAGENTA} Batch mode: $RUNS sequential runs${NC}"
  echo -e "${MAGENTA}========================================${NC}"
fi

# --- Quick health check ---
HEALTH_CODE=$(curl -s -o /dev/null -w '%{http_code}' -X POST \
  -H "Content-Type: application/json" \
  -d '{"prompt":"ping","files":[],"tripletex_credentials":{"base_url":"https://test","session_token":"test"}}' \
  --max-time 30 \
  "$TUNNEL_URL/solve" 2>/dev/null || echo "000")

if [[ "$HEALTH_CODE" == "200" ]]; then
  echo -e "${GREEN}Health check: $HEALTH_CODE${NC}"
else
  echo -e "${YELLOW}WARNING: Health check failed ($HEALTH_CODE). Submitting anyway...${NC}"
fi

# --- Batch tracking ---
declare -a BATCH_STATUSES=()
declare -a BATCH_SCORES=()

# --- Helper: get line count of a file ---
line_count() {
  if [[ -f "$1" ]]; then
    wc -l < "$1" | tr -d ' '
  else
    echo 0
  fi
}

# --- Main run loop ---
for (( RUN_NUM=1; RUN_NUM<=RUNS; RUN_NUM++ )); do

  if [[ "$RUNS" -gt 1 ]]; then
    echo ""
    echo -e "${MAGENTA}========================================${NC}"
    echo -e "${MAGENTA} Run $RUN_NUM of $RUNS${NC}"
    echo -e "${MAGENTA}========================================${NC}"
  fi

  # --- Snapshot leaderboard for diff ---
  LEADERBOARD_FILE="$LOGS_DIR/leaderboard.jsonl"

  # --- Snapshot submissions.jsonl line count for replay ---
  SUBMISSIONS_FILE="$LOGS_DIR/submissions.jsonl"
  PRE_LINE_COUNT=$(line_count "$SUBMISSIONS_FILE")

  # --- Submit ---
  echo ""
  echo -e "${CYAN}Submitting to competition...${NC}"

  SUBMIT_BODY=$(jq -n --arg url "$ENDPOINT_URL" '{endpoint_url: $url, endpoint_api_key: null}')

  SUBMIT_RESPONSE=$(curl -s -w "\n%{http_code}" -X POST \
    -H "Cookie: access_token=$TOKEN" \
    -H "Content-Type: application/json" \
    -H "Origin: https://app.ainm.no" \
    -H "Referer: https://app.ainm.no/" \
    -d "$SUBMIT_BODY" \
    "$API_BASE/tasks/$TASK_ID/submissions" 2>/dev/null) || true

  HTTP_CODE=$(echo "$SUBMIT_RESPONSE" | tail -1)
  SUBMIT_JSON=$(echo "$SUBMIT_RESPONSE" | sed '$d')

  if [[ "$HTTP_CODE" == "429" ]]; then
    echo -e "${RED}ERROR: Max 3 in-flight submissions. Wait for current runs to complete.${NC}"
    BATCH_STATUSES+=("submit_failed")
    BATCH_SCORES+=("null")
    break
  elif [[ "$HTTP_CODE" != "200" && "$HTTP_CODE" != "201" ]]; then
    echo -e "${RED}ERROR: Submission failed ($HTTP_CODE): $SUBMIT_JSON${NC}"
    BATCH_STATUSES+=("submit_failed")
    BATCH_SCORES+=("null")
    break
  fi

  SUBMISSION_ID=$(echo "$SUBMIT_JSON" | jq -r '.id // empty')
  SUB_STATUS=$(echo "$SUBMIT_JSON" | jq -r '.status // empty')
  DAILY_USED=$(echo "$SUBMIT_JSON" | jq -r '.daily_submissions_used // empty')
  DAILY_MAX=$(echo "$SUBMIT_JSON" | jq -r '.daily_submissions_max // empty')

  echo ""
  echo -e "${GREEN}========================================${NC}"
  echo -e "${GREEN} Submission queued!${NC}"
  echo -e "${GREEN}========================================${NC}"
  echo "  ID:     $SUBMISSION_ID"
  echo "  Status: $SUB_STATUS"
  echo "  Daily:  $DAILY_USED / $DAILY_MAX"
  echo ""

  if $NO_WAIT; then
    BATCH_STATUSES+=("submitted")
    BATCH_SCORES+=("null")
    continue
  fi

  # --- Poll for completion (3 minutes) ---
  echo -e "${GRAY}Polling for results (3 min, Ctrl+C to stop)...${NC}"
  POLL_INTERVAL=10
  MAX_POLLS=18

  FINAL_STATE=""
  MY_SUB_JSON=""

  for (( I=0; I<MAX_POLLS; I++ )); do
    sleep $POLL_INTERVAL

    POLL_RESPONSE=$(curl -s -X GET \
      -H "Cookie: access_token=$TOKEN" \
      "$API_BASE/tripletex/my/submissions" 2>/dev/null) || { echo -e "  [$(date +%H:%M:%S)] ${YELLOW}Poll error${NC}"; continue; }

    MY_SUB_JSON=$(echo "$POLL_RESPONSE" | jq -r --arg id "$SUBMISSION_ID" '.[] | select(.id == $id)' 2>/dev/null || true)

    if [[ -z "$MY_SUB_JSON" || "$MY_SUB_JSON" == "null" ]]; then
      echo -e "  [$(date +%H:%M:%S)] ${GRAY}Submission not in list yet...${NC}"
      continue
    fi

    STATE=$(echo "$MY_SUB_JSON" | jq -r '.status // empty')
    SCORE=$(echo "$MY_SUB_JSON" | jq -r '.score // empty')
    SCORE_STR=""
    if [[ -n "$SCORE" && "$SCORE" != "null" ]]; then
      SCORE_STR=" | Score: $SCORE"
    fi
    echo -e "  [$(date +%H:%M:%S)] Status: ${STATE}${YELLOW}${SCORE_STR}${NC}"

    if [[ "$STATE" == "completed" || "$STATE" == "failed" || "$STATE" == "error" ]]; then
      echo ""
      echo -e "${CYAN}Final result:${NC}"
      echo "$MY_SUB_JSON" | jq .
      FINAL_STATE="$STATE"
      break
    fi
  done

  if [[ -z "$FINAL_STATE" ]]; then
    echo ""
    echo -e "${YELLOW}Polling timed out after 3 minutes. Check status manually.${NC}"
    BATCH_STATUSES+=("timeout")
    BATCH_SCORES+=("null")
    if [[ "$RUNS" -gt 1 ]]; then
      echo -e "${RED}Stopping batch — cannot start next run without confirmed completion.${NC}"
      break
    fi
    continue
  fi

  # --- Persist results to results.jsonl ---
  DERIVED_RUN_ID=""
  if [[ -n "$FINAL_STATE" && -n "$MY_SUB_JSON" ]]; then
    RESULTS_FILE="$LOGS_DIR/results.jsonl"
    mkdir -p "$LOGS_DIR"

    # Parse checks
    CHECKS_JSON=$(echo "$MY_SUB_JSON" | jq '[.feedback.checks // [] | .[] | {text: ., passed: (. | test(": passed"))}]' 2>/dev/null || echo "[]")
    PASSED_COUNT=$(echo "$CHECKS_JSON" | jq '[.[] | select(.passed)] | length' 2>/dev/null || echo "0")
    FAILED_COUNT=$(echo "$CHECKS_JSON" | jq '[.[] | select(.passed | not)] | length' 2>/dev/null || echo "0")

    # Read new submissions.jsonl entries
    CURRENT_LINE_COUNT=$(line_count "$SUBMISSIONS_FILE")
    TASK_SUMMARIES="[]"
    if [[ -f "$SUBMISSIONS_FILE" && "$CURRENT_LINE_COUNT" -gt "$PRE_LINE_COUNT" ]]; then
      SKIP=$((PRE_LINE_COUNT))
      TASK_SUMMARIES=$(tail -n +$((SKIP + 1)) "$SUBMISSIONS_FILE" | \
        jq -s '[.[] | select(.prompt != "ping") | {
          task_type, task_index, run_id, handler, prompt, extraction,
          success, error, entity_id, extra_ids, handler_metadata,
          api_calls, call_count, error_count, elapsed_ms
        }]' 2>/dev/null || echo "[]")
      DERIVED_RUN_ID=$(echo "$TASK_SUMMARIES" | jq -r '[.[] | .run_id // empty] | first // empty' 2>/dev/null || true)
    fi

    SCORE_RAW=$(echo "$MY_SUB_JSON" | jq -r '.score_raw // empty')
    SCORE_MAX=$(echo "$MY_SUB_JSON" | jq -r '.score_max // empty')
    NORM_SCORE=$(echo "$MY_SUB_JSON" | jq -r '.normalized_score // empty')
    DURATION_MS=$(echo "$MY_SUB_JSON" | jq -r '.duration_ms // empty')
    FEEDBACK_COMMENT=$(echo "$MY_SUB_JSON" | jq -r '.feedback.comment // empty')
    TASK_COUNT=$(echo "$TASK_SUMMARIES" | jq 'length' 2>/dev/null || echo "0")

    RESULT_ENTRY=$(jq -n \
      --arg sid "$SUBMISSION_ID" \
      --arg rid "$DERIVED_RUN_ID" \
      --arg ts "$(date -u +%Y-%m-%dT%H:%M:%S.000Z)" \
      --arg status "$FINAL_STATE" \
      --arg score_raw "$SCORE_RAW" \
      --arg score_max "$SCORE_MAX" \
      --arg norm "$NORM_SCORE" \
      --arg dur "$DURATION_MS" \
      --arg comment "$FEEDBACK_COMMENT" \
      --argjson passed "$PASSED_COUNT" \
      --argjson failed "$FAILED_COUNT" \
      --argjson checks "$CHECKS_JSON" \
      --argjson task_count "$TASK_COUNT" \
      --argjson tasks "$TASK_SUMMARIES" \
      '{
        submission_id: $sid,
        run_id: $rid,
        timestamp: $ts,
        status: $status,
        score_raw: ($score_raw | if . == "" then null else tonumber end),
        score_max: ($score_max | if . == "" then null else tonumber end),
        normalized_score: ($norm | if . == "" then null else tonumber end),
        duration_ms: ($dur | if . == "" then null else tonumber end),
        feedback_comment: (if $comment == "" then null else $comment end),
        total_checks: ($passed + $failed),
        passed_checks: $passed,
        failed_checks: $failed,
        checks: $checks,
        task_count: $task_count,
        tasks: $tasks
      }')

    echo "$RESULT_ENTRY" | jq -c . >> "$RESULTS_FILE"

    echo ""
    echo -e "${GREEN}Results saved to results.jsonl${NC}"
    echo -e "${GRAY}  Run ID: $DERIVED_RUN_ID${NC}"
    echo -e "${CYAN}  Score: $SCORE_RAW/$SCORE_MAX | Checks: $PASSED_COUNT/$((PASSED_COUNT + FAILED_COUNT)) passed | Tasks: $TASK_COUNT${NC}"
  fi

  # --- Fetch leaderboard snapshot ---
  LB_DATA=$(curl -s --max-time 15 "$LEADERBOARD_URL" 2>/dev/null || true)
  if [[ -n "$LB_DATA" && "$LB_DATA" != "null" ]]; then
    mkdir -p "$LOGS_DIR"

    TOTAL_SCORE=$(echo "$LB_DATA" | jq '[.[].best_score] | add // 0 | . * 10000 | round / 10000' 2>/dev/null || echo "0")
    TASK_COUNT_LB=$(echo "$LB_DATA" | jq 'length' 2>/dev/null || echo "0")
    ZERO_TASKS=$(echo "$LB_DATA" | jq -r '[.[] | select(.best_score == 0) | .tx_task_id] | join(", ")' 2>/dev/null || true)

    # Load pre-submission snapshot for diff
    PRE_SCORES_JSON="{}"
    PRE_COUNTS_JSON="{}"
    if [[ -f "$LEADERBOARD_FILE" ]]; then
      LAST_LB_LINE=$(tail -1 "$LEADERBOARD_FILE" 2>/dev/null || true)
      if [[ -n "$LAST_LB_LINE" ]]; then
        PRE_SCORES_JSON=$(echo "$LAST_LB_LINE" | jq '[.tasks[] | select(.tx_task_id != null) | {(.tx_task_id): .best_score}] | add // {}' 2>/dev/null || echo "{}")
        PRE_COUNTS_JSON=$(echo "$LAST_LB_LINE" | jq '[.tasks[] | select(.tx_task_id != null) | {(.tx_task_id): .total_attempts}] | add // {}' 2>/dev/null || echo "{}")
      fi
    fi

    # Compute attempted tasks and score changes
    ATTEMPTED_TASKS=$(echo "$LB_DATA" | jq --argjson pre "$PRE_COUNTS_JSON" --argjson preS "$PRE_SCORES_JSON" '
      [.[] | select(.tx_task_id != null) |
        . as $t |
        ($pre[$t.tx_task_id] // 0) as $prevAttempts |
        ($preS[$t.tx_task_id] // 0) as $prevScore |
        select($t.total_attempts > $prevAttempts) |
        {
          tx_task_id: $t.tx_task_id,
          prev_attempts: $prevAttempts,
          new_attempts: $t.total_attempts,
          delta: ($t.total_attempts - $prevAttempts),
          prev_score: $prevScore,
          new_score: $t.best_score
        }
      ]' 2>/dev/null || echo "[]")

    SCORE_CHANGES=$(echo "$LB_DATA" | jq --argjson preS "$PRE_SCORES_JSON" '
      [.[] | select(.tx_task_id != null) |
        . as $t |
        ($preS[$t.tx_task_id] // 0) as $prevScore |
        select($t.best_score != $prevScore) |
        {
          tx_task_id: $t.tx_task_id,
          prev_score: $prevScore,
          new_score: $t.best_score,
          delta: (($t.best_score - $prevScore) * 10000 | round / 10000)
        }
      ]' 2>/dev/null || echo "[]")

    # Save leaderboard entry
    LB_ENTRY=$(jq -n \
      --arg ts "$(date -u +%Y-%m-%dT%H:%M:%S.000Z)" \
      --arg sid "$SUBMISSION_ID" \
      --arg rid "$DERIVED_RUN_ID" \
      --argjson total "$TOTAL_SCORE" \
      --argjson tc "$TASK_COUNT_LB" \
      --argjson attempted "$ATTEMPTED_TASKS" \
      --argjson changes "$SCORE_CHANGES" \
      --argjson tasks "$LB_DATA" \
      '{
        timestamp: $ts,
        submission_id: $sid,
        run_id: $rid,
        total_best_score: $total,
        task_count: $tc,
        attempted_tasks: $attempted,
        score_changes: $changes,
        tasks: $tasks
      }')
    echo "$LB_ENTRY" | jq -c . >> "$LEADERBOARD_FILE"

    echo ""
    echo -e "${GREEN}Leaderboard snapshot saved to leaderboard.jsonl${NC}"
    echo -e "${YELLOW}  Total: $TOTAL_SCORE across $TASK_COUNT_LB tasks${NC}"
    if [[ -n "$ZERO_TASKS" ]]; then
      echo -e "${YELLOW}  Zero-score tasks: $ZERO_TASKS${NC}"
    fi

    # --- Load/update task mapping ---
    MAPPING_FILE="$LOGS_DIR/task_mapping.json"
    TASK_MAPPING="{}"
    if [[ -f "$MAPPING_FILE" ]]; then
      TASK_MAPPING=$(cat "$MAPPING_FILE" 2>/dev/null || echo "{}")
    fi

    # --- Correlate attempted tasks with submissions ---
    ATTEMPTED_COUNT=$(echo "$ATTEMPTED_TASKS" | jq 'length' 2>/dev/null || echo "0")

    if [[ "$ATTEMPTED_COUNT" -gt 0 && -f "$SUBMISSIONS_FILE" ]]; then
      # Sort attempted by last_attempt_at
      SORTED_ATTEMPTED=$(echo "$LB_DATA" | jq --argjson attempted "$ATTEMPTED_TASKS" '
        [($attempted[].tx_task_id) as $tid |
         . as $lb |
         ($attempted[] | select(.tx_task_id == $tid)) as $at |
         ($lb[] | select(.tx_task_id == $tid)) as $lbt |
         ($at + {last_attempt_at: $lbt.last_attempt_at})
        ] | unique_by(.tx_task_id) | sort_by(.last_attempt_at)' 2>/dev/null || echo "[]")

      # Get new submissions (non-ping, sorted by timestamp)
      SKIP=$((PRE_LINE_COUNT))
      SORTED_SUBMISSIONS="[]"
      CURRENT_LINE_COUNT=$(line_count "$SUBMISSIONS_FILE")
      if [[ "$CURRENT_LINE_COUNT" -gt "$PRE_LINE_COUNT" ]]; then
        SORTED_SUBMISSIONS=$(tail -n +$((SKIP + 1)) "$SUBMISSIONS_FILE" | \
          jq -s '[.[] | select(.prompt != "ping" and .task_type != "unknown")] | sort_by(.timestamp)' 2>/dev/null || echo "[]")
      fi

      # Zip and correlate
      AT_LEN=$(echo "$SORTED_ATTEMPTED" | jq 'length')
      SUB_LEN=$(echo "$SORTED_SUBMISSIONS" | jq 'length')
      ZIP_LEN=$(( AT_LEN < SUB_LEN ? AT_LEN : SUB_LEN ))

      # Update task mapping from correlations
      for (( CI=0; CI<ZIP_LEN; CI++ )); do
        TID=$(echo "$SORTED_ATTEMPTED" | jq -r ".[$CI].tx_task_id")
        TTYPE=$(echo "$SORTED_SUBMISSIONS" | jq -r ".[$CI].task_type")
        TASK_MAPPING=$(echo "$TASK_MAPPING" | jq --arg tid "$TID" --arg tt "$TTYPE" \
          'if has($tid) then . else . + {($tid): $tt} end')
      done

      # Save mapping
      echo "$TASK_MAPPING" | jq -c . > "$MAPPING_FILE" 2>/dev/null || true

      # --- Print correlation summary ---
      echo ""
      echo -e "${CYAN}========================================${NC}"
      echo -e "${CYAN} Task Correlation (tx_id → handler → score)${NC}"
      echo -e "${CYAN}========================================${NC}"

      if [[ "$ZIP_LEN" -gt 0 ]]; then
        for (( CI=0; CI<ZIP_LEN; CI++ )); do
          TID=$(echo "$SORTED_ATTEMPTED" | jq -r ".[$CI].tx_task_id")
          PREV_SC=$(echo "$SORTED_ATTEMPTED" | jq -r ".[$CI].prev_score")
          NEW_SC=$(echo "$SORTED_ATTEMPTED" | jq -r ".[$CI].new_score")
          TTYPE=$(echo "$SORTED_SUBMISSIONS" | jq -r ".[$CI].task_type")
          HANDLER=$(echo "$SORTED_SUBMISSIONS" | jq -r ".[$CI].handler // empty")
          SUCCESS=$(echo "$SORTED_SUBMISSIONS" | jq -r ".[$CI].success")
          ERR=$(echo "$SORTED_SUBMISSIONS" | jq -r ".[$CI].error // empty")

          DELTA=$(echo "$NEW_SC - $PREV_SC" | bc 2>/dev/null || echo "0")
          if (( $(echo "$DELTA > 0" | bc -l 2>/dev/null || echo "0") )); then
            DELTA_STR="+$DELTA * IMPROVED"
            COLOR="$GREEN"
          elif (( $(echo "$DELTA < 0" | bc -l 2>/dev/null || echo "0") )); then
            DELTA_STR="$DELTA REGRESSED"
            COLOR="$RED"
          else
            DELTA_STR="= no change"
            if [[ "$SUCCESS" == "false" ]]; then COLOR="$YELLOW"; else COLOR="$GRAY"; fi
          fi

          STATUS_STR="OK"
          if [[ "$SUCCESS" == "false" ]]; then STATUS_STR="FAIL"; fi
          ERR_STR=""
          if [[ -n "$ERR" && "$ERR" != "null" ]]; then ERR_STR="  err: $ERR"; fi

          printf "${COLOR}  tx:%s = %-26s | %-22s | %4s | %s → %s (%s)%s${NC}\n" \
            "$TID" "$TTYPE" "$HANDLER" "$STATUS_STR" "$PREV_SC" "$NEW_SC" "$DELTA_STR" "$ERR_STR"
        done
      else
        echo -e "${GRAY}  No correlations (no new task attempts or no new submissions).${NC}"
      fi

      # Uncorrelated attempts
      if [[ "$AT_LEN" -gt "$ZIP_LEN" ]]; then
        echo ""
        echo -e "${GRAY}  Uncorrelated attempts (leaderboard only):${NC}"
        for (( CI=ZIP_LEN; CI<AT_LEN; CI++ )); do
          TID=$(echo "$SORTED_ATTEMPTED" | jq -r ".[$CI].tx_task_id")
          PREV_SC=$(echo "$SORTED_ATTEMPTED" | jq -r ".[$CI].prev_score")
          NEW_SC=$(echo "$SORTED_ATTEMPTED" | jq -r ".[$CI].new_score")
          KNOWN_TYPE=$(echo "$TASK_MAPPING" | jq -r --arg tid "$TID" '.[$tid] // "?"')
          printf "${GRAY}    tx:%s = %-26s | score: %s → %s${NC}\n" "$TID" "$KNOWN_TYPE" "$PREV_SC" "$NEW_SC"
        done
      fi

      # --- Print annotated competition checks ---
      FEEDBACK_CHECKS=$(echo "$MY_SUB_JSON" | jq '[.feedback.checks // [] | .[]]' 2>/dev/null || echo "[]")
      FEEDBACK_CHECKS_LEN=$(echo "$FEEDBACK_CHECKS" | jq 'length' 2>/dev/null || echo "0")

      if [[ "$ZIP_LEN" -gt 0 && "$FEEDBACK_CHECKS_LEN" -gt 0 ]]; then
        echo ""
        echo -e "${CYAN}Competition checks (annotated):${NC}"

        CHECK_OFFSET=0
        for (( CI=0; CI<ZIP_LEN; CI++ )); do
          TTYPE=$(echo "$SORTED_SUBMISSIONS" | jq -r ".[$CI].task_type")
          TID=$(echo "$SORTED_ATTEMPTED" | jq -r ".[$CI].tx_task_id")

          # Known check names per task type
          case "$TTYPE" in
            create_employee)       CHECK_NAMES=("employee_found" "firstName" "lastName" "email" "admin_role") ;;
            create_customer)       CHECK_NAMES=("customer_found" "name" "email" "organizationNumber" "addr.addressLine1" "addr.postalCode" "addr.city" "phoneNumber") ;;
            create_supplier)       CHECK_NAMES=("supplier_found" "name" "email" "organizationNumber" "phoneNumber") ;;
            create_product)        CHECK_NAMES=("product_found" "name" "number" "price") ;;
            create_department)     CHECK_NAMES=("department_found" "name" "departmentNumber") ;;
            create_project)        CHECK_NAMES=("project_found" "name" "has_customer" "has_project_manager") ;;
            create_invoice)        CHECK_NAMES=("invoice_found" "has_customer" "has_amount" "correct_amount") ;;
            register_payment)      CHECK_NAMES=("invoice_found" "payment_registered") ;;
            run_payroll)           CHECK_NAMES=("salary_transaction_found" "has_employee_link" "payslip_generated" "correct_amount") ;;
            create_travel_expense) CHECK_NAMES=("travel_expense_found" "has_title" "has_employee" "has_costs") ;;
            create_credit_note)    CHECK_NAMES=("credit_note_created") ;;
            create_voucher)        CHECK_NAMES=("voucher_found" "has_description" "has_postings") ;;
            *)                     CHECK_NAMES=() ;;
          esac

          N_CHECKS=${#CHECK_NAMES[@]}
          if [[ "$N_CHECKS" -eq 0 || "$CHECK_OFFSET" -ge "$FEEDBACK_CHECKS_LEN" ]]; then
            echo -e "${GRAY}  tx:$TID $TTYPE: (no check mapping)${NC}"
            continue
          fi

          # Count passed/total for this task's checks
          TASK_PASSED=0
          TASK_TOTAL=0
          for (( CK=0; CK<N_CHECKS && (CHECK_OFFSET+CK)<FEEDBACK_CHECKS_LEN; CK++ )); do
            CHK=$(echo "$FEEDBACK_CHECKS" | jq -r ".[$((CHECK_OFFSET + CK))]")
            TASK_TOTAL=$((TASK_TOTAL + 1))
            if [[ "$CHK" == *": passed"* ]]; then
              TASK_PASSED=$((TASK_PASSED + 1))
            fi
          done

          if [[ "$TASK_PASSED" -eq "$TASK_TOTAL" ]]; then
            HDR_COLOR="$GREEN"
          elif [[ "$TASK_PASSED" -eq 0 ]]; then
            HDR_COLOR="$RED"
          else
            HDR_COLOR="$YELLOW"
          fi
          echo -e "${HDR_COLOR}  tx:$TID $TTYPE: $TASK_PASSED/$TASK_TOTAL passed${NC}"

          for (( CK=0; CK<N_CHECKS && (CHECK_OFFSET+CK)<FEEDBACK_CHECKS_LEN; CK++ )); do
            CHK=$(echo "$FEEDBACK_CHECKS" | jq -r ".[$((CHECK_OFFSET + CK))]")
            NAME="${CHECK_NAMES[$CK]}"
            if [[ "$CHK" == *": passed"* ]]; then
              echo -e "${GREEN}    ✓ $NAME${NC}"
            else
              echo -e "${RED}    ✗ $NAME${NC}"
            fi
          done
          CHECK_OFFSET=$((CHECK_OFFSET + N_CHECKS))
        done
      fi
    else
      echo ""
      echo -e "${CYAN}========================================${NC}"
      echo -e "${CYAN} Task Correlation (tx_id → handler → score)${NC}"
      echo -e "${CYAN}========================================${NC}"
      echo -e "${GRAY}  No correlations (no new task attempts or no new submissions).${NC}"
    fi

    # --- Full leaderboard ---
    echo ""
    echo -e "${CYAN}Leaderboard (all tasks):${NC}"
    echo "$LB_DATA" | jq -r --argjson mapping "$TASK_MAPPING" --argjson preS "$PRE_SCORES_JSON" '
      sort_by(.tx_task_id) | .[] |
      .tx_task_id as $tid |
      ($mapping[$tid] // "?") as $name |
      (($preS[$tid] // 0)) as $prev |
      ((.best_score - $prev) * 10000 | round / 10000) as $delta |
      (if $delta > 0 then " +\($delta)" elif $delta < 0 then " \($delta)" else "" end) as $deltaStr |
      "  tx:\($tid) \($name | . + (" " * (26 - (. | length))) | .[:26]) best:\(.best_score | tostring | (" " * (7 - (. | length))) + .)  attempts:\(.total_attempts)\($deltaStr)"
    ' 2>/dev/null || true

  else
    echo -e "${YELLOW}WARNING: Failed to fetch leaderboard.${NC}"
  fi

  # --- Refresh task documentation ---
  echo ""
  echo -e "${CYAN}Refreshing task documentation...${NC}"
  bash "$SCRIPT_DIR/Refresh-Tasks.sh" 2>/dev/null || echo -e "${YELLOW}WARNING: Refresh-Tasks.sh failed${NC}"

  # Track this run's result
  RUN_SCORE=$(echo "$MY_SUB_JSON" | jq -r '.score // empty' 2>/dev/null || true)
  BATCH_STATUSES+=("$FINAL_STATE")
  BATCH_SCORES+=("${RUN_SCORE:-null}")

  # --- Replay new competition requests locally ---
  if $NO_REPLAY || [[ ! -f "$SUBMISSIONS_FILE" ]]; then
    continue
  fi

  CURRENT_LINE_COUNT=$(line_count "$SUBMISSIONS_FILE")
  NEW_COUNT=$((CURRENT_LINE_COUNT - PRE_LINE_COUNT))

  if [[ "$NEW_COUNT" -le 0 ]]; then
    echo ""
    echo -e "${GRAY}No new entries in submissions.jsonl to replay.${NC}"
    continue
  fi

  echo ""
  echo -e "${CYAN}========================================${NC}"
  echo -e "${CYAN} Replaying $NEW_COUNT competition request(s) locally${NC}"
  echo -e "${CYAN}========================================${NC}"

  SKIP=$((PRE_LINE_COUNT))
  tail -n +$((SKIP + 1)) "$SUBMISSIONS_FILE" | while IFS= read -r LINE; do
    PROMPT=$(echo "$LINE" | jq -r '.prompt // empty' 2>/dev/null || true)
    if [[ -z "$PROMPT" || "$PROMPT" == "ping" ]]; then
      continue
    fi

    TASK_TYPE=$(echo "$LINE" | jq -r '.task_type // empty' 2>/dev/null || true)
    LANGUAGE=$(echo "$LINE" | jq -r '.language // empty' 2>/dev/null || true)
    HANDLER_NAME=$(echo "$LINE" | jq -r '.handler // empty' 2>/dev/null || true)
    SUCCESS_VAL=$(echo "$LINE" | jq -r '.success // empty' 2>/dev/null || true)
    CALL_CNT=$(echo "$LINE" | jq -r '.call_count // empty' 2>/dev/null || true)
    ERR_CNT=$(echo "$LINE" | jq -r '.error_count // empty' 2>/dev/null || true)
    ERR_MSG=$(echo "$LINE" | jq -r '.error // empty' 2>/dev/null || true)

    PROMPT_PREVIEW="${PROMPT:0:80}"
    echo ""
    echo -e "${YELLOW}--- Task: $TASK_TYPE ($LANGUAGE) ---${NC}"
    echo -e "${GRAY}Prompt: ${PROMPT_PREVIEW}...${NC}"

    # Replay via Test-Solve.sh
    bash "$SCRIPT_DIR/Test-Solve.sh" "$PROMPT" || true

    # Summary
    echo ""
    echo -e "${CYAN}Competition result:${NC}"
    echo "  Handler:    $HANDLER_NAME"
    echo "  Success:    $SUCCESS_VAL"
    echo "  API calls:  $CALL_CNT"
    echo "  Errors:     $ERR_CNT"
    if [[ -n "$ERR_MSG" && "$ERR_MSG" != "null" ]]; then
      echo -e "${RED}  Error:      $ERR_MSG${NC}"
    fi
  done

done # end of run loop

# --- Batch summary ---
if [[ "$RUNS" -gt 1 && "${#BATCH_STATUSES[@]}" -gt 0 ]]; then
  echo ""
  echo -e "${MAGENTA}========================================${NC}"
  echo -e "${MAGENTA} Batch Summary (${#BATCH_STATUSES[@]} of $RUNS runs)${NC}"
  echo -e "${MAGENTA}========================================${NC}"
  for (( BI=0; BI<${#BATCH_STATUSES[@]}; BI++ )); do
    BS="${BATCH_STATUSES[$BI]}"
    BSC="${BATCH_SCORES[$BI]}"
    SCORE_STR="no score"
    if [[ "$BSC" != "null" && -n "$BSC" ]]; then
      SCORE_STR="score: $BSC"
    fi
    case "$BS" in
      completed) COLOR="$GREEN" ;;
      timeout)   COLOR="$YELLOW" ;;
      failed|error|submit_failed) COLOR="$RED" ;;
      *)         COLOR="$GRAY" ;;
    esac
    printf "${COLOR}  Run %d: %s (%s)${NC}\n" $((BI + 1)) "$BS" "$SCORE_STR"
  done
fi
