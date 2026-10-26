"""ConfigureRun and Step-command validation (README §3 and §11).

Pure functions over the generated argus.sim.v1 and argus.basilisk.v1 messages. They repeat Core
IsValid so Argus and Basilisk agree on what a valid run is (G5), add the bsk 2.11.1 limits, and
run before anything touches Basilisk. This module never imports Basilisk.
"""

from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
import math
import re
from typing import Protocol

# SensorConfiguration.profile oneof cases the service can build. BSK-5 (#18) adds "magnetometer"
# and BSK-6 (#19) "light_sensor".
SUPPORTED_SENSOR_KINDS: frozenset[str] = frozenset({"imu"})

# Core ClassicalOrbitElements: periapsis above the equator also catches an altitude entered as a.
EARTH_EQUATORIAL_RADIUS_M = 6_378_137.0

# Core Quaterniond.IsUnit.
QUATERNION_UNIT_TOLERANCE = 1e-6

# Core SimulationConfiguration.IsKernelSetId; the service turns the ID into a file name.
KERNEL_SET_ID_PATTERN = re.compile(r"[a-z0-9][a-z0-9._-]{0,63}")

# google.protobuf.Timestamp range: 0001-01-01T00:00:00Z to 9999-12-31T23:59:59Z.
_TIMESTAMP_MIN_SECONDS = -62_135_596_800
_TIMESTAMP_MAX_SECONDS = 253_402_300_799

_NANOSECONDS_PER_SECOND = 1_000_000_000
_UNIX_EPOCH = datetime(1970, 1, 1, tzinfo=timezone.utc)

# Report order: a request is only UNIMPLEMENTED or OUT_OF_RANGE once every field is valid.
_CODE_ORDER = ("INVALID_ARGUMENT", "UNIMPLEMENTED", "OUT_OF_RANGE")


class ValidationError(Exception):
    """A rejected request; the service maps ``code`` to ``grpc.StatusCode[code]``."""

    def __init__(self, code: str, details: str):
        self.code = code
        self.details = details
        super().__init__(details)

    def __repr__(self) -> str:
        return f"ValidationError({self.code!r}, {self.details!r})"


class UtcInterval(Protocol):
    """A closed UTC interval [start, end]; kernels.KernelCoverage satisfies it."""

    @property
    def start(self) -> datetime: ...

    @property
    def end(self) -> datetime: ...


@dataclass(frozen=True)
class UtcWindow:
    """A closed interval [start, end] of timezone-aware UTC datetimes."""

    start: datetime
    end: datetime


# WMM2025 is valid from 2025.0 to 2030.0 (README §3).
WMM2025_VALIDITY = UtcWindow(
    start=datetime(2025, 1, 1, tzinfo=timezone.utc),
    end=datetime(2030, 1, 1, tzinfo=timezone.utc),
)


def validate_configure_run(
    request,
    *,
    coverage_utc: UtcInterval,
    wmm_validity: UtcInterval = WMM2025_VALIDITY,
    supported_sensor_kinds: frozenset[str] = SUPPORTED_SENSOR_KINDS,
) -> None:
    """Raise the first-ranked ValidationError for a ConfigureRunRequest, or return None.

    Args:
        request: argus.basilisk.v1.ConfigureRunRequest.
        coverage_utc: The kernel set's coverage_utc (start and end, inclusive).
        wmm_validity: WMM validity window, checked only when a magnetometer is configured.
        supported_sensor_kinds: Profile oneof cases the service builds.

    The error carries every problem of its code, joined with "; ".
    """
    _raise_first(configure_run_problems(
        request,
        coverage_utc=coverage_utc,
        wmm_validity=wmm_validity,
        supported_sensor_kinds=supported_sensor_kinds,
    ))


