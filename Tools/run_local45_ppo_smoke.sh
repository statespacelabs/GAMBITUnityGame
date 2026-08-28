#!/usr/bin/env bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TRAINER="$PROJECT_ROOT/.venv-mlagents20/bin/mlagents-learn"
CONFIG="$PROJECT_ROOT/Training/local45_ppo_smoke.yaml"
LAUNCH_CONFIG="$PROJECT_ROOT/Training/local45_vs_scripted.training.json"
RESULTS="$PROJECT_ROOT/Training/results"
RUN_ID="${GAMBIT_PPO_RUN_ID:-local45-ppo-smoke-001}"

if [[ ! -x "$TRAINER" ]]; then
  "$PROJECT_ROOT/Tools/setup_local45_ppo.sh"
fi

export GAMBIT_LAUNCH_CONFIG="$LAUNCH_CONFIG"
mkdir -p "$RESULTS"

echo "Starting local45 PPO trainer: $RUN_ID"
echo "In Unity choose GAMBIT > Training > Start Training, then select:"
echo "  $LAUNCH_CONFIG"
exec "$TRAINER" "$CONFIG" \
  --run-id="$RUN_ID" \
  --results-dir="$RESULTS" \
  "$@"
