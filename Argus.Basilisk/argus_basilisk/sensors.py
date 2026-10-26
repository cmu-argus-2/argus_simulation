"""P0 sensor modules (README §9).

Build order step 2 (README §12): the IMU. The magnetometer (BSK-5, #18) and the light sensors
(BSK-6, #19) are rejected by validation until they are built.

Each Argus sensor owns its Basilisk modules and the readers subscribed to their output messages
at build time (§6). The Scenario adds the modules to the sensor task for their period, checks the
readers' freshness and reads each measurement.
"""

import math

import numpy as np
from Basilisk.simulation import imuSensor
from Basilisk.utilities import RigidBodyKinematics as rbk

from . import seeds
from .contracts import sensors_pb, types_pb

__all__ = ["Imu", "build_sensors"]


class Imu:
    """One Argus IMU as two imuSensor modules: gyroscope noise only and accelerometer noise only.

    bsk 2.11.1 seeds both channels of one imuSensor from a single RNGSeed, so each channel gets its
    own module and seed stream. Both modules share the mount and the deterministic errors; the
    gyroscope module supplies AngVelPlatform and the accelerometer module AccelPlatform.
    """

    kind = "imu"

    def __init__(self, sensor, random_seed: int, spacecraft_state_msg):
        self.sensor_id = sensor.definition.sensor_id
        self.sample_period_ns = sensor.definition.sample_period_ns
        period_s = self.sample_period_ns / 1e9
        self.gyroscope = _imu_module(
            f"imu[{self.sensor_id}].gyro", sensor, period_s, random_seed, seeds.GYRO, spacecraft_state_msg)
        self.accelerometer = _imu_module(
            f"imu[{self.sensor_id}].accel", sensor, period_s, random_seed, seeds.ACCEL, spacecraft_state_msg)
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

    def measurement(self) -> sensors_pb.ImuMeasurement:
        return sensors_pb.ImuMeasurement(
            angular_velocity_sensor_rad_per_s=_vector(self._gyroscope_reader().AngVelPlatform),
            specific_force_sensor_m_per_s2=_vector(self._accelerometer_reader().AccelPlatform),
        )


def build_sensors(configuration, spacecraft_state_msg) -> list:
    """The Argus sensors of a validated configuration, in configuration order."""
    built = []
    for sensor in configuration.sensors:
        kind = sensor.WhichOneof("profile")
        if kind != Imu.kind:
            raise ValueError(f"sensor kind {kind!r} is not built yet (sensor_id {sensor.definition.sensor_id!r})")
        built.append(Imu(sensor, configuration.random_seed, spacecraft_state_msg))
    return built


def _imu_module(model_tag: str, sensor, period_s: float, random_seed: int, stream: str, spacecraft_state_msg):
    """An imuSensor with the full profile, and noise only on the channel of its stream."""
    definition = sensor.definition
    gyroscope = sensor.imu.gyroscope
    accelerometer = sensor.imu.accelerometer

    module = imuSensor.ImuSensor()
    module.ModelTag = model_tag
    module.RNGSeed = seeds.rng_seed(random_seed, definition.sensor_id, stream)
    module.scStateInMsg.subscribeTo(spacecraft_state_msg)

    # Mount (§9): dcm_PB = EP2C([w, x, y, z]) of sensor_to_body; the lever arm feeds AccelPlatform.
    rotation = definition.mount.sensor_to_body
    module.dcm_PB = rbk.EP2C(np.array([rotation.w, rotation.x, rotation.y, rotation.z]))
    module.sensorPos_B = _array(definition.mount.position_body_m)

    # Noise: every A matrix is set explicitly, since the module defaults differ per channel.
    gyroscope_noise = _gauss_markov(
        gyroscope.noise_density_rad_per_s_per_sqrt_hz, gyroscope.bias_random_walk_rad_per_s_per_sqrt_s, period_s)
    accelerometer_noise = _gauss_markov(
        accelerometer.noise_density_m_per_s2_per_sqrt_hz, accelerometer.bias_random_walk_m_per_s2_per_sqrt_s,
        period_s)
    silent = (np.zeros((3, 3)), np.zeros((3, 3)))
    module.PMatrixGyro, gyroscope_a = gyroscope_noise if stream == seeds.GYRO else silent
    module.PMatrixAccel, accelerometer_a = accelerometer_noise if stream == seeds.ACCEL else silent
    module.setAMatrixGyro(gyroscope_a)
    module.setAMatrixAccel(accelerometer_a)
    # A bound of 0 leaves the walk unbounded. setErrorBounds* is the same bound and is never called.
    module.setWalkBoundsGyro(np.full(3, gyroscope.bias_random_walk_bound_rad_per_s))
    module.setWalkBoundsAccel(np.full(3, accelerometer.bias_random_walk_bound_m_per_s2))

    # Deterministic errors: scale (true + noise + bias), quantised, then clipped.
    module.senRotBias = _array(gyroscope.constant_bias_sensor_rad_per_s)
    module.senTransBias = _array(accelerometer.constant_bias_sensor_m_per_s2)
    module.gyroScale = _array(gyroscope.scale_factor_per_axis)
    module.accelScale = _array(accelerometer.scale_factor_per_axis)
    module.setLSBs(accelerometer.resolution_m_per_s2, gyroscope.resolution_rad_per_s)
    module.senRotMax = gyroscope.range_rad_per_s
    module.senTransMax = accelerometer.range_m_per_s2
    return module


def _gauss_markov(noise_density: float, random_walk: float, period_s: float) -> tuple:
    """(P, A) for one channel (sensors.proto): white noise N / sqrt(T) with A = 0, or a bias random
    walk K sqrt(T) with A = I. Validation rejects a channel that sets both."""
    if random_walk > 0.0:
        return random_walk * math.sqrt(period_s) * np.eye(3), np.eye(3)
    return noise_density / math.sqrt(period_s) * np.eye(3), np.zeros((3, 3))


def _array(vector) -> np.ndarray:
    return np.array([vector.x, vector.y, vector.z])


def _vector(values) -> types_pb.Vector3:
    x, y, z = values
    return types_pb.Vector3(x=x, y=y, z=z)
