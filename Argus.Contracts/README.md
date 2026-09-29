# Argus.Contracts — cross-process schemas (planned)

**Status:** not built

Versioned Protobuf schemas and generated clients for every message that crosses a process
boundary:

- run control between `BasiliskEngine` and the Basilisk service, and actuator commands
  from `BasiliskEngine` to the service
- the state, SPICE and sensor messages the service streams to `BasiliskEngine`
- the decimated snapshot stream from the headless core to Unity
- camera `RenderRequest`s from the headless core to the Unity `IImageRenderer`, and the
  `ImageFrame`s back; large pixel buffers may use shared memory plus a metadata message
  (system-architecture.md §12)
- controller traffic (observations out, commands in) for agents and HIL flight computers

The C# types in `Assets/ArgusSimulation/Core/Contracts/` stay the in-process source of
truth. The schemas here mirror them, and every field states its frame, unit and time
scale. External APIs never serialize C# implementation types directly.

Planned layout: `proto/argus/sim/v1/*.proto`. See
[target-architecture.md](../docs/target-architecture.md).
