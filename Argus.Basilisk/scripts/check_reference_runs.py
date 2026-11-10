"""Check recorded Basilisk runs against independent reference cases (HOST-8, #32).

The checker shares no physics or mapping code with the run it checks; the one import from the service is the
kernel-set loader, which verifies the kernel files' SHA-256 before SPICE reads them. It reads the run file with its own
parser, converts orbital elements with its own code, and computes each reference from the physics the service is
configured with: Earth and the Sun as point masses, and a torque-free rigid body. A recording is compared only after
`recording_problems` finds it complete and matched to its case.

Conventions follow `Assets/ArgusSimulation/Core/Recording/README.md`: quaternions are Hamilton, scalar last
(x, y, z, w), and active; `body_to_*` rotates body vectors into the named frame.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import math
import sys
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

import numpy as np

# bsk 2.11.1 astroConstants, converted to SI. The reference uses the service's own constants, because HOST-8 checks
# the implementation, not the physics' realism.
MU_EARTH = 3.98600436e14  # m^3/s^2 (MU_EARTH 398600.436 km^3/s^2)
MU_SUN = 1.3271244002331e20  # m^3/s^2 (MU_SUN 132712440023.31 km^3/s^2)
AU_M = 149597870.693e3  # m (AU 149597870.693 km)
SOLAR_IRRADIANCE_W_M2 = 1361.0  # argus_basilisk/devices/light_sensor.py

REPO = Path(__file__).resolve().parents[2]
REFERENCE_RUNS = REPO / "headless" / "Host" / "runs" / "reference"

SCHEMA_VERSION = "argus-dataset/1"
EARTH_FIXED_FRAME = "Itrf93"
DYNAMICS_BACKEND = {"name": "basilisk", "version": "2.11.1"}  # the μ constants above are this version's
DROPPED_COUNTERS = ("dropped_states", "dropped_frames", "dropped_commands")
COUNTED_FILES = ("states.jsonl", "sensors.jsonl")


def quat_multiply(p: np.ndarray, q: np.ndarray) -> np.ndarray:
    """Hamilton product p ⊗ q of quaternions stored as (x, y, z, w)."""
    px, py, pz, pw = p
    qx, qy, qz, qw = q
    return np.array([
        pw * qx + px * qw + py * qz - pz * qy,
        pw * qy - px * qz + py * qw + pz * qx,
        pw * qz + px * qy - py * qx + pz * qw,
        pw * qw - px * qx - py * qy - pz * qz,
    ])


def quat_conjugate(q: np.ndarray) -> np.ndarray:
    return np.array([-q[0], -q[1], -q[2], q[3]])


def rotation_angle(q_reference: np.ndarray, q_recorded: np.ndarray) -> float:
    """Angle in [0, π] of the rotation between two attitudes; q and -q give 0.

    2·atan2(|vector part|, |scalar part|) of Δq = q_ref* ⊗ q_rec stays exact at tiny angles, where 2·acos(|q·q'|)
    can't return anything between 0 and about 3e-8 rad.
    """
    delta = quat_multiply(quat_conjugate(q_reference), q_recorded)
    return 2.0 * math.atan2(float(np.linalg.norm(delta[:3])), abs(float(delta[3])))


def axis_angle_quat(axis, angle_rad: float) -> np.ndarray:
    a = np.asarray(axis, float)
    a = a / np.linalg.norm(a)
    half = angle_rad / 2.0
    return np.array([*(a * math.sin(half)), math.cos(half)])


def spin_reference(q_frame: np.ndarray, q0: np.ndarray, body_axis, rate_rad_s: float, t_s: float) -> np.ndarray:
    """Body-to-frame attitude of a torque-free spin about a principal body axis.

    q(t) = q_frame ⊗ q0 ⊗ q(axis, rate·t): q0 is body-to-J2000 at t = 0, q_frame is J2000-to-frame at t, and the
    rate is in body axes, so it multiplies on the right.
    """
    return quat_multiply(quat_multiply(q_frame, q0), axis_angle_quat(body_axis, rate_rad_s * t_s))


def elements_to_state(mu: float, a: float, e: float, inclination: float, raan: float, argp: float,
                      true_anomaly: float) -> tuple[np.ndarray, np.ndarray]:
    """Position and velocity from classical orbital elements (angles in radians, true anomaly)."""
    p = a * (1.0 - e * e)
    r_mag = p / (1.0 + e * math.cos(true_anomaly))
    r_pqw = r_mag * np.array([math.cos(true_anomaly), math.sin(true_anomaly), 0.0])
    v_pqw = math.sqrt(mu / p) * np.array([-math.sin(true_anomaly), e + math.cos(true_anomaly), 0.0])
    co, so = math.cos(raan), math.sin(raan)
    ci, si = math.cos(inclination), math.sin(inclination)
    cw, sw = math.cos(argp), math.sin(argp)
    pqw_to_inertial = np.array([
        [co * cw - so * sw * ci, -co * sw - so * cw * ci, so * si],
        [so * cw + co * sw * ci, -so * sw + co * cw * ci, -co * si],
        [sw * si, cw * si, ci],
    ])
    return pqw_to_inertial @ r_pqw, pqw_to_inertial @ v_pqw


def acceleration(r: np.ndarray, r_sun: np.ndarray, mu_earth: float, mu_sun: float) -> np.ndarray:
    """Earth-centred acceleration: Earth's point mass plus the Sun's direct and indirect terms."""
    earth = -mu_earth * r / np.linalg.norm(r) ** 3 if mu_earth else np.zeros(3)
    to_sun = r - r_sun
    sun = -mu_sun * (to_sun / np.linalg.norm(to_sun) ** 3 + r_sun / np.linalg.norm(r_sun) ** 3)
    return earth + sun


def dataset_problems(manifest: dict, lines_read: dict, case: dict) -> list[str]:
    """Reasons a recording can't be compared with its case; empty when it is complete and matched.

    `lines_read` counts the lines of each file in COUNTED_FILES. `case` gives the run's `duration_s`, `step_s` and
    `kernel_set_id`. A run of N steps has N + 1 states.
    """
    problems = []
    if manifest.get("schema_version") != SCHEMA_VERSION:
        problems.append(f"schema_version is {manifest.get('schema_version')!r}, not {SCHEMA_VERSION!r}")
    if manifest.get("complete") is not True:
        problems.append("complete is not true: the dataset was never closed")
    if manifest.get("termination") is not None:
        problems.append(f"termination is set ({manifest.get('termination')!r}): the run ended early")
    if manifest.get("dynamics_backend") != DYNAMICS_BACKEND:
        problems.append(f"dynamics_backend is {manifest.get('dynamics_backend')!r}, the references are {DYNAMICS_BACKEND!r}")
    if manifest.get("earth_fixed_frame") != EARTH_FIXED_FRAME:
        problems.append(f"earth_fixed_frame is {manifest.get('earth_fixed_frame')!r}, not {EARTH_FIXED_FRAME!r}")
    overflow = manifest.get("overflow")
    if not isinstance(overflow, dict):
        problems.append("overflow is missing: the recorder's drop counts are unknown")
    else:
        for counter in DROPPED_COUNTERS:
            if overflow.get(counter) != 0:
                problems.append(f"{counter} is {overflow.get(counter)!r}: the recorder dropped data, or didn't say")
    configuration = manifest.get("configuration") or {}
    if configuration.get("fixed_step_s") != case["step_s"]:
        problems.append(f"fixed_step_s is {configuration.get('fixed_step_s')!r}, the case runs at {case['step_s']}")
    if configuration.get("kernel_set_id") != case["kernel_set_id"]:
        problems.append(f"kernel_set_id is {configuration.get('kernel_set_id')!r}, the case uses {case['kernel_set_id']!r}")
    files = manifest.get("files") or {}
    for name in COUNTED_FILES:
        listed = (files.get(name) or {}).get("lines")
        if listed != lines_read.get(name):
            problems.append(f"{name}: the manifest lists {listed!r} lines and the file has {lines_read.get(name)!r}")
    expected = round(case["duration_s"] / case["step_s"]) + 1
    if lines_read.get("states.jsonl") != expected:
        problems.append(f"states: the case needs {expected}, the file has {lines_read.get('states.jsonl')!r}")
    return problems



# ----------------------------------------------------------------------------------------------------------------
# Cases and tolerances (the plan on #32, 7 Nov 2026). Each tolerance is an error budget, far below what the camera
# resolves (one pixel is 58″, or 163 m at nadir). On 8 Nov bsk 2.11.1 met each one by at least 23× (attitude); frames,
# the Sun and irradiance by 10⁴–10⁶×. docs/target-architecture.md, "G4 results", lists both.

_EVERY_CASE = ("position_m", "velocity_m_s", "frame_rad", "sun_position_rel", "earth_rate_rel")
# What each kind compares: check_dataset raises when a kind's run yields any other set, so a kind can't be added
# without its comparison.
EXPECTED_METRICS = {
    "orbit": _EVERY_CASE,
    "spin": _EVERY_CASE + ("attitude_rad", "omega_rel", "gyro_rad_s", "accel_m_s2"),
    "spin_drift": _EVERY_CASE + ("attitude_start_rad", "attitude_rad", "omega_rel", "attitude_rad_by_hour"),
    "tumble": _EVERY_CASE + ("attitude_rad", "omega_rel", "momentum_rel", "energy_rel"),
    "sun": _EVERY_CASE + ("irradiance_rel",),
}
KINDS = tuple(EXPECTED_METRICS)
# The day-long drift depends on the step size, so it is reported, not judged; the start is judged like R2.
MEASURED_ONLY = {"spin_drift": ("attitude_rad", "attitude_rad_by_hour")}
# The device kinds each case compares; a run file with any other is a mistake in the case table.
DEVICE_KINDS = {"spin": {"imu"}, "sun": {"light_sensor"}}


@dataclass(frozen=True)
class Case:
    run_file: str
    duration_s: float
    step_s: float
    kind: str  # one of KINDS

    def __post_init__(self):
        if self.kind not in KINDS:
            raise ValueError(f"kind {self.kind!r} is not one of {KINDS}")


CASES = {
    "r1-orbit-dt0.1": Case("r1-orbit-dt0.1.json", 86400.0, 0.1, "orbit"),
    "r1-orbit-dt1": Case("r1-orbit-dt1.json", 86400.0, 1.0, "orbit"),
    "r1e-eccentric": Case("r1e-eccentric.json", 7200.0, 0.1, "orbit"),
    "r2-spin": Case("r2-spin.json", 600.0, 0.1, "spin"),
    "r2-long-q0-dt0.1": Case("r2-long-q0-dt0.1.json", 86400.0, 0.1, "spin_drift"),
    "r2-long-q0-dt1": Case("r2-long-q0-dt1.json", 86400.0, 1.0, "spin_drift"),
    "r2-long-identity-dt0.1": Case("r2-long-identity-dt0.1.json", 86400.0, 0.1, "spin_drift"),
    "r2-long-identity-dt1": Case("r2-long-identity-dt1.json", 86400.0, 1.0, "spin_drift"),
    "r3-tumble": Case("r3-tumble.json", 600.0, 0.1, "tumble"),
    "r5-sun-pointing": Case("r5-sun-pointing.json", 60.0, 0.1, "sun"),
}

TOLERANCES = {
    "position_m": 1e-2,
    "velocity_m_s": 1e-5,
    "frame_rad": 1e-10,
    "sun_position_rel": 1e-10,  # a vector error, so a zero, rescaled or wrong-body Sun fails as well as a turned one
    "earth_rate_rel": 1e-10,
    "attitude_rad": 1e-6,
    "attitude_start_rad": 1e-6,
    "omega_rel": 1e-6,
    "gyro_rad_s": 1e-14,
    "accel_m_s2": 1e-9,  # the live R2 run measured 4.25e-12 m/s² (8 Nov); the lever-arm signal is about 6e-3
    "momentum_rel": 1e-6,
    "energy_rel": 1e-6,
    "irradiance_rel": 1e-3,  # catches any attitude misreading above about 2.6°
}


@dataclass
class Report:
    metrics: dict = field(default_factory=dict)
    problems: list = field(default_factory=list)


def quat_from_dcm(m: np.ndarray) -> np.ndarray:
    """The quaternion (x, y, z, w) whose active rotation equals the matrix m (Shepperd's method)."""
    m = np.asarray(m, float)
    trace = m[0, 0] + m[1, 1] + m[2, 2]
    if trace > 0.0:
        s = 2.0 * math.sqrt(1.0 + trace)
        q = [(m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s, 0.25 * s]
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = 2.0 * math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2])
        q = [0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s, (m[2, 1] - m[1, 2]) / s]
    elif m[1, 1] > m[2, 2]:
        s = 2.0 * math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2])
        q = [(m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s, (m[0, 2] - m[2, 0]) / s]
    else:
        s = 2.0 * math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1])
        q = [(m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s, (m[1, 0] - m[0, 1]) / s]
    q = np.array(q)
    return q / np.linalg.norm(q)


def dcm_from_quat(q: np.ndarray) -> np.ndarray:
    """The active rotation matrix of the quaternion (x, y, z, w)."""
    x, y, z, w = np.asarray(q, float) / np.linalg.norm(q)
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])


