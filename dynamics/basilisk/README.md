# Basilisk dynamics and sensor experiments

Haoyuan's standalone capability checks for the dynamics side of Argus simulation.
These use illustrative parameters, not confirmed Argus hardware specifications.
They are not yet connected to Unity, the visual exporter, or FSW/OD.

## Run the sensor experiments

Tested with Python 3.12 and Basilisk 2.11.1. From this directory:

```sh
python3.12 -m venv .venv
source .venv/bin/activate
python -m pip install -r requirements.txt
MPLBACKEND=Agg python experiments/gyro_truth_test.py
MPLBACKEND=Agg python experiments/accel_truth_test.py
MPLBACKEND=Agg python experiments/imu_mounting_test.py
python export_gyro.py
```

For interactive plots, omit `MPLBACKEND=Agg` and add `--show` to an experiment.
Each experiment writes CSV, metrics JSON and a plot to `experiments/results/`.
The exporter reads the gyro experiment CSV and writes full 6000-row streams
to `experiments/results/gyro_streams/`. Generated outputs are ignored by Git.
`samples/` contains only the first ten usable rows of those streams for interface review.

## What is tested

- **Gyro:** 600 s at 10 Hz, body rate `[0.01,0,0]` rad/s, aligned sensor axes.
  Compares ideal, white-noise, random-walk and default-transition cases.
  Noisy cases have an X bias of 0.002 rad/s. Noise matrices are discrete model
  parameters, not hardware noise-density specifications. Ideal readings are
  checked against truth. The default transition is compared to identity;
  white noise explicitly uses a zero transition matrix.
- **Accelerometer:** two 120 s, 10 Hz runs with Earth gravity, 10 kg mass,
  no body rotation, sensor at the center of mass. Free fall should produce
  zero ideal specific force; a 1 N body-X force should produce 0.1 m/s².
  Noisy readings add X bias 0.005 m/s² and white noise. Both runs use seed 42.
  This force experiment does not imply that Argus has propulsion.
- **Mounting:** 60 s at 10 Hz, ideal gyros, body-X rate +0.01 rad/s.
  A +90-degree mounting about body Z yields sensor rate `[0,-0.01,0]`.
  `omega_sensor = C_sensor_body @ omega_body`, with
  `C_sensor_body = [[0,1,0],[-1,0,0],[0,0,1]]`.
  Forward mapping and transformation back to body axes are checked against truth.

Initialization readings at t=0 are excluded from sensor comparisons and handoff streams.
These experiments are independent scenarios, not synchronized observations of one orbit.

## Gyro sample contract

```text
timestamp,gyroX,gyroY,gyroZ
```

- Timestamp: elapsed simulation seconds, no absolute UTC epoch. First usable sample 0.1 s.
- Rate: 10 Hz. Gyro: body relative to inertial angular velocity, expressed in
  aligned sensor/body axes, rad/s.
- `gyro_white.csv` and `gyro_random_walk.csv` are measurement examples.
  `gyro_truth.csv` is evaluation-only and must not become an estimator observation.
- This follows Krushna's requested fields; compatibility with the actual FSW
  parser has not been demonstrated. Acceleration outputs use m/s² specific force.

## Legacy orbit example

`orbit/argus_truth_sim.py` is adapted from Basilisk's `scenarioBasicOrbitStream.py`.
It retains the upstream license and interactive demo behavior.
Run `python orbit/argus_truth_sim.py`, then connect Vizard Direct Communication /
Live Streaming to `tcp://localhost:5556`. It waits for Vizard; it is not part
of the headless sensor checks. Let it finish without issuing maneuver commands.

Default orbit: Earth gravity, nominal altitude 500 km, eccentricity 0.001,
inclination 51.6 degrees, RAAN 48.2 degrees, argument of periapsis 347.8 degrees,
initial true anomaly 85.3 degrees. Mass 50 kg; inertia diag(60,30,40) kg m².
It exports `orbit/orbit_ground_truth.csv` with
`time_s,x_m,y_m,z_m,vx_m_s,vy_m_s,vz_m_s` in Earth-centered inertial model
coordinates, m and m/s. It does not export attitude, IMU or camera metadata,
and is not an ISS trajectory reconstruction. No historical orbit CSV is included.

## Next integration boundary

Unity already defines `ISpacecraftStateSource` and `SpacecraftState` under
`Assets/ArgusSimulation/Core`. It expects elapsed time, UTC time, ECEF position
and velocity (m, m/s), body-to-ECEF quaternion (x,y,z,w), and body angular rate.
The next change should create a single synchronized Basilisk scenario, define
its epoch and frame conventions, and add a file-replay state source for Unity.
No such adapter is included in this change.

Coordinate conversion must include Earth rotation in velocity and transform
attitude consistently. Keep state truth separate from noisy IMU observations.
Confirm camera geometry, image timestamps and exporter format with Sid/Krushna.
Unrelated ISS images cannot be given valid simulated IMU/truth just by assigning timestamps.
