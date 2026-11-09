"""The imu, magnetometer and light_sensor kinds: their declarations, modules, outputs and seeded
noise (README §9, §10) without SPICE; needs bsk==2.11.1 but no kernels."""

import math

import numpy as np
import pytest

pytest.importorskip("Basilisk")

from Basilisk.architecture import astroConstants, messaging  # noqa: E402
from Basilisk.simulation import extForceTorque, gravityEffector, spacecraft  # noqa: E402
from Basilisk.utilities import RigidBodyKinematics as rbk  # noqa: E402
from Basilisk.utilities import SimulationBaseClass  # noqa: E402

from Basilisk.simulation import coarseSunSensor  # noqa: E402

from argus_basilisk.devices import KINDS, build_sensors, is_actuator, of_kind  # noqa: E402
from argus_basilisk.devices.imu import Imu  # noqa: E402
from argus_basilisk.devices.light_sensor import SOLAR_IRRADIANCE_W_PER_M2, LightSensor, instrument  # noqa: E402
from argus_basilisk.devices.magnetometer import Magnetometer  # noqa: E402
from argus_basilisk.seeds import ACCEL, CSS, GYRO, MAG, rng_seed, sample_normals  # noqa: E402
from test_validation import p0_default_request, set_parameter, set_quaternion, set_vector  # noqa: E402

STEP_NS = 100_000_000
# A 90° turn about body z: sensor x is body y.
QUARTER_TURN_Z = (0.0, 0.0, math.sqrt(0.5), math.sqrt(0.5))


def xyz(value):
    """A vector3 DeviceValue as a list."""
    return [value.vector3.x, value.vector3.y, value.vector3.z]


def imu_configuration():
    """The P0 default with only its IMU, mounted off the centre of mass and turned."""
    configuration = p0_default_request().configuration
    del configuration.devices[1:]
    mount = configuration.devices[0].mount
    set_vector(mount.position_body_m, (0.01, 0.02, 0.03))
    set_quaternion(mount.device_to_body, QUARTER_TURN_Z)
    return configuration


def test_each_kind_declares_typed_parameters_and_outputs():
    types = {"number", "vector3", "quaternion", "matrix3", "flag", "text"}
    for name, kind in KINDS.items():
        assert kind.KIND == name
        declared = [kind.PARAMETERS, getattr(kind, "STATE", {})]
        # A sensor kind declares its outputs; an actuator kind its commands and idle values.
        if is_actuator(kind):
            declared.append(kind.COMMANDS)
            assert set(kind.IDLE) == set(kind.COMMANDS)
        else:
            declared.append(kind.OUTPUTS)
        assert all(set(keys.values()) <= types for keys in declared)
    assert len(KINDS["imu"].PARAMETERS) == 14
    assert all(key.split(".")[0] in ("gyroscope", "accelerometer") for key in KINDS["imu"].PARAMETERS)


def test_every_p0_device_sets_exactly_its_kind_parameters():
    for device in p0_default_request().configuration.devices:
        kind = KINDS[device.kind]
        assert {key: value.WhichOneof("value") for key, value in device.parameters.items()} == kind.PARAMETERS


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

    outputs = {imu.device_id: [] for imu in imus}
    outputs["truth"] = []
    for n in range(steps):
        sim.ConfigureStopTime(n * STEP_NS)
        sim.ExecuteSimulation()
        outputs["truth"].append(list(state().omega_BN_B))
        for imu in imus:
            output = imu.output()
            outputs[imu.device_id].append((
                xyz(output["angular_velocity_sensor_rad_per_s"]), xyz(output["specific_force_sensor_m_per_s2"])))
    return {device_id: np.array(rows) for device_id, rows in outputs.items()}


def test_each_imu_is_a_gyroscope_module_and_an_accelerometer_module():
    configuration = imu_configuration()
    hub = make_hub()
    imu = Imu(configuration.devices[0], configuration.random_seed, hub.scStateOutMsg)

    assert [module.ModelTag for module in imu.modules] == ["imu[imu.main].gyro", "imu[imu.main].accel"]
    assert imu.gyroscope.RNGSeed == rng_seed(1, "imu.main", GYRO)
    assert imu.accelerometer.RNGSeed == rng_seed(1, "imu.main", ACCEL)
    expected_dcm = rbk.EP2C(np.array([QUARTER_TURN_Z[3], *QUARTER_TURN_Z[:3]]))
    for module in imu.modules:
        assert np.allclose(module.dcm_PB, expected_dcm, atol=1e-15)
        assert np.allclose(np.ravel(module.sensorPos_B), [0.01, 0.02, 0.03])
    assert not imu.ready


