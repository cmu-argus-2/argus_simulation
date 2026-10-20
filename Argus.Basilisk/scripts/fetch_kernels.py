"""Fetch and verify the files of a kernel set into kernels/ (README §8).

Usage:
    python scripts/fetch_kernels.py <kernel_set_id>

Downloads every missing file of kernel_sets/<kernel_set_id>.json (kernels and support_files),
trying each entry's URLs in order, and keeps a download only if its SHA-256 matches the
manifest. Files already present are hashed, never replaced. Then checks with spiceypy that the
SPK and binary PCK cover coverage_utc. Run it before starting the service; the service itself
never downloads.

This is a tool, not a SPICE source for runs (D3): argus_basilisk never imports it or spiceypy.
"""

import argparse
import hashlib
import json
import os
import re
import sys
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

# Same rules as argus_basilisk/kernels.py, so the tool accepts exactly the sets the service does.
KERNEL_SET_ID_PATTERN = r"[a-z0-9][a-z0-9._-]{0,63}"
SHA256_PATTERN = r"[0-9a-fA-F]{64}"

DOWNLOAD_TIMEOUT_SECONDS = 120
CHUNK_BYTES = 1 << 20

SUN_ID = 10
EARTH_ID = 399
ITRF93_FRAME_CLASS_ID = 3000
# NAIF's Earth binary PCKs state where measured EOP data ends in their comment area.
LAST_DATUM_PATTERN = re.compile(r"UTC Epoch of last datum:\s*(.+?)\s*UTC\s*$")


class FetchError(Exception):
    """A failure the tool reports and exits non-zero for."""


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("kernel_set_id", help="manifest name in kernel_sets/, without .json")
    parser.add_argument(
        "--data-dir",
        type=Path,
        default=ROOT,
        help="directory holding kernel_sets/ and kernels/ (default: Argus.Basilisk/)",
    )
    args = parser.parse_args(argv)
    # Keep progress lines and stderr errors in order when output is piped.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(line_buffering=True)

    try:
        manifest = load_manifest(args.data_dir, args.kernel_set_id)
    except FetchError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1

    kernels_dir = args.data_dir / "kernels"
    try:
        kernels_dir.mkdir(parents=True, exist_ok=True)
        # Resolved, as kernels.py does, so fetch_file can apply the same inside-kernels/ rule.
        kernels_dir = kernels_dir.resolve(strict=True)
    except (OSError, RuntimeError) as error:
        print(f"error: {kernels_dir}: cannot use kernels directory: {error}", file=sys.stderr)
        return 1

    failed = False
    for entry in manifest["kernels"] + manifest.get("support_files", []):
        try:
            print(f"{entry['file']}: {fetch_file(entry, kernels_dir)}")
        except FetchError as error:
            print(f"error: {error}", file=sys.stderr)
            failed = True
    if failed:
        return 1

    try:
        for line in check_coverage(manifest, kernels_dir):
            print(line)
    except FetchError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


# --- Manifest ----------------------------------------------------------------------------


def load_manifest(data_dir: Path, kernel_set_id: str) -> dict:
    if not re.fullmatch(KERNEL_SET_ID_PATTERN, kernel_set_id):
        raise FetchError(f"invalid kernel_set_id {kernel_set_id!r}")

    path = data_dir / "kernel_sets" / f"{kernel_set_id}.json"
    try:
        manifest = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        raise FetchError(f"kernel set {kernel_set_id!r} not found: {path}") from None
    except (OSError, ValueError) as error:
        raise FetchError(f"{path}: cannot read manifest: {error}") from None

    if not isinstance(manifest, dict) or manifest.get("id") != kernel_set_id:
        raise FetchError(f"{path}: manifest id must equal {kernel_set_id!r}")
    if not isinstance(manifest.get("kernels"), list) or not manifest["kernels"]:
        raise FetchError(f"{path}: kernels must be a non-empty list")
    if not isinstance(manifest.get("support_files", []), list):
        raise FetchError(f"{path}: support_files must be a list")

    names = set()
    for section in ("kernels", "support_files"):
        for index, entry in enumerate(manifest.get(section, [])):
            field = f"{path}: {section}[{index}]"
            check_entry(entry, field)
            if entry["file"] in names:
                raise FetchError(f"{field}.file: duplicate filename {entry['file']!r}")
            names.add(entry["file"])
    return manifest


