"""ConfigureRun and Step-command validation over the generated contracts; no Basilisk install."""

from datetime import datetime, timezone
import math
from pathlib import Path
import subprocess
import sys

import pytest

from argus_basilisk.contracts import service_pb
from argus_basilisk.devices import KINDS
from argus_basilisk.validation import (
    WMM2025_VALIDITY,
    UtcWindow,
    ValidationError,
    check_utc_in_range,
    configure_run_problems,
    step_command_problems,
    step_request_problems,
    timestamp_to_ns,
    validate_configure_run,
    validate_step_command,
)
from argus.sim.v1 import commands_pb2, devices_pb2, run_configuration_pb2

# coverage_utc of kernel_sets/argus-2026-11-02.json, the P0 kernel set.
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


def set_parameter(device, key, value):
    """A number, or a vector3 given as an (x, y, z) tuple."""
    if isinstance(value, tuple):
        set_vector(device.parameters[key].vector3, value)
    else:
        device.parameters[key].number = value


def device(device_id, kind, parameters, position=(0.0, 0.0, 0.0), device_to_body=(0.0, 0.0, 0.0, 1.0)):
    result = devices_pb2.DeviceConfiguration(device_id=device_id, kind=kind, sample_period_ns=100_000_000)
    set_vector(result.mount.position_body_m, position)
    set_quaternion(result.mount.device_to_body, device_to_body)
    for key, value in parameters.items():
        set_parameter(result, key, value)
    return result


def light_sensor(device_id, position, sensor_x, sensor_y, sensor_z):
    return device(device_id, "light_sensor", {
        "field_of_view_half_angle_rad": math.pi / 2.0,
        "scale_factor": 1.0,
        "noise_std_w_per_m2": 0.0,
        "bias_w_per_m2": 0.0,
        "saturation_w_per_m2": 2000.0,
    }, position, from_basis(sensor_x, sensor_y, sensor_z))


