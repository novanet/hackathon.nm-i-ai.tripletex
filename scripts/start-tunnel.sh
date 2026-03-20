#!/usr/bin/env bash
# Starts an ngrok tunnel to expose the agent via HTTPS.
# Usage:
#   ./scripts/start-tunnel.sh           # start ngrok tunnel
#   ./scripts/start-tunnel.sh --kill    # stop ngrok
#   ./scripts/start-tunnel.sh --port 5001

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SRC_DIR="$SCRIPT_DIR/../src"

PORT=5000
KILL=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --kill|-k)  KILL=true; shift ;;
        --port|-p)  PORT="$2"; shift 2 ;;
        *)          echo "Unknown option: $1"; exit 1 ;;
    esac
done

# --- Kill mode ---
if $KILL; then
    PIDS=$(pgrep -f "ngrok" 2>/dev/null || true)
    if [[ -n "$PIDS" ]]; then
        echo "$PIDS" | xargs kill 2>/dev/null || true
        sleep 1
        REMAINING=$(pgrep -f "ngrok" 2>/dev/null || true)
        if [[ -n "$REMAINING" ]]; then
            echo "$REMAINING" | xargs kill -9 2>/dev/null || true
        fi
        echo -e "\033[33mngrok stopped.\033[0m"
    else
        echo -e "\033[90mngrok is not running.\033[0m"
    fi
    exit 0
fi

# Ensure agent is running
if ! pgrep -f "TripletexAgent" > /dev/null 2>&1; then
    echo -e "\033[33mWARNING: TripletexAgent is not running. Start it first with ./scripts/start-agent.sh\033[0m"
fi

# Kill existing ngrok if already running
EXISTING=$(pgrep -f "ngrok" 2>/dev/null || true)
if [[ -n "$EXISTING" ]]; then
    echo -e "\033[33mStopping existing ngrok process...\033[0m"
    echo "$EXISTING" | xargs kill 2>/dev/null || true
    sleep 1
    REMAINING=$(pgrep -f "ngrok" 2>/dev/null || true)
    if [[ -n "$REMAINING" ]]; then
        echo "$REMAINING" | xargs kill -9 2>/dev/null || true
    fi
    sleep 1
fi

# Start ngrok in background
echo -e "\033[36mStarting ngrok tunnel to http://localhost:$PORT ...\033[0m"
ngrok http "http://localhost:$PORT" > /dev/null 2>&1 &

# Wait for ngrok API to become available
MAX_WAIT=10
WAITED=0
TUNNEL_URL=""
while [[ $WAITED -lt $MAX_WAIT ]]; do
    sleep 1
    WAITED=$((WAITED + 1))
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
        break
    fi
done

if [[ -z "$TUNNEL_URL" ]]; then
    echo -e "\033[31mERROR: Could not get tunnel URL from ngrok API after ${MAX_WAIT}s.\033[0m"
    echo -e "\033[90mCheck if ngrok is running: pgrep ngrok\033[0m"
    exit 1
fi

# Read API key from user-secrets
API_KEY=""
SECRETS_OUTPUT=$(dotnet user-secrets list --project "$SRC_DIR" --id "54b40cce-1f78-4e18-ab1b-c1501ef7f7da" 2>/dev/null || true)
if [[ -n "$SECRETS_OUTPUT" ]]; then
    API_KEY=$(echo "$SECRETS_OUTPUT" | grep -E '^ApiKey\s*=' | sed 's/^.*=\s*//' | xargs)
fi

echo ""
echo -e "\033[32m========================================\033[0m"
echo -e "\033[32m ngrok tunnel is running!\033[0m"
echo -e "\033[32m========================================\033[0m"
echo ""
echo -n "  Endpoint URL:  "; echo -e "\033[33m${TUNNEL_URL}/solve\033[0m"
if [[ -n "$API_KEY" ]]; then
    echo -n "  API Key:       "; echo -e "\033[33m$API_KEY\033[0m"
else
    echo -n "  API Key:       "; echo -e "\033[31m(not found in user-secrets)\033[0m"
fi
echo ""
echo -e "  Submit at:     \033[36mhttps://app.ainm.no/submit/tripletex\033[0m"
