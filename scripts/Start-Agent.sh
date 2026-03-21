#!/usr/bin/env bash
# Start-Agent.sh — Bash equivalent of Start-Agent.ps1
# Usage: bash scripts/Start-Agent.sh              # foreground
#        bash scripts/Start-Agent.sh --background  # background (returns immediately)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/../src/TripletexAgent.csproj"
BACKGROUND=false

for arg in "$@"; do
  case "$arg" in
    --background|-b) BACKGROUND=true ;;
  esac
done

# Kill existing instances
PIDS=$(pgrep -f 'TripletexAgent' 2>/dev/null || true)
if [[ -n "$PIDS" ]]; then
  echo -e "\033[33mKilling TripletexAgent PIDs: $PIDS\033[0m"
  echo "$PIDS" | xargs kill 2>/dev/null || true
  sleep 2
fi

# Build
echo -e "\033[36mBuilding...\033[0m"
if ! dotnet build "$PROJECT" --nologo -v q; then
  echo -e "\033[31mBuild failed.\033[0m"
  exit 1
fi

echo -e "\033[36mStarting TripletexAgent...\033[0m"

if $BACKGROUND; then
  nohup dotnet run --project "$PROJECT" --no-build > /dev/null 2>&1 &
  AGENT_PID=$!
  # Poll for process to be listening (up to 10s)
  for i in $(seq 1 10); do
    sleep 1
    if kill -0 $AGENT_PID 2>/dev/null; then
      echo -e "\033[32mRunning (PID $AGENT_PID)\033[0m"
      break
    fi
  done
else
  dotnet run --project "$PROJECT" --no-build
fi
