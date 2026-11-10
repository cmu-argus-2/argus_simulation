"""The imu kind: one Argus IMU as two imuSensor modules (README §9, BSK-4 #17)."""

import math

from .. import seeds
from ._common import NUMBER, VECTOR3, Invalid, array, dcm, non_negative, positive, positive_components, vector3_value

KIND = "imu"

# imuSensor rate channel and specific-force channel. imuSensor keeps one Gauss-Markov state per
# axis, so noise density and random walk exclude each other. README §9 maps each key.
_CHANNELS = {
    "gyroscope": (
        "noise_density_rad_per_s_per_sqrt_hz",
        "bias_random_walk_rad_per_s_per_sqrt_s",
        "bias_random_walk_bound_rad_per_s",
        "constant_bias_sensor_rad_per_s",
        "scale_factor_per_axis",
        "range_rad_per_s",
        "resolution_rad_per_s",
    ),
    "accelerometer": (
        "noise_density_m_per_s2_per_sqrt_hz",
        "bias_random_walk_m_per_s2_per_sqrt_s",
        "bias_random_walk_bound_m_per_s2",
        "constant_bias_sensor_m_per_s2",
        "scale_factor_per_axis",
        "range_m_per_s2",
        "resolution_m_per_s2",
    ),
}
# The type of each channel key, in _CHANNELS order.
_CHANNEL_TYPES = (NUMBER, NUMBER, NUMBER, VECTOR3, VECTOR3, NUMBER, NUMBER)

PARAMETERS = {
    f"{channel}.{name}": value_type
    for channel, names in _CHANNELS.items()
    for name, value_type in zip(names, _CHANNEL_TYPES)
}

OUTPUTS = {
    # AngVelPlatform: body relative to inertial, sensor axes.
    "angular_velocity_sensor_rad_per_s": VECTOR3,
    # AccelPlatform: non-gravitational acceleration at the mount, including the lever-arm terms
    # dw/dt x r + w x (w x r).
    "specific_force_sensor_m_per_s2": VECTOR3,
}


def check(values, invalid: Invalid) -> None:
    """Core GyroscopeProfile / AccelerometerProfile.IsValid plus the imuSensor limits."""
    for channel, names in _CHANNELS.items():
        noise, walk, bound, _, scale, value_range, resolution = (f"{channel}.{name}" for name in names)
        for key in (noise, walk, bound):
            non_negative(values, invalid, key)
        positive_components(values, invalid, scale)
        range_valid = positive(values, invalid, value_range)
        if non_negative(values, invalid, resolution) and range_valid and values[resolution] >= values[value_range]:
            invalid(resolution, f"must be < {value_range} ({values[value_range]!r})", values[resolution])

        # imuSensor keeps one Gauss-Markov state per axis, so white noise and random walk exclude
        # each other, and the walk bound would clip white noise if there were no random walk.
        if noise in values and walk in values and values[noise] > 0.0 and values[walk] > 0.0:
            invalid(walk, f"imuSensor cannot combine it with {noise} > 0 ({values[noise]!r}); set one of them to 0",
                    values[walk])
        if bound in values and walk in values and values[bound] > 0.0 and not values[walk] > 0.0:
            invalid(bound, f"needs {walk} > 0 (imuSensor would bound white noise instead)", values[bound])


def build(device, random_seed: int, inputs) -> "Imu":
    return Imu(device, random_seed, inputs.spacecraft_state_msg)


class Imu:
    """One Argus IMU as two imuSensor modules: gyroscope noise only and accelerometer noise only.

    One imuSensor draws both channels from its single RNGSeed, so each channel gets its own module
    and seed stream. Both modules share the mount and the deterministic errors; the
    gyroscope module supplies AngVelPlatform and the accelerometer module AccelPlatform.
    """

    def __init__(self, device, random_seed: int, spacecraft_state_msg):
        self.device_id = device.device_id
        self.sample_period_ns = device.sample_period_ns
        period_s = self.sample_period_ns / 1e9
        self.gyroscope = _imu_module(
            f"imu[{self.device_id}].gyro", device, period_s, random_seed, seeds.GYRO, spacecraft_state_msg)
        self.accelerometer = _imu_module(
            f"imu[{self.device_id}].accel", device, period_s, random_seed, seeds.ACCEL, spacecraft_state_msg)
        self._gyroscope_reader = self.gyroscope.sensorOutMsg.addSubscriber()
        self._accelerometer_reader = self.accelerometer.sensorOutMsg.addSubscriber()

    @property
    def modules(self) -> tuple:
        return (self.gyroscope, self.accelerometer)

    @property
    def readers(self) -> dict:
        return {
            f"{self.gyroscope.ModelTag}.sensorOutMsg": self._gyroscope_reader,
            f"{self.accelerometer.ModelTag}.sensorOutMsg": self._accelerometer_reader,
        }

    @property
    def ready(self) -> bool:
        """False until both modules have run once; imuSensor's first firing writes a zero message."""
        return bool(self.gyroscope.NominalReady and self.accelerometer.NominalReady)

    def output(self) -> dict:
        return {
            "angular_velocity_sensor_rad_per_s": vector3_value(self._gyroscope_reader().AngVelPlatform),
            "specific_force_sensor_m_per_s2": vector3_value(self._accelerometer_reader().AccelPlatform),
        }