def test_parameters_map_as_readme_section_9_states():
    configuration = imu_configuration()
    device = configuration.devices[0]
    set_parameter(device, "gyroscope.constant_bias_sensor_rad_per_s", (1e-4, 2e-4, 3e-4))
    set_parameter(device, "gyroscope.scale_factor_per_axis", (1.01, 1.0, 0.99))
    set_parameter(device, "accelerometer.constant_bias_sensor_m_per_s2", (1e-3, 0.0, -1e-3))
    hub = make_hub()
    imu = Imu(configuration.devices[0], configuration.random_seed, hub.scStateOutMsg)
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
    device = configuration.devices[0]
    set_parameter(device, "gyroscope.noise_density_rad_per_s_per_sqrt_hz", 0.0)
    set_parameter(device, "gyroscope.bias_random_walk_rad_per_s_per_sqrt_s", 1e-5)
    set_parameter(device, "gyroscope.bias_random_walk_bound_rad_per_s", 1e-3)
    hub = make_hub()
    imu = Imu(configuration.devices[0], configuration.random_seed, hub.scStateOutMsg)

    assert np.allclose(imu.gyroscope.PMatrixGyro, 1e-5 * math.sqrt(STEP_NS / 1e9) * np.eye(3))
    assert np.allclose(imu.gyroscope.getAMatrixGyro(), np.eye(3))
    assert np.allclose(np.ravel(imu.gyroscope.getWalkBoundsGyro()), 1e-3)


def test_noise_free_imu_reads_the_body_rate_in_sensor_axes():
    configuration = imu_configuration()
    device = configuration.devices[0]
    set_parameter(device, "gyroscope.noise_density_rad_per_s_per_sqrt_hz", 0.0)
    set_parameter(device, "gyroscope.resolution_rad_per_s", 0.0)
    set_parameter(device, "accelerometer.noise_density_m_per_s2_per_sqrt_hz", 0.0)
    set_parameter(device, "accelerometer.resolution_m_per_s2", 0.0)
    set_vector(device.mount.position_body_m, (0.0, 0.0, 0.0))

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
    device = configuration.devices[0]
    set_parameter(device, "gyroscope.noise_density_rad_per_s_per_sqrt_hz", 0.0)
    set_parameter(device, "accelerometer.noise_density_m_per_s2_per_sqrt_hz", 0.0)
    set_parameter(device, "accelerometer.resolution_m_per_s2", 0.0)
    set_vector(device.mount.position_body_m, (0.0, 0.0, 0.0))

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
    imu = Imu(configuration.devices[0], configuration.random_seed, hub.scStateOutMsg)

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
        measured.append(xyz(imu.output()["specific_force_sensor_m_per_s2"]))
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
    imu = Imu(configuration.devices[0], configuration.random_seed, hub.scStateOutMsg)
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
    second = configuration.devices.add()
    second.CopyFrom(configuration.devices[0])
    second.device_id = "imu.backup"
    first_run = run_on_hub(configuration, steps=6)

    configuration.devices[1].device_id = "imu.spare"
    second_run = run_on_hub(configuration, steps=6)

    # imu.main keeps both its streams; the renamed IMU gets new ones.
    assert np.array_equal(first_run["imu.main"], second_run["imu.main"])
    assert not np.array_equal(first_run["imu.backup"][1:, 0], second_run["imu.spare"][1:, 0])
    assert not np.array_equal(first_run["imu.backup"][1:, 1], second_run["imu.spare"][1:, 1])


