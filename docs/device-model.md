# Device model (D11)

**Status:** design (CON-5, #110). The sensors are built (CORE-19, #111): §1 to §5 and §7 are
in `Core/Contracts`, the kinds in `Core/Sensors`, `argus.sim.v1/devices.proto` and
`argus_basilisk/devices/`. So are the per-device commands of §6 (CORE-20, #112), except the
per-step device state, which comes with the first stateful actuator, the momentum wheel. The
actuator kinds of §2 are registered on both sides (CON-2, #34), and the service builds the
`ideal_body_torque` and `magnetic_torquer` effectors (BSK-8, #21). Still to build: the
`momentum_wheel` and `thruster` effectors, which P0 doesn't carry (DATA-2, #68), and the profile
files (HOST-9, #113; until then the devices live in the run file's `devices`).

A spacecraft is a body plus a list of configured devices ([target-architecture.md](target-architecture.md)
D11). Every sensor and actuator is a device: an instance of a generic kind, registered by name. A
team adopts its spacecraft by writing a profile, not by changing contracts or code. This page is
the schema the follow-up issues build.

## 1. Devices

Common fields, the same for every kind:

| Field (profile JSON) | Proto `DeviceConfiguration` | Meaning |
|---|---|---|
| `deviceId` | `device_id` | Unique across all of the spacecraft's devices, sensors and actuators alike (ordinal). Commands, frames and recorded state address a device by it. |
| `kind` | `kind` | A registered kind name in snake_case: `imu`, `magnetometer`, `light_sensor`, `camera` (CORE-18), and later `ideal_body_torque`, `momentum_wheel`, `magnetic_torquer`, `thruster`. |
| `modelName` | Core only | The part, as a parameter set for its kind (`placeholder-mems-imu`, `arducam-imx708`). Recorded; not sent to the service. |
| `frameId` | Core only | The sensor frame's name in frames and datasets. |
| `mount` | `mount` | `positionBodyMeters` (lever arm from the centre of mass) and `deviceToBody` (v_body = q v_device q*). It replaces `SensorMount`'s `sensorToBody`. An actuator's axis is a body-frame parameter, so its `deviceToBody` must be the identity; a thruster acts at its `positionBodyMeters`. |
| `samplePeriodSeconds` | `sample_period_ns` | Sensors only: a positive whole number of fixed steps. Actuators have none: they take a command every step. |
| `controllerVisible` | `controller_visible` | Sensors only; unset means visible. False for ground-truth sensors (D7). |
| `parameters` | `parameters` | The kind's fixed values (§3). |
| `initialState` | `initial_state` | The kind's state at t = 0, for example a wheel's speed. Empty for kinds with no state, which includes the three P0 sensors. |

A device's role (sensor or actuator) and which side models it (Basilisk or Argus, D10 and CORE-18)
belong to its kind. They are not configured per device. An actuator's run-file entry has no
`samplePeriodSeconds` or `controllerVisible`; on the wire its `sample_period_ns` is 0 and
`controller_visible` is unset, and the manifest records it with `sample_period_s` 0 and
`controller_visible` false.

## 2. Kinds

Each kind declares:

- **its name, role and modeller** (Basilisk or Argus). The host sends only Basilisk-modelled
  devices to the service.
- **its parameters and initial state:** for each key, its type (§3), whether it is required or what
  its default is, and its range.
- **its output (sensors):** the measurement keys it publishes each sample. In Core, a typed payload
  such as `ImuMeasurement` in a `SensorFrame<T>`; on the wire, the same keys in a value map.
- **its command values and idle values (actuators):** what a command carries, and what the
  actuator does in a step whose command leaves it out (zero torque, zero dipole). Limits and
  clamp-or-reject are its parameters (CORE-10, #46).
- **its per-step state:** what the engine reports for it each step (§6). It is truth.

Validation is the kind's own. An unknown key, a missing required key, a wrong type, a non-finite
value or one out of range is rejected with the device ID and the key. Core validates when it
builds the configuration, so the host fails fast with exit code 1. The service validates again
and returns INVALID_ARGUMENT, because it never trusts its client.

**Actuator kinds** (CON-2, #34) are generic physics models, all Basilisk-modelled. Each axis is a
unit vector in body axes (within 1e-6), each limit is finite and > 0, and every kind has
`limit_policy`: `clamp` or `reject`, which the gateway applies past the limits (CORE-10, #46). A
command carries exactly the kind's command keys, and its idle values are zero:

| Kind | Parameters | Initial state | Command | Basilisk |
|---|---|---|---|---|
| `ideal_body_torque` | `max_torque_body_n_m` (vector3, a bound on each body axis) | none | `torque_body_n_m` (vector3) | `extForceTorque` |
| `momentum_wheel` | `spin_axis_body`, `spin_inertia_kg_m2`, `max_torque_n_m`, `max_speed_rad_per_s` | `speed_rad_per_s` | `motor_torque_n_m` | `reactionWheelStateEffector` (not built) |
| `magnetic_torquer` | `dipole_axis_body`, `max_dipole_a_m2` | none | `dipole_a_m2` (signed) | `MtbEffector` |
| `thruster` | `thrust_direction_body`, `max_thrust_n` | none | `thrust_n` (>= 0), held over the step | `thrusterDynamicEffector`, on-time `thrust_n / max_thrust_n` of the step (not built) |

The service builds the `ideal_body_torque` and `magnetic_torquer` effectors and refuses a run
that configures a `momentum_wheel` or `thruster` (UNIMPLEMENTED), so every command it receives
acts (`Argus.Basilisk/README.md` §9 "Actuators"). Over an engine that only records commands (the
analytic fixture), the gateway refuses (logs `Rejected`) any command that is not the kind's idle
values, so the run continues and the log tells the truth. A spacecraft whose actuators aren't
known yet uses one `ideal_body_torque` device; the P0 run file lists `torque.body` with
placeholder limits until DATA-2 (#68)'s six coils land in it.

**The IMU is one kind,** `imu`, with `gyroscope.*` and `accelerometer.*` parameters and one frame
that carries both channels. This matches Basilisk's `imuSensor` and today's `ImuMeasurement`, so
moving it onto the device model leaves `sensors.jsonl` unchanged. A spacecraft with separate parts
uses two `imu` devices or, later, separate kinds.

## 3. Values

The proto carries parameters, initial state, outputs, command values and per-step state in one
encoding: a map from key to a typed value.

```proto
message DeviceValue {
  oneof value {
    double number = 1;
    Vector3 vector3 = 2;
    Quaternion quaternion = 3;
    Matrix3 matrix3 = 4;
    bool flag = 5;
    string text = 6;
  }
}
```

- **Keys** are snake_case and carry their unit (`noise_std_tesla`, `range_rad_per_s`), as the proto
  field names do today. A parameter has one spelling everywhere: profile, proto, manifest and the
  kind's code.
- **Groups** use dotted keys (`gyroscope.noise_density_rad_per_s_per_sqrt_hz`). A profile may write
  a group as a nested object, and the parser flattens it with `.`.
- **JSON forms:** a number; `[x, y, z]`; `{"x", "y", "z", "w"}`; `[[r0], [r1], [r2]]`, row-major;
  `true` or `false`; a string. The parser converts each value to the type the kind declares for
  that key and never guesses from its shape.
- **Order:** a proto map has none. Core sorts keys ordinally wherever order shows (recording,
  hashing), so the same configuration always records the same bytes.

A new kind needs no contract change: the schema does not check fields, the kind does.

## 4. Profile and run file

A spacecraft profile holds the body and its devices. Here is the P0 profile, abbreviated: the
accelerometer, the magnetometer, the other five light sensors and the four body cameras (CORE-6)
follow the same pattern.

```json
{
  "profileId": "argus-p0",
  "body": {
    "massKilograms": 1.33,
    "inertiaBodyKilogramSquareMeters": [[2.536e-3, 0, 0], [0, 2.536e-3, 0], [0, 0, 2.217e-3]]
  },
  "devices": [
    {
      "deviceId": "imu.main",
      "kind": "imu",
      "modelName": "placeholder-mems-imu",
      "frameId": "imu_main",
      "samplePeriodSeconds": 0.1,
      "mount": { "positionBodyMeters": [0, 0, 0], "deviceToBody": { "x": 0, "y": 0, "z": 0, "w": 1 } },
      "parameters": {
        "gyroscope": {
          "noise_density_rad_per_s_per_sqrt_hz": 1.2e-4,
          "bias_random_walk_rad_per_s_per_sqrt_s": 0,
          "bias_random_walk_bound_rad_per_s": 0,
          "constant_bias_sensor_rad_per_s": [0, 0, 0],
          "scale_factor_per_axis": [1, 1, 1],
          "range_rad_per_s": 4.363,
          "resolution_rad_per_s": 1.33e-4
        },
        "accelerometer": { "noise_density_m_per_s2_per_sqrt_hz": 1.8e-3, "...": "..." }
      }
    },
    {
      "deviceId": "light.px",
      "kind": "light_sensor",
      "modelName": "placeholder-face-light-sensor",
      "frameId": "light_px",
      "samplePeriodSeconds": 0.1,
      "mount": { "positionBodyMeters": [0.05, 0, 0], "deviceToBody": { "x": 0.5, "y": 0.5, "z": 0.5, "w": 0.5 } },
      "parameters": {
        "field_of_view_half_angle_rad": 1.5707963267948966,
        "scale_factor": 1,
        "noise_std_w_per_m2": 0,
        "bias_w_per_m2": 0,
        "saturation_w_per_m2": 2000
      }
    }
  ]
}
```

The run file keeps what varies per run: the epoch, the fixed step, the seed, the kernel set, the
initial orbit and the initial attitude and rate. It names its profile by a path relative to
itself:

```json
{
  "epochUtc": "2025-01-15T00:00:00Z",
  "fixedStepSeconds": 0.1,
  "randomSeed": 1,
  "kernelSetId": "argus-2026-11-02",
  "initialOrbit": { "semiMajorAxisMeters": 6878137.0, "...": "..." },
  "initialAttitude": {
    "bodyToJ2000": { "x": 0, "y": 0, "z": 0, "w": 1 },
    "angularVelocityBodyRadiansPerSecond": [0, 0, 0]
  },
  "spacecraftProfile": "../spacecraft/argus-p0.json"
}
```

- A team keeps its profile anywhere. The repo's examples live in `headless/Host/spacecraft/`.
- Only the host reads profiles. It resolves the profile into `SimulationConfiguration` (body and
  devices) and sends the resolved devices to the service, which never reads files.
- The dataset manifest records `profileId`, the path as written, and the resolved body and
  devices, so a dataset stands alone.
- `runs/p0-default.json` splits into `spacecraft/argus-p0.json` (the 12 P0 devices: the 8 sensors
  Basilisk models and the 4 body cameras) and a run file that names it (HOST-9).

## 5. Wire

`argus.sim.v1` and `argus.basilisk.v1` change in place (§8):

```proto
message DeviceMount {
  Vector3 position_body_m = 1;
  Quaternion device_to_body = 2;
}

message DeviceConfiguration {
  string device_id = 1;
  string kind = 2;
  // Sensors: a positive multiple of fixed_step_ns. Actuators: 0.
  uint64 sample_period_ns = 3;
  DeviceMount mount = 4;
  optional bool controller_visible = 5;
  map<string, DeviceValue> parameters = 6;
  map<string, DeviceValue> initial_state = 7;
}

message RunConfiguration {
  // ... fields 1-7 unchanged ...
  reserved 8;  // repeated SensorConfiguration sensors
  // Basilisk-modelled devices only (CORE-18).
  repeated DeviceConfiguration devices = 9;
}
```

- `SensorDefinition`, `SensorMount`, `SensorConfiguration`, the per-kind profile messages and
  `sensors.proto` are removed. `DeviceValue`, `DeviceMount` and `DeviceConfiguration` are in
  `argus/sim/v1/devices.proto`.
- `BasiliskSensorSample` becomes `BasiliskDeviceSample`: `device_id`, `sample_time_ns`, and
  `map<string, DeviceValue> output`. An empty map means the sensor ran without data (imuSensor's
  first update) and maps to Unavailable. The per-kind measurement messages are removed.
- `ConfigureRunResponse.configured_sensor_ids` (field 4) becomes `configured_device_ids` (field 7),
  and `StepResponse.sensor_samples` (field 8) becomes `device_samples` (field 10). The old fields
  are reserved.
- Per-step actuator state arrives as `repeated BasiliskDeviceState device_states` on
  `StepResponse` (`device_id`, `map<string, DeviceValue> state`). It is added with the first
  stateful actuator, the momentum wheel; neither kind BSK-8 (#21) built has state.

## 6. Commands and device state

```proto
message DeviceCommand {
  string device_id = 1;
  map<string, DeviceValue> values = 2;
}

// Replaces ActuatorCommand; its sequence and time travel in the enclosing request.
message DeviceCommandSet {
  repeated DeviceCommand commands = 1;
}
```

- **A step's command is complete.** An actuator the command leaves out uses its kind's idle values.
  The empty set means no output (today's zero command), and Reset's Step(0) sends it.
- **Each device appears at most once.** It must be a configured actuator, and its values must pass
  its kind's checks. The gateway logs anything else as Rejected and does not send it on (CORE-20).
  Then each value is held to its device's limits by its kind, and the device's `limit_policy`
  decides (CORE-10, #46): `clamp` sends the value at its nearest limit and logs the command
  `Clamped`, with the requested and applied command and the reason; `reject` refuses the whole
  command (`Rejected`), so the step doesn't run.
- **In Core**, `DeviceCommandSet` (which replaced `ActuatorCommandSet`) holds the sequence, the
  apply time and the `DeviceCommand`s, each a device ID and its values. Until actuator kinds
  exist, no device is a configured actuator, so only the empty set is applied.
- **`commands.jsonl`**: `requested` and `applied` keep `sequence` and
  `apply_at_simulation_time_s`. The three vectors are replaced by `devices`, which maps each device
  ID to its values. `requested` is what the controller sent. `applied` lists every configured
  actuator with the values the engine ran, idle values included, so one line is enough to replay a
  step. `states.jsonl`'s `producing_command` has the same shape as `applied`.
- **Per-step device state** is what the engine produced: for an actuator, the force or torque it
  actually applied to the body, which can differ from its command (a clamp, a wheel at its speed
  limit). `SimulationState` carries it keyed by device ID, and `states.jsonl` records it as
  `devices`, which maps each device ID to its state. Only kinds that declare state appear. A
  controller sees device state only through a sensor device, such as a wheel tachometer (D7).
  For example, `ideal_body_torque` reports `applied_torque_body_n_m`, and `momentum_wheel` reports
  `speed_rad_per_s` and `applied_torque_body_n_m`.

## 7. Registry

- **Core:** each kind is one class implementing `IDeviceKind` (`Core/Contracts/IDeviceKind.cs`).
  The class holds the kind's name, role, modeller, declarations and validation. A sensor kind
  builds its `ISensor`; a Basilisk-modelled one (`IBackendSensorKind`) also reads its output map
  into its measurement type, and a camera kind (`ICameraKind`) gives its `CameraProfile`, for the
  manifest's `cameras` and the shared profile-ID rule (CORE-6). An actuator kind (`IActuatorKind`,
  on `ActuatorKind<TProfile>`) gives its command keys, its idle values and its command check; the
  gateway runs an actuator a command leaves out at its idle values. It also owns its limit check,
  `Limit(parameters, values, out excess)` with `LimitPolicyOf(parameters)` (CORE-10), so a new
  kind brings its own and the gateway doesn't change: `ideal_body_torque` per body axis, ±max for
  `magnetic_torquer` and a `momentum_wheel`'s motor torque, and [0, max] thrust. A wheel's
  `max_speed_rad_per_s` bounds its state, not a command, so the backend that integrates the speed
  owns it.
  `DeviceKind<TProfile>` and `BackendSensorKind<TProfile, TMeasurement>` check the keys and read
  them into the kind's typed profile, so `ImuKind`, `MagnetometerKind`, `LightSensorKind` and
  `CameraProfileKind` only list their keys and map them. `CameraKind` and
  `NadirGroundTruthCameraKind` share `CameraProfileKind`'s keys and differ only in name and model.
  A kind declares its initial-state keys the same way (`InitialStateKeys`, none by default), and names the failing group of an invalid profile
  (`parameters["gyroscope.*"]`). `DeviceKinds` lists every registration in one
  file. It replaced the `SensorKind` switches in `SensorFactory`, `SensorConfiguration`, the
  run-file parser, the recorder, `BasiliskProtoMapper` and `BasiliskStateMapper`.
- **Service:** each Basilisk-modelled kind is one module, `argus_basilisk/devices/<kind>.py`, with
  `KIND`, `PARAMETERS` and `OUTPUTS` (key to type), an optional `STATE` (none by default),
  `check` (its range rules) and `build` (its Basilisk modules and `output()`). An actuator kind
  has `ROLE = ACTUATOR`, `COMMANDS` and `IDLE` in place of `OUTPUTS`, and, once built,
  `build(devices, inputs)`, its Basilisk effectors for all of the run's devices of the kind
  (BSK-8, #21). `argus_basilisk/devices/__init__.py` registers them in `KINDS`.
  `validation.py` and the scenario walk `KINDS` instead of switching on kinds. Kind modules import
  numpy and Basilisk only inside `build`, so validation runs without Basilisk.
- **Adding a kind** means writing its Core class, its service module if Basilisk models it, one
  registration on each side, and its tests. No proto, gateway, recorder or file-format change.

Both sides declare a kind's keys. A single machine-readable schema both read is out of scope for
now.

## 8. Versioning

`Argus.Contracts/README.md` allowed incompatible v1 changes until HOST-5 (#29) verified the
Basilisk path, and dataset v1 followed the same rule. The changes that break v1 merged first;
v1 is now additive only, and the rest are additions.

| Change | Breaks v1 | Lands in |
|---|---|---|
| `DeviceConfiguration`, `DeviceValue`, `RunConfiguration.devices`, `BasiliskDeviceSample`, `configured_device_ids`; the manifest's `devices` | yes | CORE-19 (#111), merged before HOST-5 |
| `DeviceCommandSet` in `StepRequest`; `devices` in `commands.jsonl` and `producing_command` | yes | CORE-20 (#112), merged before HOST-5 |
| Profile and run file split | no (a host format) | HOST-9 (#113), any time |
| Per-step device state: `StepResponse.device_states`, `SimulationState`, `devices` in `states.jsonl` | no (new fields) | with the first stateful actuator, the momentum wheel |
| New kinds | no (registry only) | the actuator kinds in CON-2 (#34); later kinds any time |