def check_entry(entry: object, field: str) -> None:
    if not isinstance(entry, dict):
        raise FetchError(f"{field}: expected an object")
    name = entry.get("file")
    if (
        not isinstance(name, str)
        or name in ("", ".", "..")
        or any(character in name for character in "/\\\0")
    ):
        raise FetchError(f"{field}.file: expected a local filename, got {name!r}")
    urls = entry.get("urls")
    if not isinstance(urls, list) or not urls or not all(isinstance(url, str) for url in urls):
        raise FetchError(f"{field}.urls: expected a non-empty list of URLs")
    sha256 = entry.get("sha256")
    if not isinstance(sha256, str) or not re.fullmatch(SHA256_PATTERN, sha256):
        raise FetchError(f"{field}.sha256: expected 64 hexadecimal characters")


# --- Fetch and verify --------------------------------------------------------------------


def fetch_file(entry: dict, kernels_dir: Path) -> str:
    """Make kernels_dir/<file> a verified copy and return what was done."""
    path = kernels_dir / entry["file"]
    expected = entry["sha256"].lower()

    if path.exists():
        # kernels.py rejects a file that resolves outside kernels/, so the tool must too.
        if path.resolve().parent != kernels_dir:
            raise FetchError(f"{path}: resolves outside kernels directory {kernels_dir}")
        if not path.is_file():
            raise FetchError(f"{path}: exists but is not a regular file")
        actual = sha256_file(path)
        if actual != expected:
            raise FetchError(
                f"{path}: SHA-256 mismatch (expected {expected}, got {actual}); "
                "delete it to download it again"
            )
        return "present, SHA-256 verified"

    # Download next to the target and rename only after the hash matches, so a wrong or
    # partial file is never left under the real name.
    partial = path.with_name(path.name + ".part")
    attempts = []
    try:
        for url in entry["urls"]:
            try:
                actual = download(url, partial)
            except (OSError, ValueError) as error:
                attempts.append(f"{url}: {error}")
                continue
            if actual == expected:
                os.replace(partial, path)
                return f"downloaded from {url}, SHA-256 verified"
            attempts.append(f"{url}: SHA-256 mismatch (expected {expected}, got {actual})")
    finally:
        partial.unlink(missing_ok=True)

    raise FetchError(f"{path}: no URL gave the pinned file:\n  " + "\n  ".join(attempts))


def download(url: str, destination: Path) -> str:
    """Stream url into destination and return the SHA-256 of what was written."""
    print(f"Downloading {url}")
    digest = hashlib.sha256()
    with urllib.request.urlopen(url, timeout=DOWNLOAD_TIMEOUT_SECONDS) as response, open(
        destination, "wb"
    ) as out:
        while chunk := response.read(CHUNK_BYTES):
            digest.update(chunk)
            out.write(chunk)
    return digest.hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        while chunk := stream.read(CHUNK_BYTES):
            digest.update(chunk)
    return digest.hexdigest()


# --- Coverage ----------------------------------------------------------------------------