def _unit(values) -> np.ndarray:
    """A recorded quaternion, or NaNs (which fail) when it isn't unit. rotation_angle sees only direction, so a zero
    or rescaled quaternion would otherwise score 0; real recordings are unit to about 2e-16."""
    q = np.asarray(values, float)
    return q if abs(float(np.linalg.norm(q)) - 1.0) <= 1e-12 else np.full(4, math.nan)


def initial_state(run: dict) -> tuple[np.ndarray, np.ndarray]:
    o = run["initialOrbit"]
    return elements_to_state(MU_EARTH, o["semiMajorAxisMeters"], o["eccentricity"], math.radians(o["inclinationDegrees"]),
                             math.radians(o["raanDegrees"]), math.radians(o["argumentOfPeriapsisDegrees"]),
                             math.radians(o["trueAnomalyDegrees"]))


def _orbit_step(r, v, et, h, ephemeris, mu_earth, mu_sun):
    def f(state, t):
        return np.concatenate([state[3:], acceleration(state[:3], ephemeris.sun_j2000(t), mu_earth, mu_sun)])
    s0 = np.concatenate([r, v])
    k1 = f(s0, et)
    k2 = f(s0 + h / 2 * k1, et + h / 2)
    k3 = f(s0 + h / 2 * k2, et + h / 2)
    k4 = f(s0 + h * k3, et + h)
    s1 = s0 + h / 6 * (k1 + 2 * k2 + 2 * k3 + k4)
    return s1[:3], s1[3:]


