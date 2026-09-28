# Basilisk dynamics and sensor experiments

Haoyuan's standalone capability checks for the dynamics side of Argus simulation.
These use illustrative parameters, not confirmed Argus hardware specifications.
The synchronized state and gyro scenario can be replayed in Unity through the
development-only file adapter described below. It is not connected to FSW/OD,
actuator feedback or a live Basilisk process.

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

### First synchronized integration dataset

Run `python experiments/synchronized_gyro.py` for a 60-second Earth-orbit
scenario with attitude truth and ideal/noisy gyros sampled together at 20 Hz.
It verifies matching timestamps, 0.05-second spacing, finite outputs, ideal gyro
agreement and circular-orbit radius preservation. Output is written under
`experiments/results/synchronized_gyro/` as `samples.jsonl`, `metadata.json`
and `metrics.json`. The 1,200 valid records exclude the initialization reading.
Use `--duration` and `--epoch` to set duration and the timezone-aware clock epoch.

This is an intermediate dataset, **not an Argus-compatible ECEF stream**:
position/velocity use the scenario inertial N frame; attitude is `sigma_BN`
(inertial-to-body MRP). UTC is a clock label, not astronomical frame alignment.
Hardware values are illustrative. ECEF/frame conversion, the new
`ISimulationEngine` transport and actuator/FSW integration remain to be implemented.
The temporary synthetic fixed-frame adapter below exists only for the offline
Unity smoke test; it is not a production ECI/ECEF conversion.

### Original standalone gyro export

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

## Temporary frame translator (offline only)

From the repository root, using the working Basilisk interpreter:

```bash
../framework/.venv/bin/python -m unittest discover -s dynamics/basilisk -p 'test_frame_adapter.py' -v
../framework/.venv/bin/python dynamics/basilisk/frame_adapter.py dynamics/basilisk/experiments/results/synchronized_gyro/samples.jsonl
```

This creates `synthetic_fixed_samples.jsonl` next to the input; it refuses to
overwrite existing output (use `--output` with a new filename when rerunning).
It does not modify the source data or require another simulation run.

Profile `synthetic-earth-fixed-aligned-at-t0-v1` matches the constant Earth
rotation convention in `CircularOrbitModel`: fixed axes equal scenario N at
t=0, rotation rate 7.2921150e-5 rad/s. UTC labels do not determine Earth angle.
This is **not astronomical ECEF/ITRF**, and is unsuitable for real geographic
accuracy evaluation. Replace this profile with Yi Ni's agreed frame/time model.

Translation includes rotating position, subtracting Earth cross position from
inertial velocity before rotating it, and converting Basilisk N-to-body MRP to
active body-to-fixed quaternion **x,y,z,w**. Inertial-relative body gyro values
are unchanged; noisy measurements remain separate from state truth.
Output names use `fixed`, not `ecef`, deliberately. The JSON schema is an offline
intermediate format, not an existing Unity deserialization/transport contract.

## Next integration boundary

Unity already defines `ISpacecraftStateSource` and `SpacecraftState` under
`Assets/ArgusSimulation/Core`. It expects elapsed time, UTC time, ECEF position
and velocity (m, m/s), body-to-ECEF quaternion (x,y,z,w), and body angular rate.
The synchronized scenario and temporary frame translator now exist. Next add
a production frame/time profile and transport after the current file-replay
contract is reviewed. A development-only `BasiliskReplayStateSource` Unity
reader is included; `ISimulationEngine` transport and actuator feedback are not.

### Unity offline replay check

1. Open the project in Unity. Use a test scene or an unsaved copy of the current
   scene; do not replace the shared analytic scene configuration yet.
2. On the GameObject containing `SimulationRunner`, add `BasiliskReplayStateSource`.
3. Enable **Allow Synthetic Earth Fixed** on that component. The default path
   points at the translated JSONL above, relative to the Unity project root.
4. Enter Play mode. On the replay component's context menu choose **Load Replay
   And Configure Runner**. This switches the runner to replay, resets sequence,
   sets the 0.05-second step and preserves the original 0.05-second first timestamp.
5. Check the Console for `Loaded 1200 Basilisk samples`. Confirm no errors and
   check that the spacecraft marker follows the short replay trail. The trail
   uses every recorded replay position rather than the analytic 5,700-second
   orbit preview. At 60 seconds data ends; no extrapolation or looping is
   performed. Stop Play mode to restore the scene.
6. In Test Runner, run both EditMode and PlayMode tests. `BasiliskReplayTests`
   cover parsing, profile opt-in, exact timestamps, gyro/truth separation, EOF
   and runner reset. `ReplayTrailUsesEveryRecordedSample` checks that all 1,200
   replay positions reach the Unity `LineRenderer`.

The reader only accepts exact sample times, not interpolation. Noisy gyro is
available via `TryGetGyroMeasurement` but is **not wired into the FSW sensor
bus**. Existing sensor simulators must not be mistaken for this recorded gyro.
The reader maps the synthetic fixed frame into the existing ECEF-named state
fields only after explicit opt-in. This is an offline visual/interface smoke
test, not validation of geographic accuracy, nadir pointing, closed-loop
control or FSW integration. The current Basilisk demo tumbles about body X; it
is not a nadir-pointing orbit.

Coordinate conversion must include Earth rotation in velocity and transform
attitude consistently. Keep state truth separate from noisy IMU observations.
Confirm camera geometry, image timestamps and exporter format with Sid/Krushna.
Unrelated ISS images cannot be given valid simulated IMU/truth just by assigning timestamps.
