# Core/Basilisk: Basilisk-to-Argus mapping

**Diagram block:** BasiliskEngine · **Assembly:** `Argus.Simulation.Core` · **Status:** mapping built; used by `BasiliskEngine` in `headless/Host` (not yet run against a real Basilisk service)

This folder holds the transport-free half of `BasiliskEngine`: plain C# mirrors of the
Basilisk messages Argus reads, and the conversions from them to Argus contracts. The
gRPC client that fills these mirrors lives in `headless/Host` (target architecture §11),
so Core never references Protobuf or gRPC.

## Contents

| Type | Role |
|---|---|
| `BasiliskTime` | Seconds ↔ Basilisk integer nanoseconds; UTC as epoch plus elapsed seconds |
| `BasiliskSpacecraftState` | `SCStatesMsgPayload` subset: `r_BN_N`, `v_BN_N`, `sigma_BN`, `omega_BN_B` |
| `BasiliskPlanetState` | `SpicePlanetStateMsgPayload` subset: position, velocity, `J20002Pfix`, `J20002Pfix_dot` |
| `BasiliskSensorSample` | One sensor output, already an Argus measurement type |
| `BasiliskStepState` | The `StepResponse` state fields; `BasiliskEngine` checks `run_id` and `sequence` itself |
| `BasiliskStateMapper` | `MapEnvironment`, `MapSpacecraft`, `MapMeasurements` |

## Mapping

- `EnvironmentState` comes from SPICE only (D3): `q_EN` from `J20002Pfix`, Earth's rate
  from the antisymmetric part of `J20002Pfix_dot · J20002Pfixᵀ`, and the Sun rotated into
  ITRF93.
- `SpacecraftState` is Earth-fixed and tagged `Itrf93`:
  `r_E = q_EN (r_BN_N − r_Earth)`, `v_E = q_EN (v_BN_N − v_Earth) − ω × r_E`,
  `BodyToEcef = q_EN * q_NB`. The body rate passes through.
- Sensor samples become a `SensorMeasurementSet` (D10). Each configured sensor must report
  exactly on the steps its period divides, at the step time, with its kind's payload type.
- Every inconsistent input throws; nothing is repaired or approximated.

## Rules

- Every type here is `internal`, so Unity cannot see it (D8). `Core/AssemblyInfo.cs`
  exposes them only to `Argus.Simulation.Host` and the EditMode tests.
- No Protobuf types here. `headless/Host` maps Protobuf messages to these mirrors.
- `BasiliskStateMapperTests` pins the frame math to a literal SPICE sample.
