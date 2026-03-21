#!/usr/bin/env bash
# Sends a test prompt to the locally running /solve endpoint.
# Usage:
#   ./scripts/test-solve.sh "Opprett en kunde med navn 'Test AS'"
#   ./scripts/test-solve.sh --port 5001 "Create an employee named Ola Nordmann"
#   ./scripts/test-solve.sh --file invoice.pdf "Process this invoice"

set -euo pipefail

command -v python3 >/dev/null 2>&1 || { echo "ERROR: python3 is required but not found."; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SRC_DIR="$SCRIPT_DIR/../src"

PORT=5000
BASE_URL=""
SESSION_TOKEN=""
API_KEY=""
PROMPT=""
FILE_PATHS=()

while [[ $# -gt 0 ]]; do
    case "$1" in
        --port|-p)          PORT="$2"; shift 2 ;;
        --base-url)         BASE_URL="$2"; shift 2 ;;
        --session-token)    SESSION_TOKEN="$2"; shift 2 ;;
        --api-key)          API_KEY="$2"; shift 2 ;;
        --file|-f)          FILE_PATHS+=("$2"); shift 2 ;;
        -*)                 echo "Unknown option: $1"; exit 1 ;;
        *)                  PROMPT="$1"; shift ;;
    esac
done

if [[ -z "$PROMPT" ]]; then
    echo "Usage: $0 [options] <prompt>"
    echo "  --port <port>         Local port (default: 5000)"
    echo "  --base-url <url>      Tripletex API base URL"
    echo "  --session-token <tok> Tripletex session token"
    echo "  --api-key <key>       Bearer token for /solve"
    echo "  --file <path>         File to attach (can repeat)"
    exit 1
fi

# Load defaults from user-secrets if not provided
if [[ -z "$BASE_URL" || -z "$SESSION_TOKEN" || -z "$API_KEY" ]]; then
    SECRETS_OUTPUT=$(dotnet user-secrets list --project "$SRC_DIR" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>/dev/null || true)
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

# Require all values
if [[ -z "$BASE_URL" ]]; then echo "ERROR: BaseUrl not set. Pass --base-url or configure Tripletex:BaseUrl in user-secrets."; exit 1; fi
if [[ -z "$SESSION_TOKEN" ]]; then echo "ERROR: SessionToken not set. Pass --session-token or configure Tripletex:SessionToken in user-secrets."; exit 1; fi
if [[ -z "$API_KEY" ]]; then echo "ERROR: ApiKey not set. Pass --api-key or configure ApiKey in user-secrets."; exit 1; fi

# Build JSON body using python3 (safe from injection)
FILES_JSON="[]"
if [[ ${#FILE_PATHS[@]} -gt 0 ]]; then
    FILES_JSON=$(python3 -c "
import sys, json, base64, os

files = []
for fp in sys.argv[1:]:
    with open(fp, 'rb') as f:
        data = base64.b64encode(f.read()).decode()
    ext = os.path.splitext(fp)[1].lower()
    mime_map = {'.pdf': 'application/pdf', '.png': 'image/png', '.jpg': 'image/jpeg',
                '.jpeg': 'image/jpeg', '.gif': 'image/gif', '.webp': 'image/webp'}
    files.append({'filename': os.path.basename(fp), 'content_base64': data,
                  'mime_type': mime_map.get(ext, 'application/octet-stream')})
print(json.dumps(files))
" "${FILE_PATHS[@]}")
fi

BODY=$(python3 -c "
import sys, json
prompt = sys.stdin.read()
obj = {
    'prompt': prompt,
    'files': json.loads(sys.argv[1]),
    'tripletex_credentials': {
        'base_url': sys.argv[2],
        'session_token': sys.argv[3]
    }
}
print(json.dumps(obj))
" "$FILES_JSON" "$BASE_URL" "$SESSION_TOKEN" <<< "$PROMPT")

URL="http://localhost:$PORT/solve"
TMPFILE=$(mktemp)
trap 'rm -f "$TMPFILE"' EXIT

HTTP_CODE=$(curl -s -o "$TMPFILE" -w "%{http_code}" \
    -X POST "$URL" \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer $API_KEY" \
    -d "$BODY" \
    --max-time 120)

RESPONSE=$(cat "$TMPFILE")

if [[ "$HTTP_CODE" -ge 200 && "$HTTP_CODE" -lt 300 ]]; then
    echo -e "\033[32mOK ($HTTP_CODE): $RESPONSE\033[0m"
else
    echo -e "\033[31mERR ($HTTP_CODE): $RESPONSE\033[0m"
fi

# Show latest log tail
LOG_DIR="$SRC_DIR/logs"
if [[ -d "$LOG_DIR" ]]; then
    LATEST=$(ls -t "$LOG_DIR"/*.log 2>/dev/null | head -1)
    if [[ -n "$LATEST" ]]; then
        echo ""
        echo -e "\033[36m--- Log tail ---\033[0m"
        tail -10 "$LATEST"
    fi
fi
