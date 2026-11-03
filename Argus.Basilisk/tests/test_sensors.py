"""IMU, magnetometer and light-sensor modules (README §9, §10) without SPICE; needs bsk==2.11.1 but no kernels."""

import math

import numpy as np
import pytest

pytest.importorskip("Basilisk")

from Basilisk.architecture import astroConstants, messaging  # noqa: E402
from Basilisk.simulation import extForceTorque, gravityEffector, spacecraft  # noqa: E402
from Basilisk.utilities import RigidBodyKinematics as rbk  # noqa: E402
from Basilisk.utilities import SimulationBaseClass  # noqa: E402

from Basilisk.simulation import coarseSunSensor  # noqa: E402

from argus_basilisk.seeds import ACCEL, CSS, GYRO, rng_seed  # noqa: E402
from argus_basilisk.sensors import (  # noqa: E402
    Imu, LightSensor, Magnetometer, SOLAR_IRRADIANCE_W_PER_M2, build_sensors, light_sensor_instrument)
from test_validation import p0_default_request, set_quaternion, set_vector  # noqa: E402

STEP_NS = 100_000_000
# A 90° turn about body z: sensor x is body y.
QUARTER_TURN_Z = (0.0, 0.0, math.sqrt(0.5), math.sqrt(0.5))


def imu_configuration():
    """The P0 default with only its IMU, mounted off the centre of mass and turned."""
    configuration = p0_default_request().configuration
    del configuration.sensors[1:]
    mount = configuration.sensors[0].definition.mount
    set_vector(mount.position_body_m, (0.01, 0.02, 0.03))
    set_quaternion(mount.sensor_to_body, QUARTER_TURN_Z)
    return configuration


def make_hub():
    """A 1U hub in free fall, tumbling about all three axes with unequal inertia."""
    hub = spacecraft.Spacecraft()
    hub.hub.mHub = 1.33
    hub.hub.IHubPntBc_B = np.diag([2.536e-3, 3.0e-3, 2.217e-3]).tolist()
    hub.hub.r_CN_NInit = [[6.878e6], [0.0], [0.0]]
    hub.hub.v_CN_NInit = [[0.0], [7.6e3], [0.0]]
    hub.hub.omega_BN_BInit = [[0.05], [-0.2], [0.1]]
    return hub


def run_on_hub(configuration, steps):
    """Each IMU's (AngVelPlatform, AccelPlatform) per step, and the hub's omega_BN_B under
    "truth", with the hub in free fall and tumbling."""
    sim = SimulationBaseClass.SimBaseClass()
    process = sim.CreateNewProcess("dynamics")
    process.addTask(sim.CreateNewTask("dynamics", STEP_NS), 200)
    process.addTask(sim.CreateNewTask("sensors", STEP_NS), 100)
    hub = make_hub()
    sim.AddModelToTask("dynamics", hub)
    imus = build_sensors(configuration, hub.scStateOutMsg)
    for imu in imus:
        for module in imu.modules:
            sim.AddModelToTask("sensors", module)
    state = hub.scStateOutMsg.addSubscriber()
    sim.InitializeSimulation()

    outputs = {imu.sensor_id: [] for imu in imus}
    outputs["truth"] = []
    for n in range(steps):
        sim.ConfigureStopTime(n * STEP_NS)
        sim.ExecuteSimulation()
        outputs["truth"].append(list(state().omega_BN_B))
        for imu in imus:
            measurement = imu.measurement()
            outputs[imu.sensor_id].append((
                [measurement.angular_velocity_sensor_rad_per_s.x,
                 measurement.angular_velocity_sensor_rad_per_s.y,
                 measurement.angular_velocity_sensor_rad_per_s.z],
                [measurement.specific_force_sensor_m_per_s2.x,
                 measurement.specific_force_sensor_m_per_s2.y,
                 measurement.specific_force_sensor_m_per_s2.z],
            ))
    return {sensor_id: np.array(rows) for sensor_id, rows in outputs.items()}


def test_each_imu_is_a_gyroscope_module_and_an_accelerometer_module():
    configuration = imu_configuration()
    hub = make_hub()
    imu = Imu(configuration.sensors[0], configuration.random_seed, hub.scStateOutMsg)

    assert [module.ModelTag for module in imu.modules] == ["imu[imu.main].gyro", "imu[imu.main].accel"]
    assert imu.gyroscope.RNGSeed == rng_seed(1, "imu.main", GYRO)
    assert imu.accelerometer.RNGSeed == rng_seed(1, "imu.main", ACCEL)
    expected_dcm = rbk.EP2C(np.array([QUARTER_TURN_Z[3], *QUARTER_TURN_Z[:3]]))
    for module in imu.modules:
        assert np.allclose(module.dcm_PB, expected_dcm, atol=1e-15)
        assert np.allclose(np.ravel(module.sensorPos_B), [0.01, 0.02, 0.03])
    assert not imu.ready


