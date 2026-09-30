"""Imports the generated Argus.Contracts code from generated/."""

import sys
from pathlib import Path

_GENERATED = Path(__file__).resolve().parents[1] / "generated"

if not (_GENERATED / "argus" / "basilisk" / "v1").is_dir():
    raise ImportError(
        f"Generated contracts missing in {_GENERATED}; run: python scripts/generate_protos.py")

sys.path.insert(0, str(_GENERATED))

from argus.basilisk.v1 import basilisk_service_pb2 as service_pb  # noqa: E402
from argus.basilisk.v1 import basilisk_service_pb2_grpc as service_grpc  # noqa: E402

__all__ = ["service_pb", "service_grpc"]
