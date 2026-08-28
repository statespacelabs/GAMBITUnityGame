#!/usr/bin/env bash
set -euo pipefail

DEMO_ROOT="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$DEMO_ROOT/.." && pwd)"
TRAINER="$PROJECT_ROOT/.venv-mlagents20/bin/mlagents-learn"
RESULTS="$DEMO_ROOT/results"
DEFAULT_APP="$PROJECT_ROOT/Builds/macOS-Universal/GAMBIT Demo.app"
UNITY_APP="${1:-$DEFAULT_APP}"

if [[ ! -x "$TRAINER" ]]; then
  "$PROJECT_ROOT/Tools/setup_local45_ppo.sh"
fi
if [[ ! -d "$UNITY_APP" ]]; then
  echo "Unity player not found: $UNITY_APP"
  echo "Build it with GAMBIT > Build > macOS Native (Universal),"
  echo "or pass the .app path as the first argument."
  exit 2
fi

mkdir -p "$RESULTS"
RUN_GROUP="$(date +%Y%m%d-%H%M%S)"

run_one() {
  local name="$1"
  local config="$2"
  local launch="$3"
  local base_port="$4"
  local run_id="presentation-${name}-${RUN_GROUP}"

  echo ""
  echo "============================================================"
  echo "Running $name: 1000 PPO steps"
  echo "Run ID: $run_id"
  echo "============================================================"

  GAMBIT_LAUNCH_CONFIG="$launch" \
    "$TRAINER" "$config" \
      --run-id="$run_id" \
      --results-dir="$RESULTS" \
      --env="$UNITY_APP" \
      --base-port="$base_port" \
      --no-graphics
}

run_one \
  "local45" \
  "$DEMO_ROOT/configs/01_local45_ppo_1000.yaml" \
  "$DEMO_ROOT/launch/01_local45.training.json" \
  5010

run_one \
  "local45-actor231" \
  "$DEMO_ROOT/configs/02_local45_actor231_ppo_1000.yaml" \
  "$DEMO_ROOT/launch/02_local45_actor231.training.json" \
  5020

run_one \
  "gen3-unified" \
  "$DEMO_ROOT/configs/03_gen3_unified_ppo_1000.yaml" \
  "$DEMO_ROOT/launch/03_gen3_unified.training.json" \
  5030

echo ""
echo "All three 1000-step PPO demonstrations completed."
echo "Results: $RESULTS"