def test_profile_fields_map_as_sensors_proto_states():
    configuration = imu_configuration()
    profile = configuration.sensors[0].imu
    set_vector(profile.gyroscope.constant_bias_sensor_rad_per_s, (1e-4, 2e-4, 3e-4))
    set_vector(profile.gyroscope.scale_factor_per_axis, (1.01, 1.0, 0.99))
    set_vector(profile.accelerometer.constant_bias_sensor_m_per_s2, (1e-3, 0.0, -1e-3))
    hub = make_hub()
    imu = Imu(configuration.sensors[0], configuration.random_seed, hub.scStateOutMsg)
    gyroscope, accelerometer = imu.gyroscope, imu.accelerometer
    period_s = STEP_NS / 1e9

    # White noise only on the module's own channel: P = N / sqrt(T) I, A = 0.
    assert np.allclose(gyroscope.PMatrixGyro, 1.2e-4 / math.sqrt(period_s) * np.eye(3))
    assert np.allclose(gyroscope.PMatrixAccel, np.zeros((3, 3)))
    assert np.allclose(accelerometer.PMatrixAccel, 1.8e-3 / math.sqrt(period_s) * np.eye(3))
    assert np.allclose(accelerometer.PMatrixGyro, np.zeros((3, 3)))
    for module in imu.modules:
        assert np.allclose(module.getAMatrixGyro(), np.zeros((3, 3)))
        assert np.allclose(module.getAMatrixAccel(), np.zeros((3, 3)))
        assert np.allclose(np.ravel(module.getWalkBoundsGyro()), 0.0)
        assert np.allclose(np.ravel(module.senRotBias), [1e-4, 2e-4, 3e-4])
        assert np.allclose(np.ravel(module.gyroScale), [1.01, 1.0, 0.99])
        assert np.allclose(np.ravel(module.senTransBias), [1e-3, 0.0, -1e-3])
        assert np.allclose(np.ravel(module.accelScale), [1.0, 1.0, 1.0])
        assert module.senRotMax == 4.363
        assert module.senTransMax == 19.61


def test_bias_random_walk_uses_an_identity_a_matrix():
    configuration = imu_configuration()
    gyroscope = configuration.sensors[0].imu.gyroscope
    gyroscope.noise_density_rad_per_s_per_sqrt_hz = 0.0
    gyroscope.bias_random_walk_rad_per_s_per_sqrt_s = 1e-5
    gyroscope.bias_random_walk_bound_rad_per_s = 1e-3
    hub = make_hub()
    imu = Imu(configuration.sensors[0], configuration.random_seed, hub.scStateOutMsg)

    assert np.allclose(imu.gyroscope.PMatrixGyro, 1e-5 * math.sqrt(STEP_NS / 1e9) * np.eye(3))
    assert np.allclose(imu.gyroscope.getAMatrixGyro(), np.eye(3))
    assert np.allclose(np.ravel(imu.gyroscope.getWalkBoundsGyro()), 1e-3)


def test_noise_free_imu_reads_the_body_rate_in_sensor_axes():
    configuration = imu_configuration()
    profile = configuration.sensors[0].imu
    profile.gyroscope.noise_density_rad_per_s_per_sqrt_hz = 0.0
    profile.gyroscope.resolution_rad_per_s = 0.0
    profile.accelerometer.noise_density_m_per_s2_per_sqrt_hz = 0.0
    profile.accelerometer.resolution_m_per_s2 = 0.0
    set_vector(configuration.sensors[0].definition.mount.position_body_m, (0.0, 0.0, 0.0))

    run = run_on_hub(configuration, steps=6)
    measured, truth = run["imu.main"][1:], run["truth"][1:]
    # Sensor axes through the quarter turn about z: (x, y, z)_sensor = (y, -x, z)_body.
    expected = truth[:, [1, 0, 2]] * [1.0, -1.0, 1.0]
    assert np.allclose(measured[:, 0], expected, rtol=0.0, atol=1e-15)
    # The tumble changes the rate every step, so the gyroscope reads the state at the same t_n.
    assert np.abs(np.diff(truth, axis=0)).min() > 1e-6
    # Specific force at the centre of mass in free fall.
    assert np.abs(measured[:, 1]).max() < 1e-12


