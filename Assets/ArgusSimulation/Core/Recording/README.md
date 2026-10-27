# Core/Recording — run recorder

**Diagram block:** Run recorder · **Assembly:** `Argus.Simulation.Core` · **Status:** v0 on main (CORE-5, #41): `RunRecorder` writes dataset v1, with `commands.jsonl` from the gateway's command log (CORE-7, #43), except the manifest's `cameras` (CORE-6, #42); `headless/Host --record <dir>` feeds it

The run recorder is the single export route (decision D6 in
[target-architecture.md](../../../../docs/target-architecture.md)). It is a passive,
write-only archive. It never feeds data back into the run and never blocks the step loop.

## RunRecorder v0

- **API:** `IRunRecorder` (`Core/Abstractions/`): `BeginRun` on every gateway `Reset`
  (`SimulationGateway.RunStarted`), then `RecordState` (`StateProduced`), `RecordSensorOutput`
  (`SensorOutputProduced`), `RecordCommand` (`CommandLogged`) and `RecordTermination`
  (`RunTerminated`), and `Close` at the end. `headless/Host` subscribes exactly these five
  events; Unity-driven runs will call the same methods (UNITY-9, #63).
- **Off the step loop:** the calls only queue. One writer thread formats the JSON, encodes PNGs
  (`PngEncoder`, no Unity) and writes the files, so a stalled disk never delays
  `SimulationGateway.Step` (`RunRecorderTests`).
- **Overflow policy `drop-newest`:** the queue holds at most `capacity` states, sensor output
  sets and command records (65 536 by default). When it is full, the newest one is dropped, and
  the manifest's `overflow` entry counts it: `policy`, `capacity`, `dropped_states`,
  `dropped_frames` (sensor frames) and `dropped_commands`. Nothing is dropped silently.
  `BeginRun` and `RecordTermination` are never dropped.
- **Failures:** a write error, an unknown payload type or a `Jpeg` frame stops writing. The
  manifest then stays `"complete": false`, and `Close` throws. Core has no JPEG decoder, so a
  `Jpeg` `ImageFrame` cannot become the PNG dataset v1 requires. `Png` frames are written as given.
  The recorder never overwrites a run directory that already exists.
- **Not yet written:** `cameras` is `[]` until camera models exist (CORE-6, #42).
  `exposure_start_simulation_time_s` and `exposure_duration_s` are `null` until `ImageFrame`
  carries them.

## Inputs

- Every `SimulationState`: spacecraft truth and the applied commands, plus `EnvironmentState`
  and backend sensor measurements when the backend provides them (Basilisk runs; the
  analytic engine reports neither).
- Every `SensorOutputSet` from `SensorManager`, including ground-truth sensors and camera
  `ImageFrame`s once cameras are sensor models. Camera frames carry their capture time;
  late frames follow the late-frame policy (gap G3).
- The gateway's command log: requested, applied and rejected commands.

## Dataset v1

Fixed by DATA-4 (#69), confirmed with the navigation owner (Krushna Thakkar, 26 Oct). CORE-5 (#41)
writes it, and CORE-7 (#43) added `commands.jsonl`.

```text
<run_id>_<segment>/
├── manifest.json        # what the run was: schema, configuration, sensors, cameras, writer
├── states.jsonl         # one line per SimulationState
├── sensors.jsonl        # one line per sensor frame
├── commands.jsonl       # one line per gateway command record (CORE-7)
└── images/<sensorId>/<sequence>.png
```

### Conventions (every file)

| item | rule |
|---|---|
| Schema version | `manifest.json` carries `"schema_version": "argus-dataset/1"`. The value is exactly `argus-dataset/<major>`, with no minor version. A reader rejects a major version it does not know. Readers ignore keys they do not know and skip `sensors.jsonl` lines whose `payload_type` they do not know. Adding a field or a payload type keeps version 1. Renaming or removing a field, or changing its type, unit, meaning or nullability, makes version 2. |
| Encoding | UTF-8 without a byte-order mark. One JSON object per line in the `.jsonl` files, each ending in `\n` on every OS. Every JSONL line carries `run_id`. |
| Keys | Every key in these tables is always written. A value that does not exist is `null`, never an omitted key. Readers treat a missing key as `null`. |
| Enums | Strings spelled exactly as the C# member, for example `Valid`, `Itrf93`, `Imu`, `Rgb8`. Other fixed strings are listed in their field's row. |
| Units | SI: metres, seconds, radians, kilograms, tesla, W/m². Orbit-element angles are degrees, as in `ClassicalOrbitElements`. Each field name ends in its unit (`_m`, `_s`, `_rad_s`, `_deg`, …). |
| Key names | Unless a table names the keys, a key is the C# property name in snake_case, with the unit word replaced by its suffix: `SemiMajorAxisMeters` → `semi_major_axis_m`, `InclinationDegrees` → `inclination_deg`, `Eccentricity` → `eccentricity`. Quaternion keys end in `_xyzw`. This also names the keys inside `initial_orbit` and `profile`. Unit words and their suffixes: `Meters` `_m`, `Seconds` `_s`, `Degrees` `_deg`, `Radians` `_rad`, `RadiansPerSecond` `_rad_s`, `MetersPerSecond` `_m_s`, `MetersPerSecondSquared` `_m_s2`, `Kilograms` `_kg`, `KilogramSquareMeters` `_kg_m2`, `Tesla` `_t`, `WattsPerSquareMeter` `_w_m2`, `PerRootHertz` `_per_rt_hz`, `PerRootSecond` `_per_rt_s`. For example `NoiseDensityRadiansPerSecondPerRootHertz` → `noise_density_rad_s_per_rt_hz`. The manifest records the run; it is not a run-configuration file, and the HOST-1 run file (PR #91) spells the same values differently. |
| Numbers | Invariant culture (`.` as the decimal point), written round-trip so no precision is lost. JSON has no NaN or Infinity, so a non-finite value (for example in an `Invalid` frame's measurement or a rejected command) is written as `null`, element by element, so a vector keeps its length (`[null, 0.0, 0.0]`). This is the only case where a number holds `null` without its row saying so, and it is not a nullability change. |
| Vectors and matrices | Vectors are `[x, y, z]`. A 3×3 matrix is row-major and nested: `[[a, b, c], [d, e, f], [g, h, i]]`. |
| Quaternions | `[x, y, z, w]`, scalar last, Hamilton, active: `v' = q v q*` (`Core/Math/Quaterniond.cs`). A field named `a_to_b` rotates a vector expressed in frame a into frame b, as in `EnvironmentState.J2000ToItrf93`. |
| Frames | Position, velocity and attitude are Earth-fixed in the frame named by `manifest.earth_fixed_frame`: `Itrf93` in Basilisk runs, `AnalyticEarthFixed` in the analytic fixture (D9). One run never mixes them. The body frame is `SpacecraftState`'s. The camera frame is proposed as x right, y down, z out of the lens (DATA-6, #71); no code defines it yet. |
| Time | `simulation_time_s` is seconds since the run epoch. Every `*_utc` field (`timestamp_utc`, `created_utc`, `epoch_utc`) is UTC, written as `yyyy-MM-ddTHH:mm:ss.fffffffZ`: always 7 fractional digits, ending in `Z`, for example `2026-10-20T12:00:00.0000000Z`. C#'s `"O"` format on a `DateTimeOffset` writes `+00:00` instead, so the writer formats the UTC value with the explicit format and the invariant culture (in .NET custom formats, `:` and the calendar follow the culture). Python 3.11+ `datetime.fromisoformat` reads it to the microsecond; `strptime` with `%f` does not. |
| Paths | Relative to the run directory, separated by `/` on every OS. |
| Join keys | In `states.jsonl`, `sequence` is the spacecraft sequence and is unique within a run directory. Any reset restarts it at 0 (a gateway `Reset`, or `SimulationRunner.ResetSimulation` in Unity), so the recorder closes the directory and starts a new one. A gateway `Reset` keeps the `run_id`, so `manifest.segment` numbers the directories of one `run_id` from 0, and each directory is named `<run_id>_<segment>`. In `sensors.jsonl`, `sequence` counts per sensor, so a frame's key is (`sensor_id`, `sequence`). Every sensor frame carries `state_sequence`, the spacecraft sequence of the state it was captured at, so frames join to their state. How a late camera frame is handled is still open (G3; CORE-3 #38 is blocked on it). If late frames are kept, each keeps its capture state and capture time, not those of the step it arrived in. In file order, the `sequence` of `states.jsonl` and each sensor's `sequence` strictly increase; if late frames are kept, the recorder holds one back until it can be written in order. |

### `manifest.json`

| field | type | source | notes |
|---|---|---|---|
| `schema_version` | string | — | `"argus-dataset/1"` |
| `run_id` | string | `SimulationConfiguration.RunId` | |
| `segment` | int | recorder | 0 for a run's first directory, then +1 after each gateway `Reset` that keeps the `run_id` (see Join keys). The recorder skips any `<run_id>_<segment>` that already exists under its root (from an earlier process or run with the same `run_id`), so it never overwrites a dataset. |
| `created_utc` | string | recorder | When recording started. |
| `complete` | bool | recorder | `true` only once the recorder closed cleanly. |
| `termination` | object or `null` | `SimulationGateway.RunTerminated` | Set when the run ended early: `status` (`BackendFailed` when the backend failed, `Stopped` when the caller stopped it, for example on Ctrl+C or a recorder failure), `last_sequence`, `last_simulation_time_s` and `last_timestamp_utc` (the last step that exists, as in `states.jsonl`), and `reason` (for `BackendFailed`, names the step that failed). `null` when the run reached its end, for example the host's `--duration-s`. With `complete: true`, it tells a run that died at a step from one that was cut short. Added before any v1 dataset was written, so the schema stays at version 1. |
| `writer` | object | recorder | `process` (`headless-host` or `unity`), `argus_simulation_commit` (the full 40-character hash, or `null` when unknown, for example in a Unity player build), `recorder_version` (string). |
| `dynamics_backend` | object | `SimulationState.DynamicsBackend`, `BasiliskEngine.BasiliskVersion` | `name` (the `DynamicsBackend` string) and `version` (`BasiliskVersion` in Basilisk runs, `null` in analytic runs). |
| `earth_fixed_frame` | string | `SpacecraftState.EarthFixedFrame` | `Itrf93` or `AnalyticEarthFixed`. |
| `configuration` | object | `SimulationConfiguration` (G5); `AnalyticSimulationEngine` for `analytic_orbit` | `epoch_utc`, `fixed_step_s`, `random_seed`, `kernel_set_id` (or `null`), `initial_orbit` (classical elements in J2000, or `null`), `spacecraft` (`mass_kg`, `inertia_body_kg_m2` as a 3×3 matrix, `initial_body_to_j2000_xyzw`, `initial_angular_velocity_body_rad_s`; or `null`), and `analytic_orbit`. `fixed_step_s` and `random_seed` are the values the run actually uses. In Unity-driven runs these are `SimulationRunner`'s step and `SimulationSensorRuntime`'s seed, not the 0.1 s and seed 0 that `AnalyticStateSource` passes to the engine (which ignores the seed). Analytic runs have no `initial_orbit` or `spacecraft`, because `AnalyticSimulationEngine` takes its orbit from its constructor; it exposes that orbit as `AnalyticSimulationEngine.Orbit`. `analytic_orbit` (`altitude_m`, `inclination_deg`, `raan_deg`, `phase_deg`) is that orbit at the start of the run. Its angles are measured in the analytic fixture's own inertial frame, not J2000, so they cannot be compared with `initial_orbit`. The dashboard can change altitude and phase during a run, and `states.jsonl` records the result. `analytic_orbit` is `null` in Basilisk runs. |
| `sensors` | array | `SensorManager.Sensors[].Definition`, plus `SensorConfiguration` where one exists | Every sensor that publishes frames: `sensor_id`, `model_name`, `frame_id`, `sample_period_s`, `source`, `mount` (`position_body_m`, `sensor_to_body_xyzw`), `controller_visible` (bool: whether the gateway may pass the sensor's frames to a controller, `SensorDefinition.IsControllerVisible`), then `kind` and `profile` (that kind's profile fields). `kind` and `profile` are `null` for a sensor with no `SensorConfiguration`, such as the ideal body-rate sensor `imu.body_rate`. |
| `cameras` | array | camera models (CORE-6) | Per camera: `sensor_id`, `profile_id`, `intrinsics` (`width_px`, `height_px`, `fx_px`, `fy_px`, `cx_px`, `cy_px`, `source`, `calibrated`), `distortion` (`model`, `coeffs`, `source`), `noise` (`source`), `extrinsics` (`source`, `origin`, `orientation_verified`), as in the DATA-6 profile (#71). When a calibration lands, `profile_id` changes and `calibrated` becomes `true`. |
| `files` | object | recorder | `{}` until close. Then one entry per JSONL file, keyed by its path, for example `{"states.jsonl": {"lines": 1200}, "sensors.jsonl": {"lines": 5400}, "commands.jsonl": {"lines": 1199}}`. Images and the manifest are not listed. |
| `overflow` | object | recorder (CORE-5) | `policy` (`drop-newest`), `capacity`, `dropped_states`, `dropped_frames` and `dropped_commands` (added by CORE-7 before any v1 dataset was written, so the schema stays at version 1); see RunRecorder v0 above. Nothing is dropped silently. |

### `states.jsonl`: one line per `SimulationState`

Every field here is simulator truth, for evaluation only: it must never be an input to the agent or
the navigation policy (D7). The same holds for the camera pose in `ImageFrame` payloads and for frames
from sensors whose `controller_visible` is `false`, such as the nadir ground-truth camera (CORE-8). A
consumer that feeds an agent or controller uses only frames whose sensor has `controller_visible: true`.

| field | type | source |
|---|---|---|
| `run_id` | string | `SimulationState.RunId` |
| `sequence` | int | `SpacecraftState.Sequence` |
| `simulation_time_s` | number | `SpacecraftState.SimulationTimeSeconds` |
| `timestamp_utc` | string | `SpacecraftState.TimestampUtc` |
| `position_ecef_m` | [3] | `PositionEcefMeters` |
| `velocity_ecef_m_s` | [3] | `VelocityEcefMetersPerSecond`, relative to the Earth-fixed frame |
| `body_to_ecef_xyzw` | [4] | `BodyToEcef` |
| `angular_velocity_body_rad_s` | [3] | `AngularVelocityBodyRadiansPerSecond`: the body rate relative to inertial, in body axes. In analytic runs (`AnalyticEarthFixed`) it is the fixture's (0, n, 0) instead until CORE-2 (PR #83) lands, a known issue (`CircularOrbitModel` TODO; `target-architecture.md`). |
| `producing_command` | object or `null` | `AppliedCommands`: `sequence`, `apply_at_simulation_time_s`, `reaction_wheel_torque_body_n_m`, `magnetorquer_dipole_body_a_m2`, `thruster_force_body_n`. The command that produced this state: command k-1, applied over [t_k-1, t_k) (DATA-5 #70, CORE-4 #40). `null` at t_0, and in runs that apply no commands (Unity's `AnalyticStateSource`). The command chosen from state k is the next line's `producing_command`, or the `commands.jsonl` line with the same `sequence` whose `outcome` is not `Rejected`. Renamed from DATA-4's `applied_command` (command k) before any v1 dataset was written, so the schema stays at version 1. |
| `environment` | object or `null` | `EnvironmentState`: `sun_position_itrf93_m`, `j2000_to_itrf93_xyzw`, `earth_angular_velocity_itrf93_rad_s`, `spacecraft_shadow_factor` (Basilisk's illumination factor: 1 is fully sunlit, 0 is umbra). `null` in analytic runs (D9). |

Backend sensor measurements (`SensorMeasurements`) are not repeated here. They appear in
`sensors.jsonl` as the frames each `BackendSensorModel` publishes (D10).

### `sensors.jsonl`: one line per sensor frame

| field | type | source |
|---|---|---|
| `run_id` | string | `SensorFrame.RunId` |
| `sensor_id` | string | `SensorId` |
| `frame_id` | string | `FrameId` |
| `sequence` | int | `Sequence` |
| `state_sequence` | int | `SensorOutputSet.SpacecraftSequence` (late camera frames: see Join keys) |
| `simulation_time_s` | number | `SimulationTimeSeconds` |
| `timestamp_utc` | string | `TimestampUtc` |
| `status` | string | `SensorFrameStatus`: `Valid`, `Unavailable`, `Stale` or `Invalid` |
| `source` | string | `Source` |
| `payload_type` | string | the payload's C# type name without its namespace, for example `ImuMeasurement`. Set on every frame, even when `payload` is `null`. |
| `payload` | object or `null` | the payload's fields (below). `null` when `status` is `Unavailable`. Otherwise it is written as the model produced it, so an `Invalid` frame keeps its measurement; readers check `status` first. |

| `payload_type` | `payload` fields |
|---|---|
| `ImuMeasurement` | `angular_velocity_sensor_rad_s`, `specific_force_sensor_m_s2` |
| `AngularRateMeasurement` | `angular_velocity_sensor_rad_s` |
| `MagnetometerMeasurement` | `magnetic_field_sensor_t` |
| `LightSensorMeasurement` | `irradiance_w_m2` |
| `ImageFrame` | `image` (the PNG's path, or `null` when the frame has no pixels), `camera_id`, `intrinsics` (the six values, as in the manifest), `camera_position_ecef_m`, `camera_to_ecef_xyzw`, `renderer` (string), `exposure_start_simulation_time_s` and `exposure_duration_s`, from the frame's `RenderRequest` (`null` until `ImageFrame` carries them: CORE-3 #38, UNITY-4 #57). Pixels go only to the PNG. |

A sensor added later brings its own `payload_type`. That is an added payload type, so the schema stays at version 1.

### `commands.jsonl`: one line per gateway command record (CORE-7)

`SimulationGateway.CommandLogged` raises a `CommandRecord` (`Core/Contracts/CommandRecord.cs`) for
every command `Step` receives once a run has started, before the engine runs it or `Step` throws:
an invalid value, the wrong sequence or time, or a step after the run ended is `Rejected` with its
reason. `Applied` means the gateway passed the command to the engine; if the backend then fails,
the manifest's `termination` says so. `Clamped` arrives with actuator limits (CORE-10, #46). The file
is created in every run directory, so it is empty when no command was received, as in
Unity-driven runs, which bypass the gateway (G1).

| field | type | source |
|---|---|---|
| `run_id` | string | `CommandRecord.RunId` |
| `sequence` | int | `CommandRecord.Sequence`: the gateway's next step k when the command arrived, whatever the command claimed, so every line joins to state k |
| `simulation_time_s` | number | `CommandRecord.SimulationTimeSeconds`: t_k |
| `outcome` | string | `Applied`, `Clamped` or `Rejected` |
| `reason` | string or `null` | why a command was clamped or rejected; `null` when `Applied` |
| `requested` | object | the requested `ActuatorCommandSet` as the controller sent it, with the same fields as `producing_command` above; its own `sequence` and `apply_at_simulation_time_s` may differ from the line's when it was rejected |
| `applied` | object or `null` | the `ActuatorCommandSet` the engine ran over [t_k, t_k+1), which state k+1 carries as `producing_command`; `null` when rejected |

In file order `sequence` never decreases. Several lines may share one, since every command
rejected for a step is logged against it, but at most one line per `sequence` is `Applied` or
`Clamped`.

### `images/<sensorId>/<sequence>.png`

- **One PNG per camera frame that has pixels,** whatever its `status`, so a late frame marked
  `Stale` keeps its image. An `Unavailable` frame has no payload, so it has no PNG. The name is the frame's `sequence`, zero-padded to 8 digits, for example
  `images/cam_xp/00000042.png`. Readers take the path from `payload.image` and never rebuild it.
- **Run and sensor IDs are path-safe:** a lowercase letter or digit, then lowercase letters, digits, `.`, `_`
  or `-` (`[a-z0-9][a-z0-9._-]*`), not ending in `.` and not a Windows device name such as `con` or
  `nul`, as in `light.px` and `imu.body_rate`. The pattern is the kernel-set ID rule in
  `SimulationConfiguration`; with it, `.`, `..` and IDs that differ only by case cannot occur, and a
  directory name valid on one OS is valid on all. The recorder rejects any other ID
  (`RunRecorder.IsPathSafeId`), because `SimulationConfiguration` and `SensorDefinition` only check
  that an ID is not blank; `headless/Host` checks it before connecting when `--record` is given.
- **Every image is an 8-bit sRGB PNG,** RGB (RGBA for an `Rgba8` frame), with the first row at the
  top of the image, whatever the frame's `ImageEncoding`. In `Rgb8` and `Rgba8` `ImageFrame.Data`, row
  0 is the top row: the renderer flips Unity's bottom-up readback, and the recorder never flips. A
  `Jpeg` frame stops the recording (CORE-5), since Core has no JPEG decoder.

### Reader checks

A reader rejects a dataset in which:
- in `states.jsonl` or one sensor's frames, a `sequence` repeats or decreases in file order (see
  Join keys); in `commands.jsonl`, a `sequence` decreases, or more than one line with the same
  `sequence` is `Applied` or `Clamped`;
- a number is `null` where its row does not allow `null`, in `states.jsonl` or in a `Valid` frame's
  payload;
- a PNG's size differs from its frame's `intrinsics` (`width_px` × `height_px`).

## Which process writes the dataset

The recorder is Core code with no Unity references (D1). DATA-4 places it in the process that owns
the step loop, so both run types write the same format with the same code:

| run | owns the step loop | recorder runs in | images come from |
|---|---|---|---|
| Basilisk run | `headless/Host` (`BasiliskEngine`) | `headless/Host` | Unity, through Core's `IImageRenderer`, over the render link (UNITY-10 #64, G3) |
| Unity-driven analytic run (`AnalyticStateSource`) | Unity | Unity, as the same `RunRecorder` | the in-process `UnityImageRenderer` (UNITY-4 #57), once CORE-3 (#38) decides what a camera does in analytic runs |

`manifest.writer.process` records which one. In Unity-driven runs the recorder takes sensor frames
from the temporary sensor bridge (`Unity/Sensors/`). G2 moves the sensors and the recorder into the
headless core, and from then on every run records there. Dashboard attitude nudges change only the
live dashboard feeds, not the recorded state. Recorded images are rendered at the pose in each
`RenderRequest`, which the camera model takes from the recorded state (D5, CORE-3 #38), so they match
`body_to_ecef_xyzw`.
UNITY-9 (#63) points the dashboard's Capture at the recorder in the Unity process.

## Replacing `NavigationEpisodeExporter` (D6)

**No compatibility layer.** Dataset v1 replaces the exporter output (DATA-4, #69), as Krushna Thakkar,
the navigation owner, confirmed on 26 Oct. The exporter has one reader,
`argus-agentic-gnc/scripts/unity_nadir_camera_episode_to_navigation_inputs.py` in
`kru2710shna/argus-work` (`main` at `4799328`). Its output manifest feeds
`run_unity_navigation_episode.py` in the same folder, which reads
`<dir>/unity_nadir_navigation_manifest.json` and opens `<dir>/images/<image_file>` for each entry. A
dataset v1 reader therefore writes that manifest into the run directory, with `image_file` set to
`payload.image` minus the leading `images/`.
Both scripts keep only the camera `"NADIR EARTH  -Z"`, which this repo removed in `9206f11`, so they
find no frames in today's exporter output. No planned camera replaces it: CORE-6 (#42) adds the four
±X/±Y cameras and CORE-8 (#44) the north-up nadir camera. The reader's owner picks
the camera once the CORE-6 and CORE-8 cameras exist, and agrees its `camera_name` with Krushna so the
downstream scripts stay unchanged.

The reader moves to dataset v1, and Maggie Pai owns it. It emits the same manifest contract as today:
all 16 fields in the table below, with their current names and nesting, and a stable `frame_id`. The
converter builds `frame_id` as `unity_sequence_<sequence:06d>` from the exporter's `sequence`, so the
reader uses the frame's `state_sequence`. Krushna will run one reader-to-downstream check once the
reader exists. UNITY-9 (#63) retires the exporter once the reader has moved to dataset v1.

| `navigation_metadata.jsonl` | dataset v1 |
|---|---|
| `timestamp_utc`, `sequence`, `simulation_time_seconds` | `timestamp_utc`, `sequence`, `simulation_time_s` in `states.jsonl` at the camera frame's `state_sequence`. The exporter wrote the state's values, not the frame's. v1 writes `timestamp_utc` ending in `Z`, where the exporter wrote `+00:00`. |
| `image_file` | `payload.image`, a path relative to the run directory (`null` when the frame has no pixels). The exporter wrote a bare file name. |
| `camera_name` | `sensor_id`. The exporter's names are display names (`"FORWARD  +X"`, `"NADIR GT  NORTH-UP"`), not sensor IDs. Camera IDs come from the run configuration (CORE-6 #42, CORE-8 #44). |
| `image_width_px`, `image_height_px`, `fx_px`, `fy_px`, `cx_px`, `cy_px` | `payload.intrinsics`: `width_px`, `height_px`, `fx_px`, `fy_px`, `cx_px`, `cy_px` |
| `rotation_camera_to_body_unity_xyzw` | Per frame: conj(`body_to_ecef_xyzw`) ⊗ `payload.camera_to_ecef_xyzw`, using the state at `state_sequence`. For a body-fixed camera this equals `manifest.sensors[].mount.sensor_to_body_xyzw`, up to sign and rounding, if the camera model computes `camera_to_ecef` as `body_to_ecef` ⊗ `sensor_to_body` (CORE-3, #38). The nadir ground-truth camera is not body-fixed. Both use the dataset's camera frame, not Unity's left-handed, y-up camera, so the values differ from the exporter's. |
| `position_ecef_m`, `velocity_ecef_m_per_s`, `rotation_body_to_ecef_xyzw`, `angular_velocity_body_truth_rad_per_s` | `position_ecef_m`, `velocity_ecef_m_s`, `body_to_ecef_xyzw`, `angular_velocity_body_rad_s` in `states.jsonl` at the frame's `state_sequence`. Simulator truth, evaluation-only. |
