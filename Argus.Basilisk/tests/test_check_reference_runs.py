"""check_reference_runs.py (HOST-8 #32): the checker's own math and its dataset gate, offline.

Nothing here imports Basilisk, and only the SPICE test reads kernels. The checker must not share code with the
run it checks, so each function is tested against an independent property: a closed form, an invariant, or a known
geometric fact. The checker needs numpy, which the `test` extra doesn't install, so without it these tests skip.
"""

import importlib.util
import math
import sys
from pathlib import Path

import pytest

np = pytest.importorskip("numpy")

ROOT = Path(__file__).resolve().parents[1]

spec = importlib.util.spec_from_file_location("check_reference_runs", ROOT / "scripts" / "check_reference_runs.py")
check = importlib.util.module_from_spec(spec)
sys.modules["check_reference_runs"] = check  # dataclasses look their module up here
spec.loader.exec_module(check)

MU_EARTH = 3.98600436e14  # bsk 2.11.1 astroConstants.MU_EARTH, m^3/s^2
MU_SUN = 1.3271244002331e20  # bsk 2.11.1 astroConstants.MU_SUN, m^3/s^2


def quat(axis, angle_rad):
    """Hamilton quaternion (x, y, z, w) for an active rotation by angle_rad about axis."""
    a = np.asarray(axis, float)
    a = a / np.linalg.norm(a)
    return np.array([*(a * math.sin(angle_rad / 2)), math.cos(angle_rad / 2)])


# --- The angle metric: precise at tiny angles and blind to the sign of q (q and -q are one rotation).

def test_rotation_angle_resolves_a_1e_12_rad_difference():
    q = quat([1.0, -2.0, 0.5], math.radians(40.0))
    nudged = check.quat_multiply(q, quat([0.3, 0.1, -1.0], 1e-12))
    assert check.rotation_angle(q, nudged) == pytest.approx(1e-12, rel=1e-3)


@pytest.mark.parametrize("angle", [0.0, 3e-9, 1e-6, 0.5, math.pi / 2, 3.0])
def test_rotation_angle_is_the_angle_between_two_attitudes(angle):
    q = quat([0.2, 0.4, -0.9], 1.1)
    other = check.quat_multiply(q, quat([1.0, 1.0, 0.0], angle))
    assert check.rotation_angle(q, other) == pytest.approx(angle, abs=1e-15, rel=1e-12)


def test_rotation_angle_treats_q_and_minus_q_as_the_same_attitude():
    q = quat([1.0, -2.0, 0.5], math.radians(40.0))
    assert check.rotation_angle(q, -q) < 1e-15  # roundoff only: the product leaves about 7e-18


# --- Orbital elements to state: checked against the invariants that define the elements (an RV2COE round trip
# written from the definitions, so it shares nothing with the conversion under test).

def elements_from_state(r, v, mu):
    h = np.cross(r, v)
    node = np.cross([0.0, 0.0, 1.0], h)
    e_vec = ((v @ v - mu / np.linalg.norm(r)) * r - (r @ v) * v) / mu
    energy = (v @ v) / 2 - mu / np.linalg.norm(r)
    a = -mu / (2 * energy)
    inclination = math.acos(h[2] / np.linalg.norm(h))
    raan = math.atan2(node[1], node[0]) % (2 * math.pi)
    argp = math.acos(np.clip(node @ e_vec / (np.linalg.norm(node) * np.linalg.norm(e_vec)), -1, 1))
    if e_vec[2] < 0:
        argp = 2 * math.pi - argp
    true_anomaly = math.acos(np.clip(e_vec @ r / (np.linalg.norm(e_vec) * np.linalg.norm(r)), -1, 1))
    if r @ v < 0:
        true_anomaly = 2 * math.pi - true_anomaly
    return a, np.linalg.norm(e_vec), inclination, raan, argp, true_anomaly


def test_elements_to_state_recovers_every_element_for_r1e():
    a, e, i, raan, argp, f = 6958137.0, 0.01, math.radians(97.75), math.radians(251.0), math.radians(37.0), math.radians(113.0)
    r, v = check.elements_to_state(MU_EARTH, a, e, i, raan, argp, f)
    got = elements_from_state(r, v, MU_EARTH)
    assert got[0] == pytest.approx(a, rel=1e-12)
    assert got[1] == pytest.approx(e, rel=1e-9)
    for expected, actual in zip((i, raan, argp, f), got[2:]):
        assert actual == pytest.approx(expected, abs=1e-10)


def test_elements_to_state_of_a_circular_equatorial_orbit_is_the_closed_form():
    a, theta = 7000e3, 0.7
    r, v = check.elements_to_state(MU_EARTH, a, 0.0, 0.0, 0.0, 0.0, theta)
    speed = math.sqrt(MU_EARTH / a)
    np.testing.assert_allclose(r, a * np.array([math.cos(theta), math.sin(theta), 0.0]), rtol=0, atol=1e-6)
    np.testing.assert_allclose(v, speed * np.array([-math.sin(theta), math.cos(theta), 0.0]), rtol=0, atol=1e-9)


# --- The closed-form spin (R2): q(t) = q_frame ⊗ q0 ⊗ q(axis, rate·t), with the rate in body axes.

def test_spin_reference_starts_at_the_frame_times_q0():
    q_frame = quat([0.0, 0.1, 1.0], 1.2)
    q0 = quat([1.0, -2.0, 0.5], math.radians(40.0))
    got = check.spin_reference(q_frame, q0, [0.0, 1.0, 0.0], math.radians(20.17), 0.0)
    assert check.rotation_angle(got, check.quat_multiply(q_frame, q0)) < 1e-15


