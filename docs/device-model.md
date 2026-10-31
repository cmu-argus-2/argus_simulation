# Device model (D11)

**Status:** design (CON-5, #110). Built by CORE-19 (#111), the sensors; CORE-20 (#112), the
commands; and HOST-9 (#113), the profile files. Actuator kinds follow in CON-2 (#34) and BSK-8
(#21).

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
| `mount` | `mount` | `positionBodyMeters` (lever arm from the centre of mass) and `deviceToBody` (v_body = q v_device q*). It replaces `SensorMount`'s `sensorToBody`. |
| `samplePeriodSeconds` | `sample_period_ns` | Sensors only: a positive whole number of fixed steps. Actuators have none: they take a command every step. |
| `controllerVisible` | `controller_visible` | Sensors only; unset means visible. False for ground-truth sensors (D7). |
| `parameters` | `parameters` | The kind's fixed values (§3). |
| `initialState` | `initial_state` | The kind's state at t = 0, for example a wheel's speed. Empty for kinds with no state, which includes the three P0 sensors. |

A device's role (sensor or actuator) and which side models it (Basilisk or Argus, D10 and CORE-18)
belong to its kind. They are not configured per device.

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
accelerometer, the magnetometer and the other five light sensors follow the same pattern.

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
  "kernelSetId": "argus-2026-10-20",
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
- `runs/p0-default.json` splits into `spacecraft/argus-p0.json` (the 8 P0 sensors) and a run file
  that names it (HOST-9).

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

- `SensorDefinition`, `SensorMount`, `SensorConfiguration` and the per-kind profile messages are
  removed.
- `BasiliskSensorSample` becomes `BasiliskDeviceSample`: `device_id`, `sample_time_ns`, and
  `map<string, DeviceValue> output`. An empty map means the sensor ran without data (imuSensor's
  first update) and maps to Unavailable. The per-kind measurement messages are removed.
- `ConfigureRunResponse.configured_sensor_ids` becomes `configured_device_ids`.
- Per-step actuator state arrives as `repeated BasiliskDeviceState device_states` on
  `StepResponse` (`device_id`, `map<string, DeviceValue> state`). It is added with the first
  actuator (BSK-8).

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
  Limits clamp or reject per device (CORE-10).
- **In Core**, `ActuatorCommandSet` becomes `DeviceCommandSet`: the sequence, the apply time and
  the commands keyed by device ID.
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

- **Core:** each kind is one class implementing `IDeviceKind`. The class holds the kind's name,
  role, modeller, declarations and validation. A sensor kind builds its `SensorModel`; an actuator
  kind gives its idle values and its limit checks. `DeviceKinds` lists every registration in one
  file. It replaces the `SensorKind` switches in `SensorFactory`, `SensorConfiguration` (including
  `IsBackendModelled`), the run-file parser and `BasiliskProtoMapper`.
- **Service:** each Basilisk-modelled kind is one module, `argus_basilisk/devices/<kind>.py`, with
  its declarations, validation and builder. `argus_basilisk/devices/__init__.py` registers them by
  name. `validation.py` and the scenario builder walk the registry instead of switching on kinds.
- **Adding a kind** means writing its Core class, its service module if Basilisk models it, one
  registration on each side, and its tests. No proto, gateway, recorder or file-format change.

Both sides declare a kind's keys. A single machine-readable schema both read is out of scope for
now.

## 8. Before HOST-5

`Argus.Contracts/README.md` allows incompatible v1 changes until HOST-5 (#29) marks the Basilisk
path verified, and dataset v1 follows the same rule. Changes that break v1 must merge first;
additions can follow.

| Change | Breaks v1 | Lands in |
|---|---|---|
| `DeviceConfiguration`, `DeviceValue`, `RunConfiguration.devices`, `BasiliskDeviceSample`, `configured_device_ids`; the manifest's `devices` | yes | CORE-19 (#111), before HOST-5 |
| `DeviceCommandSet` in `StepRequest`; `devices` in `commands.jsonl` and `producing_command` | yes | CORE-20 (#112), before HOST-5 |
| Profile and run file split | no (a host format) | HOST-9 (#113), any time |
| Per-step device state: `StepResponse.device_states`, `SimulationState`, `devices` in `states.jsonl` | no (new fields) | BSK-8 (#21), with the first actuator |
| New kinds | no (registry only) | CON-2 (#34) and later |