def configure_run_problems(
    request,
    *,
    coverage_utc: UtcInterval,
    wmm_validity: UtcInterval = WMM2025_VALIDITY,
    supported_sensor_kinds: frozenset[str] = SUPPORTED_SENSOR_KINDS,
) -> list[ValidationError]:
    """Return every problem with a ConfigureRunRequest, INVALID_ARGUMENT first.

    Children of an unset message are not checked; the unset message is the problem.
    """
    problems = _Problems()
    if not request.HasField("configuration"):
        problems.unset("configuration")
        return problems.ranked()

    _check_real_time_factor(problems, request.real_time_factor, "real_time_factor")

    configuration = request.configuration
    path = "configuration"
    if not configuration.run_id.strip():
        problems.invalid(f"{path}.run_id", "must not be blank", configuration.run_id)
    if configuration.fixed_step_ns == 0:
        problems.invalid(f"{path}.fixed_step_ns", "must be > 0", configuration.fixed_step_ns)
    if not KERNEL_SET_ID_PATTERN.fullmatch(configuration.kernel_set_id):
        problems.invalid(
            f"{path}.kernel_set_id",
            f"must match {KERNEL_SET_ID_PATTERN.pattern}",
            configuration.kernel_set_id,
        )

    if configuration.HasField("initial_orbit"):
        _check_orbit(problems, configuration.initial_orbit, f"{path}.initial_orbit")
    else:
        problems.unset(f"{path}.initial_orbit")

    if configuration.HasField("spacecraft"):
        _check_spacecraft(problems, configuration.spacecraft, f"{path}.spacecraft")
    else:
        problems.unset(f"{path}.spacecraft")

    has_magnetometer = _check_sensors(
        problems,
        configuration.sensors,
        configuration.fixed_step_ns,
        supported_sensor_kinds,
        f"{path}.sensors",
    )

    epoch_path = f"{path}.epoch_utc"
    if not configuration.HasField("epoch_utc"):
        problems.unset(epoch_path)
    elif _check_timestamp(problems, configuration.epoch_utc, epoch_path):
        problems.extend(utc_range_problems(
            timestamp_to_ns(configuration.epoch_utc),
            epoch_path,
            coverage_utc=coverage_utc,
            wmm_validity=wmm_validity if has_magnetometer else None,
        ))

    return problems.ranked()


def validate_step_request(request, fixed_step_ns: int) -> None:
    """Raise for a StepRequest whose command is unset, non-finite, for the wrong step, or
    non-zero (README §3 Step 3). The caller has checked run_id, sequence and sim_time_ns."""
    _raise_first(step_request_problems(request, fixed_step_ns))


def step_request_problems(request, fixed_step_ns: int) -> list[ValidationError]:
    """Return every problem with a StepRequest's command, INVALID_ARGUMENT first.

    Step(n) carries command n-1, which acts over [t_n-1, t_n) (target-architecture §4), so
    command_sequence and command_time_ns must name step n-1. Step(0) follows no command: both
    are unset and the command is zero.
    """
    problems = _Problems()
    sequence = request.sequence
    names = ("command_sequence", "command_time_ns")
    if sequence == 0:
        for name in names:
            if request.HasField(name):
                problems.invalid(name, "must be unset in Step(0), which follows no command", getattr(request, name))
    else:
        expected = {"command_sequence": sequence - 1, "command_time_ns": (sequence - 1) * fixed_step_ns}
        for name in names:
            if not request.HasField(name):
                problems.unset(name)
            elif getattr(request, name) != expected[name]:
                problems.invalid(
                    name, f"expected {expected[name]}, since Step({sequence}) carries command {sequence - 1}",
                    getattr(request, name))

    if not request.HasField("command"):
        problems.unset("command")
    else:
        problems.extend(step_command_problems(request.command, first_step=sequence == 0))
    return problems.ranked()


def validate_step_command(command, path: str = "command", *, first_step: bool = False) -> None:
    """Raise for a Step ActuatorCommand that is unset, non-finite or non-zero (README §3 Step 3)."""
    _raise_first(step_command_problems(command, path, first_step=first_step))


