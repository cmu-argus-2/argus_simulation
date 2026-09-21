"""Gyro capability experiment. Illustrative parameters, not Argus hardware specs."""
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
    proc = sim.CreateNewProcess('dynamics')
    proc.addTask(sim.CreateNewTask('sample', macros.sec2nano(0.1)))
    sat = spacecraft.Spacecraft()
    sat.ModelTag = 'truth'
    sat.hub.mHub = 10.0
    sat.hub.IHubPntBc_B = [[1., 0., 0.], [0., 1., 0.], [0., 0., 1.]]
    sat.hub.omega_BN_BInit = [0.01, 0., 0.]
    sim.AddModelToTask('sample', sat, 100)
    truth = sat.scStateOutMsg.recorder()
    sim.AddModelToTask('sample', truth, 0)
    sensors, logs = [], {}
    # Identity sensor mounting: platform frame equals spacecraft body frame.
    cases = {'ideal': (0., 0.), 'white': (0., 0.001),
             'random_walk': (1., 0.0001), 'default_A': (None, 0.0001)}
    for name, (a, noise) in cases.items():
        sensor = imuSensor.ImuSensor()
        sensor.ModelTag = name
        sensor.PMatrixGyro = (np.eye(3) * noise).tolist()
        if a is not None:
            sensor.setAMatrixGyro(np.eye(3) * a)
        sensor.senRotBias = [0., 0., 0.] if name == 'ideal' else [0.002, 0., 0.]
        sensor.scStateInMsg.subscribeTo(sat.scStateOutMsg)
        sim.AddModelToTask('sample', sensor, 50)
        log = sensor.sensorOutMsg.recorder()
        sim.AddModelToTask('sample', log, 0)
        sensors.append(sensor)
        logs[name] = log
    sim.InitializeSimulation()
    sim.ConfigureStopTime(macros.sec2nano(600.))
    sim.ExecuteSimulation()
    t = truth.times() * macros.NANO2SEC
    omega = np.asarray(truth.omega_BN_B)
    valid = t > 0  # IMU first sample has no preceding integration interval.
    measurements = {}
    stats = {}
    for name, log in logs.items():
        assert np.array_equal(log.times(), truth.times()), 'Timestamps differ'
        measurements[name] = np.asarray(log.AngVelPlatform)
        err = (measurements[name] - omega)[valid, 0]
        stats[name] = {'mean_error_rad_s': float(err.mean()),
                       'std_error_rad_s': float(err.std()),
                       'lag1_error_correlation': float(np.corrcoef(err[:-1], err[1:])[0, 1]) if err.std() > 1e-12 else None}
    assert np.max(np.abs((measurements['ideal'] - omega)[valid])) < 1e-9
    columns = [t, omega]
    header = ['time_s', 'truth_wx_rad_s', 'truth_wy_rad_s', 'truth_wz_rad_s']
    for name, values in measurements.items():
        columns.append(values)
        header.extend(f'{name}_w{axis}_rad_s' for axis in 'xyz')
    np.savetxt(out / 'gyro_comparison.csv', np.column_stack(columns), delimiter=',',
               header=','.join(header), comments='')
    (out / 'metrics.json').write_text(json.dumps(stats, indent=2))
    fig, axes = plt.subplots(2, 1, figsize=(11, 7), sharex=True)
    axes[0].plot(t[valid], omega[valid, 0], 'k--', label='True body angular rate')
    for name in ['white', 'random_walk']:
        axes[0].plot(t[valid], measurements[name][valid, 0], alpha=.7, label=name)
        axes[1].plot(t[valid], (measurements[name]-omega)[valid, 0], alpha=.7, label=name)
    axes[1].axhline(.002, color='k', linestyle='--', label='Configured fixed bias')
    axes[0].set_ylabel('X angular rate (rad/s)')
    axes[1].set_ylabel('Measurement minus truth (rad/s)')
    axes[1].set_xlabel('Simulation time (s)')
    for ax in axes:
        ax.legend()
        ax.grid(alpha=.3)
    fig.suptitle('Basilisk gyro test: 10 Hz, illustrative parameters, sensor axes = body axes')
    fig.tight_layout()
    fig.savefig(out / 'gyro_comparison.png', dpi=160)
    print(json.dumps(stats, indent=2))
    print(f'Results: {out}')
    if args.show:
        plt.show()


if __name__ == '__main__':
    main()
