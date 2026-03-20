#!/usr/bin/env bash
# Stops any running TripletexAgent and starts a fresh instance.
# Usage:
#   ./scripts/start-agent.sh              # foreground (blocks terminal)
#   ./scripts/start-agent.sh --background # background (returns immediately)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT="$SCRIPT_DIR/../src/TripletexAgent.csproj"

BACKGROUND=false
if [[ "${1:-}" == "--background" || "${1:-}" == "-b" ]]; then
    BACKGROUND=true
fi

# Kill existing instances
PIDS=$(pgrep -f "TripletexAgent" 2>/dev/null || true)
if [[ -n "$PIDS" ]]; then
    echo -e "\033[33mKilling TripletexAgent PIDs: $PIDS\033[0m"
    echo "$PIDS" | xargs kill 2>/dev/null || true
    sleep 1
    # Force-kill any survivors
    REMAINING=$(pgrep -f "TripletexAgent" 2>/dev/null || true)
    if [[ -n "$REMAINING" ]]; then
        echo "$REMAINING" | xargs kill -9 2>/dev/null || true
    fi
    sleep 1
fi

# Build first so startup is fast and errors surface early
echo -e "\033[36mBuilding...\033[0m"
if ! dotnet build "$PROJECT" --nologo -v q; then
    echo -e "\033[31mBuild failed.\033[0m"
    exit 1
fi

echo -e "\033[36mStarting TripletexAgent...\033[0m"

if $BACKGROUND; then
    RESOLVED="$(cd "$(dirname "$PROJECT")" && pwd)/$(basename "$PROJECT")"
    nohup dotnet run --project "$RESOLVED" --no-build > /dev/null 2>&1 &

    # Poll for the process to appear (up to 10 seconds)
    FOUND=false
    for i in $(seq 1 10); do
        sleep 1
        NEW_PID=$(pgrep -f "TripletexAgent" 2>/dev/null || true)
        if [[ -n "$NEW_PID" ]]; then
            echo -e "\033[32mRunning (PID $NEW_PID)\033[0m"
            FOUND=true
            break
        fi
    done
    if ! $FOUND; then
        echo -e "\033[31mProcess did not appear within 10s — check logs.\033[0m"
        exit 1
    fi
else
    dotnet run --project "$PROJECT" --no-build
fi
