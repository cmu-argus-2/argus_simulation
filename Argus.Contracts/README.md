# Argus.Contracts: cross-process schemas

**Status:** Basilisk, state-stream and controller-gateway schemas defined; renderer schema planned

`headless/Host` serves the state stream (HOST-3, `Streaming/StateStreamServer.cs`); the Unity
client is UNITY-5.

Versioned Protobuf schemas for every message that crosses a process boundary. Nothing here
is generated or compiled into a committed artifact; each consumer generates its own code.

## Layout

```text
proto/argus/
├── sim/v1/                     # Shared Argus messages (package argus.sim.v1)
│   ├── types.proto             # Vector3, Quaternion, Matrix3
│   ├── commands.proto          # DeviceCommand, DeviceCommandSet (D11)
│   ├── devices.proto           # DeviceValue, DeviceMount, DeviceConfiguration (D11)
│   ├── run_configuration.proto # RunConfiguration (G5)
│   ├── state.proto             # State, environment and backend samples
│   └── sensor_output.proto     # Dashboard envelopes and non-image payloads
├── basilisk/v1/                # Basilisk-native link (package argus.basilisk.v1)
│   └── basilisk_service.proto  # BasiliskSimulationService: ConfigureRun, Reset, Step
├── stream/v1/state_stream.proto      # Host → Unity state stream (CON-1)
├── gateway/v1/gateway.proto          # Controller link for HIL and external controllers (CON-3)
└── render/v1/render.proto            # Placeholder: RenderRequest / ImageFrame (D5, G3)
```

The placeholders declare only their package; their messages are added with the PR that
first needs them.

`argus.sim.v1` mirrors Core contracts and may be reused by any future link.
`argus.basilisk.v1` carries Basilisk-native data (J2000, MRPs, SPICE matrices) and only
`BasiliskEngine` reads it, so the package split enforces decision D8.

## Source of truth

The Core C# types in `Assets/ArgusSimulation/Core/` are the in-process truth, and the
`.proto` files here are the cross-language truth. A change to either lands with the
matching change to the other in one PR. External APIs never serialize C# implementation
types directly.

## v1 rules

