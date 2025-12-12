#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Ensure the integration tests use the in-memory transport only.
unset MASBT_TEST_USE_REAL_MQTT 2>/dev/null || true
unset MASBT_TEST_MQTT_HOST 2>/dev/null || true
unset MASBT_TEST_MQTT_PORT 2>/dev/null || true
unset MASBT_TEST_MQTT_USERNAME 2>/dev/null || true
unset MASBT_TEST_MQTT_PASSWORD 2>/dev/null || true

cd "$ROOT_DIR"
dotnet test MAS-BT.csproj "$@"
