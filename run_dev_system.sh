#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

launch_process() {
  local log_tag="$1"; shift
  local cmd=("$@")

  echo "[run_dev_system] starting ${log_tag}: ${cmd[*]}"
  "${cmd[@]}" &
  local pid=$!
  echo "[run_dev_system] ${log_tag} PID=${pid}"
  echo "${pid}"
}

PIDS=()

cleanup() {
  echo "[run_dev_system] stopping..."
  for pid in "${PIDS[@]}"; do
    if kill -0 "$pid" 2>/dev/null; then
      kill -9 "$pid" 2>/dev/null || true
    fi
  done
  wait
}

trap cleanup INT TERM

# Start Dispatcher
PIDS+=("$(launch_process dispatching_agent dotnet run --project MAS-BT.csproj -- dispatching_agent)")

# Start P102
PIDS+=("$(launch_process P102 dotnet run --project MAS-BT.csproj -- P102)")

echo "[run_dev_system] waiting 5s before starting product_agent"
sleep 5

# Start product_agent
PIDS+=("$(launch_process product_agent dotnet run --project MAS-BT.csproj -- product_agent)")

echo "[run_dev_system] all processes launched. Press Ctrl+C to stop."
wait