- **v1 is additive only.** HOST-5 (#29) verified the Basilisk path end to end with the P0
  scenario, which froze v1. Allowed: new messages, fields, RPCs and oneof members, with new
  field numbers, and new device kinds, which need no message at all (D11). Never change a
  field's number, type, name, unit, frame or meaning. A deleted field becomes `reserved`. A
  breaking change goes to `v2`. Before the freeze, v1 changed in place when the client, the
  service and their tests changed together: `StepRequest`'s command interval (DATA-5, #70)
  and the device model (CORE-19 #111, CORE-20 #112). Dataset v1 follows the same rule.
- buf STANDARD style: `<Rpc>Request` / `<Rpc>Response`, a `Service` suffix, and every enum
  zero value named `_UNSPECIFIED` and rejected.
- **Presence:** an unset message field or oneof is INVALID_ARGUMENT, never defaulted; the
  exceptions are empty `BasiliskDeviceSample.output` (Unavailable), the state-stream
  presence rules below, and fields added to a frozen v1 message, which an older client cannot
  send: an unset one reads as its default (`RunConfiguration.heartbeat_timeout_wall_s`: the
  heartbeat timeout off; `physical_actuators`: the inhibit on; CORE-9). DeviceValue maps use the keys/types declared by their kind
  (`docs/device-model.md` §3). For scalars zero is a value; range checks reject
  meaningless zeros. A scalar whose zero would be a plausible wrong value is declared
  `optional` (for example `StepResponse.spacecraft_shadow_factor`, where 0 means umbra).
- Every field states its frame, unit and time scale, in its name and its comment.

**Unit suffixes:** `_m`, `_m_per_s`, `_m_per_s2`, `_rad`, `_deg`, `_rad_per_s`, `_s`, `_ns`,
`_kg`, `_kg_m2`, `_n`, `_n_m`, `_a_m2`, `_tesla`, `_w_per_m2`, `_per_s`, `_per_sqrt_hz`,
`_per_sqrt_s`.

**Frame tokens:** `_body`, `_sensor`, `_j2000`, `_itrf93`, `_inertial`, `_earth_fixed`, `_bn`.

## Code generation (never committed)

- **C#:** `Grpc.Tools` in `headless/Host` generates client code with `Access="Internal"`.
  Mapping code uses the aliases `using PbSim = Argus.Contracts.Sim.V1;` and
  `using PbBasilisk = Argus.Contracts.Basilisk.V1;` and never imports those namespaces
  unqualified, because their type names collide with Core. (`Pb` alone clashes with a
  namespace inside Google.Protobuf.) Core never references Protobuf or gRPC, and Unity
  uses the separate generated contract assembly described below; `StateStreamClient` (UNITY-5) consumes `argus.stream.v1`,
  never `argus.basilisk.v1` (D8).
- **Python:** `Argus.Basilisk/scripts/generate_protos.py` writes into the gitignored
  `Argus.Basilisk/generated/`.

Check that the schemas compile:

```bash
protoc -I Argus.Contracts/proto -I "$(brew --prefix)/include" --descriptor_set_out=/dev/null $(find Argus.Contracts/proto -name '*.proto')
```

## Endpoints

| Link | Server | Client | Variable | Default | Transport | Stream rate |
|---|---|---|---|---|---|---|
| Basilisk | `Argus.Basilisk` | `headless/Host` (`BasiliskEngine`) | `ARGUS_BASILISK_ENDPOINT` | `127.0.0.1:50051` | insecure h2c, loopback only in v1 | Not applicable |
| State stream | `headless/Host` | Unity | `ARGUS_STREAM_ENDPOINT` | `127.0.0.1:50052` | native gRPC, h2c; Unity HTTP/2 handler | `--stream-rate-hz` > `ARGUS_STREAM_RATE_HZ` > **10 Hz wall-clock** |
| Controller gateway | `headless/Host` (HOST-6) | HIL adapters (HIL-1), external controllers | `ARGUS_GATEWAY_ENDPOINT` | `127.0.0.1:50053` | native gRPC, h2c, loopback only in v1; one bidirectional stream per controller | One observation per step, never decimated |

For every endpoint, precedence is command-line flag, then environment, then default.
Basilisk uses `--basilisk-endpoint` > `ARGUS_BASILISK_ENDPOINT` > `127.0.0.1:50051`.
The stream endpoint uses `--stream-endpoint`, then `ARGUS_STREAM_ENDPOINT`, then the
default above. Stream rate uses `--stream-rate-hz`, then `ARGUS_STREAM_RATE_HZ`, default
10 Hz of wall-clock time; `headless/Host` implements all three (HOST-3) and accepts only a
loopback stream endpoint. Both endpoint settings take bare `host:port`
(bracket IPv6 addresses, e.g. `[::1]:50052`), with no URI scheme or path. The stream
client adds `http://` when constructing its channel URI; the server parses host/port.
The gateway endpoint uses `--gateway-endpoint`, then `ARGUS_GATEWAY_ENDPOINT`, then
`127.0.0.1:50053`, in the same bare `host:port` form; HOST-6 accepts only a loopback address.

## Deferred

Added with the PR that first needs them:

- the runtime state-stream Unity client (UNITY-5); the schema is below and `headless/Host`
  serves it (HOST-3);
- camera `RenderRequest`s to the Unity `IImageRenderer` and `ImageFrame`s back; large
  pixel buffers may use shared memory plus a metadata message (system-architecture.md §12).

See [target-architecture.md](../docs/target-architecture.md).

## State stream (CON-1)

`StateStreamService.Subscribe` sends a `SubscribeResponse` containing both a complete
SimulationState and a dashboard projection of SensorOutputSet. Both are required, even
if no sensor has produced a frame yet. The set run ID, snapshot sequence and snapshot
time match the state; each frame retains its own capture sequence/time. This projection
is distinct from the original per-step SensorManager output used by the recorder.

### Delivery rules

- Cap sends at 10 Hz of **wall-clock** time by default. Host rate precedence is
  `--stream-rate-hz` > `ARGUS_STREAM_RATE_HZ` > 10; require a finite value above 0 and at most 1000 Hz.
  `headless/Host` rounds the period up to whole milliseconds, so a client never receives faster than the rate.
  This configures delivery, never the physics or sensor sample period. No runtime tuning RPC.
- Host keeps the latest frame per non-camera sensor in the current generation. Every
  produced frame updates that sensor's slot, including Unavailable/Invalid (which must
  replace an older successful reading). Never cache across reset/run replacement.
- For each state, atomically snapshot those slots as of that step into the output set;
  frames must not come from a future step. Each client has one latest complete pair
  slot; the producer replaces unsent pairs without
  waiting for network I/O. Use a monotonic clock; do not send duplicate states or catch-up
  bursts. A stalled client never blocks the simulation step loop.
- This is a lossy display stream: intermediate measurements can be skipped, but a slow
  sensor remains visible in successive snapshots until its next frame. Cached frames
  preserve status, sequence, payload and acquisition times unchanged. Caching does not
  turn VALID into STALE; STALE is a producer-reported status. Display age from acquisition
  time and never present a cached reading as a new measurement. Cache size is bounded
  by configured non-camera sensors; reject unknown sensor IDs.
- Frames carry required `capture_state_sequence` from Core StateSequence, even on failure.
  Capture sequence and time may precede the output set's publication sequence and time.
- Camera frames are excluded from the stream's SensorOutputSet projection entirely, for
  every status. Camera requests/images use the render link (UNITY-10); do not encode an
  image as an unavailable scalar sensor. Unknown non-camera payloads are mapping errors.
- The server must always set a nonempty `run_generation`; clients must reject an empty
  value before consuming either state or sensor data (proto3 cannot enforce this).
  `run_generation` is unique for every ConfigureRun/Reset, including across
  host restarts (use a UUID). It is stable across reconnects in the same generation and
  independent of the recorder's storage-dependent segment number. Sequence increases
  only within a generation; a new generation may restart at zero with the same run ID.
- Reset/run replacement atomically invalidates the latest pair and all sensor slots before
  publishing the new generation. A subscription waits for a complete new pair rather than
  replaying a pre-reset cache. Finish old RPCs before publishing new-generation data.
  Unity clears interpolation on a new subscription or changed generation. Any already
  in-flight old response remains labelled with its old generation and must not be mixed
  with new-generation state. Cancel old readers and discard their callbacks after reconnect.
  On stop, finish active streams.
- `source` says where the generation's run comes from: `RUN_SOURCE_LIVE` for a run the host is
  stepping, `RUN_SOURCE_REPLAY` for a recorded run it plays back (`headless/Host --replay`,
  HOST-7). A replay follows every rule above, as a new generation with the recorded run ID, so a
  client needs nothing else to follow it. `RUN_SOURCE_UNSPECIFIED` comes only from a host that
  predates the field; treat it as live.
- The recorder and controllers retain their full-rate paths. Unpaced runs can have large
  simulation-time gaps and are not guaranteed smooth live visualization.

### Presence and encoding

Reuse sim/v1 vectors, quaternions, `DeviceValue` and `DeviceCommand`; no Basilisk imports.
Initial/no-command states omit applied_commands. Analytic states omit environment;
ITRF93 states require it. At state k, present applied_commands has sequence k-1.
Backend unavailable is an empty output map; no sample entry means not sampled.
`state.backend_samples` preserves the snapshot step’s backend truth for inspection;
the dashboard reads `sensor_output.frames`, the latest-per-sensor projection, not
backend_samples. These views intentionally differ in sampling time.
Sensor VALID/STALE requires a nonempty DeviceValue payload map; UNAVAILABLE/INVALID
requires an empty map while
retaining the envelope. STALE retains original acquisition metadata. Enum zero is invalid;
map Core enums explicitly (Core Valid=0 is wire Valid=1). All optional scalar fields in
these messages require presence: optional distinguishes missing from legitimate zero.
Validate finite values, nonnegative sequence/time and physical ranges in the mappers;
Protobuf parsing alone does not validate them. UTC timestamps are separate from elapsed
simulation seconds. Duplicate sensor IDs in backend_samples are invalid.

### Transport and Unity generation

Choose native gRPC over HTTP/2 for desktop Unity, using Grpc.Net.Client and
[YetAnotherHttpHandler](https://github.com/Cysharp/YetAnotherHttpHandler) in Unity.
This keeps the Host on ordinary ASP.NET Core gRPC without a gRPC-Web adapter.
The handler documents Unity and Apple Silicon support; WebGL is outside this decision.
For local cleartext h2c, create the handler with `Http2Only = true`. Host binds HTTP/2
on loopback only. Remote exposure/TLS is outside v1 scope.

Generate rather than vendor code. From repository root:

```sh
dotnet build Argus.Contracts/Unity/Argus.Stream.Contracts.csproj
```

This pinned Grpc.Tools 2.84.0 project compiles shared sim messages and the stream client
into a netstandard2.1 assembly, with Google.Protobuf 3.36.2 and Grpc.Core.Api 2.84.0.
It generates no Basilisk messages or server stub. The build copies that assembly and its
managed runtime (Grpc.Net.Client 2.84.0 and its dependencies, plus System.IO.Pipelines and
System.Runtime.CompilerServices.Unsafe for the handler) into the git-ignored
`Assets/ArgusSimulation/Plugins/Stream/`, leaving out the netstandard2.0 shims Unity's
.NET Standard 2.1 profile already provides. Grpc.Tools itself is never imported.
YetAnotherHttpHandler comes from `Packages/manifest.json`, pinned to the 1.11.7 git tag.
Core never references these assemblies; `Unity/Runtime/StateStreamMapper.cs` maps them to Core
in the Unity runtime assembly (UNITY-5). Host keeps its own internal
generation and generates the server side for `argus/stream/v1` only (HOST-3).

Headless CI builds the managed contract assembly in Release configuration. Installing/pinning the Unity HTTP handler,
Grpc.Net.Client and their dependencies, then verifying Editor/player cancellation and
reconnect, belongs to UNITY-5. A successful netstandard build does **not** demonstrate
that native handler loading or an actual Unity network connection works.

### Device model mapping (D11)

Use shared `DeviceValue` from devices.proto and `DeviceCommand` from commands.proto;
no per-kind messages are added here. Frame `kind` is required and names the registered
Core kind used to decode its payload. Keys and value types are declared by that kind,
with snake_case SI-unit names (e.g. `angular_velocity_sensor_rad_per_s`,
`magnetic_field_sensor_tesla`, `irradiance_w_per_m2`). Reject unknown kinds, wrong keys,
missing required keys, unset DeviceValue cases and non-finite/out-of-range values.
Applied commands list every configured actuator, including idle values, at most once;
the list is empty when the run configures no actuator. Per-device state is deferred to the
first stateful actuator, the momentum wheel (`docs/device-model.md` §8).
HOST-3 should alias generated names as `PbSim` / `PbStream` to avoid Core name collisions.

## Controller gateway (CON-3)

`ControllerGatewayService.Connect` carries Core's `IControllerGateway` (CORE-9) to HIL
adapters (HIL-1) and external controllers: claim, release, heartbeat, the command for a
step, and, in lockstep runs, the step itself and a Reset. Observations go out as Core
publishes them (`ObservationPublished`), with each run's end (`RunEnded`). `headless/Host`
serves it (HOST-6).

### Decisions

- **Shape: one bounded bidirectional stream per controller session**, not unary calls
  (system-architecture §12). The stream is the session. The server assigns its controller ID
  and revokes its authority when it ends, so a controller that disconnects or crashes fails
  safe without a separate session API. In runs the host steps (paced and HIL), observations
  arrive on the host's clock, and a stream delivers them without polling. One ordered stream
  also fixes the order of observations and call results.
- **Endpoint:** `--gateway-endpoint` > `ARGUS_GATEWAY_ENDPOINT` > `127.0.0.1:50053`,
  cleartext h2c on loopback only in v1, as the other links. HIL-1's adapters run on the host
  machine and bridge to the flight computer's own buses.
- **Reset:** a link sends only a seed. The scenario is the host's run file (G5), so a link
  cannot change the spacecraft, the heartbeat timeout or the physical-actuator setting. Only the
  authority holder of a lockstep run may reset; the host owns the runs it steps.
- **Camera images inline:** a controller-visible camera's pixels travel in the observation
  as raw RGB8 (`CameraObservation.pixels_rgb8`). `SessionStarted.max_message_bytes` tells the
  client the largest message it will receive: 4 MiB (gRPC's default) with no controller-visible
  camera, otherwise the sum of every such camera's width x height x 3 plus 1 MiB, since one
  observation can carry a frame from each. One IMX708 at 4608 x 2592 gives 36,880,384 bytes
  (about 35.2 MiB); all four of P0's give 144,375,808 bytes (about 137.7 MiB). The client sets
  its receive limit to that value; its own messages stay within the default 4 MiB. P0's cameras are not
  controller-visible, so P0 sessions stay at 4 MiB. Shared memory (system-architecture §12)
  can be added beside it if images become the bottleneck.

### Session rules

- The server's first message is `SessionStarted`: the controller ID it assigned (clients never
  choose one), the host's `StepDriver`, the run's heartbeat timeout, `max_message_bytes`, the
  command window in a run the host steps, the physical-actuator inhibit (unset reads as
  inhibited) and the spacecraft profile's ID (HOST-9). If a run is running, the run's latest
  observation follows, so a controller that joins mid-run knows step k.
- In a run the host steps, the host refuses every session with `FAILED_PRECONDITION` at
  connect, before `SessionStarted`, when the run's heartbeat timeout is 0 or shorter than two
  steps: a hung controller would keep its last command, or a healthy one would be lost
  (CORE-9). A lockstep run may leave the timeout off, since its run stops with its controller.
- Unity never claims authority (D2); how the server recognises Unity's session is UNITY-11's
  (#65), with its dashboard commands.
- The server handles a session's calls one at a time, in order, and answers each with one
  `CallResult` echoing its `request_id`. An observation or `RunEnded` that a call causes is
  sent before that call's result: a lockstep `StepCommand` gets observation k+1, then its
  result, and an accepted step whose backend fails still gets an accepted result, after the
  terminal observation and `RunEnded`. A `ResetRun` gets the old run's `RunEnded`, observation
  0, then its result; a run that ended has no holder, so a link only resets a running run.
- Every session gets every observation, holder or not. Observations are never decimated or
  dropped. The server keeps at most 16 unsent messages per session, and at most
  4 x `max_message_bytes` of them, whichever comes first; a session that falls further behind
  is ended with `RESOURCE_EXHAUSTED`, which revokes its authority (failsafe). The worst case is
  16 MiB per session with no controller-visible camera, about 141 MiB with one IMX708
  (4 x 35.2 MiB) and about 551 MiB with all four of P0's (4 x 137.7 MiB); the host holds that for
  each session it serves. HTTP/2 flow control bounds
  the calls a client can queue.
- Every accepted call is a heartbeat; a refused call is not (CORE-9). A holder silent past the
  heartbeat timeout loses authority, and every actuator runs idle until a controller claims
  again.
- Every command that decodes into a Core `DeviceCommandSet`, accepted or refused, reaches the
  command log (dataset v1). One that doesn't (`sequence`, `apply_at_simulation_time_s` or
  `commands` unset, or a `DeviceValue` with no value set) never became a command: it gets a
  refused `CallResult` naming the field and is not logged.
- A terminal observation carries its status (`BACKEND_FAILED`, `STOPPED`) and no frames, never
  its termination reason, which can carry backend failure text (D7). Refusal text is the
  gateway's own and is sent.
- A run ends with `RunEnded`. After a `ResetRun` the session goes on with the new run. A
  Reset clears authority, so a controller claims again before stepping the new run. A run that
  ends without being replaced has no holder left to reset it, so no session can start another:
  the host stops then, and ends every session with OK.

### Commands and observations

- A command for step k is computed from observation k and names it: `sequence` = k and
  `apply_at_simulation_time_s` = t_k, exactly observation k's `simulation_time_s`; it acts over
  [t_k, t_k+1) (DATA-5, #70). Late, early, duplicate and unauthorised commands are refused
  and logged, never applied (CORE-9). An actuator a command leaves out runs idle.
- In a run the host steps, command k counts only if it is accepted before the host starts step
  k. After publishing observation k the host waits at most `SessionStarted.command_window_wall_s`
  for it and steps as soon as it is accepted; one that arrives later is late, and step k holds.
- `SubmitCommand` is for runs the host steps (`STEP_DRIVER_HOST`); a step with no command
  holds the tenure's last applied one (`Held`). `StepCommand` is for lockstep runs
  (`STEP_DRIVER_CONTROLLER`). The other one is refused.
- Observation frames reuse `argus.sim.v1.SensorFrame` with the state stream's presence and
  device-model rules, except a camera: its `payload` is always empty and `camera` carries
  the `CameraObservation` for every status, with pixels only when `VALID`. No truth crosses this
  link: no `SimulationState`, no camera pose or Sun, no ground-truth sensor (D7), and no
  `argus.basilisk.v1` type.
- Generated C# aliases the package as `PbGateway`, beside `PbSim`, because `Observation`,
  `StepDriver` and `CameraObservation` collide with Core.
