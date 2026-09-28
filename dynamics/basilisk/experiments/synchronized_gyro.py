"""Step 1: one orbit/attitude/20 Hz IMU scenario, before Argus frame adaptation.

Illustrative parameters only. N is the scenario's Earth-centered inertial frame;
it is NOT yet registered to a terrestrial frame or an astronomical epoch.
UTC labels share a clock but do not establish that frame registration.
"""
import argparse
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path

import numpy as np
from Basilisk.simulation import spacecraft, imuSensor
from Basilisk.utilities import SimulationBaseClass, macros, simIncludeGravBody


def run(duration=60, seed=42):
    sim = SimulationBaseClass.SimBaseClass()
    process = sim.CreateNewProcess('dynamics')
    process.addTask(sim.CreateNewTask('sample', macros.sec2nano(0.05)))
    sat = spacecraft.Spacecraft()
    sat.ModelTag = 'synchronized_spacecraft'
    sat.hub.mHub = 10.0
    sat.hub.IHubPntBc_B = np.eye(3).tolist()
    sat.hub.r_BcB_B = [0., 0., 0.]
    sat.hub.sigma_BNInit = [0., 0., 0.]
    sat.hub.omega_BN_BInit = [0.01, 0., 0.]
    gravity = simIncludeGravBody.gravBodyFactory()
    earth = gravity.createEarth()
    earth.isCentralBody = True
    gravity.addBodiesTo(sat)
    radius = earth.radEquator + 500e3
    sat.hub.r_CN_NInit = [radius, 0., 0.]
    sat.hub.v_CN_NInit = [0., np.sqrt(earth.mu / radius), 0.]
    sim.AddModelToTask('sample', sat, 100)
    truth = sat.scStateOutMsg.recorder()
    sim.AddModelToTask('sample', truth, 0)
    sensors, logs = [], {}
    for name, bias, noise in [('ideal', 0., 0.), ('noisy', .002, .001)]:
        sensor = imuSensor.ImuSensor()
        sensor.ModelTag = name
        sensor.RNGSeed = seed
        sensor.setAMatrixGyro(np.zeros((3, 3)))
        sensor.PMatrixGyro = (np.eye(3) * noise).tolist()
        sensor.senRotBias = [bias, 0., 0.]
        sensor.scStateInMsg.subscribeTo(sat.scStateOutMsg)
        sim.AddModelToTask('sample', sensor, 50)
        log = sensor.sensorOutMsg.recorder()
        sim.AddModelToTask('sample', log, 0)
        sensors.append(sensor)
        logs[name] = log
    sim.InitializeSimulation()
    sim.ConfigureStopTime(macros.sec2nano(duration))
    sim.ExecuteSimulation()
    times = truth.times()
    for log in logs.values():
        np.testing.assert_array_equal(times, log.times())
    valid = times > 0  # IMU initialization lacks a preceding sample interval.
    data = {
        'time_s': times[valid] * macros.NANO2SEC,
        'position_N_m': np.asarray(truth.r_BN_N)[valid],
        'velocity_N_m_s': np.asarray(truth.v_BN_N)[valid],
        'sigma_BN': np.asarray(truth.sigma_BN)[valid],
        'omega_BN_B_rad_s': np.asarray(truth.omega_BN_B)[valid],
        'ideal_gyro_body_rad_s': np.asarray(logs['ideal'].AngVelPlatform)[valid],
        'noisy_gyro_body_rad_s': np.asarray(logs['noisy'].AngVelPlatform)[valid],
    }
    for values in data.values():
        assert np.isfinite(values).all(), 'Non-finite output'
    assert len(data['time_s']) == duration * 20
    np.testing.assert_allclose(np.diff(data['time_s']), .05, atol=1e-12, rtol=0)
    np.testing.assert_allclose(data['ideal_gyro_body_rad_s'],
                               data['omega_BN_B_rad_s'], atol=1e-9, rtol=0)
    # Two-body circular orbit should retain its radius in this short run.
    radius_error = np.max(np.abs(np.linalg.norm(data['position_N_m'], axis=1) - radius))
    assert radius_error < 0.1, radius_error
    error = data['noisy_gyro_body_rad_s'] - data['omega_BN_B_rad_s']
    metrics = {'samples': len(data['time_s']), 'gyro_rate_hz': 20,
               'ideal_max_error_rad_s': float(np.max(np.abs(
                   data['ideal_gyro_body_rad_s'] - data['omega_BN_B_rad_s']))),
               'max_radius_error_m': float(radius_error),
               'noisy_mean_error_rad_s': error.mean(axis=0).tolist(),
               'noisy_std_error_rad_s': error.std(axis=0).tolist()}
    return data, metrics


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--duration', type=int, default=60)
    parser.add_argument('--epoch', default='2026-09-27T00:00:00Z',
                        help='Illustrative clock epoch; use an explicit UTC offset.')
    args = parser.parse_args()
    if args.duration < 1:
        parser.error('duration must be a positive integer')
    epoch = datetime.fromisoformat(args.epoch.replace('Z', '+00:00'))
    if epoch.tzinfo is None:
        parser.error('epoch requires a UTC offset')
    epoch = epoch.astimezone(timezone.utc)
    data, metrics = run(args.duration)
    out = Path(__file__).resolve().parent / 'results' / 'synchronized_gyro'
    out.mkdir(parents=True, exist_ok=True)
    # Intermediate dataset, deliberately not represented as Argus ECEF snapshots.
    with (out / 'samples.jsonl').open('w') as stream:
        for i, time_s in enumerate(data['time_s']):
            record = {key: values[i].tolist() for key, values in data.items()}
            record.update(sequence=i + 1, schema='basilisk-eci-gyro-demo-v1',
                          timestamp_utc=(epoch + timedelta(seconds=float(time_s)))
                          .isoformat().replace('+00:00', 'Z'))
            stream.write(json.dumps(record, allow_nan=False) + '\n')
    metadata = {
        'epoch_utc': epoch.isoformat(), 'seed': 42, 'duration_s': args.duration,
        'step_s': .05, 'mass_kg': 10, 'inertia_kg_m2': [1, 1, 1],
        'altitude_m': 500000, 'orbit': 'circular equatorial, central Earth gravity',
        'sensor_mount': 'identity: platform axes = body axes, at center of mass',
        'gyro_bias_rad_s': [.002, 0, 0],
        'gyro_noise': 'PMatrixGyro = 0.001 * I, A = 0; discrete illustrative values',
        'attitude': 'sigma_BN is MRP for inertial N to body B, not a quaternion',
        'angular_rate': 'body relative to inertial N, expressed in body B',
        'frame_N': 'Earth-centered inertial scenario frame; astronomical alignment TBD',
        'limitations': 'No ECEF conversion, Argus adapter, actuator input or FSW connection.',
    }
    (out / 'metadata.json').write_text(json.dumps(metadata, indent=2))
    (out / 'metrics.json').write_text(json.dumps(metrics, indent=2))
    print(json.dumps(metrics, indent=2))
    print(f'PASS: synchronized orbit, attitude and gyro. Results: {out}')


if __name__ == '__main__':
    main()
