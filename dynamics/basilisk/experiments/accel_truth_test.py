"""Accelerometer specific-force check in Earth orbit; illustrative hardware values."""
from pathlib import Path
import argparse
import json
import numpy as np
import matplotlib.pyplot as plt
from Basilisk.utilities import SimulationBaseClass, macros, simIncludeGravBody
from Basilisk.simulation import spacecraft, imuSensor, extForceTorque


def run_case(force_n):
    sim = SimulationBaseClass.SimBaseClass()
    process = sim.CreateNewProcess('dynamics')
    process.addTask(sim.CreateNewTask('sample', macros.sec2nano(0.1)))
    sat = spacecraft.Spacecraft()
    sat.ModelTag = 'satellite'
    sat.hub.mHub = 10.
    sat.hub.IHubPntBc_B = np.eye(3).tolist()
    sat.hub.r_BcB_B = [0., 0., 0.]
    sat.hub.sigma_BNInit = [0., 0., 0.]
    sat.hub.omega_BN_BInit = [0., 0., 0.]
    gravity = simIncludeGravBody.gravBodyFactory()
    earth = gravity.createEarth()
    earth.isCentralBody = True
    gravity.addBodiesTo(sat)
    radius = earth.radEquator + 500e3
    sat.hub.r_CN_NInit = [radius, 0., 0.]
    sat.hub.v_CN_NInit = [0., np.sqrt(earth.mu / radius), 0.]
    force = extForceTorque.ExtForceTorque()
    force.ModelTag = 'constant_body_force'
    force.extForce_B = [force_n, 0., 0.]
    sat.addDynamicEffector(force)
    sim.AddModelToTask('sample', force, 110)
    sim.AddModelToTask('sample', sat, 100)
    truth = sat.scStateOutMsg.recorder()
    sim.AddModelToTask('sample', truth, 0)
    sensors, logs = [], {}
    for name, bias, noise in [('ideal', 0., 0.), ('noisy', .005, .002)]:
        sensor = imuSensor.ImuSensor()
        sensor.ModelTag = name
        # Default sensor location is the body origin, here also the center of mass.
        # Default mounting is identity; body does not rotate in this experiment.
        sensor.setAMatrixAccel(np.zeros((3, 3)))
        sensor.PMatrixAccel = (noise * np.eye(3)).tolist()
        sensor.senTransBias = [bias, 0., 0.]
        sensor.RNGSeed = 42
        sensor.scStateInMsg.subscribeTo(sat.scStateOutMsg)
        sim.AddModelToTask('sample', sensor, 50)
        log = sensor.sensorOutMsg.recorder()
        sim.AddModelToTask('sample', log, 0)
        sensors.append(sensor)
        logs[name] = log
    sim.InitializeSimulation()
    sim.ConfigureStopTime(macros.sec2nano(120.))
    sim.ExecuteSimulation()
    times = truth.times()
    valid = times > 0  # Exclude initialization: no prior IMU integration interval.
    values = {}
    for name, log in logs.items():
        assert np.array_equal(times, log.times())
        values[name] = np.asarray(log.AccelPlatform)[valid]
    expected = np.tile([force_n / 10., 0., 0.], (valid.sum(), 1))
    maximum_error = float(np.max(np.abs(values['ideal'] - expected)))
    assert maximum_error < 1e-8, maximum_error
    error = values['noisy'][:, 0] - expected[:, 0]
    metrics = {'force_n': force_n, 'mass_kg': 10.,
               'expected_specific_force_x_m_s2': force_n / 10.,
               'ideal_max_abs_error_m_s2': maximum_error,
               'noisy_mean_x_error_m_s2': float(error.mean()),
               'noisy_x_error_std_m_s2': float(error.std()),
               'initial_gravity_m_s2': earth.mu / radius**2}
    return times[valid] * macros.NANO2SEC, expected, values, metrics


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--show', action='store_true')
    args = parser.parse_args()
    out = Path(__file__).resolve().parent / 'results'
    out.mkdir(exist_ok=True)
    fig, axes = plt.subplots(2, 2, figsize=(12, 7), sharex=True)
    metrics = {}
    for col, (name, force) in enumerate([('free_fall', 0.), ('constant_force', 1.)]):
        t, expected, values, stats = run_case(force)
        metrics[name] = stats
        axes[0, col].plot(t, values['noisy'][:, 0], alpha=.65, label='Bias + white noise')
        axes[0, col].plot(t, values['ideal'][:, 0], label='Ideal accelerometer')
        axes[0, col].plot(t, expected[:, 0], 'k--', label='Expected F/m')
        axes[0, col].set_title(f'{name}: force = {force:g} N, mass = 10 kg')
        axes[1, col].plot(t, values['noisy'][:, 0] - expected[:, 0], label='Noisy minus expected')
        axes[1, col].axhline(.005, color='k', linestyle='--', label='Configured bias')
        axes[1, col].set_xlabel('Time (s)')
        axes[0, col].set_ylabel('X specific force (m/s²)')
        axes[1, col].set_ylabel('X measurement error (m/s²)')
        header = 'time_s,' + ','.join(f'{kind}_{axis}_m_s2' for kind in ['expected', 'ideal', 'noisy'] for axis in 'xyz')
        np.savetxt(out / f'accel_{name}.csv', np.column_stack([t, expected, values['ideal'], values['noisy']]), delimiter=',', header=header, comments='')
    for ax in axes.flat:
        ax.legend(fontsize=8)
        ax.grid(alpha=.3)
    fig.suptitle('Basilisk accelerometer: Earth gravity enabled, 10 Hz, illustrative sensor parameters')
    fig.tight_layout()
    fig.savefig(out / 'accel_comparison.png', dpi=160)
    (out / 'accel_metrics.json').write_text(json.dumps(metrics, indent=2))
    print(json.dumps(metrics, indent=2))
    print(f'PASS: ideal acceleration matches F/m in both cases. Results: {out}')
    if args.show:
        plt.show()


if __name__ == '__main__':
    main()
