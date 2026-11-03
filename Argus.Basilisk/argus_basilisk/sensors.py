"""P0 sensor modules (README §9).

Build order steps 2 to 4 (README §12): the IMU, the magnetometer and the light sensors, which
complete the P0 sensor set.

Each Argus sensor owns its Basilisk modules and the readers subscribed to their output messages
at build time (§6). The Scenario adds the modules to the sensor task for their period, checks the
readers' freshness and reads each measurement.
"""

import math

import numpy as np
from Basilisk.simulation import coarseSunSensor, imuSensor, magnetometer
from Basilisk.utilities import RigidBodyKinematics as rbk

from . import seeds
from .contracts import sensors_pb, types_pb

__all__ = ["Imu", "LightSensor", "Magnetometer", "build_sensors", "light_sensor_instrument"]

# coarseSunSensor scaleFactor = scale_factor x this, so a face square to the Sun at 1 AU reads
# W/m2 (sensors.proto LightSensorProfile).
SOLAR_IRRADIANCE_W_PER_M2 = 1361.0


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


class Magnetometer:
    """One Argus magnetometer (TAM) reading the MagneticFieldWMM field at the spacecraft.

    tam_S = scale (C_SB C_BN B_N + bias), clipped to +-range. Noise is 0: bsk 2.11.1 seeds the
    module's noise generator once in its constructor, so validation rejects noise_std_tesla > 0
    until BSK-9 (#22), and RNGSeed is left alone.
    """

    kind = "magnetometer"

    def __init__(self, sensor, spacecraft_state_msg, magnetic_field_msg):
        definition = sensor.definition
        profile = sensor.magnetometer
        self.sensor_id = definition.sensor_id
        self.sample_period_ns = definition.sample_period_ns

        self.module = magnetometer.Magnetometer()
        self.module.ModelTag = f"magnetometer[{self.sensor_id}]"
        self.module.stateInMsg.subscribeTo(spacecraft_state_msg)
        self.module.magInMsg.subscribeTo(magnetic_field_msg)
        rotation = definition.mount.sensor_to_body
        self.module.dcm_SB = rbk.EP2C(np.array([rotation.w, rotation.x, rotation.y, rotation.z]))
        # Every error term is set explicitly: the module defaults are a random walk (A = I) and
        # noise -1.
        self.module.senNoiseStd = np.full(3, profile.noise_std_tesla)
        self.module.setAMatrix(np.zeros((3, 3)))
        self.module.walkBounds = np.zeros(3)
        self.module.senBias = _array(profile.constant_bias_sensor_tesla)
        self.module.scaleFactor = profile.scale_factor
        self.module.maxOutput = profile.range_tesla
        self.module.minOutput = -profile.range_tesla
        self._reader = self.module.tamDataOutMsg.addSubscriber()

    @property
    def modules(self) -> tuple:
        return (self.module,)

    @property
    def readers(self) -> dict:
        return {f"{self.module.ModelTag}.tamDataOutMsg": self._reader}

    @property
    def ready(self) -> bool:
        """Always: the magnetometer measures on its first firing, from the field written at t_n."""
        return True

    def measurement(self) -> sensors_pb.MagnetometerMeasurement:
        return sensors_pb.MagnetometerMeasurement(magnetic_field_sensor_tesla=_vector(self._reader().tam_S))


