"""Generate the Python gRPC code for Argus.Contracts into generated/ (gitignored)."""

import shutil
import sys
from importlib import resources
from pathlib import Path

from grpc_tools import protoc

ROOT = Path(__file__).resolve().parents[1]
PROTO_ROOT = ROOT.parent / "Argus.Contracts" / "proto"
OUT = ROOT / "generated"


def main() -> int:
    protos = sorted(str(path) for path in PROTO_ROOT.glob("argus/**/*.proto"))
    if not protos:
        raise SystemExit(f"No .proto files under {PROTO_ROOT}")

    shutil.rmtree(OUT, ignore_errors=True)
    OUT.mkdir(parents=True)
    well_known_types = str(resources.files("grpc_tools") / "_proto")
    return protoc.main([
        "grpc_tools.protoc",
        f"-I{PROTO_ROOT}",
        f"-I{well_known_types}",
        f"--python_out={OUT}",
        f"--pyi_out={OUT}",
        f"--grpc_python_out={OUT}",
        *protos,
    ])


if __name__ == "__main__":
    sys.exit(main())