def check_coverage(manifest: dict, kernels_dir: Path) -> list[str]:
    """Check that the SPK and binary PCK cover coverage_utc, and return a report."""
    try:
        import spiceypy as spice
        from spiceypy.utils.exceptions import SpiceyError
    except ImportError:
        raise FetchError("the coverage check needs spiceypy: pip install -e \".[tools]\"") from None

    lsk = kernel_of_type(manifest, "LSK", kernels_dir)
    spk = kernel_of_type(manifest, "SPK", kernels_dir)
    pck = kernel_of_type(manifest, "binary PCK", kernels_dir)
    coverage = manifest.get("coverage_utc")
    if not isinstance(coverage, dict):
        raise FetchError("manifest has no coverage_utc object")

    spice.kclear()
    try:
        spice.furnsh(str(lsk))
        start_et = utc_to_et(spice, coverage.get("start"), "start")
        end_et = utc_to_et(spice, coverage.get("end"), "end")
        reconstructed_et = utc_to_et(spice, coverage.get("reconstructed_end"), "reconstructed_end")

        windows = [
            (f"Sun (spkcov {SUN_ID}, {spk.name})", spice.spkcov(str(spk), SUN_ID)),
            (f"Earth (spkcov {EARTH_ID}, {spk.name})", spice.spkcov(str(spk), EARTH_ID)),
            (
                f"ITRF93 (pckcov {ITRF93_FRAME_CLASS_ID}, {pck.name})",
                spice.pckcov(str(pck), ITRF93_FRAME_CLASS_ID),
            ),
        ]
        report = [
            f"Coverage required by {manifest['id']}: "
            f"{format_et(spice, start_et)} .. {format_et(spice, end_et)}"
        ]
        gaps = []
        for name, window in windows:
            intervals = [spice.wnfetd(window, i) for i in range(spice.wncard(window))]
            text = ", ".join(f"{format_et(spice, a)} .. {format_et(spice, b)}" for a, b in intervals)
            covered = spice.wnincd(start_et, end_et, window)
            report.append(f"  {name}: {text or 'no data'}  {'OK' if covered else 'GAP'}")
            if not covered:
                gaps.append(name)

        last_datum = pck_last_datum(spice, pck)
        if last_datum is None:
            report.append(f"  ITRF93 reconstructed end ({pck.name} comments): not stated")
        else:
            last_datum_et = spice.str2et(last_datum)
            matches = abs(last_datum_et - reconstructed_et) < 1e-3
            report.append(
                f"  ITRF93 reconstructed end ({pck.name} comments): "
                f"{format_et(spice, last_datum_et)}  "
                f"{'OK' if matches else 'MISMATCH with coverage_utc.reconstructed_end'}"
            )
            if not matches:
                gaps.append("reconstructed_end")
    except SpiceyError as error:
        raise FetchError(f"SPICE could not read the kernels: {error.short} {error.long}") from None
    finally:
        spice.kclear()

    if gaps:
        raise FetchError("\n".join(report) + "\ncoverage_utc is not covered: " + ", ".join(gaps))
    return report


def kernel_of_type(manifest: dict, kernel_type: str, kernels_dir: Path) -> Path:
    matches = [entry["file"] for entry in manifest["kernels"] if entry.get("type") == kernel_type]
    if len(matches) != 1:
        raise FetchError(f"coverage check needs exactly one {kernel_type!r} kernel, found {matches}")
    return kernels_dir / matches[0]


def utc_to_et(spice, value: object, field: str) -> float:
    if not isinstance(value, str):
        raise FetchError(f"coverage_utc.{field}: expected an ISO-8601 UTC timestamp")
    try:
        moment = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise FetchError(f"coverage_utc.{field}: {error}") from None
    if moment.tzinfo is None:
        raise FetchError(f"coverage_utc.{field}: timezone is required")
    # SPICE reads an ISO string without a zone as UTC.
    return spice.str2et(moment.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f"))


def format_et(spice, et: float) -> str:
    return spice.et2utc(et, "ISOC", 3) + "Z"


def pck_last_datum(spice, pck: Path) -> str | None:
    """Return the 'UTC Epoch of last datum' from a NAIF Earth PCK's comments, if stated."""
    handle = spice.dafopr(str(pck))
    try:
        done = False
        while not done:
            _, lines, done = spice.dafec(handle, 100)
            for line in lines:
                match = LAST_DATUM_PATTERN.search(line)
                if match:
                    return match.group(1)
    finally:
        spice.dafcls(handle)
    return None


if __name__ == "__main__":
    sys.exit(main())