def test_noise_free_accelerometer_reads_applied_force_over_mass_but_not_gravity():
    """Port of accel_truth_test (dynamics/basilisk/experiments, PR #6): in Earth orbit with a
    constant body force, AccelPlatform is F / m in sensor axes, and gravity is not sensed."""
    configuration = imu_configuration()
    profile = configuration.sensors[0].imu
    profile.gyroscope.noise_density_rad_per_s_per_sqrt_hz = 0.0
    profile.accelerometer.noise_density_m_per_s2_per_sqrt_hz = 0.0
    profile.accelerometer.resolution_m_per_s2 = 0.0
    set_vector(configuration.sensors[0].definition.mount.position_body_m, (0.0, 0.0, 0.0))

    hub = make_hub()
    hub.hub.omega_BN_BInit = [[0.0], [0.0], [0.0]]
    earth = gravityEffector.GravBodyData()
    earth.planetName = "earth_planet_data"
    earth.mu = astroConstants.MU_EARTH * 1e9
    earth.radEquator = astroConstants.REQ_EARTH * 1e3
    earth.isCentralBody = True
    hub.gravField.setGravBodies(gravityEffector.GravBodyVector([earth]))
    radius = 6.878e6
    hub.hub.r_CN_NInit = [[radius], [0.0], [0.0]]
    hub.hub.v_CN_NInit = [[0.0], [math.sqrt(earth.mu / radius)], [0.0]]
    force_body_n = np.array([0.5, -0.2, 0.1])
    force = extForceTorque.ExtForceTorque()
    force.extForce_B = force_body_n.reshape(3, 1).tolist()
    hub.addDynamicEffector(force)
    imu = Imu(configuration.sensors[0], configuration.random_seed, hub.scStateOutMsg)

    sim = SimulationBaseClass.SimBaseClass()
    process = sim.CreateNewProcess("dynamics")
    process.addTask(sim.CreateNewTask("dynamics", STEP_NS), 200)
    process.addTask(sim.CreateNewTask("sensors", STEP_NS), 100)
    sim.AddModelToTask("dynamics", force, 110)
    sim.AddModelToTask("dynamics", hub, 100)
    for module in imu.modules:
        sim.AddModelToTask("sensors", module)
    state = hub.scStateOutMsg.addSubscriber()
    sim.InitializeSimulation()

    measured, velocities = [], []
    for n in range(6):
        sim.ConfigureStopTime(n * STEP_NS)
        sim.ExecuteSimulation()
        value = imu.measurement().specific_force_sensor_m_per_s2
        measured.append([value.x, value.y, value.z])
        velocities.append(list(state().v_BN_N))

    # Sensor axes through the quarter turn about z: (x, y, z)_sensor = (y, -x, z)_body.
    specific_force_body = force_body_n / hub.hub.mHub
    expected = specific_force_body[[1, 0, 2]] * [1.0, -1.0, 1.0]
    assert np.allclose(measured[1:], expected, rtol=0.0, atol=1e-10)
    # The hub really accelerates under gravity (about 8.4 m/s^2), yet the reading holds only F / m.
    inertial_acceleration = np.diff(velocities, axis=0) / (STEP_NS * 1e-9)
    assert np.linalg.norm(inertial_acceleration, axis=1).min() > 8.0


def test_first_firing_has_no_data_and_later_firings_do():
    configuration = imu_configuration()
    hub = make_hub()
    imu = Imu(configuration.sensors[0], configuration.random_seed, hub.scStateOutMsg)
    sim = SimulationBaseClass.SimBaseClass()
    process = sim.CreateNewProcess("dynamics")
    process.addTask(sim.CreateNewTask("dynamics", STEP_NS), 200)
    process.addTask(sim.CreateNewTask("sensors", STEP_NS), 100)
    sim.AddModelToTask("dynamics", hub)
    for module in imu.modules:
        sim.AddModelToTask("sensors", module)
    sim.InitializeSimulation()

    assert not imu.ready
    sim.ConfigureStopTime(0)
    sim.ExecuteSimulation()
    assert imu.ready
    for reader in imu.readers.values():
        assert reader.isWritten() and reader.timeWritten() == 0