def test_spin_reference_rotates_about_the_body_axis():
    # The kinematics q' = 0.5 q ⊗ (ω_body, 0): a finite difference of the closed form must match it.
    q0 = quat([1.0, -2.0, 0.5], math.radians(40.0))
    rate, t, dt = math.radians(20.17), 7.3, 1e-6
    identity = np.array([0.0, 0.0, 0.0, 1.0])
    before = check.spin_reference(identity, q0, [0.0, 1.0, 0.0], rate, t - dt)
    after = check.spin_reference(identity, q0, [0.0, 1.0, 0.0], rate, t + dt)
    now = check.spin_reference(identity, q0, [0.0, 1.0, 0.0], rate, t)
    derivative = (after - before) / (2 * dt)
    expected = 0.5 * check.quat_multiply(now, np.array([0.0, rate, 0.0, 0.0]))
    np.testing.assert_allclose(derivative, expected, atol=1e-9)


# --- The orbit's acceleration: Earth and the Sun as point masses, the Sun with direct and indirect terms.

def test_the_suns_pull_vanishes_at_the_earths_centre():
    r_sun = np.array([1.2e11, -8.0e10, -3.4e10])
    np.testing.assert_allclose(
        check.acceleration(np.zeros(3) + 1e-30, r_sun, 0.0, MU_SUN), np.zeros(3), atol=1e-18)


def test_the_suns_tidal_pull_stretches_the_orbit_along_the_sun_line():
    r_sun = np.array([1.496e11, 0.0, 0.0])
    r = np.array([7.0e6, 0.0, 0.0])
    tidal = check.acceleration(r, r_sun, 0.0, MU_SUN)
    expected = 2 * MU_SUN * 7.0e6 / 1.496e11 ** 3  # leading tidal term, toward the Sun
    assert tidal[0] == pytest.approx(expected, rel=1e-3)
    assert abs(tidal[1]) < 1e-20 and abs(tidal[2]) < 1e-20


def test_earths_pull_is_the_inverse_square_law():
    r = np.array([3.0e6, -4.0e6, 5.0e6])
    a = check.acceleration(r, np.array([1.496e11, 0.0, 0.0]), MU_EARTH, 0.0)
    np.testing.assert_allclose(a, -MU_EARTH * r / np.linalg.norm(r) ** 3, rtol=1e-15)


# --- The dataset gate: nothing is compared until the recording is complete and is the case it claims to be.

def good_manifest(states=6001, sensors=6001):
    return {
        "schema_version": "argus-dataset/1",
        "complete": True,
        "termination": None,
        "dynamics_backend": {"name": "basilisk", "version": "2.11.1"},
        "earth_fixed_frame": "Itrf93",
        "configuration": {"fixed_step_s": 0.1, "kernel_set_id": "argus-2026-11-02"},
        "files": {"states.jsonl": {"lines": states}, "sensors.jsonl": {"lines": sensors}},
        "overflow": {"policy": "drop-newest", "capacity": 65536, "dropped_states": 0, "dropped_frames": 0,
                     "dropped_commands": 0},
    }


CASE = {"name": "r2-spin", "duration_s": 600.0, "step_s": 0.1, "kernel_set_id": "argus-2026-11-02"}


def lines(states=6001, sensors=6001):
    return {"states.jsonl": states, "sensors.jsonl": sensors}


def test_a_complete_matching_dataset_passes_the_gate():
    assert check.dataset_problems(good_manifest(), lines(), CASE) == []


@pytest.mark.parametrize("change, fragment", [
    (lambda m: m.update(schema_version="argus-dataset/2"), "schema_version"),
    (lambda m: m.update(complete=False), "complete"),
    (lambda m: m.update(termination={"status": "Stopped"}), "termination"),
    (lambda m: m.update(earth_fixed_frame="AnalyticEarthFixed"), "earth_fixed_frame"),
    (lambda m: m["overflow"].update(dropped_states=3), "dropped_states"),
    (lambda m: m["overflow"].update(dropped_frames=1), "dropped_frames"),
    (lambda m: m["configuration"].update(fixed_step_s=1.0), "fixed_step_s"),
    (lambda m: m["configuration"].update(kernel_set_id="argus-2026-10-20"), "kernel_set_id"),
    (lambda m: m.pop("overflow"), "overflow"),                                   # no block is not zero drops
    (lambda m: m["overflow"].pop("dropped_commands"), "dropped_commands"),
    (lambda m: m.update(dynamics_backend={"name": "analytic", "version": "1"}), "dynamics_backend"),
    (lambda m: m["dynamics_backend"].update(version="2.12.0"), "dynamics_backend"),  # the μ constants are 2.11.1's
])
def test_the_gate_names_each_reason_a_dataset_is_refused(change, fragment):
    manifest = good_manifest()
    change(manifest)
    problems = check.dataset_problems(manifest, lines(), CASE)
    assert any(fragment in p for p in problems), problems


@pytest.mark.parametrize("lines_in_manifest, lines_read", [(601, 601), (6001, 6000), (6002, 6002)])
def test_the_gate_refuses_a_run_of_the_wrong_length(lines_in_manifest, lines_read):
    problems = check.dataset_problems(good_manifest(lines_in_manifest), lines(states=lines_read), CASE)
    assert any("states" in p for p in problems), problems


def test_the_gate_refuses_a_sensor_file_shorter_than_its_manifest_says():
    problems = check.dataset_problems(good_manifest(sensors=6001), lines(sensors=1), CASE)
    assert any("sensors.jsonl" in p for p in problems), problems


# ======================================================================================================
# The reference cases, compared with synthetic datasets. A smooth stand-in replaces SPICE here
# (the checker takes its ephemeris as an argument), so these tests stay offline.

import json


