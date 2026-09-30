# Argus.Contracts: cross-process schemas

**Status:** v1 Basilisk link defined; gateway, snapshot-stream and renderer schemas planned

Versioned Protobuf schemas for every message that crosses a process boundary. Nothing here
is generated or compiled into a committed artifact; each consumer generates its own code.

## Layout

```text
proto/argus/
├── sim/v1/                     # Shared Argus messages (package argus.sim.v1)
│   ├── types.proto             # Vector3, Quaternion, Matrix3
│   ├── commands.proto          # ActuatorCommand
│   ├── sensors.proto           # Sensor definitions, P0 profiles and measurements
│   └── run_configuration.proto # RunConfiguration (G5)
├── basilisk/v1/                # Basilisk-native link (package argus.basilisk.v1)
│   └── basilisk_service.proto  # BasiliskSimulationService: ConfigureRun, Reset, Step
├── stream/v1/snapshot_stream.proto   # Placeholder: host → Unity snapshot stream (G2)
├── gateway/v1/gateway.proto          # Placeholder: agents and HIL link (D7, G1)
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

- Additive only: new messages, fields, RPCs and oneof members, with new field numbers.
- Never change a field's number, type, name, unit, frame or meaning. A deleted field
  becomes `reserved`. A breaking change goes to `v2`.
- buf STANDARD style: `<Rpc>Request` / `<Rpc>Response`, a `Service` suffix, and every enum
  zero value named `_UNSPECIFIED` and rejected.
- **Presence:** an unset message field or oneof is INVALID_ARGUMENT, never defaulted. For
  scalars zero is a value; range checks reject meaningless zeros. A scalar whose zero would
  be a plausible wrong value is declared `optional` (for example
  `StepResponse.spacecraft_shadow_factor`, where 0 means umbra).
- Every field states its frame, unit and time scale, in its name and its comment.

**Unit suffixes:** `_m`, `_m_per_s`, `_m_per_s2`, `_rad`, `_deg`, `_rad_per_s`, `_s`, `_ns`,
`_kg`, `_kg_m2`, `_n`, `_n_m`, `_a_m2`, `_tesla`, `_w_per_m2`, `_per_s`, `_per_sqrt_hz`,
`_per_sqrt_s`.

**Frame tokens:** `_body`, `_sensor`, `_j2000`, `_itrf93`, `_inertial`, `_bn`.

## Code generation (never committed)

- **C#:** `Grpc.Tools` in `headless/Host` generates client code with `Access="Internal"`.
  Mapping code uses the aliases `using PbSim = Argus.Contracts.Sim.V1;` and
  `using PbBasilisk = Argus.Contracts.Basilisk.V1;` and never imports those namespaces
  unqualified, because their type names collide with Core. (`Pb` alone clashes with a
  namespace inside Google.Protobuf.) Core and Unity never reference Protobuf or gRPC.
- **Python:** `Argus.Basilisk/scripts/generate_protos.py` writes into the gitignored
  `Argus.Basilisk/generated/`.

Check that the schemas compile:

```bash
protoc -I Argus.Contracts/proto -I "$(brew --prefix)/include" --descriptor_set_out=/dev/null $(find Argus.Contracts/proto -name '*.proto')
```

## Endpoints

| Link | Server | Client | Variable | Default | Transport |
|---|---|---|---|---|---|
| Basilisk | `Argus.Basilisk` | `headless/Host` (`BasiliskEngine`) | `ARGUS_BASILISK_ENDPOINT` | `127.0.0.1:50051` | insecure h2c, loopback only in v1 |

The endpoint comes from a command-line flag, then the process environment, then the
default.

## Deferred

Added with the PR that first needs them:

- the gateway link for agents and HIL flight computers (observations out, commands in);
- the decimated snapshot stream from the headless core to Unity (G2);
- camera `RenderRequest`s to the Unity `IImageRenderer` and `ImageFrame`s back; large
  pixel buffers may use shared memory plus a metadata message (system-architecture.md §12);
- `SensorFrame` envelopes for the recorder.

See [target-architecture.md](../docs/target-architecture.md).