def step_command_problems(command, path: str = "command", *, first_step: bool = False) -> list[ValidationError]:
    """Return every problem with a Step ActuatorCommand, INVALID_ARGUMENT first.

    first_step: the command of Step(0), which acts over no interval, so a non-zero value is
    INVALID_ARGUMENT rather than UNIMPLEMENTED.
    """
    problems = _Problems()
    non_zero = []
    for name in ("reaction_wheel_torque_body_n_m", "magnetorquer_dipole_body_a_m2", "thruster_force_body_n"):
        if not command.HasField(name):
            problems.unset(f"{path}.{name}")
            continue
        vector = getattr(command, name)
        if _check_finite_vector(problems, vector, f"{path}.{name}") and _components(vector) != (0.0, 0.0, 0.0):
            non_zero.append(name)

    for name in non_zero:
        if first_step:
            problems.invalid(
                f"{path}.{name}", "must be zero in Step(0), since no interval ends at t = 0",
                _components(getattr(command, name)))
        else:
            # Lifted by BSK-8 (#21) when actuators exist (G1).
            problems.unimplemented(
                f"{path}.{name}", "non-zero actuator commands are not built yet",
                _components(getattr(command, name)))
    return problems.ranked()


def check_utc_in_range(
    utc_ns: int,
    path: str,
    *,
    coverage_utc: UtcInterval,
    wmm_validity: UtcInterval | None = None,
) -> None:
    """Raise OUT_OF_RANGE when a UTC instant is outside kernel coverage or WMM validity.

    Args:
        utc_ns: Nanoseconds since 1970-01-01T00:00:00Z (see timestamp_to_ns).
        path: Field path named in the error, such as "epoch_utc + sim_time_ns".
        coverage_utc: The kernel set's coverage_utc (start and end, inclusive).
        wmm_validity: WMM validity window, or None when no magnetometer is configured.

    ConfigureRun checks the epoch; Step(n) checks epoch + t_n before executing (README §3 Step 4),
    because CSPICE aborts the whole process outside coverage.
    """
    _raise_first(utc_range_problems(utc_ns, path, coverage_utc=coverage_utc, wmm_validity=wmm_validity))


def utc_range_problems(
    utc_ns: int,
    path: str,
    *,
    coverage_utc: UtcInterval,
    wmm_validity: UtcInterval | None = None,
) -> list[ValidationError]:
    """Return the OUT_OF_RANGE problems check_utc_in_range would raise."""
    problems = _Problems()
    windows = [("kernel coverage_utc", coverage_utc)]
    if wmm_validity is not None:
        windows.append(("WMM2025 validity (magnetometer configured)", wmm_validity))
    for name, window in windows:
        start = _datetime_to_ns(window.start)
        end = _datetime_to_ns(window.end)
        if not start <= utc_ns <= end:
            problems.add(
                "OUT_OF_RANGE",
                f"{path}: {_format_ns(utc_ns)} is outside {name} "
                f"[{_format_ns(start)}, {_format_ns(end)}]",
            )
    return problems.ranked()


def timestamp_to_ns(timestamp) -> int:
    """Return a google.protobuf.Timestamp as exact integer nanoseconds since the Unix epoch."""
    return timestamp.seconds * _NANOSECONDS_PER_SECOND + timestamp.nanos


class _Problems:
    """Collects problems in check order and ranks them by _CODE_ORDER."""

    def __init__(self):
        self._items: list[ValidationError] = []

    def add(self, code: str, details: str) -> None:
        self._items.append(ValidationError(code, details))

    def extend(self, problems: list[ValidationError]) -> None:
        self._items.extend(problems)

    def invalid(self, path: str, reason: str, value: object) -> None:
        self.add("INVALID_ARGUMENT", f"{path}: {reason}, got {value!r}")

    def unset(self, path: str) -> None:
        self.add("INVALID_ARGUMENT", f"{path}: required field is not set")

    def unimplemented(self, path: str, reason: str, value: object) -> None:
        self.add("UNIMPLEMENTED", f"{path}: {reason}, got {value!r}")

    def ranked(self) -> list[ValidationError]:
        # sorted() is stable, so problems of one code keep their check order.
        return sorted(self._items, key=lambda problem: _CODE_ORDER.index(problem.code))