class FakeEphemeris:
    """ITRF93 as a z-rotation at Earth's rate after a small fixed tilt; the Sun on a slow circle at 1.016 AU, so the
    (1 AU / d)² factor in the irradiance matters."""

    ET0 = 8.3354e8
    RATE = 7.2921159e-5
    TILT = 0.0026

    def et_of(self, utc):
        return self.ET0

    def frame(self, et):
        theta = 1.2 + self.RATE * (et - self.ET0)
        c, s = math.cos(theta), math.sin(theta)
        rz = np.array([[c, s, 0.0], [-s, c, 0.0], [0.0, 0.0, 1.0]])
        rz_dot = self.RATE * np.array([[-s, c, 0.0], [-c, -s, 0.0], [0.0, 0.0, 0.0]])
        ct, st = math.cos(self.TILT), math.sin(self.TILT)
        tilt = np.array([[1.0, 0.0, 0.0], [0.0, ct, st], [0.0, -st, ct]])
        return rz @ tilt, rz_dot @ tilt

    def sun_j2000(self, et):
        angle = 1.2 + 1.99e-7 * (et - self.ET0)
        return 1.52e11 * np.array([math.cos(angle), math.sin(angle) * math.cos(0.409), math.sin(angle) * math.sin(0.409)])


def reference_run(name):
    return json.loads((ROOT.parent / "headless/Host/runs/reference" / f"{name}.json").read_text())


def utc_stamp(epoch_utc, t_s):
    """The recorder's UTC stamp: the run epoch plus t_s, to .NET's 100 ns ticks."""
    from datetime import datetime, timedelta
    whole, ticks = divmod(round(t_s * 1e7), 10 ** 7)
    stamp = datetime.fromisoformat(epoch_utc.rstrip("Z")) + timedelta(seconds=whole)
    return f"{stamp:%Y-%m-%dT%H:%M:%S}.{ticks:07d}Z"


def simulate(run, ephemeris, steps, step_s, *, mu_earth=MU_EARTH, mu_sun=MU_SUN, indirect=True, attitude=None,
             imu=None, irradiance=None):
    """A synthetic recording of `run`: RK4 at the run's own step, then mapped to ITRF93 like the host does.

    `attitude(k, t, q_frame)` returns body-to-ITRF93 for state k; `imu` and `irradiance` add sensor frames.
    """
    elements = run["initialOrbit"]
    r, v = check.elements_to_state(
        MU_EARTH, elements["semiMajorAxisMeters"], elements["eccentricity"], math.radians(elements["inclinationDegrees"]),
        math.radians(elements["raanDegrees"]), math.radians(elements["argumentOfPeriapsisDegrees"]),
        math.radians(elements["trueAnomalyDegrees"]))
    et0 = ephemeris.et_of(run["epochUtc"])

    def accel(position, et):
        sun = ephemeris.sun_j2000(et)
        a = check.acceleration(position, sun, mu_earth, mu_sun)
        if not indirect:
            a = a + mu_sun * sun / np.linalg.norm(sun) ** 3
        return a

    states, frames = [], []
    for k in range(steps + 1):
        t = k * step_s
        et = et0 + t
        frame, frame_dot = ephemeris.frame(et)
        q_frame = check.quat_from_dcm(frame)
        q = attitude(k, t, q_frame) if attitude else q_frame
        omega = run["spacecraft"]["initialAngularVelocityBodyRadiansPerSecond"]
        states.append({
            "sequence": k, "simulation_time_s": t, "timestamp_utc": utc_stamp(run["epochUtc"], t),
            "position_ecef_m": list(frame @ r), "velocity_ecef_m_s": list(frame @ v + frame_dot @ r),
            "body_to_ecef_xyzw": list(q), "angular_velocity_body_rad_s": list(omega),
            "environment": {"sun_position_itrf93_m": list(frame @ ephemeris.sun_j2000(et)),
                            "j2000_to_itrf93_xyzw": list(q_frame),
                            "earth_angular_velocity_itrf93_rad_s": [0.0, 0.0, ephemeris.RATE],
                            "spacecraft_shadow_factor": 1.0},
        })
        if imu and k == 0:  # as the host records it: no IMU reading before the first step (HOST-5, G4)
            frames.append({"sensor_id": "imu.ref", "state_sequence": 0, "status": "Unavailable", "payload": None})
        elif imu:
            gyro, accel_s = imu(np.asarray(omega))
            frames.append({"sensor_id": "imu.ref", "state_sequence": k, "status": "Valid",
                           "payload": {"angular_velocity_sensor_rad_s": list(gyro), "specific_force_sensor_m_s2": list(accel_s)}})
        if irradiance:
            frames.append({"sensor_id": "light.pz", "state_sequence": k, "status": "Valid",
                           "payload": {"irradiance_w_m2": irradiance(k, r, et)}})
        if k < steps:
            def f(s, et_s):
                return np.concatenate([s[3:], accel(s[:3], et_s)])
            s0 = np.concatenate([r, v])
            k1 = f(s0, et); k2 = f(s0 + step_s / 2 * k1, et + step_s / 2)
            k3 = f(s0 + step_s / 2 * k2, et + step_s / 2); k4 = f(s0 + step_s * k3, et + step_s)
            s1 = s0 + step_s / 6 * (k1 + 2 * k2 + 2 * k3 + k4)
            r, v = s1[:3], s1[3:]
    return states, frames


def flatten(parameters, prefix=""):
    out = {}
    for key, value in parameters.items():
        out.update(flatten(value, f"{prefix}{key}.") if isinstance(value, dict) else {f"{prefix}{key}": value})
    return out


