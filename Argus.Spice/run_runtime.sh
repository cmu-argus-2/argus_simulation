#!/usr/bin/env bash
# Starts the persistent SPICE worker. First launch provisions its isolated Python
# environment; no scenario-generation process is required before the simulation.
set -euo pipefail
cd "$(dirname "$0")"

ARGUS_SPICE_PYTHON="${ARGUS_SPICE_PYTHON:-python3.12}"
if [ ! -x .venv/bin/python ]; then
    "$ARGUS_SPICE_PYTHON" -m venv .venv
fi
.venv/bin/python -m pip install --quiet --disable-pip-version-check -r requirements.txt

exec .venv/bin/python runtime_service.py "$@"