def p0_default_request():
    """headless/Host/runs/p0-default.json as BasiliskProtoMapper sends it."""
    configuration = run_configuration_pb2.RunConfiguration(
        run_id="p0-default",
        fixed_step_ns=100_000_000,
        random_seed=1,
        kernel_set_id="argus-2026-11-02",
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

    imu = device("imu.main", "imu", {
        "gyroscope.noise_density_rad_per_s_per_sqrt_hz": 1.2e-4,
        "gyroscope.bias_random_walk_rad_per_s_per_sqrt_s": 0.0,
        "gyroscope.bias_random_walk_bound_rad_per_s": 0.0,
        "gyroscope.constant_bias_sensor_rad_per_s": (0.0, 0.0, 0.0),
        "gyroscope.scale_factor_per_axis": (1.0, 1.0, 1.0),
        "gyroscope.range_rad_per_s": 4.363,
        "gyroscope.resolution_rad_per_s": 1.33e-4,
        "accelerometer.noise_density_m_per_s2_per_sqrt_hz": 1.8e-3,
        "accelerometer.bias_random_walk_m_per_s2_per_sqrt_s": 0.0,
        "accelerometer.bias_random_walk_bound_m_per_s2": 0.0,
        "accelerometer.constant_bias_sensor_m_per_s2": (0.0, 0.0, 0.0),
        "accelerometer.scale_factor_per_axis": (1.0, 1.0, 1.0),
        "accelerometer.range_m_per_s2": 19.61,
        "accelerometer.resolution_m_per_s2": 6.0e-4,
    })
    magnetometer = device("mag.main", "magnetometer", {
        "noise_std_tesla": 0.0,
        "constant_bias_sensor_tesla": (0.0, 0.0, 0.0),
        "scale_factor": 1.0,
        "range_tesla": 1.3e-3,
    })

    configuration.devices.extend([
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


def problems_of(request):
    return configure_run_problems(request, coverage_utc=COVERAGE)


def only_problem(request):
    problems = problems_of(request)
    assert len(problems) == 1, problems
    return problems[0]


def parameter(request, index, key):
    return request.configuration.devices[index].parameters[key]


def test_registry_holds_every_p0_sensor_kind():
    # The IMU (BSK-4), the magnetometer (BSK-5) and the light sensors (BSK-6) complete the P0 set.
    assert sorted(KINDS) == ["imu", "light_sensor", "magnetometer"]
    assert all(kind.KIND == name for name, kind in KINDS.items())


def test_p0_default_passes():
    assert problems_of(p0_default_request()) == []
    validate_configure_run(p0_default_request(), coverage_utc=COVERAGE)


def test_kernel_coverage_dataclass_is_accepted():
    """Any object with start and end works, such as kernels.KernelCoverage."""

    class Coverage:
        start = COVERAGE.start
        end = COVERAGE.end
        reconstructed_end = datetime(2026, 8, 6, tzinfo=timezone.utc)

    assert configure_run_problems(p0_default_request(), coverage_utc=Coverage()) == []


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
    ("no mount", lambda r: r.configuration.devices[0].ClearField("mount"),
     "configuration.devices[0].mount: required field is not set"),
    ("no mount position", lambda r: r.configuration.devices[0].mount.ClearField("position_body_m"),
     "configuration.devices[0].mount.position_body_m: required field is not set"),
    # Devices.
    ("duplicate device id", lambda r: setattr(r.configuration.devices[3], "device_id", "light.px"),
     "configuration.devices[3].device_id: duplicates configuration.devices[2]"),
    ("blank device id", lambda r: setattr(r.configuration.devices[1], "device_id", ""),
     "configuration.devices[1].device_id: must not be blank"),
    ("zero sample period", lambda r: setattr(r.configuration.devices[0], "sample_period_ns", 0),
     "configuration.devices[0].sample_period_ns: must be > 0"),
    ("period off the step grid", lambda r: setattr(r.configuration.devices[0], "sample_period_ns", 150_000_000),
     "configuration.devices[0].sample_period_ns: must be a multiple of fixed_step_ns"),
    ("non-unit mount", lambda r: set_quaternion(r.configuration.devices[2].mount.device_to_body, (0, 0, 0, 0)),
     "configuration.devices[2].mount.device_to_body"),
    ("unknown kind", lambda r: setattr(r.configuration.devices[1], "kind", "star_tracker"),
     "configuration.devices[1].kind: must be a registered kind (imu, light_sensor, magnetometer), got 'star_tracker'"),
    ("empty kind", lambda r: setattr(r.configuration.devices[0], "kind", ""),
     "configuration.devices[0].kind: must be a registered kind (imu, light_sensor, magnetometer), got ''"),
    ("initial state", lambda r: setattr(r.configuration.devices[0].initial_state["speed_rad_per_s"], "number", 1.0),
     "configuration.devices[0].initial_state: must be empty: sensor kind 'imu' has no state, got ['speed_rad_per_s']"),
    # Parameter maps (the kind's declarations).
    ("unknown key", lambda r: setattr(parameter(r, 1, "noise_density_tesla"), "number", 1e-9),
     'configuration.devices[1].parameters["noise_density_tesla"]: unknown parameter for kind \'magnetometer\', got 1e-09'),
    ("missing key", lambda r: r.configuration.devices[0].parameters.pop("gyroscope.range_rad_per_s"),
     'configuration.devices[0].parameters["gyroscope.range_rad_per_s"]: required field is not set'),
    ("number given as a vector3", lambda r: set_vector(parameter(r, 1, "range_tesla").vector3, (1.0, 1.0, 1.0)),
     'configuration.devices[1].parameters["range_tesla"]: must be a number, got \'vector3\''),
    ("vector3 given as a number", lambda r: setattr(parameter(r, 0, "gyroscope.scale_factor_per_axis"), "number", 1.0),
     'configuration.devices[0].parameters["gyroscope.scale_factor_per_axis"]: must be a vector3, got \'number\''),
    ("value with no case", lambda r: parameter(r, 4, "scale_factor").Clear(),
     'configuration.devices[4].parameters["scale_factor"]: must be a number, got \'no value\''),
    ("NaN number", lambda r: setattr(parameter(r, 4, "bias_w_per_m2"), "number", math.nan),
     'configuration.devices[4].parameters["bias_w_per_m2"]: must be finite, got nan'),
    ("infinite vector component", lambda r: setattr(parameter(r, 1, "constant_bias_sensor_tesla").vector3, "z", math.inf),
     'configuration.devices[1].parameters["constant_bias_sensor_tesla"]: must be finite, got (0.0, 0.0, inf)'),
    # Kind range checks (Core *Profile.IsValid).
    ("zero gyro scale", lambda r: setattr(parameter(r, 0, "gyroscope.scale_factor_per_axis").vector3, "y", 0.0),
     'configuration.devices[0].parameters["gyroscope.scale_factor_per_axis"]: each component must be finite and > 0'),
    ("resolution not below range",
     lambda r: setattr(parameter(r, 0, "accelerometer.resolution_m_per_s2"), "number", 19.61),
     'configuration.devices[0].parameters["accelerometer.resolution_m_per_s2"]: '
     'must be < accelerometer.range_m_per_s2 (19.61)'),
    ("negative magnetometer noise", lambda r: setattr(parameter(r, 1, "noise_std_tesla"), "number", -1e-9),
     'configuration.devices[1].parameters["noise_std_tesla"]: must be finite and >= 0, got -1e-09'),
    ("zero magnetometer range", lambda r: setattr(parameter(r, 1, "range_tesla"), "number", 0.0),
     'configuration.devices[1].parameters["range_tesla"]: must be finite and > 0'),
    ("zero light saturation", lambda r: setattr(parameter(r, 4, "saturation_w_per_m2"), "number", 0.0),
     'configuration.devices[4].parameters["saturation_w_per_m2"]: must be finite and > 0'),
    # bsk 2.11.1 limits.
    ("gyro noise and random walk",
     lambda r: setattr(parameter(r, 0, "gyroscope.bias_random_walk_rad_per_s_per_sqrt_s"), "number", 1e-6),
     'configuration.devices[0].parameters["gyroscope.bias_random_walk_rad_per_s_per_sqrt_s"]: bsk 2.11.1 cannot combine'),
    ("accel noise and random walk",
     lambda r: setattr(parameter(r, 0, "accelerometer.bias_random_walk_m_per_s2_per_sqrt_s"), "number", 1e-5),
     'configuration.devices[0].parameters["accelerometer.bias_random_walk_m_per_s2_per_sqrt_s"]: '
     'bsk 2.11.1 cannot combine'),
    ("walk bound without random walk",
     lambda r: setattr(parameter(r, 0, "gyroscope.bias_random_walk_bound_rad_per_s"), "number", 1e-3),
     'configuration.devices[0].parameters["gyroscope.bias_random_walk_bound_rad_per_s"]: needs'),
    ("light half-angle above pi/2",
     lambda r: setattr(parameter(r, 2, "field_of_view_half_angle_rad"), "number", math.pi / 2.0 + 1e-9),
     'configuration.devices[2].parameters["field_of_view_half_angle_rad"]: must be in (0, pi/2]'),
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
    imu = request.configuration.devices[0]
    set_parameter(imu, "gyroscope.noise_density_rad_per_s_per_sqrt_hz", 0.0)
    set_parameter(imu, "gyroscope.bias_random_walk_rad_per_s_per_sqrt_s", 1e-6)
    set_parameter(imu, "gyroscope.bias_random_walk_bound_rad_per_s", 1e-3)
    assert problems_of(request) == []


def test_sample_period_multiple_of_the_step_is_valid():
    request = p0_default_request()
    request.configuration.devices[0].sample_period_ns = 300_000_000
    assert problems_of(request) == []


def test_magnetometer_noise_is_valid():
    request = p0_default_request()
    set_parameter(request.configuration.devices[1], "noise_std_tesla", 1e-7)
    assert problems_of(request) == []


def test_light_sensor_noise_is_valid():
    request = p0_default_request()
    set_parameter(request.configuration.devices[2], "noise_std_w_per_m2", 1.0)
    assert problems_of(request) == []


def test_device_problems_are_collected_with_their_keys():
    """Every problem of every device is reported, in device and declaration order."""
    request = p0_default_request()
    request.configuration.devices[0].parameters.pop("gyroscope.range_rad_per_s")
    set_parameter(request.configuration.devices[0], "gyroscope.extra", 1.0)
    set_parameter(request.configuration.devices[1], "scale_factor", (1.0, 1.0, 1.0))
    set_parameter(request.configuration.devices[5], "saturation_w_per_m2", -1.0)
    details = [problem.details.split(":")[0] for problem in problems_of(request)]
    assert details == [
        'configuration.devices[0].parameters["gyroscope.extra"]',
        'configuration.devices[0].parameters["gyroscope.range_rad_per_s"]',
        'configuration.devices[1].parameters["scale_factor"]',
        'configuration.devices[5].parameters["saturation_w_per_m2"]',
    ]


def test_an_unknown_kind_skips_the_parameter_checks():
    request = p0_default_request()
    request.configuration.devices[1].kind = "star_tracker"
    request.configuration.devices[1].parameters.pop("range_tesla")
    problem = only_problem(request)
    assert problem.details.startswith("configuration.devices[1].kind")


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
        validate_configure_run(request, coverage_utc=COVERAGE)
    assert error.value.code == "INVALID_ARGUMENT"
    assert "configuration.spacecraft.mass_kg" in error.value.details


@pytest.mark.parametrize("epoch", [
    datetime(1962, 1, 19, 23, 59, 59, tzinfo=timezone.utc),
    datetime(2026, 8, 6, 0, 0, 1, tzinfo=timezone.utc),
])
def test_epoch_outside_kernel_coverage_is_out_of_range(epoch):
    request = p0_default_request()
    request.configuration.devices.pop(1)  # no magnetometer: only kernel coverage applies
    request.configuration.epoch_utc.FromDatetime(epoch)
    problems = problems_of(request)
    assert [problem.code for problem in problems] == ["OUT_OF_RANGE"]
    assert problems[0].details.startswith("configuration.epoch_utc")
    assert "kernel coverage_utc" in problems[0].details


def test_coverage_end_is_inclusive_to_the_nanosecond():
    request = p0_default_request()
    request.configuration.devices.pop(1)  # no magnetometer: only kernel coverage applies
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

    request.configuration.devices.pop(1)
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
    """The empty DeviceCommandSet: no output (docs/device-model.md §6)."""
    return commands_pb2.DeviceCommandSet()


def torque_command(device_id="torque.body", x=1e-4):
    commands = commands_pb2.DeviceCommandSet()
    command = commands.commands.add(device_id=device_id)
    set_vector(command.values["torque_body_n_m"].vector3, (x, 0.0, 0.0))
    return commands


def test_empty_step_command_is_valid():
    validate_step_command(zero_command())


def test_step_command_for_a_device_that_is_not_a_configured_actuator_is_invalid():
    # P0 configures sensors only, and no actuator kind exists yet (CON-2), so no ID is valid.
    with pytest.raises(ValidationError) as error:
        validate_step_command(torque_command("imu.main"))
    assert error.value.code == "INVALID_ARGUMENT"
    assert error.value.details.startswith("commands.commands[0].device_id: is not an actuator this run configures")


def test_step_command_for_a_configured_actuator_is_unimplemented():
    with pytest.raises(ValidationError) as error:
        validate_step_command(torque_command(), actuator_ids=frozenset({"torque.body"}))
    assert error.value.code == "UNIMPLEMENTED"
    assert error.value.details.startswith("commands.commands[0]: applying actuator commands is not built yet")


def test_malformed_step_commands_are_invalid():
    unnamed = torque_command(device_id="")
    repeated = torque_command()
    repeated.commands.add().CopyFrom(repeated.commands[0])
    non_finite = torque_command(x=math.nan)
    unset = torque_command()
    unset.commands[0].values["torque_body_n_m"].ClearField("vector3")
    actuators = frozenset({"torque.body"})
    assert step_command_problems(unnamed, actuator_ids=actuators)[0].details == (
        "commands.commands[0].device_id: required field is not set")
    assert step_command_problems(repeated, actuator_ids=actuators)[0].details.startswith(
        "commands.commands[1].device_id: repeats commands[0]")
    assert step_command_problems(non_finite, actuator_ids=actuators)[0].details.startswith(
        'commands.commands[0].values["torque_body_n_m"]: must be finite')
    assert step_command_problems(unset, actuator_ids=actuators)[0].details == (
        'commands.commands[0].values["torque_body_n_m"]: required field is not set')


def step(sequence, command_sequence=None, command_time_ns=None):
    request = service_pb.StepRequest(sequence=sequence, sim_time_ns=sequence * 100_000_000, commands=zero_command())
    if command_sequence is not None:
        request.command_sequence = command_sequence
    if command_time_ns is not None:
        request.command_time_ns = command_time_ns
    return request


@pytest.mark.parametrize("request_, path", [
    (step(0, 0, 0), "command_sequence: must be unset in Step(0)"),
    (step(3), "command_sequence: required field is not set"),
    # Command 3 sent in Step(3) would act a step late.
    (step(3, 3, 200_000_000), "command_sequence: expected 2"),
    (step(3, 2, 300_000_000), "command_time_ns: expected 200000000"),
])
def test_step_n_must_carry_command_n_minus_1(request_, path):
    problems = step_request_problems(request_, 100_000_000)
    assert {problem.code for problem in problems} == {"INVALID_ARGUMENT"}
    assert problems[0].details.startswith(path)


def test_step_command_timing_and_zero_step_zero():
    assert step_request_problems(step(0), 100_000_000) == []
    assert step_request_problems(step(3, 2, 200_000_000), 100_000_000) == []

    actuators = frozenset({"torque.body"})
    first = step(0)
    first.commands.CopyFrom(torque_command())
    assert [problem.code for problem in step_request_problems(first, 100_000_000, actuators)] == ["INVALID_ARGUMENT"]

    later = step(3, 2, 200_000_000)
    later.commands.CopyFrom(torque_command())
    assert [problem.code for problem in step_request_problems(later, 100_000_000, actuators)] == ["UNIMPLEMENTED"]
    assert [problem.code for problem in step_request_problems(later, 100_000_000)] == ["INVALID_ARGUMENT"]

    # A command for the wrong step outranks the actuator command it carries.
    later.command_sequence = 3
    assert [problem.code for problem in step_request_problems(later, 100_000_000, actuators)] == [
        "INVALID_ARGUMENT", "UNIMPLEMENTED"]

    unset = step(3, 2, 200_000_000)
    unset.ClearField("commands")
    assert step_request_problems(unset, 100_000_000)[0].details == "commands: required field is not set"


def test_validation_never_imports_basilisk():
    code = (
        "import sys, argus_basilisk.validation; "
        "assert not any(name == 'Basilisk' or name.startswith('Basilisk.') for name in sys.modules)"
    )
    subprocess.run([sys.executable, "-c", code], check=True, cwd=Path(__file__).resolve().parents[1])