def manifest_device(device):
    """A run-file device as the host's manifest echoes it (snake_case, parameters flattened with dots)."""
    q = device["mount"]["deviceToBody"]
    return {"device_id": device["deviceId"], "model_name": device["modelName"], "frame_id": device["frameId"],
            "sample_period_s": device["samplePeriodSeconds"], "source": device["source"],
            "mount": {"position_body_m": device["mount"]["positionBodyMeters"],
                      "device_to_body_xyzw": [q["x"], q["y"], q["z"], q["w"]]},
            "controller_visible": device["controllerVisible"], "kind": device["kind"],
            "parameters": flatten(device["parameters"]), "initial_state": {}}


def write_dataset(directory, run, states, frames=()):
    sc = run["spacecraft"]
    q = sc["initialBodyToJ2000"]
    manifest = {
        "schema_version": "argus-dataset/1", "complete": True, "termination": None, "earth_fixed_frame": "Itrf93",
        "dynamics_backend": {"name": "basilisk", "version": "2.11.1"},
        "configuration": {
            "epoch_utc": run["epochUtc"].replace("Z", ".0000000Z"), "fixed_step_s": run["fixedStepSeconds"],
            "random_seed": run["randomSeed"], "kernel_set_id": run["kernelSetId"],
            "initial_orbit": {"semi_major_axis_m": run["initialOrbit"]["semiMajorAxisMeters"],
                              "eccentricity": run["initialOrbit"]["eccentricity"],
                              "inclination_deg": run["initialOrbit"]["inclinationDegrees"],
                              "raan_deg": run["initialOrbit"]["raanDegrees"],
                              "argument_of_periapsis_deg": run["initialOrbit"]["argumentOfPeriapsisDegrees"],
                              "true_anomaly_deg": run["initialOrbit"]["trueAnomalyDegrees"]},
            "spacecraft": {"mass_kg": sc["massKilograms"], "inertia_body_kg_m2": sc["inertiaBodyKilogramSquareMeters"],
                           "initial_body_to_j2000_xyzw": [q["x"], q["y"], q["z"], q["w"]],
                           "initial_angular_velocity_body_rad_s": sc["initialAngularVelocityBodyRadiansPerSecond"]},
        },
        "devices": [manifest_device(d) for d in run["devices"]],
        "files": {"states.jsonl": {"lines": len(states)}, "sensors.jsonl": {"lines": len(frames)}},
        "overflow": {"policy": "drop-newest", "capacity": 65536, "dropped_states": 0, "dropped_frames": 0,
                     "dropped_commands": 0},
    }
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "manifest.json").write_text(json.dumps(manifest))
    (directory / "states.jsonl").write_text("".join(json.dumps(s) + "\n" for s in states))
    (directory / "sensors.jsonl").write_text("".join(json.dumps(f) + "\n" for f in frames))
    return directory


def short(run, step_s, duration_s):
    """The same run, shorter or at another step, for fast tests."""
    out = json.loads(json.dumps(run))
    out["fixedStepSeconds"] = step_s
    return out, check.Case(run_file="(test)", duration_s=duration_s, step_s=step_s, kind="orbit")


@pytest.mark.parametrize("matrix", [np.eye(3), FakeEphemeris().frame(FakeEphemeris.ET0 + 5000.0)[0]])
def test_quat_from_dcm_rotates_vectors_like_the_matrix(matrix):
    q = check.quat_from_dcm(matrix)
    np.testing.assert_allclose(check.dcm_from_quat(q), matrix, atol=1e-15)


def test_every_reference_run_file_has_a_case():
    names = sorted(p.stem for p in (ROOT.parent / "headless/Host/runs/reference").glob("*.json"))
    assert sorted(check.CASES) == names
    for name, case in check.CASES.items():
        assert reference_run(name)["fixedStepSeconds"] == case.step_s, name


def test_the_orbit_propagator_follows_a_circle_without_the_sun():
    a = 7000e3
    r0, v0 = check.elements_to_state(MU_EARTH, a, 0.0, 0.0, 0.0, 0.0, 0.0)
    eph = FakeEphemeris()
    states = check.propagate_orbit(r0, v0, eph.ET0, 1.0, 7200, eph, MU_EARTH, 0.0)
    n = math.sqrt(MU_EARTH / a ** 3)
    r, _ = states[-1]
    assert np.linalg.norm(r - a * np.array([math.cos(n * 7200), math.sin(n * 7200), 0.0])) < 1e-4


@pytest.fixture
def orbit_case():
    run, case = short(reference_run("r1e-eccentric"), 1.0, 7200.0)
    case = check.Case(run_file="(test)", duration_s=7200.0, step_s=1.0, kind="orbit")
    return run, case


def test_a_matching_orbit_recording_passes(tmp_path, orbit_case):
    run, case = orbit_case
    states, _ = simulate(run, FakeEphemeris(), 7200, 1.0)
    report = check.check_dataset(case, run, write_dataset(tmp_path / "ok", run, states), FakeEphemeris())
    assert report.problems == [], report.problems
    assert report.metrics["position_m"] < 1e-3


@pytest.mark.parametrize("planted", [
    {"mu_sun": 0.0},                       # no Sun: about 2 m in 2 h
    {"mu_earth": 3.986004418e14},          # the WGS-84 μ in the dynamics
    {"indirect": False},                   # the Sun's direct term only
])
def test_a_planted_orbit_error_fails(tmp_path, orbit_case, planted):
    run, case = orbit_case
    states, _ = simulate(run, FakeEphemeris(), 7200, 1.0, **planted)
    report = check.check_dataset(case, run, write_dataset(tmp_path / "bad", run, states), FakeEphemeris())
    assert any("position" in p for p in report.problems), (planted, report.metrics)


@pytest.fixture
def spin_case():
    run = reference_run("r2-spin")
    return run, check.CASES["r2-spin"]


