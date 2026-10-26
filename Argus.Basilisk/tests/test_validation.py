"""ConfigureRun and Step-command validation over the generated contracts; no Basilisk install."""

from datetime import datetime, timezone
import math
from pathlib import Path
import subprocess
import sys

import pytest

from argus_basilisk.contracts import service_pb
from argus_basilisk.validation import (
    WMM2025_VALIDITY,
    UtcWindow,
    ValidationError,
    check_utc_in_range,
    configure_run_problems,
    step_command_problems,
    timestamp_to_ns,
    validate_configure_run,
    validate_step_command,
)
from argus.sim.v1 import commands_pb2, run_configuration_pb2, sensors_pb2

ALL_KINDS = frozenset({"imu", "magnetometer", "light_sensor"})

# coverage_utc of kernel_sets/argus-2026-10-20.json, the P0 kernel set.
COVERAGE = UtcWindow(
    start=datetime(1962, 1, 20, tzinfo=timezone.utc),
    end=datetime(2026, 8, 6, tzinfo=timezone.utc),
)

X = (1.0, 0.0, 0.0)
Y = (0.0, 1.0, 0.0)
Z = (0.0, 0.0, 1.0)


def neg(v):
    return tuple(-c for c in v)


def from_basis(x_axis, y_axis, z_axis):
    """Core Quaterniond.FromBasis: (x, y, z, w) of the rotation whose columns are the axes."""
    m00, m01, m02 = x_axis[0], y_axis[0], z_axis[0]
    m10, m11, m12 = x_axis[1], y_axis[1], z_axis[1]
    m20, m21, m22 = x_axis[2], y_axis[2], z_axis[2]
    trace = m00 + m11 + m22
    if trace > 0.0:
        s = math.sqrt(trace + 1.0) * 2.0
        q = ((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25 * s)
    elif m00 > m11 and m00 > m22:
        s = math.sqrt(1.0 + m00 - m11 - m22) * 2.0
        q = (0.25 * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s)
    elif m11 > m22:
        s = math.sqrt(1.0 + m11 - m00 - m22) * 2.0
        q = ((m01 + m10) / s, 0.25 * s, (m12 + m21) / s, (m02 - m20) / s)
    else:
        s = math.sqrt(1.0 + m22 - m00 - m11) * 2.0
        q = ((m02 + m20) / s, (m12 + m21) / s, 0.25 * s, (m10 - m01) / s)
    norm = math.sqrt(sum(c * c for c in q))
    return tuple(c / norm for c in q)


def set_vector(message, values):
    message.x, message.y, message.z = values


def set_quaternion(message, values):
    message.x, message.y, message.z, message.w = values


def definition(sensor_id, position=(0.0, 0.0, 0.0), sensor_to_body=(0.0, 0.0, 0.0, 1.0)):
    result = sensors_pb2.SensorDefinition(sensor_id=sensor_id, sample_period_ns=100_000_000)
    set_vector(result.mount.position_body_m, position)
    set_quaternion(result.mount.sensor_to_body, sensor_to_body)
    return result


def light_sensor(sensor_id, position, sensor_x, sensor_y, sensor_z):
    sensor = sensors_pb2.SensorConfiguration(
        definition=definition(sensor_id, position, from_basis(sensor_x, sensor_y, sensor_z)))
    profile = sensor.light_sensor
    profile.field_of_view_half_angle_rad = math.pi / 2.0
    profile.scale_factor = 1.0
    profile.noise_std_w_per_m2 = 1.0
    profile.bias_w_per_m2 = 0.0
    profile.saturation_w_per_m2 = 2000.0
    return sensor


def p0_default_request():
    """headless/Host/runs/p0-default.json as BasiliskProtoMapper sends it."""
    configuration = run_configuration_pb2.RunConfiguration(
        run_id="p0-default",
        fixed_step_ns=100_000_000,
        random_seed=1,
        kernel_set_id="argus-2026-10-20",
    )
    configuration.epoch_utc.FromDatetime(datetime(2025, 1, 15, tzinfo=timezone.utc))

    orbit = configuration.initial_orbit
    orbit.semi_major_axis_m = 6_878_137.0
    orbit.eccentricity = 0.0
    orbit.inclination_deg = 51.6

    spacecraft = configuration.spacecraft
    spacecraft.mass_kg = 1.33
    set_vector(spacecraft.inertia_body_kg_m2.row0, (2.536e-3, 0.0, 0.0))
    set_vector(spacecraft.inertia_body_kg_m2.row1, (0.0, 2.536e-3, 0.0))
    set_vector(spacecraft.inertia_body_kg_m2.row2, (0.0, 0.0, 2.217e-3))
    set_quaternion(spacecraft.initial_body_to_j2000, (0.0, 0.0, 0.0, 1.0))
    set_vector(spacecraft.initial_angular_velocity_body_rad_per_s, (0.0, 0.0, 0.0))

    imu = sensors_pb2.SensorConfiguration(definition=definition("imu.main"))
    gyroscope = imu.imu.gyroscope
    gyroscope.noise_density_rad_per_s_per_sqrt_hz = 1.2e-4
    set_vector(gyroscope.constant_bias_sensor_rad_per_s, (0.0, 0.0, 0.0))
    set_vector(gyroscope.scale_factor_per_axis, (1.0, 1.0, 1.0))
    gyroscope.range_rad_per_s = 4.363
    gyroscope.resolution_rad_per_s = 1.33e-4
    accelerometer = imu.imu.accelerometer
    accelerometer.noise_density_m_per_s2_per_sqrt_hz = 1.8e-3
    set_vector(accelerometer.constant_bias_sensor_m_per_s2, (0.0, 0.0, 0.0))
    set_vector(accelerometer.scale_factor_per_axis, (1.0, 1.0, 1.0))
    accelerometer.range_m_per_s2 = 19.61
    accelerometer.resolution_m_per_s2 = 6.0e-4

    magnetometer = sensors_pb2.SensorConfiguration(definition=definition("mag.main"))
    magnetometer.magnetometer.noise_std_tesla = 0.0
    set_vector(magnetometer.magnetometer.constant_bias_sensor_tesla, (0.0, 0.0, 0.0))
    magnetometer.magnetometer.scale_factor = 1.0
    magnetometer.magnetometer.range_tesla = 1.3e-3

    configuration.sensors.extend([
        imu,
        magnetometer,
        light_sensor("light.px", (0.05, 0.0, 0.0), Y, Z, X),
        light_sensor("light.mx", (-0.05, 0.0, 0.0), neg(Y), Z, neg(X)),
        light_sensor("light.py", (0.0, 0.05, 0.0), Z, X, Y),
        light_sensor("light.my", (0.0, -0.05, 0.0), neg(Z), X, neg(Y)),
        light_sensor("light.pz", (0.0, 0.0, 0.05675), X, Y, Z),
        light_sensor("light.mz", (0.0, 0.0, -0.05675), X, neg(Y), neg(Z)),
    ])
    return service_pb.ConfigureRunRequest(configuration=configuration, real_time_factor=0.0)


def problems_of(request, kinds=ALL_KINDS):
    return configure_run_problems(request, coverage_utc=COVERAGE, supported_sensor_kinds=kinds)


def only_problem(request, kinds=ALL_KINDS):
    problems = problems_of(request, kinds)
    assert len(problems) == 1, problems
    return problems[0]


def test_p0_default_fails_only_with_unimplemented_sensor_kinds():
    request = p0_default_request()
    problems = configure_run_problems(request, coverage_utc=COVERAGE)

    assert [problem.code for problem in problems] == ["UNIMPLEMENTED"] * 8
    kinds = ["imu", "magnetometer"] + ["light_sensor"] * 6
    for index, (problem, kind) in enumerate(zip(problems, kinds)):
        assert problem.details.startswith(f"configuration.sensors[{index}].{kind}: sensor kind {kind!r}")
        assert request.configuration.sensors[index].definition.sensor_id in problem.details

    with pytest.raises(ValidationError) as error:
        validate_configure_run(request, coverage_utc=COVERAGE)
    assert error.value.code == "UNIMPLEMENTED"
    assert error.value.details.count("is not built yet") == 8


def test_p0_default_passes_once_every_kind_is_built():
    assert problems_of(p0_default_request()) == []
    validate_configure_run(p0_default_request(), coverage_utc=COVERAGE, supported_sensor_kinds=ALL_KINDS)


def test_kernel_coverage_dataclass_is_accepted():
    """Any object with start and end works, such as kernels.KernelCoverage."""

    class Coverage:
        start = COVERAGE.start
        end = COVERAGE.end
        reconstructed_end = datetime(2026, 8, 6, tzinfo=timezone.utc)

    assert configure_run_problems(p0_default_request(), coverage_utc=Coverage(), supported_sensor_kinds=ALL_KINDS) == []


INVALID_CASES = [
    # Orbit (Core ClassicalOrbitElements).
    ("altitude as a", lambda r: setattr(r.configuration.initial_orbit, "semi_major_axis_m", 500_000.0),
     "configuration.initial_orbit.semi_major_axis_m"),
    ("periapsis inside Earth", lambda r: setattr(r.configuration.initial_orbit, "eccentricity", 0.1),
     "configuration.initial_orbit.semi_major_axis_m"),
    ("e = 1", lambda r: setattr(r.configuration.initial_orbit, "eccentricity", 1.0),
     "configuration.initial_orbit.eccentricity"),
    ("e < 0", lambda r: setattr(r.configuration.initial_orbit, "eccentricity", -0.01),
     "configuration.initial_orbit.eccentricity"),
    ("i > 180", lambda r: setattr(r.configuration.initial_orbit, "inclination_deg", 180.5),
     "configuration.initial_orbit.inclination_deg"),
    ("NaN RAAN", lambda r: setattr(r.configuration.initial_orbit, "raan_deg", math.nan),
     "configuration.initial_orbit.raan_deg"),
    # Spacecraft (Core SpacecraftConfiguration).
    ("zero mass", lambda r: setattr(r.configuration.spacecraft, "mass_kg", 0.0),
     "configuration.spacecraft.mass_kg"),
    ("asymmetric inertia", lambda r: setattr(r.configuration.spacecraft.inertia_body_kg_m2.row0, "y", 1e-4),
     "configuration.spacecraft.inertia_body_kg_m2: must be symmetric"),
    ("indefinite inertia", lambda r: setattr(r.configuration.spacecraft.inertia_body_kg_m2.row2, "z", -2.217e-3),
     "configuration.spacecraft.inertia_body_kg_m2: must be positive definite"),
    ("non-unit attitude", lambda r: setattr(r.configuration.spacecraft.initial_body_to_j2000, "w", 1.00001),
     "configuration.spacecraft.initial_body_to_j2000"),
    ("infinite rate", lambda r: setattr(r.configuration.spacecraft.initial_angular_velocity_body_rad_per_s, "x", math.inf),
     "configuration.spacecraft.initial_angular_velocity_body_rad_per_s"),
    # Run fields.
    ("blank run_id", lambda r: setattr(r.configuration, "run_id", "  "), "configuration.run_id"),
    ("upper-case kernel set", lambda r: setattr(r.configuration, "kernel_set_id", "Argus-2026-10-20"),
     "configuration.kernel_set_id"),
    ("kernel set starts with a separator", lambda r: setattr(r.configuration, "kernel_set_id", "-argus"),
     "configuration.kernel_set_id"),
    ("kernel set too long", lambda r: setattr(r.configuration, "kernel_set_id", "a" * 65),
     "configuration.kernel_set_id"),
    ("kernel set path", lambda r: setattr(r.configuration, "kernel_set_id", "../argus"),
     "configuration.kernel_set_id"),
    ("negative real_time_factor", lambda r: setattr(r, "real_time_factor", -1.0), "real_time_factor"),
    ("NaN real_time_factor", lambda r: setattr(r, "real_time_factor", math.nan), "real_time_factor"),
    ("bad timestamp nanos", lambda r: setattr(r.configuration.epoch_utc, "nanos", 1_000_000_000),
     "configuration.epoch_utc.nanos"),
    # Required messages and oneofs.
    ("no orbit", lambda r: r.configuration.ClearField("initial_orbit"),
     "configuration.initial_orbit: required field is not set"),
    ("no spacecraft", lambda r: r.configuration.ClearField("spacecraft"),
     "configuration.spacecraft: required field is not set"),
    ("no epoch", lambda r: r.configuration.ClearField("epoch_utc"),
     "configuration.epoch_utc: required field is not set"),
    ("no inertia row", lambda r: r.configuration.spacecraft.inertia_body_kg_m2.ClearField("row1"),
     "configuration.spacecraft.inertia_body_kg_m2.row1: required field is not set"),
    ("no profile", lambda r: r.configuration.sensors[0].ClearField("imu"),
     "configuration.sensors[0].profile: required field is not set"),
    ("no mount", lambda r: r.configuration.sensors[0].definition.ClearField("mount"),
     "configuration.sensors[0].definition.mount: required field is not set"),
    ("no accelerometer", lambda r: r.configuration.sensors[0].imu.ClearField("accelerometer"),
     "configuration.sensors[0].imu.accelerometer: required field is not set"),
    ("no gyro scale", lambda r: r.configuration.sensors[0].imu.gyroscope.ClearField("scale_factor_per_axis"),
     "configuration.sensors[0].imu.gyroscope.scale_factor_per_axis: required field is not set"),
    # Sensor definitions.
    ("duplicate sensor id", lambda r: setattr(r.configuration.sensors[3].definition, "sensor_id", "light.px"),
     "configuration.sensors[3].definition.sensor_id: duplicates configuration.sensors[2]"),
    ("blank sensor id", lambda r: setattr(r.configuration.sensors[1].definition, "sensor_id", ""),
     "configuration.sensors[1].definition.sensor_id: must not be blank"),
    ("zero sample period", lambda r: setattr(r.configuration.sensors[0].definition, "sample_period_ns", 0),
     "configuration.sensors[0].definition.sample_period_ns: must be > 0"),
    ("period off the step grid", lambda r: setattr(r.configuration.sensors[0].definition, "sample_period_ns", 150_000_000),
     "configuration.sensors[0].definition.sample_period_ns: must be a multiple of fixed_step_ns"),
    ("non-unit mount", lambda r: set_quaternion(r.configuration.sensors[2].definition.mount.sensor_to_body, (0, 0, 0, 0)),
     "configuration.sensors[2].definition.mount.sensor_to_body"),
    # Profiles (Core *Profile.IsValid).
    ("zero gyro scale", lambda r: setattr(r.configuration.sensors[0].imu.gyroscope.scale_factor_per_axis, "y", 0.0),
     "configuration.sensors[0].imu.gyroscope.scale_factor_per_axis"),
    ("resolution not below range", lambda r: setattr(r.configuration.sensors[0].imu.accelerometer, "resolution_m_per_s2", 19.61),
     "configuration.sensors[0].imu.accelerometer.resolution_m_per_s2"),
    ("negative magnetometer noise", lambda r: setattr(r.configuration.sensors[1].magnetometer, "noise_std_tesla", -1e-9),
     "configuration.sensors[1].magnetometer.noise_std_tesla"),
    ("zero magnetometer range", lambda r: setattr(r.configuration.sensors[1].magnetometer, "range_tesla", 0.0),
     "configuration.sensors[1].magnetometer.range_tesla"),
    ("zero light saturation", lambda r: setattr(r.configuration.sensors[4].light_sensor, "saturation_w_per_m2", 0.0),
     "configuration.sensors[4].light_sensor.saturation_w_per_m2"),
    # bsk 2.11.1 limits.
    ("gyro noise and random walk", lambda r: setattr(r.configuration.sensors[0].imu.gyroscope, "bias_random_walk_rad_per_s_per_sqrt_s", 1e-6),
     "configuration.sensors[0].imu.gyroscope.bias_random_walk_rad_per_s_per_sqrt_s: bsk 2.11.1 cannot combine"),
    ("accel noise and random walk", lambda r: setattr(r.configuration.sensors[0].imu.accelerometer, "bias_random_walk_m_per_s2_per_sqrt_s", 1e-5),
     "configuration.sensors[0].imu.accelerometer.bias_random_walk_m_per_s2_per_sqrt_s: bsk 2.11.1 cannot combine"),
    ("walk bound without random walk", lambda r: setattr(r.configuration.sensors[0].imu.gyroscope, "bias_random_walk_bound_rad_per_s", 1e-3),
     "configuration.sensors[0].imu.gyroscope.bias_random_walk_bound_rad_per_s: needs"),
    ("light half-angle above pi/2", lambda r: setattr(r.configuration.sensors[2].light_sensor, "field_of_view_half_angle_rad", math.pi / 2.0 + 1e-9),
     "configuration.sensors[2].light_sensor.field_of_view_half_angle_rad: must be in (0, pi/2]"),
]


@pytest.mark.parametrize("mutate, path", [case[1:] for case in INVALID_CASES], ids=[case[0] for case in INVALID_CASES])
def test_invalid_argument_names_the_field(mutate, path):
    request = p0_default_request()
    mutate(request)

    problem = only_problem(request)

    assert problem.code == "INVALID_ARGUMENT"
    assert problem.details.startswith(path), problem.details


def test_bad_value_is_named():
    request = p0_default_request()
    request.configuration.initial_orbit.inclination_deg = 181.25
    assert only_problem(request).details.endswith("got 181.25")


def test_missing_configuration():
    problem = only_problem(service_pb.ConfigureRunRequest())
    assert problem.code == "INVALID_ARGUMENT"
    assert problem.details == "configuration: required field is not set"


def test_random_walk_alone_with_bound_is_valid():
    request = p0_default_request()
    gyroscope = request.configuration.sensors[0].imu.gyroscope
    gyroscope.noise_density_rad_per_s_per_sqrt_hz = 0.0
    gyroscope.bias_random_walk_rad_per_s_per_sqrt_s = 1e-6
    gyroscope.bias_random_walk_bound_rad_per_s = 1e-3
    assert problems_of(request) == []


def test_sample_period_multiple_of_the_step_is_valid():
    request = p0_default_request()
    request.configuration.sensors[0].definition.sample_period_ns = 300_000_000
    assert problems_of(request) == []


def test_magnetometer_noise_is_unimplemented():
    request = p0_default_request()
    request.configuration.sensors[1].magnetometer.noise_std_tesla = 1e-7
    problem = only_problem(request)
    assert problem.code == "UNIMPLEMENTED"
    assert problem.details.startswith("configuration.sensors[1].magnetometer.noise_std_tesla")


def test_pacing_is_unimplemented():
    request = p0_default_request()
    request.real_time_factor = 1.0
    problem = only_problem(request)
    assert problem.code == "UNIMPLEMENTED"
    assert problem.details.startswith("real_time_factor")


def test_invalid_argument_outranks_unimplemented_and_out_of_range():
    request = p0_default_request()
    request.real_time_factor = 2.0
    request.configuration.epoch_utc.FromDatetime(datetime(2027, 1, 1, tzinfo=timezone.utc))
    request.configuration.spacecraft.mass_kg = -1.0

    codes = [problem.code for problem in problems_of(request)]
    assert codes == ["INVALID_ARGUMENT", "UNIMPLEMENTED", "OUT_OF_RANGE"]
    with pytest.raises(ValidationError) as error:
        validate_configure_run(request, coverage_utc=COVERAGE, supported_sensor_kinds=ALL_KINDS)
    assert error.value.code == "INVALID_ARGUMENT"
    assert "configuration.spacecraft.mass_kg" in error.value.details


@pytest.mark.parametrize("epoch", [
    datetime(1962, 1, 19, 23, 59, 59, tzinfo=timezone.utc),
    datetime(2026, 8, 6, 0, 0, 1, tzinfo=timezone.utc),
])
def test_epoch_outside_kernel_coverage_is_out_of_range(epoch):
    request = p0_default_request()
    request.configuration.sensors.pop(1)  # no magnetometer: only kernel coverage applies
    request.configuration.epoch_utc.FromDatetime(epoch)
    problems = problems_of(request)
    assert [problem.code for problem in problems] == ["OUT_OF_RANGE"]
    assert problems[0].details.startswith("configuration.epoch_utc")
    assert "kernel coverage_utc" in problems[0].details


def test_coverage_end_is_inclusive_to_the_nanosecond():
    request = p0_default_request()
    request.configuration.sensors.pop(1)  # no magnetometer: only kernel coverage applies
    request.configuration.epoch_utc.FromDatetime(COVERAGE.end)
    assert problems_of(request) == []

    request.configuration.epoch_utc.nanos = 1
    assert [problem.code for problem in problems_of(request)] == ["OUT_OF_RANGE"]


def test_wmm_window_applies_only_with_a_magnetometer():
    request = p0_default_request()
    request.configuration.epoch_utc.FromDatetime(datetime(2024, 6, 1, tzinfo=timezone.utc))
    problem = only_problem(request)
    assert problem.code == "OUT_OF_RANGE"
    assert "WMM2025 validity" in problem.details

    request.configuration.sensors.pop(1)
    assert problems_of(request) == []


def test_step_time_check_uses_exact_nanoseconds():
    request = p0_default_request()
    epoch_ns = timestamp_to_ns(request.configuration.epoch_utc)
    end_ns = timestamp_to_ns(_timestamp(COVERAGE.end))

    check_utc_in_range(end_ns, "epoch_utc + sim_time_ns", coverage_utc=COVERAGE)
    with pytest.raises(ValidationError) as error:
        check_utc_in_range(end_ns + 1, "epoch_utc + sim_time_ns", coverage_utc=COVERAGE)
    assert error.value.code == "OUT_OF_RANGE"
    assert "2026-08-06T00:00:00.000000001Z" in error.value.details

    with pytest.raises(ValidationError):
        check_utc_in_range(
            epoch_ns + 6 * 365 * 86_400 * 10**9, "epoch_utc + sim_time_ns",
            coverage_utc=UtcWindow(COVERAGE.start, datetime(2100, 1, 1, tzinfo=timezone.utc)),
            wmm_validity=WMM2025_VALIDITY)


def _timestamp(value):
    message = run_configuration_pb2.RunConfiguration()
    message.epoch_utc.FromDatetime(value)
    return message.epoch_utc


def zero_command():
    command = commands_pb2.ActuatorCommand()
    for name in ("reaction_wheel_torque_body_n_m", "magnetorquer_dipole_body_a_m2", "thruster_force_body_n"):
        set_vector(getattr(command, name), (0.0, 0.0, 0.0))
    return command


def test_zero_step_command_is_valid():
    validate_step_command(zero_command())


def test_step_command_needs_every_vector():
    command = zero_command()
    command.ClearField("thruster_force_body_n")
    with pytest.raises(ValidationError) as error:
        validate_step_command(command)
    assert error.value.code == "INVALID_ARGUMENT"
    assert error.value.details == "command.thruster_force_body_n: required field is not set"


def test_non_finite_step_command_is_invalid():
    command = zero_command()
    command.magnetorquer_dipole_body_a_m2.z = math.nan
    problems = step_command_problems(command)
    assert [problem.code for problem in problems] == ["INVALID_ARGUMENT"]


def test_non_zero_step_command_is_unimplemented():
    command = zero_command()
    command.reaction_wheel_torque_body_n_m.x = 1e-4
    with pytest.raises(ValidationError) as error:
        validate_step_command(command)
    assert error.value.code == "UNIMPLEMENTED"
    assert error.value.details.startswith("command.reaction_wheel_torque_body_n_m")


def test_validation_never_imports_basilisk():
    code = (
        "import sys, argus_basilisk.validation; "
        "assert not any(name == 'Basilisk' or name.startswith('Basilisk.') for name in sys.modules)"
    )
    subprocess.run([sys.executable, "-c", code], check=True, cwd=Path(__file__).resolve().parents[1])
