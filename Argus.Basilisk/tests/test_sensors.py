"""IMU modules (README §9, §10) on a spacecraft hub alone; needs bsk==2.11.1 but no kernels."""

import math

import numpy as np
import pytest

pytest.importorskip("Basilisk")

from Basilisk.simulation import spacecraft  # noqa: E402
from Basilisk.utilities import RigidBodyKinematics as rbk  # noqa: E402
from Basilisk.utilities import SimulationBaseClass  # noqa: E402

from argus_basilisk.seeds import ACCEL, GYRO, rng_seed  # noqa: E402
from argus_basilisk.sensors import Imu, build_sensors  # noqa: E402
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
