#!/usr/bin/env bash
set -euo pipefail

DEMO_ROOT="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$DEMO_ROOT/.." && pwd)"
TRAINER="$PROJECT_ROOT/.venv-mlagents20/bin/mlagents-learn"
RESULTS="$DEMO_ROOT/results"

case "${1:-}" in
  1|local45)
    NAME="local45"
    CONFIG="$DEMO_ROOT/configs/01_local45_ppo_1000.yaml"
    LAUNCH="$DEMO_ROOT/launch/01_local45.training.json"
    ;;
  2|local45-231|local45_actor231)
    NAME="local45-actor231"
    CONFIG="$DEMO_ROOT/configs/02_local45_actor231_ppo_1000.yaml"
    LAUNCH="$DEMO_ROOT/launch/02_local45_actor231.training.json"
    ;;
  3|gen3|gen3-unified)
    NAME="gen3-unified"
    CONFIG="$DEMO_ROOT/configs/03_gen3_unified_ppo_1000.yaml"
    LAUNCH="$DEMO_ROOT/launch/03_gen3_unified.training.json"
    ;;
  *)
    echo "Usage: demo/run_editor.sh {1|2|3}"
    echo "  1  local45 standalone"
    echo "  2  local45 + frozen actor231 navigator"
    echo "  3  Gen3 unified full policy"
    exit 2
    ;;
esac

if [[ ! -x "$TRAINER" ]]; then
  "$PROJECT_ROOT/Tools/setup_local45_ppo.sh"
fi

mkdir -p "$RESULTS"
RUN_ID="presentation-${NAME}-$(date +%Y%m%d-%H%M%S)"
export GAMBIT_LAUNCH_CONFIG="$LAUNCH"

echo "Experiment: $NAME"
echo "PPO steps: 1000"
echo "Run ID: $RUN_ID"
echo ""
echo "In Unity choose GAMBIT > Training > Start Training..."
echo "Then select:"
echo "  $LAUNCH"
echo ""
echo "Waiting for Unity..."

exec "$TRAINER" "$CONFIG" \
  --run-id="$RUN_ID" \
  --results-dir="$RESULTS" \
  "${@:2}"
