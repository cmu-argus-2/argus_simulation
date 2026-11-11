# Argus.Contracts: cross-process schemas

**Status:** Basilisk and state-stream schemas defined; gateway and renderer schemas planned

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
├── gateway/v1/gateway.proto          # Placeholder: HIL and controller link (D7, G1)
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
  exceptions are empty `BasiliskDeviceSample.output` (Unavailable) and the state-stream
  presence rules below. DeviceValue maps use the keys/types declared by their kind
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

For every endpoint, precedence is command-line flag, then environment, then default.
Basilisk uses `--basilisk-endpoint` > `ARGUS_BASILISK_ENDPOINT` > `127.0.0.1:50051`.
The stream endpoint uses `--stream-endpoint`, then `ARGUS_STREAM_ENDPOINT`, then the
default above. Stream rate uses `--stream-rate-hz`, then `ARGUS_STREAM_RATE_HZ`, default
10 Hz of wall-clock time; `headless/Host` implements all three (HOST-3) and accepts only a
loopback stream endpoint. Both endpoint settings take bare `host:port`
(bracket IPv6 addresses, e.g. `[::1]:50052`), with no URI scheme or path. The stream
client adds `http://` when constructing its channel URI; the server parses host/port.

## Deferred

Added with the PR that first needs them:

- the gateway link for HIL flight computers and other external controllers (observations
  out, commands in);
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
  No historical replay; on stop, finish active streams.
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
the list is empty when the run configures no actuator. Per-device state is deferred to BSK-8.
HOST-3 should alias generated names as `PbSim` / `PbStream` to avoid Core name collisions.