def _imu_module(model_tag: str, device, period_s: float, random_seed: int, stream: str, spacecraft_state_msg):
    """An imuSensor with every parameter, and noise only on the channel of its stream."""
    import numpy as np
    from Basilisk.simulation import imuSensor

    parameters = device.parameters

    def number(key: str) -> float:
        return parameters[key].number

    def vector(key: str):
        return array(parameters[key].vector3)

    module = imuSensor.ImuSensor()
    module.ModelTag = model_tag
    module.RNGSeed = seeds.rng_seed(random_seed, device.device_id, stream)
    module.scStateInMsg.subscribeTo(spacecraft_state_msg)

    # Mount (§9): dcm_PB = EP2C([w, x, y, z]) of device_to_body; the lever arm feeds AccelPlatform.
    module.dcm_PB = dcm(device.mount.device_to_body)
    module.sensorPos_B = array(device.mount.position_body_m)

    # Noise: every A matrix is set explicitly, since the module defaults differ per channel.
    gyroscope_noise = _gauss_markov(
        number("gyroscope.noise_density_rad_per_s_per_sqrt_hz"),
        number("gyroscope.bias_random_walk_rad_per_s_per_sqrt_s"), period_s)
    accelerometer_noise = _gauss_markov(
        number("accelerometer.noise_density_m_per_s2_per_sqrt_hz"),
        number("accelerometer.bias_random_walk_m_per_s2_per_sqrt_s"), period_s)
    silent = (np.zeros((3, 3)), np.zeros((3, 3)))
    module.PMatrixGyro, gyroscope_a = gyroscope_noise if stream == seeds.GYRO else silent
    module.PMatrixAccel, accelerometer_a = accelerometer_noise if stream == seeds.ACCEL else silent
    module.setAMatrixGyro(gyroscope_a)
    module.setAMatrixAccel(accelerometer_a)
    # A bound of 0 leaves the walk unbounded. setErrorBounds* is the same bound and is never called.
    module.setWalkBoundsGyro(np.full(3, number("gyroscope.bias_random_walk_bound_rad_per_s")))
    module.setWalkBoundsAccel(np.full(3, number("accelerometer.bias_random_walk_bound_m_per_s2")))

    # Deterministic errors: scale (true + noise + bias), quantised, then clipped. setLSBs truncates
    # toward zero, so ±1 LSB reads 0 (README §9).
    module.senRotBias = vector("gyroscope.constant_bias_sensor_rad_per_s")
    module.senTransBias = vector("accelerometer.constant_bias_sensor_m_per_s2")
    module.gyroScale = vector("gyroscope.scale_factor_per_axis")
    module.accelScale = vector("accelerometer.scale_factor_per_axis")
    module.setLSBs(number("accelerometer.resolution_m_per_s2"), number("gyroscope.resolution_rad_per_s"))
    module.senRotMax = number("gyroscope.range_rad_per_s")
    module.senTransMax = number("accelerometer.range_m_per_s2")
    return module


def _gauss_markov(noise_density: float, random_walk: float, period_s: float) -> tuple:
    """(P, A) for one channel (README §9): white noise N / sqrt(T) with A = 0, or a bias random
    walk K sqrt(T) with A = I. Validation rejects a channel that sets both."""
    import numpy as np

    if random_walk > 0.0:
        return random_walk * math.sqrt(period_s) * np.eye(3), np.eye(3)
    return noise_density / math.sqrt(period_s) * np.eye(3), np.zeros((3, 3))
