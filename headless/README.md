# Headless build and host

`Argus.Simulation.Core` must run without Unity (see
[target architecture](../docs/target-architecture.md), decision D1 and gap G2). This
folder builds the same source files outside Unity, so that rule can be checked with plain
`dotnet` and the core tests can run without a Unity licence, and it holds the headless
simulation host. [Headless CI](../.github/workflows/headless.yml) checks the build,
tests and schemas on every pull request and push to `main`.

Nothing here is copied from `Assets/`. The projects compile the Unity source in place:

| Project | Compiles | Output assembly |
|---|---|---|
| `Core/Argus.Simulation.Core.Headless.csproj` | `Assets/ArgusSimulation/Core/**/*.cs` (netstandard2.1) | `Argus.Simulation.Core` |
| `Tests/Argus.Simulation.Tests.EditMode.Headless.csproj` | `Assets/ArgusSimulation/Tests/EditMode/**/*.cs` (NUnit) | `Argus.Simulation.Tests.EditMode` |
| `Host/Argus.Simulation.Host.csproj` | `headless/Host/**/*.cs` plus client code generated from `Argus.Contracts/proto` (net8.0 exe) | `Argus.Simulation.Host` |
| `Host.Tests/Argus.Simulation.Host.Tests.csproj` | `headless/Host.Tests/**/*.cs` (NUnit) | `Argus.Simulation.Host.Tests` |

The assembly names match the Unity assemblies and the `InternalsVisibleTo` entries in
`Assets/ArgusSimulation/Core/AssemblyInfo.cs`, which expose the internal `Core/Basilisk`
mapping to the host and the EditMode tests. `Directory.Build.props` pins C# 9 to match
Unity 6.

## Continuous integration

GitHub Actions runs the **Headless build, tests and schemas** job on Ubuntu 24.04
with .NET 8 and `protoc`. It builds the host (including Core and generated client
code), runs the headless EditMode tests and the host tests, and compiles every `.proto` file under
`Argus.Contracts/proto` into a temporary descriptor set. Any failed command fails
the job, including a failing test. Generated output is not committed.

