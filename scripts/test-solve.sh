#!/usr/bin/env bash
# Sends a test prompt to the locally running /solve endpoint.
# Usage:
#   ./scripts/test-solve.sh "Opprett en kunde med navn 'Test AS'"
#   ./scripts/test-solve.sh --port 5001 "Create an employee named Ola Nordmann"
#   ./scripts/test-solve.sh --file invoice.pdf "Process this invoice"

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SRC_DIR="$SCRIPT_DIR/../src"

PORT=5000
BASE_URL=""
SESSION_TOKEN=""
API_KEY=""
FILES=()
PROMPT=""

# --- Parse arguments ---
while [[ $# -gt 0 ]]; do
    case "$1" in
        --port)       PORT="$2"; shift 2 ;;
        --base-url)   BASE_URL="$2"; shift 2 ;;
        --token)      SESSION_TOKEN="$2"; shift 2 ;;
        --api-key)    API_KEY="$2"; shift 2 ;;
        --file)       FILES+=("$2"); shift 2 ;;
        -*)           echo "Unknown option: $1"; exit 1 ;;
        *)            PROMPT="$1"; shift ;;
    esac
done

if [[ -z "$PROMPT" ]]; then
    echo "Usage: $0 [--port PORT] [--file PATH] \"prompt text\""
    exit 1
fi

# --- Load defaults from user-secrets if not provided ---
SECRETS_ID="54b40cce-1f78-4e18-ab1b-c1501ef7f7da"
if [[ -z "$BASE_URL" || -z "$SESSION_TOKEN" || -z "$API_KEY" ]]; then
    SECRETS_OUTPUT=$(dotnet user-secrets list --project "$SRC_DIR" --id "$SECRETS_ID" 2>/dev/null || true)
    if [[ -n "$SECRETS_OUTPUT" ]]; then
        if [[ -z "$BASE_URL" ]]; then
            BASE_URL=$(echo "$SECRETS_OUTPUT" | grep -E '^Tripletex:BaseUrl\s*=' | sed 's/^.*=\s*//' | xargs)
        fi
        if [[ -z "$SESSION_TOKEN" ]]; then
            SESSION_TOKEN=$(echo "$SECRETS_OUTPUT" | grep -E '^Tripletex:SessionToken\s*=' | sed 's/^.*=\s*//' | xargs)
        fi
        if [[ -z "$API_KEY" ]]; then
            API_KEY=$(echo "$SECRETS_OUTPUT" | grep -E '^ApiKey\s*=' | sed 's/^.*=\s*//' | xargs)
        fi
    fi
fi

# --- Require all values ---
if [[ -z "$BASE_URL" ]]; then
    echo -e "\033[31mBaseUrl not set. Pass --base-url or configure Tripletex:BaseUrl in user-secrets.\033[0m"
    exit 1
fi
if [[ -z "$SESSION_TOKEN" ]]; then
    echo -e "\033[31mSessionToken not set. Pass --token or configure Tripletex:SessionToken in user-secrets.\033[0m"
    exit 1
fi
if [[ -z "$API_KEY" ]]; then
    echo -e "\033[31mApiKey not set. Pass --api-key or configure ApiKey in user-secrets.\033[0m"
    exit 1
fi

# --- Build JSON body ---
# Escape prompt for JSON
ESCAPED_PROMPT=$(echo -n "$PROMPT" | python3 -c 'import sys,json; print(json.dumps(sys.stdin.read()))')

FILES_JSON="[]"
if [[ ${#FILES[@]} -gt 0 ]]; then
    FILES_JSON="["
    FIRST=true
    for fp in "${FILES[@]}"; do
        RESOLVED=$(cd "$(dirname "$fp")" && pwd)/$(basename "$fp")
        FILENAME=$(basename "$fp")
        EXT="${fp##*.}"
        EXT_LOWER=$(echo "$EXT" | tr '[:upper:]' '[:lower:]')
        case "$EXT_LOWER" in
            pdf)  MIME="application/pdf" ;;
            png)  MIME="image/png" ;;
            jpg)  MIME="image/jpeg" ;;
            jpeg) MIME="image/jpeg" ;;
            gif)  MIME="image/gif" ;;
            webp) MIME="image/webp" ;;
            *)    MIME="application/octet-stream" ;;
        esac
        B64=$(base64 < "$RESOLVED" | tr -d '\n')
        if ! $FIRST; then FILES_JSON+=","; fi
        FILES_JSON+="{\"filename\":\"$FILENAME\",\"content_base64\":\"$B64\",\"mime_type\":\"$MIME\"}"
        FIRST=false
    done
    FILES_JSON+="]"
fi

BODY=$(cat <<EOF
{
  "prompt": $ESCAPED_PROMPT,
  "files": $FILES_JSON,
  "tripletex_credentials": {
    "base_url": "$BASE_URL",
    "session_token": "$SESSION_TOKEN"
  }
}
EOF
)

URL="http://localhost:$PORT/solve"

# --- Send request ---
HTTP_CODE=$(curl -s -o /tmp/test-solve-response.txt -w "%{http_code}" \
    -X POST "$URL" \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer $API_KEY" \
    -d "$BODY" \
    --max-time 120)

RESPONSE=$(cat /tmp/test-solve-response.txt)

if [[ "$HTTP_CODE" == "200" ]]; then
    echo -e "\033[32mOK: $RESPONSE\033[0m"
else
    echo -e "\033[31mERR ($HTTP_CODE): $RESPONSE\033[0m"
fi

# --- Show latest log tail ---
LOG_DIR="$SRC_DIR/logs"
if [[ -d "$LOG_DIR" ]]; then
    LATEST=$(ls -t "$LOG_DIR"/*.log 2>/dev/null | head -1)
    if [[ -n "$LATEST" ]]; then
        echo ""
        echo -e "\033[36m--- Log tail ---\033[0m"
        tail -10 "$LATEST"
    fi
fi