ORBIT_SUBSTEPS = 2  # the reference orbit's RK4 runs at half the recorded step
BODY_SUBSTEPS = 20  # the reference tumble's RK4 runs at a twentieth of it (5 ms at 0.1 s)


def advance_orbit(r, v, et, step_s, ephemeris, mu_earth=MU_EARTH, mu_sun=MU_SUN, substeps=ORBIT_SUBSTEPS):
    """The J2000 state one recorded step after (r, v) at et: RK4 at step_s / substeps. The one implementation of the
    reference orbit; propagate_orbit and check_dataset both use it."""
    h = step_s / substeps
    for j in range(substeps):
        r, v = _orbit_step(r, v, et + j * h, h, ephemeris, mu_earth, mu_sun)
    return r, v


def propagate_orbit(r0, v0, et0, step_s, steps, ephemeris, mu_earth=MU_EARTH, mu_sun=MU_SUN, substeps=ORBIT_SUBSTEPS):
    """J2000 states at et0 + k·step_s, k = 0..steps. Times are never accumulated."""
    r, v = np.asarray(r0, float), np.asarray(v0, float)
    states = [(r, v)]
    for k in range(steps):
        r, v = advance_orbit(r, v, et0 + k * step_s, step_s, ephemeris, mu_earth, mu_sun, substeps)
        states.append((r, v))
    return states


def _rigid_rate(q, w, inertia, inertia_inv):
    q_dot = 0.5 * quat_multiply(q, np.array([w[0], w[1], w[2], 0.0]))
    w_dot = inertia_inv @ (-np.cross(w, inertia @ w))
    return q_dot, w_dot


def advance_rigid_body(q, w, inertia, inertia_inv, step_s, substeps=BODY_SUBSTEPS):
    """Torque-free rigid body one recorded step on: RK4 at step_s / substeps. The caller inverts the inertia once."""
    h = step_s / substeps
    for _ in range(substeps):
        a_q, a_w = _rigid_rate(q, w, inertia, inertia_inv)
        b_q, b_w = _rigid_rate(q + h / 2 * a_q, w + h / 2 * a_w, inertia, inertia_inv)
        c_q, c_w = _rigid_rate(q + h / 2 * b_q, w + h / 2 * b_w, inertia, inertia_inv)
        d_q, d_w = _rigid_rate(q + h * c_q, w + h * c_w, inertia, inertia_inv)
        q = q + h / 6 * (a_q + 2 * b_q + 2 * c_q + d_q)
        w = w + h / 6 * (a_w + 2 * b_w + 2 * c_w + d_w)
        q = q / np.linalg.norm(q)
    return q, w


def propagate_rigid_body(q0, w0, inertia, step_s, steps, substeps=BODY_SUBSTEPS):
    """Torque-free rigid body: body-to-J2000 q and body rate ω at k·step_s."""
    inertia = np.asarray(inertia, float)
    inertia_inv = np.linalg.inv(inertia)
    q, w = np.asarray(q0, float), np.asarray(w0, float)
    out = [(q, w)]
    for _ in range(steps):
        q, w = advance_rigid_body(q, w, inertia, inertia_inv, step_s, substeps)
        out.append((q, w))
    return out