def _raise_first(problems: list[ValidationError]) -> None:
    if not problems:
        return
    code = problems[0].code
    raise ValidationError(code, "; ".join(problem.details for problem in problems if problem.code == code))


def _check_real_time_factor(problems: _Problems, value: float, path: str) -> None:
    if not math.isfinite(value) or value < 0.0:
        problems.invalid(path, "must be finite and >= 0", value)
    elif value > 0.0:
        # Lifted by BSK-7 (#20) when ClockSynch pacing is built (README §5).
        problems.unimplemented(path, "pacing (ClockSynch) is not built yet; use 0", value)


def _check_orbit(problems: _Problems, orbit, path: str) -> None:
    names = (
        "semi_major_axis_m",
        "eccentricity",
        "inclination_deg",
        "raan_deg",
        "argument_of_periapsis_deg",
        "true_anomaly_deg",
    )
    finite = True
    for name in names:
        finite &= _check_finite(problems, getattr(orbit, name), f"{path}.{name}")
    if not finite:
        return

    if not 0.0 <= orbit.inclination_deg <= 180.0:
        problems.invalid(f"{path}.inclination_deg", "must be in [0, 180]", orbit.inclination_deg)
    if not 0.0 <= orbit.eccentricity < 1.0:
        problems.invalid(f"{path}.eccentricity", "must be in [0, 1)", orbit.eccentricity)
        return
    periapsis = orbit.semi_major_axis_m * (1.0 - orbit.eccentricity)
    if not periapsis > EARTH_EQUATORIAL_RADIUS_M:
        problems.invalid(
            f"{path}.semi_major_axis_m",
            f"periapsis a(1 - e) = {periapsis!r} m must exceed {EARTH_EQUATORIAL_RADIUS_M!r} m",
            orbit.semi_major_axis_m,
        )


def _check_spacecraft(problems: _Problems, spacecraft, path: str) -> None:
    if not (math.isfinite(spacecraft.mass_kg) and spacecraft.mass_kg > 0.0):
        problems.invalid(f"{path}.mass_kg", "must be finite and > 0", spacecraft.mass_kg)

    inertia_path = f"{path}.inertia_body_kg_m2"
    if not spacecraft.HasField("inertia_body_kg_m2"):
        problems.unset(inertia_path)
    else:
        _check_inertia(problems, spacecraft.inertia_body_kg_m2, inertia_path)

    if _require(problems, spacecraft, "initial_body_to_j2000", path):
        _check_unit_quaternion(problems, spacecraft.initial_body_to_j2000, f"{path}.initial_body_to_j2000")
    if _require(problems, spacecraft, "initial_angular_velocity_body_rad_per_s", path):
        _check_finite_vector(
            problems,
            spacecraft.initial_angular_velocity_body_rad_per_s,
            f"{path}.initial_angular_velocity_body_rad_per_s",
        )


def _check_inertia(problems: _Problems, matrix, path: str) -> None:
    """Core SpacecraftConfiguration.IsSymmetricPositiveDefinite."""
    rows_set = True
    for name in ("row0", "row1", "row2"):
        rows_set &= _require(problems, matrix, name, path)
    if not rows_set:
        return
    finite = True
    for name in ("row0", "row1", "row2"):
        finite &= _check_finite_vector(problems, getattr(matrix, name), f"{path}.{name}")
    if not finite:
        return

    m = [_components(matrix.row0), _components(matrix.row1), _components(matrix.row2)]
    # Symmetric within 1e-12 of the trace.
    tolerance = 1e-12 * abs(m[0][0] + m[1][1] + m[2][2])
    if (abs(m[0][1] - m[1][0]) > tolerance or
            abs(m[0][2] - m[2][0]) > tolerance or
            abs(m[1][2] - m[2][1]) > tolerance):
        problems.invalid(path, "must be symmetric within 1e-12 of the trace", m)
        return

    # Positive leading principal minors (Sylvester), as Core computes them.
    first_minor = m[0][0]
    second_minor = m[0][0] * m[1][1] - m[0][1] * m[1][0]
    determinant = (
        m[0][0] * (m[1][1] * m[2][2] - m[1][2] * m[2][1])
        + m[0][1] * (m[1][2] * m[2][0] - m[1][0] * m[2][2])
        + m[0][2] * (m[1][0] * m[2][1] - m[1][1] * m[2][0])
    )
    if not (first_minor > 0.0 and second_minor > 0.0 and determinant > 0.0):
        problems.invalid(path, "must be positive definite", m)