def test_changing_one_seed_stream_changes_only_that_module():
    configuration = imu_configuration()
    second = configuration.sensors.add()
    second.CopyFrom(configuration.sensors[0])
    second.definition.sensor_id = "imu.backup"
    first_run = run_on_hub(configuration, steps=6)

    configuration.sensors[1].definition.sensor_id = "imu.spare"
    second_run = run_on_hub(configuration, steps=6)

    # imu.main keeps both its streams; the renamed IMU gets new ones.
    assert np.array_equal(first_run["imu.main"], second_run["imu.main"])
    assert not np.array_equal(first_run["imu.backup"][1:, 0], second_run["imu.spare"][1:, 0])
    assert not np.array_equal(first_run["imu.backup"][1:, 1], second_run["imu.spare"][1:, 1])


def test_gyroscope_and_accelerometer_noise_are_independent():
    configuration = imu_configuration()
    profile = configuration.sensors[0].imu
    profile.gyroscope.resolution_rad_per_s = 0.0
    profile.accelerometer.resolution_m_per_s2 = 0.0
    set_vector(configuration.sensors[0].definition.mount.position_body_m, (0.0, 0.0, 0.0))
    profile.accelerometer.noise_density_m_per_s2_per_sqrt_hz = profile.gyroscope.noise_density_rad_per_s_per_sqrt_hz
    noisy = run_on_hub(configuration, steps=6)["imu.main"][1:]

    profile.gyroscope.noise_density_rad_per_s_per_sqrt_hz = 0.0
    profile.accelerometer.noise_density_m_per_s2_per_sqrt_hz = 0.0
    clean = run_on_hub(configuration, steps=6)["imu.main"][1:]

    # With one RNGSeed for both channels the two error sequences would be identical.
    gyroscope_error = noisy[:, 0] - clean[:, 0]
    accelerometer_error = noisy[:, 1] - clean[:, 1]
    assert np.abs(gyroscope_error).max() > 0.0
    assert not np.allclose(gyroscope_error, accelerometer_error)


def magnetometer_configuration():
    """The P0 default with only its magnetometer, turned, biased, scaled and range-limited."""
    configuration = p0_default_request().configuration
    del configuration.sensors[2:]
    del configuration.sensors[0]
    sensor = configuration.sensors[0]
    set_quaternion(sensor.definition.mount.sensor_to_body, QUARTER_TURN_Z)
    set_vector(sensor.magnetometer.constant_bias_sensor_tesla, (1e-6, -2e-6, 3e-6))
    sensor.magnetometer.scale_factor = 1.1
    sensor.magnetometer.range_tesla = 6e-5
    return configuration


def test_magnetometer_sets_every_error_term_explicitly():
    configuration = magnetometer_configuration()
    hub = make_hub()
    field = messaging.MagneticFieldMsg()
    sensor = Magnetometer(configuration.sensors[0], hub.scStateOutMsg, field)
    module = sensor.module

    assert module.ModelTag == "magnetometer[mag.main]"
    assert np.allclose(module.dcm_SB, rbk.EP2C(np.array([QUARTER_TURN_Z[3], *QUARTER_TURN_Z[:3]])), atol=1e-15)
    # Defaults are a random walk (A = I) and noise -1; both are overridden.
    assert np.allclose(module.getAMatrix(), np.zeros((3, 3)))
    assert np.allclose(np.ravel(module.senNoiseStd), 0.0)
    assert np.allclose(np.ravel(module.walkBounds), 0.0)
    assert np.allclose(np.ravel(module.senBias), [1e-6, -2e-6, 3e-6])
    assert module.scaleFactor == 1.1
    assert (module.minOutput, module.maxOutput) == (-6e-5, 6e-5)
    assert sensor.ready


def test_magnetometer_needs_the_magnetic_field_message():
    configuration = magnetometer_configuration()
    with pytest.raises(ValueError, match="MagneticFieldWMM"):
        build_sensors(configuration, make_hub().scStateOutMsg)


# --- Light sensors (BSK-6) ----------------------------------------------------------------

AU_M = astroConstants.AU * 1000.0


def light_sensors(configuration):
    return [sensor for sensor in configuration.sensors if sensor.WhichOneof("profile") == "light_sensor"]


def environment_messages(sun_direction, shadow_factor, albedo_ratio):
    """A Sun 1 AU along sun_direction, an eclipse factor and an albedo ratio, as written messages."""
    sun = messaging.SpicePlanetStateMsgPayload()
    sun.PlanetName = "sun"
    sun.PositionVector = list(AU_M * np.asarray(sun_direction, dtype=float))
    eclipse = messaging.EclipseMsgPayload()
    eclipse.illuminationFactor = shadow_factor
    albedo = messaging.AlbedoMsgPayload()
    albedo.albedoAtInstrument = albedo_ratio
    return (messaging.SpicePlanetStateMsg().write(sun), messaging.EclipseMsg().write(eclipse),
            messaging.AlbedoMsg().write(albedo))


