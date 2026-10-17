"""Read and verify local kernel sets without importing Basilisk or downloading data."""

from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
from types import MappingProxyType
from typing import Mapping, NoReturn


class KernelSetError(Exception):
    """Startup error; the service maps ``code`` to ``grpc.StatusCode[code]``."""

    def __init__(self, code: str, details: str):
        self.code = code
        self.details = details
        super().__init__(details)


@dataclass(frozen=True)
class KernelCoverage:
    start: datetime
    end: datetime
    reconstructed_end: datetime


@dataclass(frozen=True)
class KernelSet:
    kernel_set_id: str
    kernel_paths: tuple[Path, ...]
    coverage_utc: KernelCoverage
    support_files: Mapping[str, Path]


def _fail(details: str) -> NoReturn:
    raise KernelSetError("FAILED_PRECONDITION", details)


def _parse_utc(value: object, field: str) -> datetime:
    if not isinstance(value, str):
        _fail(f"{field}: expected an ISO-8601 timestamp, got {value!r}")
    try:
        result = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if result.tzinfo is None or result.utcoffset() is None:
            raise ValueError("timezone is required")
        return result.astimezone(timezone.utc)
    except (ValueError, OverflowError) as exc:
        _fail(f"{field}: invalid timestamp {value!r}: {exc}")


def _verify_files(
    entries: object,
    directory: Path,
    field: str,
) -> dict[str, Path]:
    """Verify listed local files and return filename-to-path pairs in list order.

    Args:
        entries: Manifest entries, each containing file and sha256.
        directory: Absolute, resolved directory containing the data files.
        field: Manifest field path used to identify errors.
    """
    if not isinstance(entries, list):
        _fail(f"{field}: expected a list of file entries")

    verified_paths: dict[str, Path] = {}

    for index, entry in enumerate(entries):
        entry_field = f"{field}[{index}]"

        # 1. Check that the entry describes a single local filename.
        if not isinstance(entry, dict):
            _fail(f"{entry_field}: expected an object")

        filename = entry.get("file")
        if not isinstance(filename, str) or not filename:
            _fail(
                f"{entry_field}.file: expected a local filename, got {filename!r}"
            )

        if filename in (".", ".."):
            _fail(f"{entry_field}.file: expected a local filename, got {filename!r}")

        forbidden_characters = ("/", "\\", "\0")
        for character in forbidden_characters:
            if character in filename:
                _fail(
                    f"{entry_field}.file: expected a local filename, "
                    f"got {filename!r}"
                )

        if filename in verified_paths:
            _fail(f"{entry_field}.file: duplicate filename {filename!r}")

        # 2. Check the expected SHA-256 before reading the file.
        file_path = directory / filename
        expected_hash = entry.get("sha256")
        hash_pattern = r"[0-9a-fA-F]{64}"
        if (
            not isinstance(expected_hash, str)
            or not re.fullmatch(hash_pattern, expected_hash)
        ):
            _fail(
                f"{file_path}: {entry_field}.sha256 must contain "
                "64 hexadecimal characters"
            )
        expected_hash = expected_hash.lower()

        try:
            # 3. Resolve links and require a regular file inside the directory.
            resolved_path = file_path.resolve()
            if resolved_path.parent != directory:
                _fail(
                    f"{file_path}: file resolves outside kernels directory "
                    f"{directory}"
                )
            if not resolved_path.is_file():
                _fail(
                    f"{file_path}: required file is missing "
                    "or is not a regular file"
                )

            # 4. Hash in 1 MiB chunks to keep memory use bounded.
            hasher = hashlib.sha256()
            chunk_size = 1024 * 1024
            with resolved_path.open("rb") as stream:
                while True:
                    chunk = stream.read(chunk_size)
                    if not chunk:
                        break
                    hasher.update(chunk)
        except (OSError, RuntimeError) as exc:
            _fail(f"{file_path}: cannot read file: {exc}")

        # 5. Reject altered contents before recording the verified path.
        actual_hash = hasher.hexdigest()
        if actual_hash != expected_hash:
            _fail(
                f"{file_path}: SHA-256 mismatch "
                f"(expected {expected_hash}, got {actual_hash})"
            )

        verified_paths[filename] = resolved_path

    return verified_paths


def _resolve_directory(path: Path, *, strict: bool) -> Path:
    """Resolve a directory, mapping symlink loops and other OS errors to
    FAILED_PRECONDITION. Python < 3.13 raises RuntimeError for loops."""
    try:
        resolved = path.resolve(strict=strict)
        if strict and not resolved.is_dir():
            _fail(f"{path}: expected a directory")
    except (OSError, RuntimeError) as exc:
        _fail(f"{path}: cannot resolve directory: {exc}")
    return resolved