def _check_sensors(
    problems: _Problems,
    sensors,
    fixed_step_ns: int,
    supported_sensor_kinds: frozenset[str],
    path: str,
) -> bool:
    """Check every SensorConfiguration and return whether a magnetometer is configured."""
    has_magnetometer = False
    seen_ids: dict[str, int] = {}
    unsupported = []
    for index, sensor in enumerate(sensors):
        sensor_path = f"{path}[{index}]"

        if _require(problems, sensor, "definition", sensor_path):
            _check_definition(problems, sensor.definition, fixed_step_ns, f"{sensor_path}.definition")
            sensor_id = sensor.definition.sensor_id
            if sensor_id in seen_ids:
                problems.invalid(
                    f"{sensor_path}.definition.sensor_id",
                    f"duplicates {path}[{seen_ids[sensor_id]}]",
                    sensor_id,
                )
            else:
                seen_ids[sensor_id] = index

        kind = sensor.WhichOneof("profile")
        if kind is None:
            problems.unset(f"{sensor_path}.profile")
            continue
        profile_path = f"{sensor_path}.{kind}"
        if kind == "imu":
            _check_imu(problems, sensor.imu, profile_path)
        elif kind == "magnetometer":
            has_magnetometer = True
            _check_magnetometer(problems, sensor.magnetometer, profile_path)
        elif kind == "light_sensor":
            _check_light_sensor(problems, sensor.light_sensor, profile_path)

        if kind not in supported_sensor_kinds:
            unsupported.append((profile_path, kind, sensor.definition.sensor_id))

    for profile_path, kind, sensor_id in unsupported:
        problems.unimplemented(profile_path, f"sensor kind {kind!r} is not built yet (sensor_id {sensor_id!r})", kind)
    return has_magnetometer


def _check_definition(problems: _Problems, definition, fixed_step_ns: int, path: str) -> None:
    if not definition.sensor_id.strip():
        problems.invalid(f"{path}.sensor_id", "must not be blank", definition.sensor_id)

    period = definition.sample_period_ns
    if period == 0:
        problems.invalid(f"{path}.sample_period_ns", "must be > 0", period)
    elif fixed_step_ns > 0 and period % fixed_step_ns != 0:
        problems.invalid(
            f"{path}.sample_period_ns",
            f"must be a multiple of fixed_step_ns ({fixed_step_ns})",
            period,
        )

    mount_path = f"{path}.mount"
    if _require(problems, definition, "mount", path):
        mount = definition.mount
        if _require(problems, mount, "position_body_m", mount_path):
            _check_finite_vector(problems, mount.position_body_m, f"{mount_path}.position_body_m")
        if _require(problems, mount, "sensor_to_body", mount_path):
            _check_unit_quaternion(problems, mount.sensor_to_body, f"{mount_path}.sensor_to_body")


# Field names of one imuSensor channel: noise density, random walk, walk bound, constant bias,
# scale factor, range and resolution.
_GYROSCOPE_FIELDS = (
    "noise_density_rad_per_s_per_sqrt_hz",
    "bias_random_walk_rad_per_s_per_sqrt_s",
    "bias_random_walk_bound_rad_per_s",
    "constant_bias_sensor_rad_per_s",
    "scale_factor_per_axis",
    "range_rad_per_s",
    "resolution_rad_per_s",
)
_ACCELEROMETER_FIELDS = (
    "noise_density_m_per_s2_per_sqrt_hz",
    "bias_random_walk_m_per_s2_per_sqrt_s",
    "bias_random_walk_bound_m_per_s2",
    "constant_bias_sensor_m_per_s2",
    "scale_factor_per_axis",
    "range_m_per_s2",
    "resolution_m_per_s2",
)