class _RecentSun:
    """An ephemeris whose sun_j2000 remembers its last few answers, by exact time. Each recorded step asks for its own
    time and for both RK4 substeps' times, which share their ends: 5 distinct times, not 9 calls (#137 review). Only
    an identical float is reused, so the reference is bit-for-bit what it was."""

    def __init__(self, ephemeris, size=8):
        self._ephemeris, self._size, self._recent = ephemeris, size, {}

    def sun_j2000(self, et):
        sun = self._recent.get(et)
        if sun is None:
            sun = self._recent[et] = self._ephemeris.sun_j2000(et)
            if len(self._recent) > self._size:
                del self._recent[next(iter(self._recent))]
        return sun

    def __getattr__(self, name):
        return getattr(self._ephemeris, name)


def _utc(value: str) -> tuple:
    """A UTC stamp as (datetime to the second, fractional seconds); the run file and manifest spell it differently."""
    text = value.rstrip("Z")
    whole, _, fraction = text.partition(".")
    return datetime.fromisoformat(whole), float("0." + fraction) if fraction else 0.0


def _parsed_utc(value):
    """_utc of a recorded stamp, or None when it isn't one (null, a number, or text in another form)."""
    if not isinstance(value, str):
        return None
    try:
        whole, fraction = _utc(value)
    except ValueError:
        return None
    return (whole, fraction) if whole.tzinfo is None else None


def _number(value):
    """A recorded JSON number as a float, or None for anything else (null, text, true or false, or an integer too big
    for a float)."""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    try:
        return float(value)
    except OverflowError:
        return None


def _integer(value):
    """A recorded JSON integer, or None for anything else (5.0 and true included)."""
    return value if isinstance(value, int) and not isinstance(value, bool) else None


def echo_problems(manifest: dict, run: dict) -> list[str]:
    """The manifest's echo of the configuration must carry the run file's values (compared by value, not text)."""
    c = manifest.get("configuration") or {}
    o, oc = run["initialOrbit"], c.get("initial_orbit") or {}
    sc, scc = run["spacecraft"], c.get("spacecraft") or {}
    q = sc["initialBodyToJ2000"]
    expected = {
        "random_seed": (c.get("random_seed"), run["randomSeed"]),
        "semi_major_axis_m": (oc.get("semi_major_axis_m"), o["semiMajorAxisMeters"]),
        "eccentricity": (oc.get("eccentricity"), o["eccentricity"]),
        "inclination_deg": (oc.get("inclination_deg"), o["inclinationDegrees"]),
        "raan_deg": (oc.get("raan_deg"), o["raanDegrees"]),
        "argument_of_periapsis_deg": (oc.get("argument_of_periapsis_deg"), o["argumentOfPeriapsisDegrees"]),
        "true_anomaly_deg": (oc.get("true_anomaly_deg"), o["trueAnomalyDegrees"]),
        "mass_kg": (scc.get("mass_kg"), sc["massKilograms"]),
        "inertia_body_kg_m2": (scc.get("inertia_body_kg_m2"), sc["inertiaBodyKilogramSquareMeters"]),
        "initial_body_to_j2000_xyzw": (scc.get("initial_body_to_j2000_xyzw"), [q["x"], q["y"], q["z"], q["w"]]),
        "initial_angular_velocity_body_rad_s": (scc.get("initial_angular_velocity_body_rad_s"),
                                                sc["initialAngularVelocityBodyRadiansPerSecond"]),
    }
    problems = [f"{key} in the manifest is {got!r}, the run file has {want!r}"
                for key, (got, want) in expected.items() if got != want]
    epoch = _parsed_utc(c.get("epoch_utc"))
    if epoch is None or epoch != _utc(run["epochUtc"]):
        problems.append(f"epoch_utc in the manifest is {c.get('epoch_utc')!r}, the run file has {run['epochUtc']!r}")
    listed = [str(d.get("device_id")) for d in manifest.get("devices") or []]
    recorded = {d.get("device_id"): _manifest_device(d) for d in manifest.get("devices") or []}
    wanted = {d["deviceId"]: _run_device(d) for d in run["devices"]}
    if sorted(listed) != sorted(wanted):  # a list, so a device listed twice is refused too
        problems.append(f"devices in the manifest are {sorted(listed)}, the run file has {sorted(wanted)}")
    for device_id in sorted(set(recorded) & set(wanted)):
        problems += [f"device {device_id}: {key} in the manifest is {recorded[device_id].get(key)!r}, the run file has {want!r}"
                     for key, want in wanted[device_id].items() if recorded[device_id].get(key) != want]
    return problems


def _flatten(parameters: dict, prefix: str = "") -> dict:
    """Nested run-file parameters as the manifest spells them: {"gyroscope": {"range": 1}} → {"gyroscope.range": 1}."""
    out = {}
    for key, value in parameters.items():
        out.update(_flatten(value, f"{prefix}{key}.") if isinstance(value, dict) else {f"{prefix}{key}": value})
    return out


# Who models each device kind the cases compare: all of them Basilisk (D10). A run file no longer names it; the host
# derives it from the kind (CORE-22 #136) and writes it to the manifest.
KIND_SOURCE = {"imu": "basilisk", "light_sensor": "basilisk"}


def _run_device(device: dict) -> dict:
    """A run-file device's values under the manifest's names, with `source` from the kind (KIND_SOURCE)."""
    q = device["mount"]["deviceToBody"]
    values = {"kind": device["kind"], "model_name": device["modelName"], "frame_id": device["frameId"],
              "sample_period_s": device["samplePeriodSeconds"], "controller_visible": device["controllerVisible"],
              "mount.position_body_m": device["mount"]["positionBodyMeters"],
              "mount.device_to_body_xyzw": [q["x"], q["y"], q["z"], q["w"]],
              "parameters": _flatten(device.get("parameters") or {})}
    values["source"] = KIND_SOURCE.get(device["kind"])
    return values


def _manifest_device(device: dict) -> dict:
    mount = device.get("mount") or {}
    return {"kind": device.get("kind"), "model_name": device.get("model_name"), "frame_id": device.get("frame_id"),
            "sample_period_s": device.get("sample_period_s"), "controller_visible": device.get("controller_visible"),
            "mount.position_body_m": mount.get("position_body_m"), "mount.device_to_body_xyzw": mount.get("device_to_body_xyzw"),
            "parameters": device.get("parameters"), "source": device.get("source")}


class _Unreadable(str):
    """A JSONL line that doesn't parse, such as the last one a killed host left half-written."""


