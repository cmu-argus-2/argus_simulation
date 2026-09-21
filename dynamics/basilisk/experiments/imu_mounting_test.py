"""Verify body-to-IMU axis mapping and inverse mapping with ideal gyros."""
from pathlib import Path
import argparse
import json
import numpy as np
import matplotlib.pyplot as plt
from Basilisk.utilities import SimulationBaseClass, macros
from Basilisk.simulation import spacecraft, imuSensor


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--show', action='store_true')
    args = parser.parse_args()
    out = Path(__file__).resolve().parent / 'results'
    out.mkdir(exist_ok=True)
    sim = SimulationBaseClass.SimBaseClass()
    process = sim.CreateNewProcess('dynamics')
    process.addTask(sim.CreateNewTask('sample', macros.sec2nano(.1)))
    sat = spacecraft.Spacecraft()
    sat.ModelTag = 'truth'
    sat.hub.mHub = 10.
    sat.hub.IHubPntBc_B = np.eye(3).tolist()
    sat.hub.omega_BN_BInit = [.01, 0., 0.]
    sim.AddModelToTask('sample', sat, 100)
    truth = sat.scStateOutMsg.recorder()
    sim.AddModelToTask('sample', truth, 0)
    # Sensor axes rotated +90 deg about body Z:
    # x_P = y_B, y_P = -x_B, z_P = z_B.
    # Column vectors transform as w_P = C_PB @ w_B.
    rotated = np.array([[0., 1., 0.], [-1., 0., 0.], [0., 0., 1.]])
    matrices = {'aligned': np.eye(3), 'rotated': rotated}
    sensors, logs = [], {}
    for name, matrix in matrices.items():
        sensor = imuSensor.ImuSensor()
        sensor.ModelTag = name
        sensor.dcm_PB = matrix.tolist()
        sensor.PMatrixGyro = np.zeros((3, 3)).tolist()
        sensor.senRotBias = [0., 0., 0.]
        sensor.scStateInMsg.subscribeTo(sat.scStateOutMsg)
        sim.AddModelToTask('sample', sensor, 50)
        log = sensor.sensorOutMsg.recorder()
        sim.AddModelToTask('sample', log, 0)
        sensors.append(sensor)
        logs[name] = log
    sim.InitializeSimulation()
    sim.ConfigureStopTime(macros.sec2nano(60.))
    sim.ExecuteSimulation()
    valid = truth.times() > 0
    t = truth.times()[valid] * macros.NANO2SEC
    body = np.asarray(truth.omega_BN_B)[valid]
    data = {}
    metrics = {}
    for name, matrix in matrices.items():
        assert np.array_equal(truth.times(), logs[name].times())
        assert np.allclose(matrix @ matrix.T, np.eye(3))
        measured = np.asarray(logs[name].AngVelPlatform)[valid]
        expected = body @ matrix.T  # Row-vector storage.
        recovered = measured @ matrix
        forward_error = float(np.max(np.abs(measured - expected)))
        inverse_error = float(np.max(np.abs(recovered - body)))
        assert forward_error < 1e-10, (name, forward_error)
        assert inverse_error < 1e-10, (name, inverse_error)
        data[name] = (measured, recovered)
        metrics[name] = {'C_PB': matrix.tolist(),
                         'first_sensor_gyro_rad_s': measured[0].tolist(),
                         'forward_max_error_rad_s': forward_error,
                         'inverse_max_error_rad_s': inverse_error}
    np.savetxt(out / 'imu_mounting.csv',
               np.column_stack([t, body, data['aligned'][0], data['rotated'][0], data['rotated'][1]]),
               delimiter=',', comments='', header='time_s,' + ','.join(
                   f'{kind}_{axis}_rad_s' for kind in ['truth_body', 'aligned_sensor', 'rotated_sensor', 'recovered_body'] for axis in 'xyz'))
    (out / 'imu_mounting_metrics.json').write_text(json.dumps(metrics, indent=2))
    fig, axes = plt.subplots(2, 2, figsize=(12, 7), sharex=True)
    panels = [(body, 'True angular velocity (body axes)'),
              (data['aligned'][0], 'Aligned IMU (sensor axes)'),
              (data['rotated'][0], 'IMU mounted +90 degrees about body Z (sensor axes)'),
              (data['rotated'][1], 'Rotated IMU transformed back to body axes')]
    for ax, (values, title) in zip(axes.flat, panels):
        for index, axis in enumerate('XYZ'):
            ax.plot(t, values[:, index], label=axis, linestyle=['-', '--', ':'][index])
        ax.set_title(title, fontsize=10)
        ax.set_ylim(-.012, .012)
        ax.set_ylabel('Angular rate (rad/s)')
        ax.set_xlabel('Time (s)')
        ax.grid(alpha=.3)
        ax.legend()
    fig.suptitle('Basilisk mounting check: ideal gyros, body X rotation = +0.01 rad/s')
    fig.tight_layout()
    fig.savefig(out / 'imu_mounting.png', dpi=160)
    print(json.dumps(metrics, indent=2))
    print(f'PASS: forward and inverse transforms match truth. Results: {out}')
    if args.show:
        plt.show()


if __name__ == '__main__':
    main()