def _check_imu(problems: _Problems, imu, path: str) -> None:
    if _require(problems, imu, "gyroscope", path):
        _check_imu_channel(problems, imu.gyroscope, _GYROSCOPE_FIELDS, f"{path}.gyroscope")
    if _require(problems, imu, "accelerometer", path):
        _check_imu_channel(problems, imu.accelerometer, _ACCELEROMETER_FIELDS, f"{path}.accelerometer")


def _check_imu_channel(problems: _Problems, channel, fields: tuple[str, ...], path: str) -> None:
    """Core GyroscopeProfile / AccelerometerProfile.IsValid plus the bsk 2.11.1 limits."""
    noise_name, walk_name, bound_name, bias_name, scale_name, range_name, resolution_name = fields
    noise = getattr(channel, noise_name)
    walk = getattr(channel, walk_name)
    bound = getattr(channel, bound_name)
    value_range = getattr(channel, range_name)
    resolution = getattr(channel, resolution_name)

    for name, value in ((noise_name, noise), (walk_name, walk), (bound_name, bound)):
        _check_finite_non_negative(problems, value, f"{path}.{name}")
    if _require(problems, channel, bias_name, path):
        _check_finite_vector(problems, getattr(channel, bias_name), f"{path}.{bias_name}")
    if _require(problems, channel, scale_name, path):
        _check_positive_vector(problems, getattr(channel, scale_name), f"{path}.{scale_name}")
    range_valid = _check_finite_positive(problems, value_range, f"{path}.{range_name}")
    if _check_finite_non_negative(problems, resolution, f"{path}.{resolution_name}") and range_valid:
        if resolution >= value_range:
            problems.invalid(
                f"{path}.{resolution_name}",
                f"must be < {range_name} ({value_range!r})",
                resolution,
            )

    # bsk 2.11.1 keeps one Gauss-Markov state per axis, so white noise and random walk exclude
    # each other, and the walk bound would clip white noise if there were no random walk.
    if noise > 0.0 and walk > 0.0:
        problems.invalid(
            f"{path}.{walk_name}",
            f"bsk 2.11.1 cannot combine it with {noise_name} > 0 ({noise!r}); set one of them to 0",
            walk,
        )
    if bound > 0.0 and not walk > 0.0:
        problems.invalid(
            f"{path}.{bound_name}",
            f"needs {walk_name} > 0 (bsk 2.11.1 would bound white noise instead)",
            bound,
        )


def _check_magnetometer(problems: _Problems, magnetometer, path: str) -> None:
    noise_valid = _check_finite_non_negative(problems, magnetometer.noise_std_tesla, f"{path}.noise_std_tesla")
    if _require(problems, magnetometer, "constant_bias_sensor_tesla", path):
        _check_finite_vector(problems, magnetometer.constant_bias_sensor_tesla, f"{path}.constant_bias_sensor_tesla")
    _check_finite_positive(problems, magnetometer.scale_factor, f"{path}.scale_factor")
    _check_finite_positive(problems, magnetometer.range_tesla, f"{path}.range_tesla")

    # bsk 2.11.1 seeds the magnetometer noise generator once with the default seed (README §9).
    # Lifted by BSK-9 (#22) when seeded magnetometer noise exists.
    if noise_valid and magnetometer.noise_std_tesla > 0.0:
        problems.unimplemented(
            f"{path}.noise_std_tesla",
            "bsk 2.11.1 cannot seed magnetometer noise; use 0",
            magnetometer.noise_std_tesla,
        )


