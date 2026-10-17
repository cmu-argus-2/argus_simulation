"""Generate the Argus.Contracts Python code once if generated/ is missing (needs grpcio-tools only)."""

from pathlib import Path
import subprocess
import sys

_ROOT = Path(__file__).resolve().parents[1]

if not (_ROOT / "generated" / "argus" / "basilisk" / "v1").is_dir():
    subprocess.run([sys.executable, str(_ROOT / "scripts" / "generate_protos.py")], check=True)