def spin_attitude(run, bug=None):
    sc = run["spacecraft"]
    q0 = np.array([sc["initialBodyToJ2000"][k] for k in "xyzw"])
    omega = np.asarray(sc["initialAngularVelocityBodyRadiansPerSecond"])
    rate, axis = np.linalg.norm(omega), omega / np.linalg.norm(omega)

    def attitude(k, t, q_frame):
        if bug == "conjugated q0":
            return check.spin_reference(q_frame, check.quat_conjugate(q0), axis, rate, t)
        if bug == "reversed order":
            body = check.quat_multiply(q0, check.axis_angle_quat(axis, rate * t))
            return check.quat_multiply(body, q_frame)
        if bug == "rate in the inertial frame":
            return check.quat_multiply(q_frame, check.quat_multiply(check.axis_angle_quat(axis, rate * t), q0))
        return check.spin_reference(q_frame, q0, axis, rate, t)
    return attitude


def imu_model(run, transpose=False):
    device = run["devices"][0]
    m = device["mount"]["deviceToBody"]
    q_mount = np.array([m["x"], m["y"], m["z"], m["w"]])
    lever = np.asarray(device["mount"]["positionBodyMeters"])
    body_to_device = check.dcm_from_quat(q_mount).T
    if transpose:
        body_to_device = body_to_device.T

    def model(omega):
        return body_to_device @ omega, body_to_device @ np.cross(omega, np.cross(omega, lever))
    return model


def test_a_matching_spin_recording_passes(tmp_path, spin_case):
    run, case = spin_case
    states, frames = simulate(run, FakeEphemeris(), 6000, 0.1, attitude=spin_attitude(run), imu=imu_model(run))
    report = check.check_dataset(case, run, write_dataset(tmp_path / "ok", run, states, frames), FakeEphemeris())
    assert report.problems == [], report.problems
    assert report.metrics["attitude_rad"] < 1e-12


@pytest.mark.parametrize("bug", ["conjugated q0", "reversed order", "rate in the inertial frame"])
def test_a_planted_attitude_convention_error_fails(tmp_path, spin_case, bug):
    run, case = spin_case
    states, frames = simulate(run, FakeEphemeris(), 6000, 0.1, attitude=spin_attitude(run, bug), imu=imu_model(run))
    report = check.check_dataset(case, run, write_dataset(tmp_path / "bad", run, states, frames), FakeEphemeris())
    assert any("attitude" in p for p in report.problems), (bug, report.metrics)


def test_an_imu_mount_read_the_wrong_way_fails(tmp_path, spin_case):
    run, case = spin_case
    states, frames = simulate(run, FakeEphemeris(), 6000, 0.1, attitude=spin_attitude(run), imu=imu_model(run, transpose=True))
    report = check.check_dataset(case, run, write_dataset(tmp_path / "bad", run, states, frames), FakeEphemeris())
    assert any("gyro" in p for p in report.problems), report.metrics


def tumble_attitude(run, ephemeris, inertia=None):
    """The rigid body integrated at 10 ms (the checker's reference uses 5 ms), as a stand-in for Basilisk's output."""
    sc = run["spacecraft"]
    I = np.asarray(inertia if inertia is not None else sc["inertiaBodyKilogramSquareMeters"])
    q0 = np.array([sc["initialBodyToJ2000"][k] for k in "xyzw"])
    body = check.propagate_rigid_body(q0, np.asarray(sc["initialAngularVelocityBodyRadiansPerSecond"]), I, 0.1, 600, 10)
    omegas = [w for _, w in body]

    def attitude(k, t, q_frame):
        return check.quat_multiply(q_frame, body[k][0])
    return attitude, omegas


# R3 shortened to 60 s for speed: the wrong inertia separates within seconds.
TUMBLE_60_S = check.Case(run_file="(test)", duration_s=60.0, step_s=0.1, kind="tumble")


def test_a_matching_tumble_recording_passes(tmp_path):
    run, case = reference_run("r3-tumble"), TUMBLE_60_S
    attitude, omegas = tumble_attitude(run, FakeEphemeris())
    states, _ = simulate(run, FakeEphemeris(), 600, 0.1, attitude=attitude)
    for state, omega in zip(states, omegas):
        state["angular_velocity_body_rad_s"] = list(omega)
    report = check.check_dataset(case, run, write_dataset(tmp_path / "ok", run, states), FakeEphemeris())
    assert report.problems == [], report.problems
    assert report.metrics["momentum_rel"] < 1e-9 and report.metrics["energy_rel"] < 1e-9


def test_a_tumble_with_the_wrong_inertia_fails(tmp_path):
    run, case = reference_run("r3-tumble"), TUMBLE_60_S
    wrong = np.diag(np.diag(np.asarray(run["spacecraft"]["inertiaBodyKilogramSquareMeters"])))  # products dropped
    attitude, omegas = tumble_attitude(run, FakeEphemeris(), inertia=wrong)
    states, _ = simulate(run, FakeEphemeris(), 600, 0.1, attitude=attitude)
    for state, omega in zip(states, omegas):
        state["angular_velocity_body_rad_s"] = list(omega)
    report = check.check_dataset(case, run, write_dataset(tmp_path / "bad", run, states), FakeEphemeris())
    assert any("attitude" in p or "momentum" in p for p in report.problems), report.metrics


def sun_irradiance(run, ephemeris, tilt_rad=0.0):
    def irradiance(k, r, et):
        d = np.linalg.norm(ephemeris.sun_j2000(et) - r)
        return 1361.0 * (check.AU_M / d) ** 2 * math.cos(tilt_rad)
    return irradiance


def test_the_sun_pointing_case_passes_head_on_and_fails_tilted(tmp_path):
    run, case = reference_run("r5-sun-pointing"), check.CASES["r5-sun-pointing"]
    eph = FakeEphemeris()
    for tilt, should_pass in ((0.0, True), (math.radians(5.0), False)):
        states, frames = simulate(run, eph, 600, 0.1, irradiance=sun_irradiance(run, eph, tilt))
        report = check.check_dataset(case, run, write_dataset(tmp_path / f"t{tilt}", run, states, frames), eph)
        assert (report.problems == []) is should_pass, (tilt, report.problems)


