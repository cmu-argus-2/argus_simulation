#!/usr/bin/env python3
"""Generate the SPICE environment reference dataset for one Argus scenario.

The dataset holds, per sample time, the J2000 -> ITRF93 state transformation and the
Sun position relative to Earth. It is environment data only: the spacecraft trajectory
still comes from the simulation core.

If the scenario defines spacecraft_checks, a second file holds the analytic orbit's
state transformed with exact (non-interpolated) SPICE calls; C# tests compare against it.

Usage (normally through regenerate.sh):
    python generate_reference.py [scenario.json] [--check] [--pin-kernels]
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import sys
import tempfile
import urllib.request
from datetime import datetime, timedelta, timezone
from pathlib import Path

import numpy as np
import spiceypy as spice

ROOT = Path(__file__).resolve().parent
KERNEL_DIR = ROOT / "kernels"
MANIFEST_PATH = ROOT / "kernels.json"
DEFAULT_SCENARIO = ROOT / "scenarios" / "foundation_one_orbit.json"

FORMAT_NAME = "argus.spice.environment_reference"
FORMAT_VERSION = 1
INERTIAL_FRAME = "J2000"
EARTH_FIXED_FRAME = "ITRF93"
ITRF93_FRAME_CLASS_ID = 3000
SUN_ID = 10
EARTH_ID = 399
SUN_ABERRATION_CORRECTION = "NONE"
METERS_PER_KILOMETER = 1000.0
ASTRONOMICAL_UNIT_METERS = 149_597_870_700.0
SPACECRAFT_ID = -999001
ECLIPSE_SEARCH_STEP_SECONDS = 2.0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("scenario", nargs="?", type=Path, default=DEFAULT_SCENARIO)
    parser.add_argument(
        "--check",
        action="store_true",
        help="Regenerate in memory and fail if the committed output differs.",
    )
    parser.add_argument(
        "--pin-kernels",
        action="store_true",
        help="Record SHA-256 hashes for kernels whose manifest entry has none.",
    )
    args = parser.parse_args()

    scenario_path = args.scenario.resolve()
    scenario = json.loads(scenario_path.read_text())
    output_path = (scenario_path.parent / scenario["output"]).resolve()

    kernels = ensure_kernels(pin=args.pin_kernels)
    spice.kclear()
    try:
        for kernel in kernels:
            spice.furnsh(str(KERNEL_DIR / kernel["file"]))
        document = build_reference(scenario, kernels)
        outputs = [(output_path, serialize(document))]
        checks = scenario.get("spacecraft_checks")
        if checks is not None:
            check_path = (scenario_path.parent / checks["output"]).resolve()
            check_document = build_spacecraft_checks(scenario, document)
            outputs.append((check_path, serialize(check_document)))
    finally:
        spice.kclear()

    stale = False
    for path, text in outputs:
        digest = hashlib.sha256(text.encode()).hexdigest()
        if args.check:
            if not path.exists() or path.read_text() != text:
                print(f"MISMATCH: {path} is stale; rerun without --check.", file=sys.stderr)
                stale = True
            else:
                print(f"OK: {path} matches regenerated data (sha256 {digest}).")
            continue

        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)
        print(f"Wrote {path}")
        print(f"  samples: {len(document_samples(text))}, sha256: {digest}")

    if not args.check:
        print(f"Earth rotation angle at epoch: {document['summary']['earth_rotation_angle_at_epoch_degrees']:.4f} deg")
    return 1 if stale else 0


def document_samples(text: str) -> list:
    return json.loads(text)["samples"]


# --- Kernels -----------------------------------------------------------------------------


def ensure_kernels(pin: bool) -> list[dict]:
    manifest = json.loads(MANIFEST_PATH.read_text())
    KERNEL_DIR.mkdir(exist_ok=True)
    manifest_changed = False

    for kernel in manifest["kernels"]:
        path = KERNEL_DIR / kernel["file"]
        if not path.exists():
            download(kernel, path)

        actual = sha256_file(path)
        expected = kernel.get("sha256")
        if expected is None:
            if not pin:
                raise SystemExit(
                    f"{kernel['file']} has no pinned SHA-256; rerun with --pin-kernels to record {actual}."
                )
            kernel["sha256"] = actual
            manifest_changed = True
            print(f"Pinned {kernel['file']} sha256 {actual}")
        elif actual != expected:
            raise SystemExit(
                f"{kernel['file']} SHA-256 mismatch: expected {expected}, found {actual}. "
                f"Delete {path} to re-download, or investigate the source."
            )

    if manifest_changed:
        MANIFEST_PATH.write_text(json.dumps(manifest, indent=2) + "\n")
    return manifest["kernels"]


def download(kernel: dict, path: Path) -> None:
    partial = path.with_suffix(path.suffix + ".part")
    errors = []
    for url in kernel["urls"]:
        try:
            print(f"Downloading {url}")
            with urllib.request.urlopen(url, timeout=120) as response, open(partial, "wb") as out:
                while chunk := response.read(1 << 20):
                    out.write(chunk)
            partial.rename(path)
            return
        except OSError as error:
            errors.append(f"{url}: {error}")
            partial.unlink(missing_ok=True)
    raise SystemExit(f"Could not download {kernel['file']}:\n  " + "\n  ".join(errors))


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        while chunk := stream.read(1 << 20):
            digest.update(chunk)
    return digest.hexdigest()


# --- Reference data ----------------------------------------------------------------------


def build_reference(scenario: dict, kernels: list[dict]) -> dict:
    orbit = scenario["orbit"]
    step = float(scenario["sample_step_seconds"])
    radius = orbit["earth_equatorial_radius_meters"] + orbit["altitude_meters"]
    period = 2.0 * math.pi * math.sqrt(radius**3 / orbit["earth_gravitational_parameter_m3_s2"])
    intervals = math.ceil(period / step)
    duration = intervals * step

    epoch = parse_utc(scenario["epoch_utc"])
    # SPICE reads an ISO string without a zone as UTC.
    epoch_et = spice.str2et(epoch.strftime("%Y-%m-%dT%H:%M:%S.%f"))
    end_et = epoch_et + duration

    coverage = check_coverage(kernels, epoch_et, end_et)

    samples = []
    for index in range(intervals + 1):
        t = index * step
        et = epoch_et + t
        samples.append(sample(t, et, epoch))

    first_rotation = np.array(samples[0]["rotation"]).reshape(3, 3)
    itrf_x_in_j2000 = first_rotation.T[:, 0]
    earth_angle = math.degrees(math.atan2(itrf_x_in_j2000[1], itrf_x_in_j2000[0])) % 360.0

    return {
        "format": FORMAT_NAME,
        "format_version": FORMAT_VERSION,
        "scenario": {
            key: value for key, value in scenario.items() if key not in ("output", "spacecraft_checks")
        },
        "time": {
            "epoch_utc": scenario["epoch_utc"],
            "epoch_et_seconds": epoch_et,
            "sample_step_seconds": step,
            "sample_count": len(samples),
            "duration_seconds": duration,
            "orbit_period_seconds": period,
            "mapping": (
                "Sample field t is simulation time in seconds since the epoch. "
                "et = epoch_et_seconds + t, where et is SPICE ephemeris time (TDB seconds past J2000). "
                "utc is SPICE et2utc(et); over this interval it equals epoch_utc + t within 1 ms "
                "(no leap second occurs)."
            ),
        },
        "frames": {
            "inertial": INERTIAL_FRAME,
            "earth_fixed": EARTH_FIXED_FRAME,
            "rotation": (
                "3x3 row-major matrix R mapping J2000 vectors to ITRF93: v_itrf93 = R v_j2000."
            ),
            "rotation_rate": (
                "3x3 row-major dR/dt in 1/s. The SPICE 6x6 state transform (sxform) is "
                "[[R, 0], [dR/dt, R]], so v_itrf93 = R v_j2000 + dR/dt r_j2000."
            ),
        },
        "units": {
            "t": "s",
            "et": "s (TDB past J2000)",
            "rotation": "dimensionless",
            "rotation_rate": "1/s",
            "positions": "m",
        },
        "sun": {
            "target": "SUN",
            "observer": "EARTH",
            "aberration_correction": SUN_ABERRATION_CORRECTION,
            "note": (
                "Geometric Sun position relative to Earth's center. Spacecraft-to-Sun is "
                "sun_position - spacecraft_position in the same frame at the same t. Omitting "
                "light time and aberration shifts the Sun direction by about 20 arcseconds."
            ),
        },
        "kernels": [
            {"file": kernel["file"], "type": kernel["type"], "sha256": kernel["sha256"]}
            for kernel in kernels
        ],
        "coverage": coverage,
        "generator": {
            "script": "Argus.Spice/generate_reference.py",
            "spiceypy": spice.__version__,
            "cspice": spice.tkvrsn("TOOLKIT"),
        },
        "summary": {
            "earth_rotation_angle_at_epoch_degrees": earth_angle,
            "sun_distance_at_epoch_au": float(
                np.linalg.norm(samples[0]["sun_position_j2000_m"]) / ASTRONOMICAL_UNIT_METERS
            ),
        },
        "samples": samples,
    }


def sample(t: float, et: float, epoch: datetime) -> dict:
    transform = np.asarray(spice.sxform(INERTIAL_FRAME, EARTH_FIXED_FRAME, et))
    rotation = transform[:3, :3]
    rotation_rate = transform[3:, :3]

    sun_j2000_km, _ = spice.spkpos("SUN", et, INERTIAL_FRAME, SUN_ABERRATION_CORRECTION, "EARTH")
    sun_itrf93_km, _ = spice.spkpos("SUN", et, EARTH_FIXED_FRAME, SUN_ABERRATION_CORRECTION, "EARTH")
    sun_j2000 = np.asarray(sun_j2000_km) * METERS_PER_KILOMETER
    sun_itrf93 = np.asarray(sun_itrf93_km) * METERS_PER_KILOMETER
    utc = spice.et2utc(et, "ISOC", 6) + "Z"

    validate_sample(t, epoch, utc, rotation, rotation_rate, sun_j2000, sun_itrf93)

    return {
        "t": t,
        "et": et,
        "utc": utc,
        "rotation": rotation.flatten().tolist(),
        "rotation_rate": rotation_rate.flatten().tolist(),
        "sun_position_j2000_m": sun_j2000.tolist(),
        "sun_position_itrf93_m": sun_itrf93.tolist(),
    }


def validate_sample(t, epoch, utc, rotation, rotation_rate, sun_j2000, sun_itrf93) -> None:
    def fail(message: str) -> None:
        raise SystemExit(f"Validation failed at t={t}: {message}")

    if np.max(np.abs(rotation @ rotation.T - np.eye(3))) > 1e-12:
        fail("rotation is not orthonormal")
    if abs(np.linalg.det(rotation) - 1.0) > 1e-12:
        fail("rotation is not proper (det != 1)")

    # dR/dt = -[w]x R, so [w]x = -dR/dt R^T with w in ITRF93 coordinates.
    skew = -rotation_rate @ rotation.T
    earth_rate = np.array([skew[2, 1], skew[0, 2], skew[1, 0]])
    if abs(np.linalg.norm(earth_rate) - 7.2921150e-5) > 1e-8:
        fail(f"Earth rotation rate {np.linalg.norm(earth_rate)} rad/s is implausible")

    if np.linalg.norm(rotation @ sun_j2000 - sun_itrf93) > 1e-6 * np.linalg.norm(sun_j2000):
        fail("Sun position in ITRF93 disagrees with rotated J2000 position")
    sun_distance_au = np.linalg.norm(sun_j2000) / ASTRONOMICAL_UNIT_METERS
    if not 0.98 < sun_distance_au < 1.02:
        fail(f"Sun distance {sun_distance_au} au is implausible")

    expected_utc = epoch + timedelta(seconds=t)
    if abs((parse_utc(utc) - expected_utc).total_seconds()) > 1e-3:
        fail(f"UTC {utc} differs from epoch + t by more than 1 ms")


def build_spacecraft_checks(scenario: dict, reference: dict) -> dict:
    """Analytic orbit (same math as CircularOrbitModel.SampleJ2000) through exact SPICE sxform."""
    orbit = scenario["orbit"]
    radius = orbit["earth_equatorial_radius_meters"] + orbit["altitude_meters"]
    mean_motion = math.sqrt(orbit["earth_gravitational_parameter_m3_s2"] / radius**3)
    inclination = math.radians(orbit["inclination_degrees"])
    raan = math.radians(orbit["raan_degrees"])
    phase = math.radians(orbit["phase_degrees"])
    epoch_et = reference["time"]["epoch_et_seconds"]

    def rotate_x(v, angle):
        c, s = math.cos(angle), math.sin(angle)
        return np.array([v[0], c * v[1] - s * v[2], s * v[1] + c * v[2]])

    def rotate_z(v, angle):
        c, s = math.cos(angle), math.sin(angle)
        return np.array([c * v[0] - s * v[1], s * v[0] + c * v[1], v[2]])

    samples = []
    for t in scenario["spacecraft_checks"]["times_seconds"]:
        argument = phase + mean_motion * t
        position_orbital = [radius * math.cos(argument), radius * math.sin(argument), 0.0]
        velocity_orbital = [
            -radius * mean_motion * math.sin(argument),
            radius * mean_motion * math.cos(argument),
            0.0,
        ]
        position_j2000 = rotate_z(rotate_x(position_orbital, inclination), raan)
        velocity_j2000 = rotate_z(rotate_x(velocity_orbital, inclination), raan)

        transform = np.asarray(spice.sxform(INERTIAL_FRAME, EARTH_FIXED_FRAME, epoch_et + t))
        state_itrf93 = transform @ np.concatenate([position_j2000, velocity_j2000])
        samples.append({
            "t": t,
            "position_j2000_m": position_j2000.tolist(),
            "velocity_j2000_m_s": velocity_j2000.tolist(),
            "position_itrf93_m": state_itrf93[:3].tolist(),
            "velocity_itrf93_m_s": state_itrf93[3:].tolist(),
        })

    def j2000_state(t: float) -> np.ndarray:
        argument = phase + mean_motion * t
        position = rotate_z(rotate_x([radius * math.cos(argument), radius * math.sin(argument), 0.0], inclination), raan)
        velocity = rotate_z(rotate_x([
            -radius * mean_motion * math.sin(argument),
            radius * mean_motion * math.cos(argument),
            0.0,
        ], inclination), raan)
        return np.concatenate([position, velocity])

    eclipse = find_eclipses(j2000_state, epoch_et, reference["time"]["duration_seconds"])

    return {
        "format": "argus.spice.spacecraft_reference",
        "format_version": 1,
        "scenario": scenario["name"],
        "description": scenario["spacecraft_checks"]["description"],
        "epoch_utc": scenario["epoch_utc"],
        "epoch_et_seconds": epoch_et,
        "frames": {"inertial": INERTIAL_FRAME, "earth_fixed": EARTH_FIXED_FRAME},
        "units": {"t": "s", "positions": "m", "velocities": "m/s"},
        "kernels": reference["kernels"],
        "eclipse": eclipse,
        "samples": samples,
    }


def find_eclipses(j2000_state, epoch_et: float, duration: float) -> dict:
    """SPICE occultation search for the analytic orbit, written to a temporary SPK."""
    step = 10.0
    times = np.arange(-60.0, duration + 60.0 + step / 2, step)
    states_km = np.array([j2000_state(t) / METERS_PER_KILOMETER for t in times])

    confine = spice.cell_double(2)
    spice.wninsd(epoch_et, epoch_et + duration, confine)
    windows = {}
    with tempfile.TemporaryDirectory() as directory:
        spk = str(Path(directory) / "analytic_orbit.bsp")
        handle = spice.spkopn(spk, "argus analytic orbit", 0)
        spice.spkw13(
            handle, SPACECRAFT_ID, EARTH_ID, INERTIAL_FRAME,
            epoch_et + times[0], epoch_et + times[-1], "analytic circular orbit",
            7, len(times), states_km, epoch_et + times,
        )
        spice.spkcls(handle)
        spice.furnsh(spk)
        try:
            for name, occultation in (("penumbra_or_umbra", "ANY"), ("umbra", "FULL")):
                result = spice.cell_double(200)
                spice.gfoclt(
                    occultation, "EARTH", "ELLIPSOID", EARTH_FIXED_FRAME,
                    "SUN", "ELLIPSOID", "IAU_SUN", SUN_ABERRATION_CORRECTION,
                    str(SPACECRAFT_ID), ECLIPSE_SEARCH_STEP_SECONDS, confine, result,
                )
                windows[name] = [
                    [left - epoch_et, right - epoch_et]
                    for left, right in (spice.wnfetd(result, i) for i in range(spice.wncard(result)))
                ]
        finally:
            spice.unload(spk)

    return {
        "method": (
            "SPICE gfoclt: Earth as ITRF93 ellipsoid and Sun as IAU_SUN ellipsoid (pck00011 radii), "
            f"geometric (no aberration correction), {ECLIPSE_SEARCH_STEP_SECONDS} s search step."
        ),
        "windows_t_seconds": windows,
    }


def check_coverage(kernels: list[dict], start_et: float, end_et: float) -> dict:
    def by_type(kernel_type: str) -> str:
        return str(KERNEL_DIR / next(k["file"] for k in kernels if k["type"] == kernel_type))

    spk = by_type("SPK")
    pck = by_type("binary PCK")
    windows = {
        "SUN (SPK)": spice.spkcov(spk, SUN_ID),
        "EARTH (SPK)": spice.spkcov(spk, EARTH_ID),
        "ITRF93 (binary PCK)": spice.pckcov(pck, ITRF93_FRAME_CLASS_ID),
    }

    coverage = {
        "required_utc": [spice.et2utc(start_et, "ISOC", 3) + "Z", spice.et2utc(end_et, "ISOC", 3) + "Z"]
    }
    for name, window in windows.items():
        if not spice.wnincd(start_et, end_et, window):
            raise SystemExit(f"{name} does not cover the scenario interval.")
        for index in range(spice.wncard(window)):
            left, right = spice.wnfetd(window, index)
            if left <= start_et and end_et <= right:
                coverage[name] = [spice.et2utc(left, "ISOC", 0) + "Z", spice.et2utc(right, "ISOC", 0) + "Z"]
    return coverage


# --- Output ------------------------------------------------------------------------------


def serialize(document: dict) -> str:
    """Deterministic JSON: readable header, one compact line per sample."""
    header = dict(document)
    samples = header.pop("samples")
    head = json.dumps(header, indent=2)
    rows = ",\n".join("    " + json.dumps(s, separators=(",", ":")) for s in samples)
    return head[:-2] + ',\n  "samples": [\n' + rows + "\n  ]\n}\n"


def parse_utc(text: str) -> datetime:
    return datetime.fromisoformat(text.replace("Z", "+00:00")).astimezone(timezone.utc)


if __name__ == "__main__":
    sys.exit(main())
