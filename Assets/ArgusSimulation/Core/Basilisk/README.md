# Core/Basilisk — BasiliskEngine adapter (planned)

**Diagram block:** BasiliskEngine · **Assembly:** `Argus.Simulation.Core` · **Status:** not built

`BasiliskEngine` will implement `ISimulationEngine` (`Core/Abstractions/ISimulationEngine.cs`)
and talk to the Basilisk service in [`Argus.Basilisk/`](../../../../Argus.Basilisk/README.md)
over gRPC, using the schemas in [`Argus.Contracts/`](../../../../Argus.Contracts/README.md).

## Responsibilities

- Build every `SimulationSnapshot` from Basilisk state and Basilisk's SPICE output. That
  covers spacecraft truth plus the planned `EnvironmentState` (decisions D3 and D4).
- Follow Basilisk's clock. Basilisk owns simulation time in every Basilisk run, and its
  pacing in real-time and HIL runs (decision D2), so this adapter never invents time.
- Map Basilisk sensor messages to Argus `SensorFrame`s for the sensor models.
- Carry gateway actuator commands to Basilisk's actuator modules (gap G1).
- Convert frames and units at the boundary: Basilisk inertial frame and MRP attitude to
  Argus contracts (gap G4).

## Rules

- No Basilisk-specific types leave this folder (decision D8). Unity, agents and hardware
  adapters see only Argus contracts.
- Code arrives here only after the Protobuf v1 schemas exist in `Argus.Contracts/`.
- Whether the gRPC client itself lives in this assembly or in a separate headless transport
  assembly behind a Core interface is open (target architecture §11).