class LightSensor:
    """One Argus light sensor: a coarseSunSensor reporting irradiance in W/m2, with Earth albedo.

    OutputData = scale (cos(theta) x shadow + albedo + noise + bias), clipped to [0, saturation],
    with scale = scale_factor x 1361 W/m2 and theta the angle between the boresight and the Sun.
    """

    kind = "light_sensor"

    def __init__(self, sensor, random_seed: int, spacecraft_state_msg, sun_msg, eclipse_msg, albedo_msg):
        definition = sensor.definition
        profile = sensor.light_sensor
        self.sensor_id = definition.sensor_id
        self.sample_period_ns = definition.sample_period_ns
        field_of_view, boresight_body, position_body = light_sensor_instrument(sensor)
        scale = profile.scale_factor * SOLAR_IRRADIANCE_W_PER_M2

        self.module = coarseSunSensor.CoarseSunSensor()
        self.module.ModelTag = f"css[{self.sensor_id}]"
        self.module.RNGSeed = seeds.rng_seed(random_seed, self.sensor_id, seeds.CSS)
        self.module.stateInMsg.subscribeTo(spacecraft_state_msg)
        self.module.sunInMsg.subscribeTo(sun_msg)
        self.module.sunEclipseInMsg.subscribeTo(eclipse_msg)
        self.module.albedoInMsg.subscribeTo(albedo_msg)
        self.module.fov = field_of_view
        # nHat_B is set directly; setUnitDirectionVectorWithPerturbation is never used.
        self.module.nHat_B = boresight_body
        self.module.r_B = position_body
        self.module.scaleFactor = scale
        # Noise and bias are in units of the unscaled output, so the W/m2 profile values are divided
        # by the scale. Validation keeps the noise at 0: setAMatrix cannot be called from Python in
        # bsk 2.11.1, and the default A makes the noise a random walk (README §9).
        self.module.senNoiseStd = profile.noise_std_w_per_m2 / scale
        self.module.senBias = profile.bias_w_per_m2 / scale
        self.module.maxOutput = profile.saturation_w_per_m2
        self.module.minOutput = 0.0
        self.module.kellyFactor = 0.0
        self._reader = self.module.cssDataOutMsg.addSubscriber()

    @property
    def modules(self) -> tuple:
        return (self.module,)

    @property
    def readers(self) -> dict:
        return {f"{self.module.ModelTag}.cssDataOutMsg": self._reader}

    @property
    def ready(self) -> bool:
        """Always: the sensor measures on its first firing, from the Sun, eclipse and albedo at t_n."""
        return True

    def measurement(self) -> sensors_pb.LightSensorMeasurement:
        return sensors_pb.LightSensorMeasurement(irradiance_w_per_m2=self._reader().OutputData)


def light_sensor_instrument(sensor) -> tuple:
    """(fov, nHat_B, r_B) of a light sensor, for coarseSunSensor and albedo addInstrumentConfig.

    The boresight is sensor +z, so nHat_B is the third row of C_SB = EP2C(sensor_to_body).
    """
    rotation = sensor.definition.mount.sensor_to_body
    c_sb = rbk.EP2C(np.array([rotation.w, rotation.x, rotation.y, rotation.z]))
    return (
        sensor.light_sensor.field_of_view_half_angle_rad,
        np.array(c_sb[2, :]),
        _array(sensor.definition.mount.position_body_m),
    )


def build_sensors(
        configuration, spacecraft_state_msg, magnetic_field_msg=None, *,
        sun_msg=None, eclipse_msg=None, albedo_msgs=()) -> list:
    """The Argus sensors of a validated configuration, in configuration order.

    magnetic_field_msg is the MagneticFieldWMM output for the spacecraft, required with a
    magnetometer. Light sensors need the SPICE Sun, the eclipse output and one albedo output each,
    albedo_msgs[i] for the i-th light sensor in configuration order.
    """
    built = []
    light_sensor_index = 0
    for sensor in configuration.sensors:
        kind = sensor.WhichOneof("profile")
        sensor_id = sensor.definition.sensor_id
        if kind == Imu.kind:
            built.append(Imu(sensor, configuration.random_seed, spacecraft_state_msg))
        elif kind == Magnetometer.kind:
            if magnetic_field_msg is None:
                raise ValueError(f"magnetometer {sensor_id!r} needs the MagneticFieldWMM output")
            built.append(Magnetometer(sensor, spacecraft_state_msg, magnetic_field_msg))
        elif kind == LightSensor.kind:
            if sun_msg is None or eclipse_msg is None or light_sensor_index >= len(albedo_msgs):
                raise ValueError(f"light sensor {sensor_id!r} needs the Sun, eclipse and albedo outputs")
            built.append(LightSensor(
                sensor, configuration.random_seed, spacecraft_state_msg, sun_msg, eclipse_msg,
                albedo_msgs[light_sensor_index]))
            light_sensor_index += 1
        else:
            raise ValueError(f"sensor kind {kind!r} is not built yet (sensor_id {sensor_id!r})")
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
