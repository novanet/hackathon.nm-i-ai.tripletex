#!/usr/bin/env bash
# Start-Cloudflared.sh — Bash equivalent of Start-Cloudflared.ps1
#
# Starts a cloudflared quick tunnel (no account needed) to expose localhost:5000.
#
# Usage:
#   ./scripts/Start-Cloudflared.sh              # start tunnel on port 5000
#   ./scripts/Start-Cloudflared.sh --port 8080  # custom port
#   ./scripts/Start-Cloudflared.sh --kill       # stop running cloudflared

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOGS_DIR="$SCRIPT_DIR/../src/logs"
LOG_FILE="$LOGS_DIR/cloudflared.log"

# --- Colors ---
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
CYAN='\033[0;36m'
GRAY='\033[0;90m'
NC='\033[0m'

# --- Defaults ---
PORT=5000
KILL=false

# --- Argument parsing ---
while [[ $# -gt 0 ]]; do
  case "$1" in
    --port|-p)
      PORT="$2"; shift 2 ;;
    --kill|-k)
      KILL=true; shift ;;
    *)
      echo -e "${RED}Unknown option: $1${NC}" >&2; exit 1 ;;
  esac
done

# --- Kill mode ---
if $KILL; then
  CF_PIDS=$(pgrep -f 'cloudflared' 2>/dev/null || true)
  if [[ -n "$CF_PIDS" ]]; then
    echo "$CF_PIDS" | xargs kill 2>/dev/null || true
    echo -e "${YELLOW}cloudflared stopped.${NC}"
  else
    echo -e "${GRAY}cloudflared is not running.${NC}"
  fi
  exit 0
fi

# --- Check agent is running ---
AGENT_PID=$(pgrep -f 'TripletexAgent' 2>/dev/null || true)
if [[ -z "$AGENT_PID" ]]; then
  echo -e "${YELLOW}WARNING: TripletexAgent is not running. Start it first with ./scripts/Start-Agent.sh${NC}"
fi

# --- Kill existing cloudflared ---
EXISTING=$(pgrep -f 'cloudflared' 2>/dev/null || true)
if [[ -n "$EXISTING" ]]; then
  echo -e "${YELLOW}Stopping existing cloudflared process...${NC}"
  echo "$EXISTING" | xargs kill 2>/dev/null || true
  sleep 1
fi

# --- Find cloudflared ---
CF_EXE=$(command -v cloudflared 2>/dev/null || true)
if [[ -z "$CF_EXE" ]]; then
  echo -e "${RED}ERROR: cloudflared not found. Install with: brew install cloudflared${NC}"
  exit 1
fi

# --- Start cloudflared ---
echo -e "${CYAN}Starting cloudflared tunnel to http://localhost:$PORT ...${NC}"

mkdir -p "$LOGS_DIR"
nohup "$CF_EXE" tunnel --url "http://localhost:$PORT" --no-autoupdate \
  > /dev/null 2> "$LOG_FILE" &

# --- Wait for URL ---
MAX_WAIT=20
WAITED=0
TUNNEL_URL=""

while [[ $WAITED -lt $MAX_WAIT ]]; do
  sleep 1
  WAITED=$((WAITED + 1))
  if [[ -f "$LOG_FILE" ]]; then
    TUNNEL_URL=$(grep -oE 'https://[a-z0-9-]+\.trycloudflare\.com' "$LOG_FILE" | tail -1 || true)
    if [[ -n "$TUNNEL_URL" ]]; then
      break
    fi
  fi
done

if [[ -z "$TUNNEL_URL" ]]; then
  echo -e "${RED}ERROR: Could not get tunnel URL after ${MAX_WAIT}s.${NC}"
  echo -e "${GRAY}Check log: $LOG_FILE${NC}"
  exit 1
fi

echo ""
echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN} cloudflared tunnel is running!${NC}"
echo -e "${GREEN}========================================${NC}"
echo ""
echo -e "  Endpoint URL:  ${YELLOW}$TUNNEL_URL/solve${NC}"
echo ""
echo -e "  Submit at:     ${CYAN}https://app.ainm.no/submit/tripletex${NC}"
echo ""
echo -e "  Stop with:     ${GRAY}./scripts/Start-Cloudflared.sh --kill${NC}"