def test_each_p0_light_sensor_looks_out_of_its_face():
    """The boresight is sensor +z, so nHat_B is the face normal named by the sensor ID."""
    normals = {"px": [1, 0, 0], "mx": [-1, 0, 0], "py": [0, 1, 0], "my": [0, -1, 0], "pz": [0, 0, 1], "mz": [0, 0, -1]}
    for sensor in light_sensors(p0_default_request().configuration):
        field_of_view, boresight, position = light_sensor_instrument(sensor)
        face = sensor.definition.sensor_id.split(".")[1]
        assert np.allclose(boresight, normals[face], atol=1e-15), sensor.definition.sensor_id
        assert field_of_view == sensor.light_sensor.field_of_view_half_angle_rad
        assert np.allclose(position, [getattr(sensor.definition.mount.position_body_m, a) for a in "xyz"])


def test_light_sensor_maps_the_profile_into_the_module():
    configuration = p0_default_request().configuration
    sensor = light_sensors(configuration)[0]
    profile = sensor.light_sensor
    profile.scale_factor = 0.9
    profile.noise_std_w_per_m2 = 2.0
    profile.bias_w_per_m2 = 3.0
    hub = make_hub()
    sun, eclipse, albedo = environment_messages([1, 0, 0], 1.0, 0.0)
    module = LightSensor(sensor, configuration.random_seed, hub.scStateOutMsg, sun, eclipse, albedo).module
    scale = 0.9 * SOLAR_IRRADIANCE_W_PER_M2

    assert module.ModelTag == "css[light.px]"
    assert module.RNGSeed == rng_seed(configuration.random_seed, "light.px", CSS)
    assert module.scaleFactor == scale
    assert module.senNoiseStd == 2.0 / scale
    assert module.senBias == 3.0 / scale
    assert module.fov == profile.field_of_view_half_angle_rad
    assert (module.minOutput, module.maxOutput) == (0.0, profile.saturation_w_per_m2)
    assert module.kellyFactor == 0.0
    assert module.faultState == coarseSunSensor.NOMINAL
    assert np.allclose(np.ravel(module.nHat_B), [1, 0, 0], atol=1e-15)


def test_light_sensor_needs_the_sun_eclipse_and_albedo():
    configuration = p0_default_request().configuration
    del configuration.sensors[:2]
    with pytest.raises(ValueError, match="Sun, eclipse and albedo"):
        build_sensors(configuration, make_hub().scStateOutMsg)


@pytest.mark.parametrize("shadow_factor", [1.0, 0.0])
def test_noise_free_light_sensor_reads_direct_sun_plus_albedo(shadow_factor):
    """OutputData = scale (cos(theta) shadow (1 AU / d)^2 + albedo); the Sun here is exactly 1 AU away."""
    configuration = p0_default_request().configuration
    sensor = light_sensors(configuration)[0]  # light.px: boresight body +x
    sensor.light_sensor.noise_std_w_per_m2 = 0.0
    sensor.light_sensor.saturation_w_per_m2 = 1e6
    hub = make_hub()
    hub.hub.omega_BN_BInit = [[0.0], [0.0], [0.0]]
    hub.hub.r_CN_NInit = [[0.0], [0.0], [0.0]]
    theta = math.radians(60.0)
    sun, eclipse, albedo = environment_messages([math.cos(theta), math.sin(theta), 0.0], shadow_factor, 0.05)
    light = LightSensor(sensor, configuration.random_seed, hub.scStateOutMsg, sun, eclipse, albedo)
    sim = SimulationBaseClass.SimBaseClass()
    process = sim.CreateNewProcess("dynamics")
    process.addTask(sim.CreateNewTask("dynamics", STEP_NS), 200)
    process.addTask(sim.CreateNewTask("sensors", STEP_NS), 100)
    sim.AddModelToTask("dynamics", hub)
    sim.AddModelToTask("sensors", light.module)
    sim.InitializeSimulation()
    sim.ConfigureStopTime(0)
    sim.ExecuteSimulation()

    expected = SOLAR_IRRADIANCE_W_PER_M2 * (math.cos(theta) * shadow_factor + 0.05)
    assert light.measurement().irradiance_w_per_m2 == pytest.approx(expected, rel=1e-9)
