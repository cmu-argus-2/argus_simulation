#!/usr/bin/env bash
# One command: create the venv if needed, install pinned deps, fetch/verify kernels,
# and regenerate the reference dataset. Extra args go to generate_reference.py
# (e.g. --check).
set -euo pipefail
cd "$(dirname "$0")"

PYTHON="${PYTHON:-python3.12}"
if [ ! -x .venv/bin/python ]; then
    "$PYTHON" -m venv .venv
fi
.venv/bin/python -m pip install --quiet --disable-pip-version-check -r requirements.txt
exec .venv/bin/python generate_reference.py "$@"