def _read_jsonl(path: Path):
    """Each non-blank line's JSON value; a line that doesn't parse comes back as _Unreadable, so the gate reports it."""
    if not path.exists():
        return
    with path.open() as handle:
        for line in handle:
            if line.strip():
                try:
                    yield json.loads(line)
                except json.JSONDecodeError:
                    yield _Unreadable(line.strip())


def _quat(d: dict) -> np.ndarray:
    return np.array([d["x"], d["y"], d["z"], d["w"]], float)


def _seconds_after(stamp: tuple, epoch: tuple) -> float:
    (whole, fraction), (whole0, fraction0) = stamp, epoch
    return (whole - whole0).total_seconds() + (fraction - fraction0)


def _scan_states(path: Path, step_s: float, epoch_utc: str) -> tuple[int, list[str]]:
    """The number of states, and the first one whose sequence, simulation time or UTC stamp isn't its place in the
    file. The stamp is the run epoch plus the simulation time, to the recorder's 100 ns ticks. A field of the wrong
    type is reported under its own name, never raised."""
    count, problems, epoch = 0, [], _utc(epoch_utc)
    for k, state in enumerate(_read_jsonl(path)):
        count += 1
        if problems:
            continue
        if not isinstance(state, dict):
            problems.append(f"states.jsonl: line {k + 1} is not a JSON object: {str(state)[:60]!r}")
            continue
        time, stamp = _number(state.get("simulation_time_s")), _parsed_utc(state.get("timestamp_utc"))
        if _integer(state.get("sequence")) != k:
            problems.append(f"sequence: state {k} in the file has sequence {state.get('sequence')!r}")
        elif time is None or not abs(time - k * step_s) <= 1e-9:
            problems.append(f"simulation_time_s: state {k} is at {state.get('simulation_time_s')!r} s, not {k * step_s:.10g} s")
        elif stamp is None or not abs(_seconds_after(stamp, epoch) - k * step_s) <= 1e-6:
            problems.append(f"timestamp_utc: state {k} is stamped {state.get('timestamp_utc')!r}, not {epoch_utc} + {k * step_s:.10g} s")
    return count, problems


def _scan_sensors(path: Path, device_ids) -> tuple[int, list[str]]:
    """The number of sensor frames, and any from a sensor the run file doesn't declare."""
    count, unknown, problems = 0, set(), []
    for frame in _read_jsonl(path):
        count += 1
        if not isinstance(frame, dict):
            if not problems:
                problems.append(f"sensors.jsonl: line {count} is not a JSON object: {str(frame)[:60]!r}")
        elif frame.get("sensor_id") not in device_ids:
            unknown.add(str(frame.get("sensor_id")))
    if unknown:
        problems.append(f"sensors.jsonl has frames from {sorted(unknown)}, which the run file doesn't declare")
    return count, problems


def recording_problems(case: Case, run: dict, directory) -> list[str]:
    """Why a recording can't stand for its case: the manifest gate, each state's place, the sensors present, and the
    configuration echo."""
    directory = Path(directory)
    manifest = json.loads((directory / "manifest.json").read_text())
    states, out_of_step = _scan_states(directory / "states.jsonl", case.step_s, run["epochUtc"])
    frames, undeclared = _scan_sensors(directory / "sensors.jsonl", {d["deviceId"] for d in run["devices"]})
    lines_read = {"states.jsonl": states, "sensors.jsonl": frames}
    gate_case = {"duration_s": case.duration_s, "step_s": case.step_s, "kernel_set_id": run["kernelSetId"]}
    return dataset_problems(manifest, lines_read, gate_case) + out_of_step + undeclared + echo_problems(manifest, run)


def _worst(metrics: dict, key: str, value) -> None:
    """Keep the largest error; a non-finite one becomes inf, so it fails instead of vanishing in max()."""
    value = float(value)
    metrics[key] = max(metrics[key], value) if math.isfinite(value) else math.inf


