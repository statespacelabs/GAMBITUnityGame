#!/usr/bin/env bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VENV="$PROJECT_ROOT/.venv-mlagents20"
PYTHON="$VENV/bin/python"
TRAINER="$VENV/bin/mlagents-learn"

if ! command -v uv >/dev/null 2>&1; then
  echo "ERROR: uv is required. Install uv, then rerun this script." >&2
  exit 1
fi

if [[ ! -x "$PYTHON" ]]; then
  uv venv --python 3.10 "$VENV"
fi

if [[ ! -x "$TRAINER" ]]; then
  uv pip install --python "$PYTHON" \
    "mlagents==0.30.0" \
    "mlagents-envs==0.30.0" \
    "protobuf==3.20.3" \
    "tensorboard==2.11.2" \
    "setuptools<81" \
    "six==1.17.0"
fi

"$TRAINER" --help >/dev/null
echo "local45 PPO environment ready: $VENV"