def test_gyroscope_and_accelerometer_noise_are_independent():
    configuration = imu_configuration()
    device = configuration.devices[0]
    set_parameter(device, "gyroscope.resolution_rad_per_s", 0.0)
    set_parameter(device, "accelerometer.resolution_m_per_s2", 0.0)
    set_vector(device.mount.position_body_m, (0.0, 0.0, 0.0))
    set_parameter(device, "accelerometer.noise_density_m_per_s2_per_sqrt_hz",
                  device.parameters["gyroscope.noise_density_rad_per_s_per_sqrt_hz"].number)
    noisy = run_on_hub(configuration, steps=6)["imu.main"][1:]

    set_parameter(device, "gyroscope.noise_density_rad_per_s_per_sqrt_hz", 0.0)
    set_parameter(device, "accelerometer.noise_density_m_per_s2_per_sqrt_hz", 0.0)
    clean = run_on_hub(configuration, steps=6)["imu.main"][1:]

    # With one RNGSeed for both channels the two error sequences would be identical.
    gyroscope_error = noisy[:, 0] - clean[:, 0]
    accelerometer_error = noisy[:, 1] - clean[:, 1]
    assert np.abs(gyroscope_error).max() > 0.0
    assert not np.allclose(gyroscope_error, accelerometer_error)


def magnetometer_configuration():
    """The P0 default with only its magnetometer, turned, biased, scaled and range-limited."""
    configuration = p0_default_request().configuration
    del configuration.devices[2:]
    del configuration.devices[0]
    device = configuration.devices[0]
    set_quaternion(device.mount.device_to_body, QUARTER_TURN_Z)
    set_parameter(device, "constant_bias_sensor_tesla", (1e-6, -2e-6, 3e-6))
    set_parameter(device, "scale_factor", 1.1)
    set_parameter(device, "range_tesla", 6e-5)
    return configuration


# About 50 uT, so a 3e-5 T range clips some axes.
MAGNETIC_FIELD_N_T = (2.0e-5, -3.5e-5, 2.5e-5)


def run_magnetometers(configuration, steps):
    """Each magnetometer's output per step, in a constant field on the tumbling hub."""
    sim = SimulationBaseClass.SimBaseClass()
    process = sim.CreateNewProcess("dynamics")
    process.addTask(sim.CreateNewTask("dynamics", STEP_NS), 200)
    process.addTask(sim.CreateNewTask("sensors", STEP_NS), 100)
    hub = make_hub()
    sim.AddModelToTask("dynamics", hub)
    field = messaging.MagneticFieldMsgPayload()
    field.magField_N = list(MAGNETIC_FIELD_N_T)
    field_msg = messaging.MagneticFieldMsg().write(field)
    magnetometers = build_sensors(configuration, hub.scStateOutMsg, field_msg)
    for sensor in magnetometers:
        sim.AddModelToTask("sensors", sensor.module)
    sim.InitializeSimulation()

    outputs = {sensor.device_id: [] for sensor in magnetometers}
    for n in range(steps):
        sim.ConfigureStopTime(n * STEP_NS)
        sim.ExecuteSimulation()
        for sensor in magnetometers:
            outputs[sensor.device_id].append(xyz(sensor.output()["magnetic_field_sensor_tesla"]))
    return {device_id: np.array(rows) for device_id, rows in outputs.items()}


def test_magnetometer_sets_every_error_term_explicitly():
    configuration = magnetometer_configuration()
    hub = make_hub()
    field = messaging.MagneticFieldMsg()
    sensor = Magnetometer(configuration.devices[0], configuration.random_seed, hub.scStateOutMsg, field)
    module = sensor.module

    assert module.ModelTag == "magnetometer[mag.main]"
    assert np.allclose(module.dcm_SB, rbk.EP2C(np.array([QUARTER_TURN_Z[3], *QUARTER_TURN_Z[:3]])), atol=1e-15)
    # Defaults are a random walk (A = I) and noise -1; both are overridden. The module never
    # adds noise or clips: bsk 2.11.1 ignores its RNGSeed, so output() does both.
    assert np.allclose(module.getAMatrix(), np.zeros((3, 3)))
    assert np.allclose(np.ravel(module.senNoiseStd), 0.0)
    assert np.allclose(np.ravel(module.walkBounds), 0.0)
    assert np.allclose(np.ravel(module.senBias), [1e-6, -2e-6, 3e-6])
    assert module.scaleFactor == 1.1
    assert (module.minOutput, module.maxOutput) == (-math.inf, math.inf)
    assert sensor.rng_seed == rng_seed(configuration.random_seed, "mag.main", MAG)
    assert sensor.ready