def check_dataset(case: Case, run: dict, directory, ephemeris, tolerances: dict = TOLERANCES) -> Report:
    """Compare one recording with its reference case. Nothing is compared unless `recording_problems` is empty."""
    directory = Path(directory)
    report = Report()
    unchecked = {d["kind"] for d in run["devices"]} - DEVICE_KINDS.get(case.kind, set())
    if unchecked:
        raise ValueError(f"a {case.kind!r} case compares no {sorted(unchecked)} device, so the run file can't carry one")
    report.problems += recording_problems(case, run, directory)
    if report.problems:
        return report

    step, sc = case.step_s, run["spacecraft"]
    ephemeris = _RecentSun(ephemeris)
    et0 = ephemeris.et_of(run["epochUtc"])
    q0 = _quat(sc["initialBodyToJ2000"])
    w0 = np.asarray(sc["initialAngularVelocityBodyRadiansPerSecond"], float)
    inertia = np.asarray(sc["inertiaBodyKilogramSquareMeters"], float)
    r, v = initial_state(run)
    rate, axis = float(np.linalg.norm(w0)), (w0 / np.linalg.norm(w0) if np.linalg.norm(w0) else w0)
    if case.kind in ("spin", "spin_drift"):
        if np.linalg.norm(np.cross(w0, inertia @ w0)) > 1e-12 * rate * np.linalg.norm(inertia @ w0):
            report.problems.append("the closed-form spin needs a principal-axis rate: ω × Iω is not 0")
            return report
    if case.kind == "tumble":
        q_body, w_body, inertia_inv = q0, w0, np.linalg.inv(inertia)

    m = dict.fromkeys(_EVERY_CASE, 0.0)
    if case.kind in ("spin", "spin_drift", "tumble"):
        m.update(attitude_rad=0.0, omega_rel=0.0)
    if case.kind == "spin_drift":
        m.update(attitude_start_rad=0.0, attitude_rad_by_hour=[])
    if case.kind == "tumble":
        m.update(momentum_rel=0.0, energy_rel=0.0)
    h_n0 = t_0 = None
    itrf_to_j2000_by_k = {}
    for k, state in enumerate(_read_jsonl(directory / "states.jsonl")):
        t = k * step
        et = et0 + t
        frame, frame_dot = ephemeris.frame(et)
        q_frame = quat_from_dcm(frame)
        env = state["environment"]
        _worst(m, "position_m", np.linalg.norm(np.asarray(state["position_ecef_m"]) - frame @ r))
        _worst(m, "velocity_m_s", np.linalg.norm(np.asarray(state["velocity_ecef_m_s"]) - (frame @ v + frame_dot @ r)))
        _worst(m, "frame_rad", rotation_angle(q_frame, _unit(env["j2000_to_itrf93_xyzw"])))
        sun = frame @ ephemeris.sun_j2000(et)
        _worst(m, "sun_position_rel", np.linalg.norm(np.asarray(env["sun_position_itrf93_m"], float) - sun) / np.linalg.norm(sun))
        omega_cross = -frame_dot @ frame.T
        earth_rate = np.array([omega_cross[2, 1], omega_cross[0, 2], omega_cross[1, 0]])
        _worst(m, "earth_rate_rel",
               np.linalg.norm(np.asarray(env["earth_angular_velocity_itrf93_rad_s"]) - earth_rate) / np.linalg.norm(earth_rate))
        q_rec = _unit(state["body_to_ecef_xyzw"])
        w_rec = np.asarray(state["angular_velocity_body_rad_s"], float)
        if case.kind in ("spin", "spin_drift"):
            error = rotation_angle(spin_reference(q_frame, q0, axis, rate, t), q_rec)
            _worst(m, "attitude_rad", error)
            _worst(m, "omega_rel", np.linalg.norm(w_rec - w0) / rate)
            if case.kind == "spin_drift" and k == 0:
                _worst(m, "attitude_start_rad", error)
            if case.kind == "spin_drift" and abs(t / 3600.0 - round(t / 3600.0)) < 1e-9:
                m["attitude_rad_by_hour"].append(error)
        if case.kind == "tumble":
            _worst(m, "attitude_rad", rotation_angle(quat_multiply(q_frame, q_body), q_rec))
            _worst(m, "omega_rel", np.linalg.norm(w_rec - w_body) / np.linalg.norm(w_body))
            q_nb = quat_multiply(quat_conjugate(q_frame), q_rec)
            h_n = dcm_from_quat(q_nb) @ inertia @ w_rec
            energy = 0.5 * float(w_rec @ inertia @ w_rec)
            if h_n0 is None:
                h_n0, t_0 = h_n, energy
            _worst(m, "momentum_rel", np.linalg.norm(h_n - h_n0) / np.linalg.norm(h_n0))
            _worst(m, "energy_rel", abs(energy - t_0) / t_0)
        if case.kind == "sun":
            itrf_to_j2000_by_k[k] = (frame.T @ np.asarray(state["position_ecef_m"]), et, env.get("spacecraft_shadow_factor"))
        # advance the references to the next state
        r, v = advance_orbit(r, v, et, step, ephemeris)
        if case.kind == "tumble":
            q_body, w_body = advance_rigid_body(q_body, w_body, inertia, inertia_inv, step)

    steps = round(case.duration_s / case.step_s)
    if case.kind == "spin":
        _check_imu(run, directory, w0, steps, step, m, report)
    if case.kind == "sun":
        _check_irradiance(run, directory, ephemeris, itrf_to_j2000_by_k, steps, step, m, report)
    report.metrics = m
    expected = EXPECTED_METRICS.get(case.kind, ())
    if set(m) != set(expected) and not report.problems:
        raise ValueError(f"kind {case.kind!r} compared {sorted(m)}, not its expected {sorted(expected)}")
    for key, value in m.items():
        if key in MEASURED_ONLY.get(case.kind, ()):
            if not all(math.isfinite(v) for v in (value if isinstance(value, list) else [value])):
                report.problems.append(f"{key.split('_')[0]}: {key} is not finite, so it measures nothing")
            continue
        if key not in tolerances:
            raise ValueError(f"{key} has no tolerance and isn't measured-only for {case.kind!r}")
        if not value <= tolerances[key]:
            name = key.split("_")[0]
            report.problems.append(f"{name}: {key} = {value:.3g}, above the tolerance {tolerances[key]:g}")
    return report


# The first state with a Valid frame: the IMU has no reading at t = 0 and reads from step 1; the light sensor reads
# from t = 0 (HOST-5 #29, G4; both live recordings of 8 Nov).
FIRST_VALID_STATE = {"imu": 1, "light_sensor": 0}


def _valid_frames(device: dict, directory: Path, steps: int, step_s: float, report: Report):
    """The device's Valid frames, or None after a problem. A device sampled every step has exactly one frame per
    state, in order: Unavailable before FIRST_VALID_STATE and Valid from it on. Anything else is refused."""
    device_id = device["deviceId"]
    if device["samplePeriodSeconds"] != step_s:
        report.problems.append(f"{device_id}: sampled every {device['samplePeriodSeconds']} s, not every {step_s} s step")
        return None
    first_valid = FIRST_VALID_STATE[device["kind"]]
    frames = [f for f in _read_jsonl(directory / "sensors.jsonl") if f["sensor_id"] == device_id]
    got = [(f["state_sequence"], f["status"]) for f in frames]
    want = [(k, "Unavailable" if k < first_valid else "Valid") for k in range(steps + 1)]
    if got != want:
        at = next((i for i, (g, w) in enumerate(zip(got, want)) if g != w), min(len(got), len(want)))
        report.problems.append(f"{device_id}: {len(frames)} frames where the case needs {steps + 1}, one per state "
                               f"(Valid from state {first_valid}); they first differ at frame {at}")
        return None
    return [f for f in frames if f["status"] == "Valid"]