def test_a_manifest_that_echoes_another_run_is_refused(tmp_path, spin_case):
    run, case = spin_case
    states, frames = simulate(run, FakeEphemeris(), 6000, 0.1, attitude=spin_attitude(run), imu=imu_model(run))
    directory = write_dataset(tmp_path / "echo", run, states, frames)
    manifest = json.loads((directory / "manifest.json").read_text())
    manifest["configuration"]["spacecraft"]["initial_body_to_j2000_xyzw"] = [0.0, 0.0, 0.0, 1.0]
    (directory / "manifest.json").write_text(json.dumps(manifest))
    report = check.check_dataset(case, run, directory, FakeEphemeris())
    assert any("initial_body_to_j2000_xyzw" in p for p in report.problems), report.problems


def test_check_dataset_compares_nothing_when_the_recording_is_incomplete(tmp_path, spin_case):
    run, case = spin_case
    states, frames = simulate(run, FakeEphemeris(), 6000, 0.1, attitude=spin_attitude(run), imu=imu_model(run))
    directory = write_dataset(tmp_path / "cut", run, states[:3000], frames)  # Ctrl+C half-way: too few states
    report = check.check_dataset(case, run, directory, FakeEphemeris())
    assert any("states" in p for p in report.problems), report.problems
    assert report.metrics == {}


# --- Incomplete or mismatched recordings: each must be refused.

def spin_recording(run):
    return simulate(run, FakeEphemeris(), 6000, 0.1, attitude=spin_attitude(run), imu=imu_model(run))


def invalid_from_step_2(frames):
    return frames[:2] + [{**f, "status": "Invalid", "payload": {"angular_velocity_sensor_rad_s": [9.0, 9.0, 9.0],
                                                                "specific_force_sensor_m_s2": [9.0, 9.0, 9.0]}}
                         for f in frames[2:]]


@pytest.mark.parametrize("mutate", [
    lambda frames: frames[:2],                                                   # 1 of 6,000 Valid frames
    lambda frames: frames[:2] + [{**f, "status": "Unavailable", "payload": None} for f in frames[2:]],
    invalid_from_step_2,
    lambda frames: frames[1:],                                                   # no Unavailable frame at t = 0
    lambda frames: frames + frames[-1:],                                         # one state with two frames
], ids=["one-valid-frame", "unavailable-after-step-1", "invalid-after-step-1", "no-frame-at-0", "duplicate"])
def test_the_spin_case_needs_one_imu_frame_per_state(tmp_path, spin_case, mutate):
    run, case = spin_case
    states, frames = spin_recording(run)
    report = check.check_dataset(case, run, write_dataset(tmp_path / "imu", run, states, mutate(frames)), FakeEphemeris())
    assert any("imu.ref" in p for p in report.problems), report.problems


def test_the_sun_pointing_case_needs_one_valid_light_sensor_frame_per_state(tmp_path):
    run, case = reference_run("r5-sun-pointing"), check.CASES["r5-sun-pointing"]
    eph = FakeEphemeris()
    states, frames = simulate(run, eph, 600, 0.1, irradiance=sun_irradiance(run, eph))
    report = check.check_dataset(case, run, write_dataset(tmp_path / "light", run, states, frames[:1]), eph)
    assert any("light.pz" in p for p in report.problems), report.problems


@pytest.mark.parametrize("field, shift", [("sequence", 1), ("simulation_time_s", 0.1)])
def test_a_state_out_of_step_is_refused(tmp_path, spin_case, field, shift):
    run, case = spin_case
    states, frames = spin_recording(run)
    for state in states:
        state[field] += shift
    report = check.check_dataset(case, run, write_dataset(tmp_path / "shift", run, states, frames), FakeEphemeris())
    assert any(field in p for p in report.problems), report.problems
    assert report.metrics == {}


@pytest.mark.parametrize("field, metric", [("position_ecef_m", "position_m"), ("body_to_ecef_xyzw", "attitude_rad")])
def test_a_non_finite_value_fails_instead_of_vanishing_in_the_maximum(tmp_path, spin_case, field, metric):
    run, case = spin_case
    states, frames = spin_recording(run)
    states[3000][field] = [float("nan")] * len(states[3000][field])
    report = check.check_dataset(case, run, write_dataset(tmp_path / "nan", run, states, frames), FakeEphemeris())
    assert any(metric in p for p in report.problems), report.metrics


def test_a_manifest_device_with_another_mount_is_refused(tmp_path, spin_case):
    run, case = spin_case
    states, frames = spin_recording(run)
    directory = write_dataset(tmp_path / "mount", run, states, frames)
    manifest = json.loads((directory / "manifest.json").read_text())
    manifest["devices"][0]["mount"]["device_to_body_xyzw"] = [0.0, 0.0, 0.0, 1.0]
    (directory / "manifest.json").write_text(json.dumps(manifest))
    report = check.check_dataset(case, run, directory, FakeEphemeris())
    assert any("device_to_body_xyzw" in p for p in report.problems), report.problems


def test_a_case_of_an_unknown_kind_is_rejected():
    with pytest.raises(ValueError, match="kind"):
        check.Case(run_file="(test)", duration_s=60.0, step_s=0.1, kind="spinn")


def test_a_metric_without_a_tolerance_is_an_error_not_a_pass(tmp_path, orbit_case):
    run, case = orbit_case
    states, _ = simulate(run, FakeEphemeris(), 7200, 1.0)
    tolerances = {k: v for k, v in check.TOLERANCES.items() if k != "velocity_m_s"}
    with pytest.raises(ValueError, match="velocity_m_s"):
        check.check_dataset(case, run, write_dataset(tmp_path / "tol", run, states), FakeEphemeris(), tolerances)


