#!/usr/bin/env bash
#
# Test-Solve.sh — Bash equivalent of Test-Solve.ps1
#
# Sends a POST request to http://localhost:<port>/solve with the given prompt
# and Tripletex sandbox credentials. Useful for verifying handler behavior
# during development.
#
# Usage:
#   ./scripts/Test-Solve.sh "Opprett en kunde med navn 'Test AS'"
#   ./scripts/Test-Solve.sh --port 5001 "Create an employee named Ola Nordmann"
#   ./scripts/Test-Solve.sh --file invoice.pdf --file receipt.png "Process these files"
#
# Options:
#   --base-url URL        Tripletex API base URL (default: from user-secrets)
#   --session-token TOKEN  Tripletex session token (default: from user-secrets)
#   --api-key KEY         Bearer token for /solve endpoint (default: from user-secrets)
#   --port PORT           Local port the agent listens on (default: 5000)
#   --file PATH           Attach a file (repeatable)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_DIR="$SCRIPT_DIR/../src"

# Defaults
PORT=5000
BASE_URL=""
SESSION_TOKEN=""
API_KEY=""
PROMPT=""
FILES=()

# ── Argument parsing ──────────────────────────────────────────────────────────

while [[ $# -gt 0 ]]; do
  case "$1" in
    --base-url)
      BASE_URL="$2"; shift 2 ;;
    --session-token)
      SESSION_TOKEN="$2"; shift 2 ;;
    --api-key)
      API_KEY="$2"; shift 2 ;;
    --port)
      PORT="$2"; shift 2 ;;
    --file)
      FILES+=("$2"); shift 2 ;;
    -*)
      echo "Unknown option: $1" >&2; exit 1 ;;
    *)
      if [[ -z "$PROMPT" ]]; then
        PROMPT="$1"; shift
      else
        echo "Unexpected argument: $1 (prompt already set)" >&2; exit 1
      fi
      ;;
  esac
done

if [[ -z "$PROMPT" ]]; then
  echo "Usage: $0 [options] \"<prompt>\"" >&2
  exit 1
fi

# ── Load secrets from dotnet user-secrets ─────────────────────────────────────

load_secrets() {
  local secrets
  secrets=$(dotnet user-secrets list --project "$SRC_DIR" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>/dev/null) || return 0

  while IFS= read -r line; do
    if [[ -z "$BASE_URL" && "$line" =~ ^Tripletex:BaseUrl[[:space:]]*=[[:space:]]*(.+)$ ]]; then
      BASE_URL="${BASH_REMATCH[1]}"
    fi
    if [[ -z "$SESSION_TOKEN" && "$line" =~ ^Tripletex:SessionToken[[:space:]]*=[[:space:]]*(.+)$ ]]; then
      SESSION_TOKEN="${BASH_REMATCH[1]}"
    fi
    if [[ -z "$API_KEY" && "$line" =~ ^ApiKey[[:space:]]*=[[:space:]]*(.+)$ ]]; then
      API_KEY="${BASH_REMATCH[1]}"
    fi
  done <<< "$secrets"
}

load_secrets

# ── Validate required values ──────────────────────────────────────────────────

if [[ -z "$BASE_URL" ]]; then
  echo "BaseUrl not set. Pass --base-url or configure Tripletex:BaseUrl in user-secrets." >&2
  exit 1
fi
if [[ -z "$SESSION_TOKEN" ]]; then
  echo "SessionToken not set. Pass --session-token or configure Tripletex:SessionToken in user-secrets." >&2
  exit 1
fi
# API key is optional — server has auth disabled for competition

# ── MIME type helper ──────────────────────────────────────────────────────────

get_mime() {
  local ext="${1##*.}"
  ext=$(echo "$ext" | tr '[:upper:]' '[:lower:]')
  case "$ext" in
    pdf)  echo "application/pdf" ;;
    png)  echo "image/png" ;;
    jpg)  echo "image/jpeg" ;;
    jpeg) echo "image/jpeg" ;;
    gif)  echo "image/gif" ;;
    webp) echo "image/webp" ;;
    csv)  echo "text/csv" ;;
    *)    echo "application/octet-stream" ;;
  esac
}

# ── Build JSON body ───────────────────────────────────────────────────────────

# Start with base object
BODY=$(jq -n \
  --arg prompt "$PROMPT" \
  --arg base_url "$BASE_URL" \
  --arg session_token "$SESSION_TOKEN" \
  '{
    prompt: $prompt,
    tripletex_credentials: {
      base_url: $base_url,
      session_token: $session_token
    }
  }')

# Add files array if any files specified
if [[ ${#FILES[@]} -gt 0 ]]; then
  FILES_JSON="[]"
  for fp in "${FILES[@]}"; do
    if [[ ! -f "$fp" ]]; then
      echo "File not found: $fp" >&2
      exit 1
    fi
    filename=$(basename "$fp")
    mime=$(get_mime "$fp")
    content_b64=$(base64 < "$fp")
    FILES_JSON=$(echo "$FILES_JSON" | jq \
      --arg fn "$filename" \
      --arg mime "$mime" \
      --arg b64 "$content_b64" \
      '. + [{filename: $fn, content_base64: $b64, mime_type: $mime}]')
  done
  BODY=$(echo "$BODY" | jq --argjson files "$FILES_JSON" '. + {files: $files}')
fi

# ── Send request ──────────────────────────────────────────────────────────────

URL="http://localhost:${PORT}/solve"

if [[ -n "$API_KEY" ]]; then
  HTTP_CODE=$(curl -s -o /tmp/test-solve-response.txt -w "%{http_code}" \
    -X POST "$URL" \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer $API_KEY" \
    -d "$BODY")
else
  HTTP_CODE=$(curl -s -o /tmp/test-solve-response.txt -w "%{http_code}" \
    -X POST "$URL" \
    -H "Content-Type: application/json" \
    -d "$BODY")
fi

RESPONSE=$(cat /tmp/test-solve-response.txt)

if [[ "$HTTP_CODE" -ge 200 && "$HTTP_CODE" -lt 300 ]]; then
  echo -e "\033[32mOK: $RESPONSE\033[0m"
else
  echo -e "\033[31mERR ($HTTP_CODE): $RESPONSE\033[0m"
fi

# ── Tail latest log ───────────────────────────────────────────────────────────

LOG_DIR="$SRC_DIR/logs"
if [[ -d "$LOG_DIR" ]]; then
  LATEST_LOG=$(ls -t "$LOG_DIR"/*.log 2>/dev/null | head -1)
  if [[ -n "$LATEST_LOG" ]]; then
    echo ""
    echo -e "\033[36m--- Log tail ---\033[0m"
    tail -10 "$LATEST_LOG"
  fi
fi
