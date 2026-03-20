#!/usr/bin/env bash
# Starts a cloudflared tunnel to expose the agent via HTTPS.
# Usage:
#   ./scripts/start-cloudflared.sh           # start tunnel
#   ./scripts/start-cloudflared.sh --kill    # stop cloudflared
#   ./scripts/start-cloudflared.sh --port 5001

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
    PIDS=$(pgrep -f "cloudflared" 2>/dev/null || true)
    if [[ -n "$PIDS" ]]; then
        echo "$PIDS" | xargs kill 2>/dev/null || true
        sleep 1
        REMAINING=$(pgrep -f "cloudflared" 2>/dev/null || true)
        if [[ -n "$REMAINING" ]]; then
            echo "$REMAINING" | xargs kill -9 2>/dev/null || true
        fi
        echo -e "\033[33mcloudflared stopped.\033[0m"
    else
        echo -e "\033[90mcloudflared is not running.\033[0m"
    fi
    exit 0
fi

# Ensure agent is running
if ! pgrep -f "TripletexAgent" > /dev/null 2>&1; then
    echo -e "\033[33mWARNING: TripletexAgent is not running. Start it first with ./scripts/start-agent.sh\033[0m"
fi

# Kill existing cloudflared if already running
EXISTING=$(pgrep -f "cloudflared" 2>/dev/null || true)
if [[ -n "$EXISTING" ]]; then
    echo -e "\033[33mStopping existing cloudflared process...\033[0m"
    echo "$EXISTING" | xargs kill 2>/dev/null || true
    sleep 1
    REMAINING=$(pgrep -f "cloudflared" 2>/dev/null || true)
    if [[ -n "$REMAINING" ]]; then
        echo "$REMAINING" | xargs kill -9 2>/dev/null || true
    fi
    sleep 1
fi

# Ensure logs directory exists
LOG_DIR="$SRC_DIR/logs"
mkdir -p "$LOG_DIR"
LOG_FILE="$LOG_DIR/cloudflared.log"

# Find cloudflared
CF_EXE=$(command -v cloudflared 2>/dev/null || true)
if [[ -z "$CF_EXE" ]]; then
    echo -e "\033[31mERROR: cloudflared not found. Install with: brew install cloudflared\033[0m"
    exit 1
fi

# Start cloudflared — stderr has the URL
echo -e "\033[36mStarting cloudflared tunnel to http://localhost:$PORT ...\033[0m"
"$CF_EXE" tunnel --url "http://localhost:$PORT" --no-autoupdate 2>"$LOG_FILE" &

# Wait for the URL to appear in the log
MAX_WAIT=20
WAITED=0
TUNNEL_URL=""
while [[ $WAITED -lt $MAX_WAIT ]]; do
    sleep 1
    WAITED=$((WAITED + 1))
    if [[ -f "$LOG_FILE" ]]; then
        TUNNEL_URL=$(grep -oE 'https://[a-z0-9-]+\.trycloudflare\.com' "$LOG_FILE" 2>/dev/null | head -1 || true)
        if [[ -n "$TUNNEL_URL" ]]; then
            break
        fi
    fi
done

if [[ -z "$TUNNEL_URL" ]]; then
    echo -e "\033[31mERROR: Could not get tunnel URL after ${MAX_WAIT}s.\033[0m"
    echo -e "\033[90mCheck log: $LOG_FILE\033[0m"
    exit 1
fi

echo ""
echo -e "\033[32m========================================\033[0m"
echo -e "\033[32m cloudflared tunnel is running!\033[0m"
echo -e "\033[32m========================================\033[0m"
echo ""
echo -n "  Endpoint URL:  "; echo -e "\033[33m${TUNNEL_URL}/solve\033[0m"
echo ""
echo -e "  Submit at:     \033[36mhttps://app.ainm.no/submit/tripletex\033[0m"
echo ""
echo -e "  Stop with:     \033[90m./scripts/start-cloudflared.sh --kill\033[0m"