def test_r4_refuses_a_recording_that_is_not_a_complete_r1_orbit_dt1_run(tmp_path, capsys):
    run = reference_run("r1-orbit-dt1")
    states, _ = simulate(run, FakeEphemeris(), 600, 1.0)  # 10 min of a 24 h case
    directory = write_dataset(tmp_path / "r4", run, states)
    assert check.main(["r4", str(directory)]) == 1
    assert "states" in capsys.readouterr().out


# --- Values an angle can't see, and sets that must stay closed.

def scaled(values, factor):
    return [v * factor for v in values]


@pytest.mark.parametrize("field, change, metric", [
    ("body_to_ecef_xyzw", lambda q, env: [0.0, 0.0, 0.0, 0.0], "attitude_rad"),         # an unfilled quaternion
    ("body_to_ecef_xyzw", lambda q, env: scaled(q, 2.0), "attitude_rad"),               # not a unit quaternion
    ("j2000_to_itrf93_xyzw", lambda q, env: [0.0, 0.0, 0.0, 0.0], "frame_rad"),
    ("sun_position_itrf93_m", lambda q, env: [0.0, 0.0, 0.0], "sun_position_rel"),      # Earth's state read instead
    ("sun_position_itrf93_m", lambda q, env: scaled(env["sun_position_itrf93_m"], 1e-3), "sun_position_rel"),  # km
], ids=["zero-attitude", "attitude-x2", "zero-frame", "zero-sun", "sun-in-km"])
def test_a_zero_or_wrongly_scaled_value_fails(tmp_path, spin_case, field, change, metric):
    run, case = spin_case
    states, frames = spin_recording(run)
    for state in states:
        env = state["environment"]
        if field == "body_to_ecef_xyzw":
            state[field] = change(state[field], env)
        else:
            env[field] = change(env[field], env)
    report = check.check_dataset(case, run, write_dataset(tmp_path / "scale", run, states, frames), FakeEphemeris())
    assert any(metric in p for p in report.problems), report.metrics


def test_a_manifest_listing_a_device_twice_is_refused(tmp_path, spin_case):
    run, case = spin_case
    states, frames = spin_recording(run)
    directory = write_dataset(tmp_path / "twice", run, states, frames)
    manifest = json.loads((directory / "manifest.json").read_text())
    wrong = json.loads(json.dumps(manifest["devices"][0]))
    wrong["mount"]["position_body_m"] = [9.0, 9.0, 9.0]
    manifest["devices"].insert(0, wrong)
    (directory / "manifest.json").write_text(json.dumps(manifest))
    report = check.check_dataset(case, run, directory, FakeEphemeris())
    assert any("imu.ref" in p for p in report.problems), report.problems


def test_frames_from_a_sensor_the_run_file_does_not_declare_are_refused(tmp_path, spin_case):
    run, case = spin_case
    states, frames = spin_recording(run)
    ghosts = [{**f, "sensor_id": "ghost"} for f in frames[:10]]
    report = check.check_dataset(case, run, write_dataset(tmp_path / "ghost", run, states, frames + ghosts), FakeEphemeris())
    assert any("ghost" in p for p in report.problems), report.problems


def test_a_kind_with_no_comparison_of_its_own_is_an_error(tmp_path, spin_case, monkeypatch):
    run, _ = spin_case
    monkeypatch.setitem(check.EXPECTED_METRICS, "spin_imu", check.EXPECTED_METRICS["spin"])
    monkeypatch.setattr(check, "KINDS", (*check.KINDS, "spin_imu"))
    case = check.Case(run_file="(test)", duration_s=600.0, step_s=0.1, kind="spin_imu")  # listed, never dispatched
    states, frames = spin_recording(run)
    with pytest.raises(ValueError, match="spin_imu"):
        check.check_dataset(case, run, write_dataset(tmp_path / "kind", run, states, frames), FakeEphemeris())


def r4_inputs(tmp_path, csv_hours, offset_m=0.0):
    """An hour of r1-orbit-dt1 at 1 s and a fixture CSV of `csv_hours`, every 60 s, on Basilisk's own positions."""
    run = reference_run("r1-orbit-dt1")
    states, _ = simulate(run, FakeEphemeris(), 3600, 1.0)
    directory = write_dataset(tmp_path / "r4", run, states)
    rows = ["t_s,x_m,y_m,z_m"]
    for k in range(0, round(csv_hours * 3600) + 1, 60):
        x, y, z = states[min(k, 3600)]["position_ecef_m"]
        rows.append(f"{k},{float(x) + (offset_m if k == 0 else 0.0)!r},{float(y)!r},{float(z)!r}")
    (tmp_path / "fixture.csv").write_text("\n".join(rows) + "\n")
    return directory, tmp_path / "fixture.csv"


def test_r4_refuses_a_fixture_that_does_not_cover_the_day(tmp_path):
    directory, csv = r4_inputs(tmp_path, csv_hours=1)
    with pytest.raises(ValueError, match="every 60 s"):
        check.r4_deviation(directory, csv, 1.0)


def test_r4_refuses_a_fixture_that_does_not_start_on_the_recorded_orbit(tmp_path):
    directory, csv = r4_inputs(tmp_path, csv_hours=24, offset_m=1000.0)
    with pytest.raises(ValueError, match="start"):
        check.r4_deviation(directory, csv, 1.0)


# --- The UTC stamp, the drift case's start, and device kinds no comparison covers.