def test_magnetometer_noise_is_scaled_then_clipped():
    """tam_S = clip(scale (B_S + noise + bias), +-range), with noise sigma z_k from the mag stream."""
    configuration = magnetometer_configuration()
    device = configuration.devices[0]
    set_parameter(device, "range_tesla", 1.0)
    unclipped = run_magnetometers(configuration, steps=20)["mag.main"]

    set_parameter(device, "noise_std_tesla", 1e-6)
    set_parameter(device, "range_tesla", 3e-5)
    noisy = run_magnetometers(configuration, steps=20)["mag.main"]

    seed = rng_seed(configuration.random_seed, "mag.main", MAG)
    noise = np.array([sample_normals(seed, MAG, n) for n in range(20)])
    expected = np.clip(unclipped + 1.1 * 1e-6 * noise, -3e-5, 3e-5)
    assert np.array_equal(noisy, expected)
    assert np.sum(np.abs(noisy) == 3e-5) > 0  # Some axes clip.
    assert np.sum(np.abs(noisy) < 3e-5) > 0  # And some carry noise.


def test_magnetometer_noise_is_white_with_the_configured_sigma():
    configuration = magnetometer_configuration()
    device = configuration.devices[0]
    set_parameter(device, "scale_factor", 1.0)
    set_parameter(device, "range_tesla", 1.0)
    clean = run_magnetometers(configuration, steps=400)["mag.main"]
    set_parameter(device, "noise_std_tesla", 1e-7)
    error = run_magnetometers(configuration, steps=400)["mag.main"] - clean

    assert np.allclose(error.std(axis=0), 1e-7, rtol=0.15)
    assert np.all(np.abs(error.mean(axis=0)) < 4 * 1e-7 / math.sqrt(400))
    # Successive samples and the three axes are uncorrelated.
    assert abs(np.corrcoef(error[:-1, 0], error[1:, 0])[0, 1]) < 0.2
    assert np.all(np.abs(np.corrcoef(error.T)[np.triu_indices(3, 1)]) < 0.2)


def test_changing_one_magnetometer_seed_changes_only_its_noise():
    configuration = magnetometer_configuration()
    set_parameter(configuration.devices[0], "noise_std_tesla", 1e-7)
    set_parameter(configuration.devices[0], "range_tesla", 1.0)
    second = configuration.devices.add()
    second.CopyFrom(configuration.devices[0])
    second.device_id = "mag.backup"
    first_run = run_magnetometers(configuration, steps=10)

    configuration.devices[1].device_id = "mag.spare"
    second_run = run_magnetometers(configuration, steps=10)

    # bsk's own generator would give every magnetometer the same stream.
    assert not np.array_equal(first_run["mag.main"], first_run["mag.backup"])
    assert np.array_equal(first_run["mag.main"], second_run["mag.main"])
    assert not np.array_equal(first_run["mag.backup"], second_run["mag.spare"])

    configuration.random_seed += 1
    reseeded = run_magnetometers(configuration, steps=10)
    assert not np.array_equal(first_run["mag.main"], reseeded["mag.main"])


def test_sample_normals_are_pinned_per_seed_stream_and_index():
    """Pinned values (numpy 2.4.6 in uv.lock), so a change inside sample_normals fails here."""
    seed = rng_seed(1, "mag.main", MAG)
    assert sample_normals(seed, MAG, 7).tolist() == [-1.2898110306945079, 0.30201081627962734, -1.2967257220131292]
    assert not np.array_equal(sample_normals(seed, MAG, 7), sample_normals(seed, MAG, 8))
    assert not np.array_equal(sample_normals(seed, MAG, 7), sample_normals(seed + 1, MAG, 7))
    assert not np.array_equal(sample_normals(seed, MAG, 7), sample_normals(seed, CSS, 7))
    # The stream tag keeps sample 0 apart from a generator seeded with the seed alone.
    assert not np.array_equal(sample_normals(seed, MAG, 0), np.random.default_rng(seed).standard_normal(3))
    assert not np.array_equal(sample_normals(seed, MAG, 0), np.random.default_rng([seed, 0]).standard_normal(3))


def test_magnetometer_needs_the_magnetic_field_message():
    configuration = magnetometer_configuration()
    with pytest.raises(ValueError, match="MagneticFieldWMM"):
        build_sensors(configuration, make_hub().scStateOutMsg)