def _check_imu(run, directory, w0, steps, step_s, m, report):
    for device in (d for d in run["devices"] if d["kind"] == "imu"):
        valid = _valid_frames(device, directory, steps, step_s, report)
        if valid is None:
            continue
        mount = device["mount"]
        body_to_device = dcm_from_quat(_quat(mount["deviceToBody"])).T
        lever = np.asarray(mount["positionBodyMeters"], float)
        gyro_expected = body_to_device @ w0
        accel_expected = body_to_device @ np.cross(w0, np.cross(w0, lever))
        m.setdefault("gyro_rad_s", 0.0)
        m.setdefault("accel_m_s2", 0.0)
        for f in valid:
            _worst(m, "gyro_rad_s", np.max(np.abs(np.asarray(f["payload"]["angular_velocity_sensor_rad_s"]) - gyro_expected)))
            _worst(m, "accel_m_s2", np.max(np.abs(np.asarray(f["payload"]["specific_force_sensor_m_s2"]) - accel_expected)))


def _check_irradiance(run, directory, ephemeris, positions, steps, step_s, m, report):
    """Each light sensor must read 1361·scale·(AU/d)², which holds only with its boresight on the Sun. R5's start
    puts its one sensor there; a sensor pointed elsewhere fails here."""
    for sensor in (d for d in run["devices"] if d["kind"] == "light_sensor"):
        valid = _valid_frames(sensor, directory, steps, step_s, report)
        if valid is None:
            continue
        scale = sensor["parameters"]["scale_factor"]
        m.setdefault("irradiance_rel", 0.0)
        for f in valid:
            r_j2000, et, shadow = positions[f["state_sequence"]]
            if shadow != 1.0:
                report.problems.append(f"irradiance: the spacecraft is not fully sunlit at state {f['state_sequence']} ({shadow})")
                return
            distance = np.linalg.norm(ephemeris.sun_j2000(et) - r_j2000)
            expected = SOLAR_IRRADIANCE_W_M2 * scale * (AU_M / distance) ** 2
            _worst(m, "irradiance_rel", abs(f["payload"]["irradiance_w_m2"] - expected) / expected)


# ----------------------------------------------------------------------------------------------------------------
# R4 (acceptance 3): the analytic fixture's deviation, measured once. The fixture (CircularOrbitModel) starts on the
# circular orbit Basilisk is on at t = 0, in ITRF93 axes frozen at t = 0, so what remains is the difference in physics:
# no Sun, its own μ, a circular orbit, and a constant-rate Earth.

FIXTURE_EARTH_RADIUS_M = 6378137.0  # CircularOrbitModel.EarthEquatorialRadiusMeters
FIXTURE_MU = 3.986004418e14  # CircularOrbitModel.EarthGravitationalParameter (WGS-84), m^3/s^2
R4_CASE = "r1-orbit-dt1"  # R4 compares every 60 s, which the 1 s recording covers; the 0.1 s one would add nothing
R4_FIXTURE_TIMES_S = [60.0 * i for i in range(1441)]  # 0 to 86,400 s, as the Host.Tests harness writes them
R4_START_LIMIT_M = 1.0  # at t = 0 the fixture must sit on the recorded orbit; the harness's start agrees to about 1e-8 m


def fixture_parameters(first_state: dict) -> dict:
    """The fixture's altitude, inclination, RAAN and phase (argument of latitude) from Basilisk's state at t = 0."""
    r = np.asarray(first_state["position_ecef_m"], float)
    earth_rate = np.asarray(first_state["environment"]["earth_angular_velocity_itrf93_rad_s"], float)
    v_inertial = np.asarray(first_state["velocity_ecef_m_s"], float) + np.cross(earth_rate, r)
    h = np.cross(r, v_inertial)
    h_hat = h / np.linalg.norm(h)
    node = np.array([-h[1], h[0], 0.0])
    node_hat = node / np.linalg.norm(node)
    phase = math.atan2(float(r @ np.cross(h_hat, node_hat)), float(r @ node_hat))
    return {
        "altitude_m": float(np.linalg.norm(r)) - FIXTURE_EARTH_RADIUS_M,
        "inclination_deg": math.degrees(math.acos(h_hat[2])),
        "raan_deg": math.degrees(math.atan2(h[0], -h[1])) % 360.0,
        "phase_deg": math.degrees(phase) % 360.0,
    }


def ric(position: np.ndarray, r: np.ndarray, v: np.ndarray) -> tuple[float, float, float]:
    """Radial, along-track and cross-track parts of position − r, in the frame of the orbit (r, v)."""
    radial_hat = r / np.linalg.norm(r)
    cross_hat = np.cross(r, v) / np.linalg.norm(np.cross(r, v))
    along_hat = np.cross(cross_hat, radial_hat)
    d = np.asarray(position, float) - r
    return float(d @ radial_hat), float(d @ along_hat), float(d @ cross_hat)


def r4_deviation(dataset: Path, fixture_csv: Path, step_s: float) -> dict:
    """The fixture's position against Basilisk's at 0 s, about one fixture orbit, 6 h and 24 h, split into RIC parts.

    The fixture is sampled every 60 s, so "1 orbit" is the sample nearest the fixture's period, which the report gives.
    """
    rows = [line.split(",") for line in fixture_csv.read_text().splitlines()[1:] if line.strip()]
    times = [float(row[0]) for row in rows]
    if times != R4_FIXTURE_TIMES_S:
        raise ValueError(f"the fixture CSV must give t = 0 to 86,400 s every 60 s; it has {len(times)} rows"
                         + (f", from {times[0]:g} to {times[-1]:g} s" if times else ""))
    fixture = {round(float(t) / step_s): np.array([float(x), float(y), float(z)]) for t, x, y, z in rows}
    params = None
    report = {}
    for k, state in enumerate(_read_jsonl(dataset / "states.jsonl")):
        if params is None:
            params = fixture_parameters(state)
            radius = params["altitude_m"] + FIXTURE_EARTH_RADIUS_M
            report["fixture_period_s"] = 2 * math.pi * math.sqrt(radius ** 3 / FIXTURE_MU)
            orbit_k = round(report["fixture_period_s"] / 60.0) * round(60.0 / step_s)
            wanted = {0: "0 s", orbit_k: "1 orbit", round(21600 / step_s): "6 h", round(86400 / step_s): "24 h"}
        if k in fixture:
            r = np.asarray(state["position_ecef_m"], float)
            earth_rate = np.asarray(state["environment"]["earth_angular_velocity_itrf93_rad_s"], float)
            v = np.asarray(state["velocity_ecef_m_s"], float) + np.cross(earth_rate, r)
            radial, along, cross = ric(fixture[k], r, v)
            total = float(np.linalg.norm(fixture[k] - r))
            if total > report.get("max_m", -1.0):
                report["max_m"], report["max_at_s"] = total, k * step_s
            if k in wanted:
                report[wanted[k]] = {"t_s": k * step_s, "total_m": total, "radial_m": radial, "along_track_m": along,
                                     "cross_track_m": cross}
    start = report.get("0 s", {}).get("total_m", math.inf)
    if not start <= R4_START_LIMIT_M:
        raise ValueError(f"the fixture doesn't start on this recording's orbit: {start:.3g} m apart at t = 0. "
                         "Start it from the parameters `r4 <dataset>` prints.")
    report["fixture_parameters"] = params
    return report