def test_a_utc_stamp_off_the_run_epoch_is_refused(tmp_path, spin_case):
    run, case = spin_case
    states, frames = spin_recording(run)
    for k, state in enumerate(states):
        state["timestamp_utc"] = utc_stamp(run["epochUtc"], k * 0.1 + 69.0)  # TDB read as UTC: about 69 s late
    report = check.check_dataset(case, run, write_dataset(tmp_path / "utc", run, states, frames), FakeEphemeris())
    assert any("timestamp_utc" in p for p in report.problems), report.problems
    assert report.metrics == {}


@pytest.mark.parametrize("bug, passes", [(None, True), ("conjugated q0", False)])
def test_the_drift_case_judges_its_starting_attitude(tmp_path, bug, passes):
    run = json.loads(json.dumps(reference_run("r2-long-q0-dt1")))
    case = check.Case(run_file="(test)", duration_s=60.0, step_s=1.0, kind="spin_drift")
    states, _ = simulate(run, FakeEphemeris(), 60, 1.0, attitude=spin_attitude(run, bug))
    report = check.check_dataset(case, run, write_dataset(tmp_path / "drift", run, states), FakeEphemeris())
    assert (report.problems == []) is passes, report.problems
    if not passes:
        assert any("attitude_start_rad" in p for p in report.problems), report.problems


def test_a_device_kind_the_case_does_not_compare_is_an_error(tmp_path, orbit_case):
    run, case = orbit_case
    run["devices"] = reference_run("r2-spin")["devices"]  # an IMU in an orbit case would go unchecked
    states, _ = simulate(run, FakeEphemeris(), 7200, 1.0)
    with pytest.raises(ValueError, match="imu"):
        check.check_dataset(case, run, write_dataset(tmp_path / "dev", run, states), FakeEphemeris())


KERNEL_DATA = ROOT  # Argus.Basilisk/, as the service uses it
_KERNEL_SET = json.loads((ROOT / "kernel_sets" / "argus-2026-11-02.json").read_text())
_HAVE_KERNELS = all((ROOT / "kernels" / entry["file"]).exists()
                    for entry in _KERNEL_SET["kernels"] + _KERNEL_SET.get("support_files", []))  # all load_kernel_set checks


@pytest.mark.skipif(not _HAVE_KERNELS, reason="needs the fetched argus-2026-11-02 kernels")
def test_the_spice_ephemeris_gives_a_rotating_earth_and_the_june_sun():
    pytest.importorskip("spiceypy")
    eph = check.SpiceEphemeris("argus-2026-11-02", KERNEL_DATA)
    et = eph.et_of("2026-06-01T12:00:00Z")
    frame, frame_dot = eph.frame(et)
    np.testing.assert_allclose(frame @ frame.T, np.eye(3), atol=1e-15)
    after, _ = eph.frame(et + 1.0)
    before, _ = eph.frame(et - 1.0)
    # sxform's rate block is the derivative of the rotation: a 1 s central difference agrees to about 6e-14
    # (smaller steps are swamped by rounding in the differences).
    np.testing.assert_allclose(frame_dot, (after - before) / 2.0, atol=1e-12)
    omega = -frame_dot @ frame.T
    assert omega[1, 0] == pytest.approx(7.2921e-5, rel=1e-4)  # Earth's rate about +Z
    distance = np.linalg.norm(eph.sun_j2000(et)) / check.AU_M
    assert 1.012 < distance < 1.016  # 1 June: about 1.014 AU


# --- R4: the analytic fixture's deviation. The fixture starts on the orbit Basilisk is on at t = 0, in ITRF93 axes
# frozen at t = 0, so what remains is the difference in physics, not a frame offset.

def fixture_state(altitude, inclination, raan, phase, earth_rate):
    """CircularOrbitModel's state at t = 0 (its Earth angle is 0 there), in its own Earth-fixed axes."""
    radius = 6378137.0 + altitude
    n = math.sqrt(3.986004418e14 / radius ** 3)
    r_orb = radius * np.array([math.cos(phase), math.sin(phase), 0.0])
    v_orb = radius * n * np.array([-math.sin(phase), math.cos(phase), 0.0])
    rx = np.array([[1, 0, 0], [0, math.cos(inclination), -math.sin(inclination)], [0, math.sin(inclination), math.cos(inclination)]])
    rz = np.array([[math.cos(raan), -math.sin(raan), 0], [math.sin(raan), math.cos(raan), 0], [0, 0, 1]])
    r, v_inertial = rz @ rx @ r_orb, rz @ rx @ v_orb
    return r, v_inertial - np.cross([0.0, 0.0, earth_rate], r)


def test_fixture_parameters_recover_the_circular_orbit_basilisk_starts_on():
    altitude, inclination, raan, phase = 580e3, math.radians(97.75), math.radians(234.06), math.radians(155.7)
    r, v = fixture_state(altitude, inclination, raan, phase, 7.292115e-5)
    state = {"position_ecef_m": list(r), "velocity_ecef_m_s": list(v),
             "environment": {"earth_angular_velocity_itrf93_rad_s": [0.0, 0.0, 7.292115e-5]}}
    got = check.fixture_parameters(state)
    assert got["altitude_m"] == pytest.approx(altitude, abs=1e-6)
    for key, expected in (("inclination_deg", inclination), ("raan_deg", raan), ("phase_deg", phase)):
        assert math.radians(got[key]) == pytest.approx(expected, abs=1e-12), key


def test_ric_splits_a_position_error_into_radial_along_track_and_cross_track():
    r, v = np.array([7.0e6, 0.0, 0.0]), np.array([0.0, 7.5e3, 0.0])
    radial, along, cross = check.ric(np.array([7.0e6 + 3.0, -2.0, 5.0]), r, v)
    assert (radial, along, cross) == pytest.approx((3.0, -2.0, 5.0), abs=1e-9)
