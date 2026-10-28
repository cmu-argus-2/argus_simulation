# Argus.Contracts: cross-process schemas

**Status:** v1 Basilisk link defined; state-stream schema drafted (CON-1, awaiting review); gateway and renderer planned

This PR defines the contract; HOST-3 and UNITY-5 implement its runtime behavior.

Versioned Protobuf schemas for every message that crosses a process boundary. Nothing here
is generated or compiled into a committed artifact; each consumer generates its own code.

## Layout

```text
proto/argus/
├── sim/v1/                     # Shared Argus messages (package argus.sim.v1)
│   ├── types.proto             # Vector3, Quaternion, Matrix3
│   ├── commands.proto          # ActuatorCommand
│   ├── sensors.proto           # Sensor definitions, P0 profiles and measurements
│   ├── run_configuration.proto # RunConfiguration (G5)
│   ├── state.proto             # State, environment and backend samples
│   └── sensor_output.proto     # Dashboard envelopes and non-image payloads
├── basilisk/v1/                # Basilisk-native link (package argus.basilisk.v1)
│   └── basilisk_service.proto  # BasiliskSimulationService: ConfigureRun, Reset, Step
├── stream/v1/state_stream.proto      # Draft host → Unity state stream (CON-1)
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

- **Until the first end-to-end run, v1 may change incompatibly.** Nothing yet runs the
  host against the service end to end with the P0 scenario, so no two deployed revisions
  depend on each other. Until HOST-5 (#29) marks the Basilisk path verified, a v1 change
  may rename a field or change its meaning, provided the client, the service and their
  tests change in the same PR (or in PRs merged together). The change to
  `StepRequest.command`'s interval (DATA-5, #70) is one.
- From then on, additive only: new messages, fields, RPCs and oneof members, with new
  field numbers. Never change a field's number, type, name, unit, frame or meaning. A
  deleted field becomes `reserved`. A breaking change goes to `v2`.
- buf STANDARD style: `<Rpc>Request` / `<Rpc>Response`, a `Service` suffix, and every enum
  zero value named `_UNSPECIFIED` and rejected.
- **Presence:** an unset message field or oneof is INVALID_ARGUMENT, never defaulted; the
  exceptions are `BasiliskSensorSample.measurement` (Unavailable) and the state-stream
  presence rules below. For scalars zero is a value; range checks reject
  meaningless zeros. A scalar whose zero would be a plausible wrong value is declared
  `optional` (for example `StepResponse.spacecraft_shadow_factor`, where 0 means umbra).
- Every field states its frame, unit and time scale, in its name and its comment.

**Unit suffixes:** `_m`, `_m_per_s`, `_m_per_s2`, `_rad`, `_deg`, `_rad_per_s`, `_s`, `_ns`,
`_kg`, `_kg_m2`, `_n`, `_n_m`, `_a_m2`, `_tesla`, `_w_per_m2`, `_per_s`, `_per_sqrt_hz`,
`_per_sqrt_s`.

**Frame tokens:** `_body`, `_sensor`, `_j2000`, `_itrf93`, `_inertial`, `_planet_fixed`, `_bn`.

## Code generation (never committed)

- **C#:** `Grpc.Tools` in `headless/Host` generates client code with `Access="Internal"`.
  Mapping code uses the aliases `using PbSim = Argus.Contracts.Sim.V1;` and
  `using PbBasilisk = Argus.Contracts.Basilisk.V1;` and never imports those namespaces
  unqualified, because their type names collide with Core. (`Pb` alone clashes with a
  namespace inside Google.Protobuf.) Core never references Protobuf or gRPC, and Unity
  does not yet; the planned Unity `StateStreamClient` (G2) will consume `argus.stream.v1`,
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
| State stream (HOST-3 implements) | `headless/Host` | Unity | `ARGUS_STREAM_ENDPOINT` | `http://127.0.0.1:50052` | native gRPC, h2c; Unity HTTP/2 handler | `--stream-rate-hz` > `ARGUS_STREAM_RATE_HZ` > **10 Hz wall-clock** |

The stream endpoint uses `--stream-endpoint`, then `ARGUS_STREAM_ENDPOINT`, then the
default above. Stream rate uses `--stream-rate-hz`, then `ARGUS_STREAM_RATE_HZ`, default
10 Hz of wall-clock time. These settings are the contract for HOST-3/UNITY-5, not options
implemented by this schema-only change.

## Deferred

Added with the PR that first needs them:

- the gateway link for HIL flight computers and other external controllers (observations
  out, commands in);
- runtime state-stream server (HOST-3) and Unity client (UNITY-5); schema is defined below;
- camera `RenderRequest`s to the Unity `IImageRenderer` and `ImageFrame`s back; large
  pixel buffers may use shared memory plus a metadata message (system-architecture.md §12).

See [target-architecture.md](../docs/target-architecture.md).

## State stream (CON-1)

`StateStreamService.Subscribe` sends a `SubscribeResponse` containing both a complete
SimulationState and that step's SensorOutputSet. Both are required, including an empty
output set when no supported sensors fired. Run ID, publication sequence and simulation
time must match. Preserve original sequence numbers and timestamps after decimation.

### Delivery rules

- Cap sends at 10 Hz of **wall-clock** time by default. Host rate precedence is
  `--stream-rate-hz` > `ARGUS_STREAM_RATE_HZ` > 10; require a finite positive value.
  This configures delivery, never the physics or sensor sample period. No runtime tuning RPC.
- Each client has one latest complete pair slot; the producer replaces unsent pairs without
  waiting for network I/O. Use a monotonic clock; do not send duplicate states or catch-up
  bursts. A stalled client never blocks the simulation step loop.
- This is a lossy display stream. Intermediate sensor samples may be skipped with their
  states. Do not fill a pair with cached samples from another step. A dashboard can retain
  an earlier reading only if it shows its original timestamp/age, not as a new measurement.
- Frames carry required `capture_state_sequence` from Core StateSequence, even on failure.
  Capture sequence and time may precede the output set's publication sequence and time.
- Camera frames are excluded from the stream's SensorOutputSet projection entirely, for
  every status. Camera requests/images use the render link (UNITY-10); do not encode an
  image as an unavailable scalar sensor. Unknown non-camera payloads are mapping errors.
- Stream starts with the newest available complete pair. Within a subscription the run ID
  is constant and state sequence strictly increases. On reset, run replacement or stop,
  finish the existing RPC before sending any new-run state (even if run_id is reused).
  Unity clears interpolation history on every new subscription. No historical replay.
- The recorder and controllers retain their full-rate paths. Unpaced runs can have large
  simulation-time gaps and are not guaranteed smooth live visualization.

### Presence and encoding

Reuse sim/v1 vectors, quaternions, commands and measurements; no Basilisk imports.
Initial/no-command states omit applied_commands. Analytic states omit environment;
ITRF93 states require it. At state k, present applied_commands has sequence k-1.
Backend unavailable is an explicit marker; no sample entry means not sampled.
Sensor VALID/STALE requires a typed payload; UNAVAILABLE/INVALID omits payload while
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
It generates no Basilisk messages or server stub. UNITY-5 imports this built assembly and
its compatible runtime dependencies into a dedicated Unity plugin assembly; do not import
Grpc.Tools itself. Core never references these assemblies. Host keeps its own internal
generation; HOST-3 enables server generation for stream only.

The managed contract build is checked here. Installing/pinning the Unity HTTP handler,
Grpc.Net.Client and their dependencies, then verifying Editor/player cancellation and
reconnect, belongs to UNITY-5. A successful netstandard build does **not** demonstrate
that native handler loading or an actual Unity network connection works.
