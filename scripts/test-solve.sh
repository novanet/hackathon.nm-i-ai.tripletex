#!/usr/bin/env bash
#
# Sends a test prompt to the locally running /solve endpoint.
# Bash equivalent of Test-Solve.ps1.
#
# Usage:
#   ./scripts/test-solve.sh "Opprett en kunde med navn 'Test AS'"
#   ./scripts/test-solve.sh "Create an employee" --file invoice.pdf
#   ./scripts/test-solve.sh "test" --port 5001 --base-url https://... --session-token abc --api-key xyz

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

# Parse arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        --base-url)      BASE_URL="$2"; shift 2 ;;
        --session-token) SESSION_TOKEN="$2"; shift 2 ;;
        --api-key)       API_KEY="$2"; shift 2 ;;
        --port)          PORT="$2"; shift 2 ;;
        --file)          FILES+=("$2"); shift 2 ;;
        -*)              echo "Unknown option: $1" >&2; exit 1 ;;
        *)
            if [[ -z "$PROMPT" ]]; then
                PROMPT="$1"
            else
                echo "Unexpected argument: $1" >&2; exit 1
            fi
            shift
            ;;
    esac
done

if [[ -z "$PROMPT" ]]; then
    echo "Usage: $0 <prompt> [--file <path>]... [--base-url <url>] [--session-token <token>] [--api-key <key>] [--port <port>]" >&2
    exit 1
fi

# Load defaults from .NET user-secrets if not provided
if [[ -z "$BASE_URL" || -z "$SESSION_TOKEN" || -z "$API_KEY" ]]; then
    SECRETS=$(dotnet user-secrets list --project "$SRC_DIR" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>/dev/null || true)
    if [[ -n "$SECRETS" ]]; then
        if [[ -z "$BASE_URL" ]]; then
            BASE_URL=$(echo "$SECRETS" | grep '^Tripletex:BaseUrl' | sed 's/^[^=]*= *//' || true)
        fi
        if [[ -z "$SESSION_TOKEN" ]]; then
            SESSION_TOKEN=$(echo "$SECRETS" | grep '^Tripletex:SessionToken' | sed 's/^[^=]*= *//' || true)
        fi
        if [[ -z "$API_KEY" ]]; then
            API_KEY=$(echo "$SECRETS" | grep '^ApiKey' | sed 's/^[^=]*= *//' || true)
        fi
    fi
fi

# Validate
if [[ -z "$BASE_URL" ]]; then echo "ERROR: BaseUrl not set. Pass --base-url or configure Tripletex:BaseUrl in user-secrets." >&2; exit 1; fi
if [[ -z "$SESSION_TOKEN" ]]; then echo "ERROR: SessionToken not set. Pass --session-token or configure Tripletex:SessionToken in user-secrets." >&2; exit 1; fi

# Build JSON body using jq
if ! command -v jq &>/dev/null; then
    echo "ERROR: jq is required but not installed. Install with: brew install jq" >&2
    exit 1
fi

# Start with base body
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

# Add file attachments if any
if [[ ${#FILES[@]} -gt 0 ]]; then
    FILES_JSON="[]"
    for fp in "${FILES[@]}"; do
        if [[ ! -f "$fp" ]]; then
            echo "ERROR: File not found: $fp" >&2
            exit 1
        fi
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
        B64=$(base64 < "$fp")
        FILES_JSON=$(echo "$FILES_JSON" | jq \
            --arg filename "$FILENAME" \
            --arg content_base64 "$B64" \
            --arg mime_type "$MIME" \
            '. + [{filename: $filename, content_base64: $content_base64, mime_type: $mime_type}]')
    done
    BODY=$(echo "$BODY" | jq --argjson files "$FILES_JSON" '. + {files: $files}')
fi

URL="http://localhost:$PORT/solve"

# Send request
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

if [[ "$HTTP_CODE" =~ ^2 ]]; then
    echo -e "\033[32mOK ($HTTP_CODE): $RESPONSE\033[0m"
else
    echo -e "\033[31mERR ($HTTP_CODE): $RESPONSE\033[0m"
fi

# Show latest log tail
LOG_DIR="$SRC_DIR/logs"
if [[ -d "$LOG_DIR" ]]; then
    LATEST=$(ls -t "$LOG_DIR"/*.log 2>/dev/null | head -1)
    if [[ -n "$LATEST" ]]; then
        echo -e "\n\033[36m--- Log tail ---\033[0m"
        tail -10 "$LATEST"
    fi
fi