The **Basilisk service and host run** job (BSK-11, #24) checks the two sides of the
Basilisk link together. It installs the service from `Argus.Basilisk/uv.lock` (bsk
included), generates its Protobuf code, and fetches and verifies the P0 kernel set
(cached; `Argus.Basilisk/README.md` §8 "Kernels in CI"). It runs the service tests,
where only the `argus-2026-09-25` tests may skip, then runs the host for 60 s of the P0 run
file with `--start-basilisk`, so the host starts the service itself and must leave none
running when it exits. Last, with a service started by the job, it applies a torque through the gateway and
`BasiliskEngine` and checks the body rate over that step
(`AGatewayOverTheServiceAppliesATorqueOverItsStep`, BSK-8 #21). Both sides build from `Argus.Contracts/proto`,
so a contract change that one side does not follow fails the job: the host build, a
service test, or the host run (`StepResponse.spacecraft_shadow_factor is unset.`, exit 1).

The workflow also supports manual runs from the Actions tab. New commits replace
outdated runs for the same pull request. No job launches Unity; Unity PlayMode
tests are separate work.

To require a passing result before merge, select **Headless build, tests and
schemas** and **Basilisk service and host run** as required status checks in the
repository's branch rules.

## Run

Requires the .NET SDK 8 or newer.

```bash
dotnet test headless/Tests/Argus.Simulation.Tests.EditMode.Headless.csproj
dotnet test headless/Host.Tests/Argus.Simulation.Host.Tests.csproj
```

The host reads a run configuration file, runs the gateway and its sensors over
`BasiliskEngine`, and prints one line per simulated second. With `--start-basilisk` it starts
the Basilisk service itself, so nothing else needs starting:

```bash
dotnet run --project headless/Host/Argus.Simulation.Host.csproj -- --start-basilisk --duration-s 60 --real-time-factor 0 --run-config headless/Host/runs/p0-default.json
```

`--start-basilisk` needs `Argus.Basilisk/.venv` (`uv sync --frozen --all-extras`, root README)
and nothing else set up:
- It finds `Argus.Basilisk` above the host binary or the current directory, or takes
  `--basilisk-dir <dir>`.
- It runs the service's own setup steps first: `scripts/generate_protos.py`, then
  `scripts/fetch_kernels.py <kernelSetId>` for the run file's kernel set. That check only hashes
  files already in `kernels/` and downloads the ones that are missing.
- It serves on the Basilisk endpoint with `--stop-on-stdin-eof` and waits for its "listening"
  line. The service's output appears with a `[basilisk]` prefix.
- When the host exits, for any reason including being killed, the service's standard input
  closes and it stops.
- If a service is already listening on the endpoint, the host uses that one and starts none.

Without `--start-basilisk`, start the service first (`Argus.Basilisk/README.md` §2).

`--validate` checks a run file without running it: the run file and its spacecraft profile, read
exactly as a run reads them, then the kernel set's coverage of the whole run, from `epochUtc` to
`epochUtc + --duration-s`. Coverage comes from the set's manifest in `Argus.Basilisk/kernel_sets/`,
the check the service would otherwise make with OUT_OF_RANGE once the run had started. It prints
the result and exits 0 when valid or 2 when not, and it starts and connects to nothing. Unity's
Host Launcher Inspector runs it for its **Validate** button.

```bash
dotnet run --project headless/Host/Argus.Simulation.Host.csproj -- --validate --run-config headless/Host/runs/p0-default.json --duration-s 3600
```

`--stop-on-stdin-eof` stops the run, as Ctrl+C does, when the host's standard input closes. It is
for a launcher such as Unity's `HostLauncher` (root README, Run the Unity visualization), so the
host never outlives whatever started it. Don't pass it from a shell that gives the host no
standard input, or the run stops at once.

The endpoint comes from `--basilisk-endpoint`, then `ARGUS_BASILISK_ENDPOINT`, then
`127.0.0.1:50051`. `--run-config` defaults to the `runs/p0-default.json` copied next to the
host binary. Exit codes: 0 on success or Ctrl+C, 1 on a failure (for example
`Basilisk ConfigureRun failed: Unimplemented: …` against a service that has not built a
configured sensor, or `Unavailable` with no service), 2 on bad arguments or a run
configuration file or spacecraft profile that is unreadable or invalid. A bad file exits
before the host connects to the service, and the message names the file and the field.

Against the service, the full P0 run (`--duration-s 600`, the default run file) exits 0 with
no mapper, period or freshness error. HOST-5 (#29) checked its output against CSPICE and an
independent orbit propagation (target-architecture G4), so the Basilisk path is verified and
v1 is additive only (`Argus.Contracts/README.md`).

The host also serves the state stream to Unity (`argus/stream/v1`, CON-1,
`Argus.Contracts/README.md` "State stream"). It listens on `--stream-endpoint`, then
`ARGUS_STREAM_ENDPOINT`, then `127.0.0.1:50052`, a loopback `host:port` only, in cleartext
HTTP/2. Each client receives at most `--stream-rate-hz` (then `ARGUS_STREAM_RATE_HZ`, then 10)
pairs per second of wall-clock time: the latest state with the latest frame of each non-camera
sensor. The rate must be above 0 and at most 1000 Hz, and its period is rounded up to whole
milliseconds (400 Hz sends every 3 ms). The web host reads no `appsettings.json` or
`ASPNETCORE_`/`DOTNET_` variables, so nothing but `--stream-endpoint` opens a listener. The step
loop only replaces that latest pair, so a slow or stalled client never delays a step; it just
receives fewer pairs. A port already in use exits 1 before the host configures the service. An
unpaced run may finish before a client connects.

`--record <dir>` writes the run as dataset v1 (`Assets/ArgusSimulation/Core/Recording/README.md`)
into `<dir>/<run_id>_0`. The Core `RunRecorder` receives every state and every sensor frame,
including ground-truth sensors, every command the gateway logged, applied, clamped, held,
failsafe or rejected (`commands.jsonl`), and every authority change (`authority.jsonl`), from the
gateway's events. It writes on its own thread, so a slow disk
never delays a step, and `manifest.json` counts anything it had to drop.

### Replay a recorded run (HOST-7)

`--replay <run dir>` plays one run directory that `--record` wrote (`<run_id>_<segment>`, the
folder holding `manifest.json`) back over the same state stream, so Unity follows a replay
exactly as it follows a live run. No Basilisk service is needed:

```bash
dotnet run --project headless/Host/Argus.Simulation.Host.csproj -- --replay runs-out/host-0123abcd_0 --replay-speed 4
```

- **What it serves:** every state in `states.jsonl`, with its environment and the command that
  produced it, and each step's non-camera frames from `sensors.jsonl`, through the same
  `StateStreamPublisher` as a live run. The backend sensors' frames also rebuild each state's
  `backend_samples`. A replay is identical to what the live run streamed, and the same every
  time; only `run_generation` differs (`RunReplayTests`). Every response carries
  `source: RUN_SOURCE_REPLAY`, which a live run sends as `RUN_SOURCE_LIVE`, and Unity's dashboard
  shows **REPLAYING RECORDED RUN** while it follows a replay.
- **What it does not serve:** camera frames and images (the live stream carries none either;
  nothing is re-rendered) and `commands.jsonl` (the stream has no command log). No gateway,
  controller or recorder runs.
- **Pacing:** state k is published at (t_k − t_0) / `--replay-speed` of wall-clock time after
  state 0, by the host's own clock, never a Basilisk one (D2). The speed must be above 0 and
  defaults to 1. The stream still decimates to `--stream-rate-hz`. After the last state the host
  stops the stream and exits 0.
- **Checks:** it rejects a `schema_version` other than `argus-dataset/1`, a device kind that is
  not registered, devices that do not make a valid `SimulationConfiguration` (for example a
  sample period that is not a whole number of steps), a frame of a sensor the manifest does not
  list, and a line that breaks the dataset README's reader checks, naming the file and
  line (exit 2 for a bad manifest, 1 for a bad line during the replay). A recording with
  `complete: false` replays up to its last full line, with a warning. Files it does not read,
  such as `commands.jsonl` or `authority.jsonl`, are ignored.
- **Options:** a replay takes `--stream-endpoint` and `--stream-rate-hz`, and rejects the
  live-run options `--duration-s`, `--real-time-factor`, `--basilisk-endpoint`, `--run-config`
  and `--record`.

## Run configuration files

A run configuration file defines one run once (G5): epoch, fixed step, seed, kernel set,
orbit, initial attitude and rate, and the spacecraft profile it flies, which holds the body and
its devices (D11, `docs/device-model.md` §4, HOST-9). `RunConfigurationFile` reads both into
Core `SimulationConfiguration`, and `BasiliskEngine` sends it to the service as
`argus.sim.v1.RunConfiguration`, without the devices Argus computes. Only the host reads
profiles; the service never reads files. The run ID is not in the file; the host gives each run
its own.

- **Location:** run files in `headless/Host/runs/`, the repo's spacecraft profiles in
  `headless/Host/spacecraft/` (`argus-p0.json`, which `runs/p0-default.json` flies). A team keeps
  its profile anywhere. HOST-8's frozen reference cases are in `runs/reference/` (see its
  README). The build copies every `runs/*.json`, `runs/reference/*.json` and `spacecraft/*.json`
  next to the host binary.
- **Run file:** `epochUtc`, `fixedStepSeconds`, `randomSeed`, `kernelSetId`, `initialOrbit`,
  `initialAttitude` (`bodyToJ2000`, `angularVelocityBodyRadiansPerSecond`) and
  `spacecraftProfile`: the profile's path, relative to the run file (or absolute), such as
  `"../spacecraft/argus-p0.json"`, or the profile object itself, which keeps a frozen reference
  case in one file. A run file from before the split, with `spacecraft` and `devices`, is told
  where they went.
- **Spacecraft profile:** `profileId` (`[a-z0-9][a-z0-9._-]{0,63}`), `body` (`massKilograms`,
  `inertiaBodyKilogramSquareMeters`) and `devices`. The manifest records the profile in
  `configuration.spacecraft_profile` (`profile_id`, and `path` as the run file wrote it, or
  `null` inline) beside the resolved body and devices, so a dataset stands alone.
- **Format:** JSON whose field names are the Core property names in camelCase, in SI units
  with the unit in the name (`semiMajorAxisMeters`, `samplePeriodSeconds`). Vectors are
  `[x, y, z]`, matrices are three such rows, and quaternions are `{"x", "y", "z", "w"}`
  objects. `epochUtc` is a UTC time ending in `Z`, such as `2025-01-15T00:00:00Z`.
- **Devices** (D11, `docs/device-model.md`): each entry holds the common fields (`deviceId`,
  `kind`, `modelName`, `frameId`, `samplePeriodSeconds`, `controllerVisible`,
  `mount` with `positionBodyMeters` and `deviceToBody`) and `parameters`, the keys its kind
  declares. There is no `source`: which side models a device belongs to its kind
  (`docs/device-model.md` §1), so the host records `basilisk` or `argus` from the kind, and a
  file that still sets `source` exits 2 naming it (CORE-22). `kind` is a registered name (`Core/Contracts/DeviceKinds.cs`): `imu`,
  `magnetometer` and `light_sensor`, which Basilisk models, and `camera` and
  `nadir_ground_truth_camera` (the north-up nadir ground-truth camera, CORE-8), which Argus
  computes and never sends to the service (CORE-18), and the actuator kinds `ideal_body_torque`,
  `momentum_wheel`, `magnetic_torquer` and `thruster` (CON-2, `docs/device-model.md` §2). An
  actuator has no `samplePeriodSeconds` or `controllerVisible`, and its `deviceToBody` must be the
  identity, since its axis is a body-frame parameter. Both camera kinds take the same keys; a
  nadir camera needs its own `profile_id`, since cameras that share one share its parameters. Parameter keys are snake_case with their unit,
  the same spelling as on the wire and in the manifest (`noise_std_tesla`,
  `field_of_view_half_angle_rad`); `Argus.Basilisk/README.md` §9 lists the keys of the kinds
  Basilisk models, and `Core/Sensors/Camera/README.md` the camera's. A key's group may be
  written as a nested object, so the IMU's `gyroscope.range_rad_per_s` is
  `"gyroscope": { "range_rad_per_s": ... }`. The camera's parameters are the DATA-6 (#71)
  profile, each value with where it came from: `profile_id`, `intrinsics.*` with `source` and
  `calibrated`, `distortion.*` (`model` `brown-conrady-5`, `k1` … `k3` all zero, `source`),
  `noise.source` (`none`), `exposure_duration_s` and `extrinsics.*`. Each value is read as the
  type its kind declares. `initialState` holds the kind's initial-state keys: only
  `momentum_wheel` has one (`speed_rad_per_s`), and the field is optional for the other kinds. `controllerVisible` is `false` for a ground-truth sensor: the gateway
  never returns its frames to a controller, though the recorder still receives them (D7). A
  visible `camera` reaches a controller as a `CameraObservation`, with the pixels and exposure
  timing but not the camera pose or the Sun (CORE-23). The host builds its cameras with no
  renderer and serves no controller yet, so here a visible camera's frames are `Unavailable`
  until a renderer exists (UNITY-4 #57, UNITY-10 #64) and the gateway is served (HOST-6 #30).
  P0's cameras stay `false`, so a run file opts a camera in. It is always `false` for the nadir
  ground-truth camera. `spacecraft/argus-p0.json` includes the four body cameras
  (`cam.px`, `cam.py`, `cam.mx`, `cam.my`) with the DATA-6 profile and mounts, every 15 s, with
  a 0.5 ms exposure (CORE-6), and `torque.body`, an `ideal_body_torque` device that rejects
  anything past 1e-4 N·m per axis, as DATA-2 (#68) gives for the ideal torque, until DATA-2's six
  coils land in the profile. The list may hold any subset of the devices,
  including none, so a staged run against a partly built service needs only a different
  profile.
- **Gateway settings** (CORE-9, #45), the only optional fields of the run file. Absent, the inhibit stays
  on, and the heartbeat failsafe is off, which is safe only while no controller attaches (HOST-6 is
  to refuse a controller session on a run without a timeout): `heartbeatTimeoutWallSeconds`, the heartbeat timeout for the controller holding
  actuator authority (absent or `0`: off; set it for any run a controller attaches to, or a
  crashed controller's last command is held for the rest of the run), and `physicalActuators`, which lifts the
  physical-actuator inhibit: `{"enabled": true, "testProcedure": "<the bench procedure>"}`, with
  both keys required. Lifting it needs `heartbeatTimeoutWallSeconds` in (0, 60] s, so a silent
  controller fails safe, and a paced run whose step (`fixedStepSeconds / --real-time-factor`) is no
  longer than half the timeout, since the gateway hears a controller about once per paced step:
  otherwise the host exits 2. An explicit `null` is refused: omit
  the key instead. The inhibit is enforced by HIL-1 (#66); runs without hardware are
  unaffected.
- **Validation:** every other field is required and unknown fields are rejected, including
  parameters the kind does not declare. Each part is checked with its Core `IsValid` and
  each device by its kind, so the file accepts exactly what Core accepts and an error names
  the key. An error in a profile file starts with the file's full path, and a duplicate device
  ID or an unknown kind also names the device (`spacecraft profile
  /path/to/headless/Host/spacecraft/argus-p0.json: devices[1].deviceId: 'imu.main' is already
  devices[0]'s ID`); one in an inline profile starts with `spacecraftProfile.`. A device ID that
  `--record` cannot use as a directory name is reported the same way. Every
  `samplePeriodSeconds` must be a whole multiple of `fixedStepSeconds`, since sensors sample only
  at steps. A camera's `exposure_duration_s` must not exceed its `samplePeriodSeconds`, since
  one camera can't start an exposure before the last one ends. Cameras that share a `profile_id` must share its parameter set; the error names
  the camera that set it and the part that differs
  (`devices[16].parameters["profile_id"]: names another parameter set than cam.px: it differs in intrinsics`).
- **Orbit:** the elements are osculating at the epoch in J2000 axes, so RAAN is measured
  from J2000 x, not from Greenwich as in the Unity analytic fixture.

`runs/p0-default.json` and `spacecraft/argus-p0.json` hold the DATA-1 (#67) values. The IMU,
magnetometer and light-sensor parts come from datasheets and flight code. The 2U body (mass, inertia, light-sensor positions),
the epoch and the orbit are team estimates, not measured or launch-contract numbers; real
numbers are a change to these files. The four body cameras keep Argus-2's mounts (z = −0.029 m),
while the 2U body's ±Z faces are at ±0.1135 m, until the 2U camera layout is known (#133).

## Rules

- Code under `Assets/ArgusSimulation/Core/` must compile here. The Core `.asmdef` sets
  `noEngineReferences`, so Unity rejects `UnityEngine` usage in Core as well.
- EditMode tests reference only Core. Tests that need Unity belong in
  `Assets/ArgusSimulation/Tests/PlayMode/`, and tests of `Host/` belong in `Host.Tests/`.
- gRPC and Protobuf appear only in `Host/` (today only `Host/Basilisk/` uses them).
  Generated code is `Access="Internal"`, lives in `obj/` and is never committed.
  `BasiliskEngine` owns the client and the request and response envelopes;
  `BasiliskProtoMapper` converts payloads to and from Core types.
