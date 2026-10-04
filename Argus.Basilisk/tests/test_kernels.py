"""Kernel startup checks use synthetic local files, never real SPICE data."""

from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import sys

import pytest

from argus_basilisk.kernels import KernelSetError, load_kernel_set


@pytest.fixture
def manifest(tmp_path):
    (tmp_path / "kernel_sets").mkdir()
    (tmp_path / "kernels").mkdir()
    entries = []
    for name in ("z.tls", "a.bsp", "WMM2025.COF"):
        content = name.encode()
        (tmp_path / "kernels" / name).write_bytes(content)
        entries.append({"file": name, "sha256": hashlib.sha256(content).hexdigest()})
    data = {
        "id": "test-set",
        "coverage_utc": {
            "start": "2025-01-01T00:00:00Z",
            "end": "2030-01-01T00:00:00Z",
            "reconstructed_end": "2026-09-25T02:00:00+02:00",
        },
        "kernels": entries[:2],
        "support_files": entries[2:],
    }
    return tmp_path, data


def write_manifest(root, data):
    path = root / "kernel_sets" / "test-set.json"
    path.write_text(json.dumps(data), encoding="utf-8")
    return path


def test_success_order_coverage_and_support(manifest):
    root, data = manifest
    write_manifest(root, data)
    result = load_kernel_set("test-set", data_dir=root)
    assert result.kernel_set_id == "test-set"
    assert result.kernel_paths == (root / "kernels/z.tls", root / "kernels/a.bsp")
    assert result.support_files == {"WMM2025.COF": root / "kernels/WMM2025.COF"}
    assert result.coverage_utc.start == datetime(2025, 1, 1, tzinfo=timezone.utc)
    assert result.coverage_utc.end == datetime(2030, 1, 1, tzinfo=timezone.utc)
    assert result.coverage_utc.reconstructed_end == datetime(2026, 9, 25, tzinfo=timezone.utc)
    with pytest.raises(TypeError):
        result.support_files["extra"] = root


def test_optional_support_files(manifest):
    root, data = manifest
    del data["support_files"]
    write_manifest(root, data)
    assert not load_kernel_set("test-set", data_dir=root).support_files


def test_unknown_set_names_manifest(tmp_path):
    with pytest.raises(KernelSetError) as error:
        load_kernel_set("unknown", data_dir=tmp_path)
    assert error.value.code == "NOT_FOUND"
    assert str(tmp_path / "kernel_sets/unknown.json") in error.value.details


@pytest.mark.parametrize("name", ["z.tls", "WMM2025.COF"])
@pytest.mark.parametrize("damage", ["missing", "altered", "directory"])
def test_bad_files_fail_with_filename(manifest, name, damage):
    root, data = manifest
    write_manifest(root, data)
    path = root / "kernels" / name
    if damage == "altered":
        path.write_bytes(b"changed")
    else:
        path.unlink()
        if damage == "directory":
            path.mkdir()
    with pytest.raises(KernelSetError) as error:
        load_kernel_set("test-set", data_dir=root)
    assert error.value.code == "FAILED_PRECONDITION"
    assert str(path) in error.value.details


@pytest.mark.parametrize("value", ["../escape", "/absolute", "", "Uppercase", "x" * 65])
def test_invalid_id(tmp_path, value):
    with pytest.raises(KernelSetError) as error:
        load_kernel_set(value, data_dir=tmp_path)
    assert error.value.code == "INVALID_ARGUMENT"


@pytest.mark.parametrize("value", ["not-time", "2025-01-01T00:00:00", None])
def test_invalid_coverage(manifest, value):
    root, data = manifest
    data["coverage_utc"]["start"] = value
    write_manifest(root, data)
    with pytest.raises(KernelSetError, match="coverage_utc.start") as error:
        load_kernel_set("test-set", data_dir=root)
    assert error.value.code == "FAILED_PRECONDITION"


def test_reversed_coverage(manifest):
    root, data = manifest
    data["coverage_utc"]["reconstructed_end"] = "2031-01-01T00:00:00Z"
    write_manifest(root, data)
    with pytest.raises(KernelSetError, match="start <= reconstructed_end <= end"):
        load_kernel_set("test-set", data_dir=root)


@pytest.mark.parametrize("mutation", ["id", "hash", "traversal", "duplicate", "overlap", "shape", "empty"])
def test_invalid_manifest(manifest, mutation):
    root, data = manifest
    if mutation == "id":
        data["id"] = "different"
    elif mutation == "hash":
        data["kernels"][0]["sha256"] = "invalid"
    elif mutation == "traversal":
        data["kernels"][0]["file"] = "../outside"
    elif mutation == "duplicate":
        data["kernels"].append(data["kernels"][0])
    elif mutation == "overlap":
        data["support_files"] = [data["kernels"][0]]
    elif mutation == "shape":
        data["support_files"] = {}
    else:
        data["kernels"] = []
    write_manifest(root, data)
    with pytest.raises(KernelSetError) as error:
        load_kernel_set("test-set", data_dir=root)
    assert error.value.code == "FAILED_PRECONDITION"


def test_corrupt_json(manifest):
    root, data = manifest
    path = write_manifest(root, data)
    path.write_text("{broken", encoding="utf-8")
    with pytest.raises(KernelSetError, match="cannot read manifest") as error:
        load_kernel_set("test-set", data_dir=root)
    assert error.value.code == "FAILED_PRECONDITION"


def test_symlink_outside_kernels(manifest):
    root, data = manifest
    outside = root / "outside"
    outside.write_bytes(b"z.tls")
    path = root / "kernels/z.tls"
    path.unlink()
    path.symlink_to(outside)
    write_manifest(root, data)
    with pytest.raises(KernelSetError, match="outside kernels directory"):
        load_kernel_set("test-set", data_dir=root)


def test_offline_and_basilisk_free_from_another_cwd(manifest):
    root, data = manifest
    write_manifest(root, data)
    package_root = Path(__file__).resolve().parents[1]
    # A fresh interpreter blocks Basilisk, gRPC, generated contracts and networking.
    script = '''
import sys
sys.path.insert(0, sys.argv[1])
def audit(event, args):
    if event.startswith("socket."):
        raise AssertionError("network attempted")
    if event == "import" and args[0].split(".")[0] in {"Basilisk", "bsk", "grpc", "generated"}:
        raise AssertionError("forbidden import: " + args[0])
sys.addaudithook(audit)
from argus_basilisk.kernels import load_kernel_set, KernelSetError
assert len(load_kernel_set("test-set", data_dir=sys.argv[2]).kernel_paths) == 2
# Default root must locate the checked-in manifest despite a different cwd.
try:
    load_kernel_set("argus-2026-09-25")
except KernelSetError as exc:
    assert exc.code == "FAILED_PRECONDITION", exc
'''
    subprocess.run([sys.executable, "-I", "-S", "-c", script, str(package_root), str(root)],
                   cwd=root, check=True, capture_output=True, text=True)