# --- Light sensors (BSK-6) ----------------------------------------------------------------

AU_M = astroConstants.AU * 1000.0


def light_sensors(configuration):
    return of_kind(configuration.devices, "light_sensor")


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
    for device in light_sensors(p0_default_request().configuration):
        field_of_view, boresight, position = instrument(device)
        face = device.device_id.split(".")[1]
        assert np.allclose(boresight, normals[face], atol=1e-15), device.device_id
        assert field_of_view == device.parameters["field_of_view_half_angle_rad"].number
        assert np.allclose(position, [getattr(device.mount.position_body_m, a) for a in "xyz"])


def test_light_sensor_maps_the_parameters_into_the_module():
    configuration = p0_default_request().configuration
    sensor = light_sensors(configuration)[0]
    set_parameter(sensor, "scale_factor", 0.9)
    set_parameter(sensor, "noise_std_w_per_m2", 2.0)
    set_parameter(sensor, "bias_w_per_m2", 3.0)
    hub = make_hub()
    sun, eclipse, albedo = environment_messages([1, 0, 0], 1.0, 0.0)
    light = LightSensor(sensor, configuration.random_seed, hub.scStateOutMsg, sun, eclipse, albedo)
    module = light.module
    scale = 0.9 * SOLAR_IRRADIANCE_W_PER_M2

    assert module.ModelTag == "css[light.px]"
    assert light.rng_seed == rng_seed(configuration.random_seed, "light.px", CSS)
    assert module.scaleFactor == scale
    # The module never adds noise or clips: bsk 2.11.1's noise would be a random walk, so
    # output() does both.
    assert module.senNoiseStd == 0.0
    assert module.senBias == 3.0 / scale
    assert module.fov == sensor.parameters["field_of_view_half_angle_rad"].number
    assert (module.minOutput, module.maxOutput) == (-math.inf, math.inf)
    assert module.kellyFactor == 0.0
    assert module.faultState == coarseSunSensor.NOMINAL
    assert np.allclose(np.ravel(module.nHat_B), [1, 0, 0], atol=1e-15)


def test_light_sensor_needs_the_sun_eclipse_and_albedo():
    configuration = p0_default_request().configuration
    del configuration.devices[:2]
    with pytest.raises(ValueError, match="Sun, eclipse and albedo"):
        build_sensors(configuration, make_hub().scStateOutMsg)


@pytest.mark.parametrize("shadow_factor", [1.0, 0.0])
def test_noise_free_light_sensor_reads_direct_sun_plus_albedo(shadow_factor):
    """OutputData = scale (cos(theta) shadow (1 AU / d)^2 + albedo); the Sun here is exactly 1 AU away."""
    configuration = p0_default_request().configuration
    sensor = light_sensors(configuration)[0]  # light.px: boresight body +x
    set_parameter(sensor, "noise_std_w_per_m2", 0.0)
    set_parameter(sensor, "saturation_w_per_m2", 1e6)
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
    assert light.output()["irradiance_w_per_m2"].number == pytest.approx(expected, rel=1e-9)


def run_light_sensors(sensors, steps, sun_direction=(0.6, 0.8, 0.0), shadow_factor=1.0, albedo_ratio=0.05):
    """Each light sensor's irradiance per step on a still hub, in a fixed Sun, eclipse and albedo."""
    hub = make_hub()
    hub.hub.omega_BN_BInit = [[0.0], [0.0], [0.0]]
    hub.hub.r_CN_NInit = [[0.0], [0.0], [0.0]]
    hub.hub.v_CN_NInit = [[0.0], [0.0], [0.0]]
    sun, eclipse, albedo = environment_messages(list(sun_direction), shadow_factor, albedo_ratio)
    lights = [LightSensor(sensor, 1, hub.scStateOutMsg, sun, eclipse, albedo) for sensor in sensors]
    sim = SimulationBaseClass.SimBaseClass()
    process = sim.CreateNewProcess("dynamics")
    process.addTask(sim.CreateNewTask("dynamics", STEP_NS), 200)
    process.addTask(sim.CreateNewTask("sensors", STEP_NS), 100)
    sim.AddModelToTask("dynamics", hub)
    for light in lights:
        sim.AddModelToTask("sensors", light.module)
    sim.InitializeSimulation()

    outputs = {light.device_id: [] for light in lights}
    for n in range(steps):
        sim.ConfigureStopTime(n * STEP_NS)
        sim.ExecuteSimulation()
        for light in lights:
            outputs[light.device_id].append(light.output()["irradiance_w_per_m2"].number)
    return {device_id: np.array(values) for device_id, values in outputs.items()}


