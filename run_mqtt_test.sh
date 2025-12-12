#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

HOST="${1:-${MASBT_TEST_MQTT_HOST:-localhost}}"
PORT="${2:-${MASBT_TEST_MQTT_PORT:-1883}}"
USERNAME="${3:-${MASBT_TEST_MQTT_USERNAME:-}}"
PASSWORD="${4:-${MASBT_TEST_MQTT_PASSWORD:-}}"

export MASBT_TEST_USE_REAL_MQTT=true
export MASBT_TEST_MQTT_HOST="$HOST"
export MASBT_TEST_MQTT_PORT="$PORT"

if [[ -n "$USERNAME" ]]; then
  export MASBT_TEST_MQTT_USERNAME="$USERNAME"
else
  unset MASBT_TEST_MQTT_USERNAME 2>/dev/null || true
fi

if [[ -n "$PASSWORD" ]]; then
  export MASBT_TEST_MQTT_PASSWORD="$PASSWORD"
else
  unset MASBT_TEST_MQTT_PASSWORD 2>/dev/null || true
fi

cd "$ROOT_DIR"
dotnet test MAS-BT.csproj "${@:5}"