_BASILISK_DIR = Path(__file__).resolve().parents[1]  # Argus.Basilisk/ of the repo this script is in


def _kernels_module():
    """This repo's argus_basilisk/kernels.py, loaded from its file: never another copy that sys.path or --data-dir
    could put first (#137 review). It verifies each kernel's SHA-256 and makes no SPICE call."""
    name = "check_reference_runs._argus_kernels"
    if name not in sys.modules:
        spec = importlib.util.spec_from_file_location(name, _BASILISK_DIR / "argus_basilisk" / "kernels.py")
        module = importlib.util.module_from_spec(spec)
        sys.modules[name] = module  # dataclasses look their module up here
        spec.loader.exec_module(module)
    return sys.modules[name]


def kernel_paths(kernel_set_id: str, data_dir: Path) -> list:
    """The kernel set's files in load order, each SHA-checked. data_dir holds kernel_sets/ and kernels/; it never
    decides which code does the checking."""
    return list(_kernels_module().load_kernel_set(kernel_set_id, data_dir=Path(data_dir)).kernel_paths)


class SpiceEphemeris:
    """Every SPICE call the checker makes, in one place.

    D3 has Basilisk alone load the kernel set at run time, and tests use fixed tables instead of a second SPICE. Its
    row makes one exception, for this offline checker and its tests (HOST-8, #32), which read the same pinned,
    SHA-checked set to verify Basilisk's output. Without that exception, this class is the one thing to replace, with
    one reading committed CSPICE tables.
    """

    def __init__(self, kernel_set_id: str, data_dir: Path):
        import spiceypy  # only here: nothing else in the checker touches SPICE

        self._sp = spiceypy
        for path in kernel_paths(kernel_set_id, data_dir):
            spiceypy.furnsh(str(path))

    def et_of(self, utc: str) -> float:
        return self._sp.str2et(utc)

    def frame(self, et: float) -> tuple[np.ndarray, np.ndarray]:
        """J2000 → ITRF93 rotation and its rate, from sxform."""
        m = np.asarray(self._sp.sxform("J2000", "ITRF93", et))
        return m[:3, :3], m[3:, :3]

    def sun_j2000(self, et: float) -> np.ndarray:
        """The Sun from the Earth's centre, J2000, geometric (no aberration correction), in metres."""
        return np.asarray(self._sp.spkpos("SUN", et, "J2000", "NONE", "EARTH")[0]) * 1e3


def main_r4(argv) -> int:
    parser = argparse.ArgumentParser(prog="check_reference_runs.py r4",
                                     description="R4: the analytic fixture's starting parameters, or its deviation.")
    parser.add_argument("dataset", type=Path, help="a recorded r1-orbit-dt1 run directory")
    parser.add_argument("--fixture", type=Path, help="the fixture trajectory CSV written by the Host.Tests R4 harness")
    parser.add_argument("--json", type=Path)
    args = parser.parse_args(argv)
    case = CASES[R4_CASE]
    problems = recording_problems(case, json.loads((REFERENCE_RUNS / case.run_file).read_text()), args.dataset)
    if problems:
        for problem in problems:
            print("FAIL", problem)
        print(f"not a complete {R4_CASE} recording: R4 measures nothing")
        return 1
    if args.fixture is None:
        first = next(_read_jsonl(args.dataset / "states.jsonl"))
        for key, value in fixture_parameters(first).items():
            print(f"HOST8_R4_{key.upper()}={value!r}")
        return 0
    try:
        report = r4_deviation(args.dataset, args.fixture, case.step_s)
    except ValueError as error:
        print("FAIL", error)
        return 1
    print(json.dumps(report, indent=2))
    if args.json:
        args.json.write_text(json.dumps(report, indent=2))
    return 0


def main(argv=None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    if argv and argv[0] == "r4":
        return main_r4(argv[1:])
    parser = argparse.ArgumentParser(description="Check a recorded run against its HOST-8 reference case.")
    parser.add_argument("case", choices=sorted(CASES))
    parser.add_argument("dataset", type=Path, help="the run directory, holding manifest.json and states.jsonl")
    parser.add_argument("--data-dir", type=Path, default=_BASILISK_DIR,
                        help="the folder holding kernel_sets/ and kernels/ (default: this repo's Argus.Basilisk/). "
                             "The SHA-256 pins come from its kernel_sets/<id>.json; the code that checks them is "
                             "always this repo's")
    parser.add_argument("--json", type=Path, help="also write the metrics and problems as JSON")
    args = parser.parse_args(argv)
    case = CASES[args.case]
    run = json.loads((REFERENCE_RUNS / case.run_file).read_text())
    report = check_dataset(case, run, args.dataset, SpiceEphemeris(run["kernelSetId"], args.data_dir))
    for key, value in report.metrics.items():
        print(f"{key:28s} {value if isinstance(value, list) else f'{value:.3e}'}")
    for problem in report.problems:
        print("FAIL", problem)
    if args.json:
        args.json.write_text(json.dumps({"case": args.case, "metrics": report.metrics, "problems": report.problems}, indent=2))
    measured = ", ".join(MEASURED_ONLY.get(case.kind, ()))
    print(f"{len(report.problems)} problem(s)" if report.problems
          else f"PASS ({measured} measured, not judged)" if measured else "PASS")
    return 0 if not report.problems else 1


if __name__ == "__main__":
    sys.exit(main())