def test_light_sensor_noise_is_white_with_the_configured_sigma():
    """10,000 samples of a fixed geometry: the noise bsk 2.11.1 would make a random walk is white."""
    sensor = light_sensors(p0_default_request().configuration)[0]
    set_parameter(sensor, "noise_std_w_per_m2", 0.0)
    clean = run_light_sensors([sensor], steps=10_000)["light.px"]
    set_parameter(sensor, "noise_std_w_per_m2", 2.0)
    error = run_light_sensors([sensor], steps=10_000)["light.px"] - clean

    assert np.ptp(clean) == 0.0 and 0.0 < clean[0] < sensor.parameters["saturation_w_per_m2"].number
    assert abs(error.mean()) < 3 * 2.0 / math.sqrt(error.size)
    assert error.std() == pytest.approx(2.0, rel=0.02)
    assert abs(np.corrcoef(error[:-1], error[1:])[0, 1]) < 0.05


def test_light_sensor_noise_is_added_then_clipped_to_zero_and_saturation():
    sensor = light_sensors(p0_default_request().configuration)[0]
    set_parameter(sensor, "noise_std_w_per_m2", 0.0)
    set_parameter(sensor, "saturation_w_per_m2", 1e6)
    unlit = run_light_sensors([sensor], steps=50, shadow_factor=0.0, albedo_ratio=0.0)["light.px"]
    lit = run_light_sensors([sensor], steps=50)["light.px"]
    assert np.all(unlit == 0.0)

    set_parameter(sensor, "noise_std_w_per_m2", 5.0)
    noise = 5.0 * np.array([sample_normals(rng_seed(1, "light.px", CSS), CSS, k, size=1)[0] for k in range(50)])
    noisy_unlit = run_light_sensors([sensor], steps=50, shadow_factor=0.0, albedo_ratio=0.0)["light.px"]
    assert np.array_equal(noisy_unlit, np.maximum(noise, 0.0))
    assert np.any(noisy_unlit == 0.0) and np.any(noisy_unlit > 0.0)

    set_parameter(sensor, "saturation_w_per_m2", float(lit[0]))
    noisy_lit = run_light_sensors([sensor], steps=50)["light.px"]
    assert np.array_equal(noisy_lit, np.minimum(lit + noise, lit[0]))
    assert np.any(noisy_lit == lit[0]) and np.any(noisy_lit < lit[0])


def test_changing_one_light_sensor_seed_changes_only_its_noise():
    sensors = light_sensors(p0_default_request().configuration)[:2]
    for sensor in sensors:
        set_parameter(sensor, "noise_std_w_per_m2", 2.0)
        set_parameter(sensor, "saturation_w_per_m2", 1e6)
    first_run = run_light_sensors(sensors, steps=20)

    renamed = sensors[1].device_id + ".spare"
    sensors[1].device_id = renamed
    second_run = run_light_sensors(sensors, steps=20)

    first_id = sensors[0].device_id
    assert np.array_equal(first_run[first_id], second_run[first_id])
    (other_id,) = set(first_run) - {first_id}
    assert not np.array_equal(first_run[other_id] - first_run[other_id].mean(),
                              second_run[renamed] - second_run[renamed].mean())


def test_output_finite_check_covers_numbers_and_skips_text_and_flags():
    # A kind's output may hold text or a flag (DeviceValue oneof); only numbers can be non-finite.
    from argus_basilisk.contracts import devices_pb
    from argus_basilisk.scenario import StaleMessageError, _check_finite_fields

    _check_finite_fields(devices_pb.DeviceValue(text="ok"), "status")
    _check_finite_fields(devices_pb.DeviceValue(flag=True), "locked")
    with pytest.raises(StaleMessageError, match="irradiance.number is not finite"):
        _check_finite_fields(devices_pb.DeviceValue(number=math.nan), "irradiance")