def _check_light_sensor(problems: _Problems, light_sensor, path: str) -> None:
    half_angle = light_sensor.field_of_view_half_angle_rad
    if not (math.isfinite(half_angle) and 0.0 < half_angle <= math.pi / 2.0):
        # coarseSunSensor and albedo gate on cos(angle) >= cos(fov) without clamping, so a wider
        # field adds negative irradiance from behind the aperture.
        problems.invalid(f"{path}.field_of_view_half_angle_rad", "must be in (0, pi/2]", half_angle)
    _check_finite_positive(problems, light_sensor.scale_factor, f"{path}.scale_factor")
    _check_finite_non_negative(problems, light_sensor.noise_std_w_per_m2, f"{path}.noise_std_w_per_m2")
    _check_finite(problems, light_sensor.bias_w_per_m2, f"{path}.bias_w_per_m2")
    _check_finite_positive(problems, light_sensor.saturation_w_per_m2, f"{path}.saturation_w_per_m2")


def _check_timestamp(problems: _Problems, timestamp, path: str) -> bool:
    valid = True
    if not _TIMESTAMP_MIN_SECONDS <= timestamp.seconds <= _TIMESTAMP_MAX_SECONDS:
        problems.invalid(f"{path}.seconds", "must be within 0001-01-01 to 9999-12-31", timestamp.seconds)
        valid = False
    if not 0 <= timestamp.nanos < _NANOSECONDS_PER_SECOND:
        problems.invalid(f"{path}.nanos", "must be in [0, 999999999]", timestamp.nanos)
        valid = False
    return valid


def _require(problems: _Problems, message, name: str, path: str) -> bool:
    if message.HasField(name):
        return True
    problems.unset(f"{path}.{name}")
    return False


def _check_finite(problems: _Problems, value: float, path: str) -> bool:
    if math.isfinite(value):
        return True
    problems.invalid(path, "must be finite", value)
    return False


def _check_finite_non_negative(problems: _Problems, value: float, path: str) -> bool:
    if math.isfinite(value) and value >= 0.0:
        return True
    problems.invalid(path, "must be finite and >= 0", value)
    return False


def _check_finite_positive(problems: _Problems, value: float, path: str) -> bool:
    if math.isfinite(value) and value > 0.0:
        return True
    problems.invalid(path, "must be finite and > 0", value)
    return False


def _check_finite_vector(problems: _Problems, vector, path: str) -> bool:
    components = _components(vector)
    if all(math.isfinite(component) for component in components):
        return True
    problems.invalid(path, "must be finite", components)
    return False


def _check_positive_vector(problems: _Problems, vector, path: str) -> bool:
    components = _components(vector)
    if all(math.isfinite(component) and component > 0.0 for component in components):
        return True
    problems.invalid(path, "each component must be finite and > 0", components)
    return False


def _check_unit_quaternion(problems: _Problems, quaternion, path: str) -> bool:
    components = (quaternion.x, quaternion.y, quaternion.z, quaternion.w)
    if not all(math.isfinite(component) for component in components):
        problems.invalid(path, "must be finite", components)
        return False
    norm = math.sqrt(sum(component * component for component in components))
    if abs(norm - 1.0) > QUATERNION_UNIT_TOLERANCE:
        problems.invalid(path, f"must have unit length within {QUATERNION_UNIT_TOLERANCE} (norm {norm!r})", components)
        return False
    return True


def _components(vector) -> tuple[float, float, float]:
    return (vector.x, vector.y, vector.z)


def _datetime_to_ns(value: datetime) -> int:
    if value.tzinfo is None or value.utcoffset() is None:
        raise ValueError(f"expected a timezone-aware datetime, got {value!r}")
    delta = value - _UNIX_EPOCH
    return (delta.days * 86_400 + delta.seconds) * _NANOSECONDS_PER_SECOND + delta.microseconds * 1_000


def _format_ns(utc_ns: int) -> str:
    """Format exact nanoseconds as yyyy-MM-ddTHH:mm:ss.fffffffffZ without going through microseconds."""
    seconds, nanos = divmod(utc_ns, _NANOSECONDS_PER_SECOND)
    whole = _UNIX_EPOCH + timedelta(seconds=seconds)
    return f"{whole:%Y-%m-%dT%H:%M:%S}.{nanos:09d}Z"