def _read_manifest(manifest_path: Path, kernel_set_id: str) -> dict:
    """Read the JSON manifest and check that it describes the requested set."""
    try:
        with manifest_path.open(encoding="utf-8") as stream:
            manifest_data = json.load(stream)
    except FileNotFoundError as exc:
        raise KernelSetError(
            "NOT_FOUND",
            f"kernel_set_id {kernel_set_id!r}: manifest not found: {manifest_path}",
        ) from exc
    except (OSError, ValueError) as exc:
        _fail(f"{manifest_path}: cannot read manifest: {exc}")

    if not isinstance(manifest_data, dict):
        _fail(f"{manifest_path}: manifest must be an object")
    if manifest_data.get("id") != kernel_set_id:
        _fail(f"{manifest_path}: manifest id must equal {kernel_set_id!r}")

    return manifest_data


def _parse_coverage(value: object, manifest_path: Path) -> KernelCoverage:
    """Parse each coverage boundary and check their chronological order."""
    if not isinstance(value, dict):
        _fail(f"{manifest_path}: coverage_utc must be an object")

    field_prefix = f"{manifest_path}: coverage_utc"
    start = _parse_utc(value.get("start"), f"{field_prefix}.start")
    end = _parse_utc(value.get("end"), f"{field_prefix}.end")
    reconstructed_end = _parse_utc(
        value.get("reconstructed_end"),
        f"{field_prefix}.reconstructed_end",
    )

    if not start <= reconstructed_end <= end:
        _fail(
            f"{manifest_path}: coverage_utc requires "
            "start <= reconstructed_end <= end"
        )

    return KernelCoverage(
        start=start,
        end=end,
        reconstructed_end=reconstructed_end,
    )


def load_kernel_set(
    kernel_set_id: str,
    *,
    data_dir: str | Path | None = None,
) -> KernelSet:
    """Read a manifest, verify its local files, and return paths and UTC coverage.

    Args:
        kernel_set_id: Manifest filename without the .json suffix.
        data_dir: Directory containing kernel_sets/ and kernels/.
            Defaults to Argus.Basilisk/, independent of the working directory.

    Support files are returned separately from SPICE kernels. Their urls and
    validity metadata are left to tooling and consumers. No SPICE calls occur.
    """
    # 1. Validate the ID before using it to construct a filesystem path.
    id_pattern = r"[a-z0-9][a-z0-9._-]{0,63}"
    if (
        not isinstance(kernel_set_id, str)
        or not re.fullmatch(id_pattern, kernel_set_id)
    ):
        raise KernelSetError(
            "INVALID_ARGUMENT",
            f"kernel_set_id: invalid value {kernel_set_id!r}",
        )

    # 2. Locate and read the manifest.
    if data_dir is None:
        root = Path(__file__).resolve().parents[1]
    else:
        root = _resolve_directory(Path(data_dir), strict=False)

    manifest_path = root / "kernel_sets" / f"{kernel_set_id}.json"
    manifest_data = _read_manifest(manifest_path, kernel_set_id)

    # 3. Parse the declared coverage into timezone-aware UTC datetimes.
    coverage = _parse_coverage(
        manifest_data.get("coverage_utc"),
        manifest_path,
    )

    # 4. Verify SPICE kernels in manifest order.
    kernels_directory = _resolve_directory(root / "kernels", strict=True)
    kernel_files = _verify_files(
        manifest_data.get("kernels"),
        kernels_directory,
        f"{manifest_path}: kernels",
    )
    if not kernel_files:
        _fail(f"{manifest_path}: kernels must not be empty")

    # 5. Verify support files separately; they must not be passed to SPICE.
    support_files = _verify_files(
        manifest_data.get("support_files", []),
        kernels_directory,
        f"{manifest_path}: support_files",
    )
    shared_filenames = kernel_files.keys() & support_files.keys()
    if shared_filenames:
        _fail(
            f"{manifest_path}: files cannot be both kernels and support_files: "
            f"{sorted(shared_filenames)}"
        )

    # 6. Return immutable results for validation and scenario construction.
    return KernelSet(
        kernel_set_id=kernel_set_id,
        kernel_paths=tuple(kernel_files.values()),
        coverage_utc=coverage,
        support_files=MappingProxyType(support_files),
    )
