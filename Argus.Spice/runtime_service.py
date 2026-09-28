#!/usr/bin/env python3
"""Persistent runtime SPICE worker for the Argus simulation core.

The service loads pinned NAIF kernels once, then reads tab-separated SAMPLE requests
from stdin and writes one response per request to stdout. It never reads or writes a
scenario trajectory or precomputed ephemeris dataset.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import ssl
import sys
import urllib.request
from pathlib import Path

import certifi
import spiceypy as spice


ROOT = Path(__file__).resolve().parent
DEFAULT_MANIFEST = ROOT / "kernels.json"
DEFAULT_KERNEL_DIR = ROOT / "kernels"
METERS_PER_KILOMETER = 1000.0
PROTOCOL = "argus.spice.runtime"
PROTOCOL_VERSION = "1"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--epoch", required=True, help="Simulation epoch in UTC ISO-8601 form.")
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--kernel-dir", type=Path, default=DEFAULT_KERNEL_DIR)
    args = parser.parse_args()

    try:
        kernels = ensure_kernels(args.manifest.resolve(), args.kernel_dir.resolve())
        spice.kclear()
        for kernel in kernels:
            spice.furnsh(str(kernel))
        epoch_et = float(spice.str2et(args.epoch))
    except Exception as error:  # startup errors belong on stderr; stdout is the protocol
        print(f"SPICE startup failed: {error}", file=sys.stderr, flush=True)
        return 1

    print(f"READY\t{PROTOCOL}\t{PROTOCOL_VERSION}\tJ2000\tITRF93", flush=True)
    try:
        for raw_line in sys.stdin:
            line = raw_line.strip()
            if not line:
                continue
            if line == "QUIT":
                return 0
            handle_request(line, epoch_et)
    finally:
        spice.kclear()
    return 0


def ensure_kernels(manifest_path: Path, kernel_dir: Path) -> list[Path]:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    kernel_dir.mkdir(parents=True, exist_ok=True)
    resolved: list[Path] = []

    for entry in manifest["kernels"]:
        path = kernel_dir / entry["file"]
        expected = entry["sha256"].lower()
        if not path.exists() or sha256(path) != expected:
            if path.exists():
                path.unlink()
            download(entry["urls"], path)
        actual = sha256(path)
        if actual != expected:
            path.unlink(missing_ok=True)
            raise RuntimeError(
                f"SHA-256 mismatch for {entry['file']}: expected {expected}, got {actual}"
            )
        resolved.append(path)

    return resolved


def download(urls: list[str], destination: Path) -> None:
    last_error: Exception | None = None
    temporary = destination.with_suffix(destination.suffix + ".download")
    for url in urls:
        try:
            print(f"Downloading pinned SPICE kernel {destination.name}", file=sys.stderr, flush=True)
            context = ssl.create_default_context(cafile=certifi.where())
            with urllib.request.urlopen(url, timeout=60, context=context) as response, temporary.open("wb") as output:
                while chunk := response.read(1024 * 1024):
                    output.write(chunk)
            temporary.replace(destination)
            return
        except Exception as error:
            last_error = error
            temporary.unlink(missing_ok=True)
    raise RuntimeError(f"Could not download {destination.name}: {last_error}")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        while chunk := source.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def handle_request(line: str, epoch_et: float) -> None:
    fields = line.split("\t")
    request_id = fields[1] if len(fields) > 1 else "?"
    try:
        if len(fields) != 3 or fields[0] != "SAMPLE":
            raise ValueError("expected SAMPLE<TAB>request-id<TAB>simulation-seconds")
        simulation_seconds = float(fields[2])
        if not math.isfinite(simulation_seconds) or simulation_seconds < 0.0:
            raise ValueError("simulation time must be finite and non-negative")

        et = epoch_et + simulation_seconds
        state_transform = spice.sxform("J2000", "ITRF93", et)
        rotation = state_transform[:3, :3].reshape(-1)
        rotation_rate = state_transform[3:, :3].reshape(-1)
        sun_km, _ = spice.spkpos("SUN", et, "J2000", "NONE", "EARTH")
        values = [et, *rotation, *rotation_rate, *(sun_km * METERS_PER_KILOMETER)]
        encoded = "\t".join(format(float(value), ".17g") for value in values)
        print(f"SAMPLE\t{request_id}\t{encoded}", flush=True)
    except Exception as error:
        message = str(error).replace("\t", " ").replace("\r", " ").replace("\n", " ")
        print(f"ERROR\t{request_id}\t{message}", flush=True)


if __name__ == "__main__":
    raise SystemExit(main())
