# Core/Basilisk: Basilisk-to-Argus mapping

**Diagram block:** BasiliskEngine · **Assembly:** `Argus.Simulation.Core` · **Status:** mapping built; used by `BasiliskEngine` in `headless/Host`, and verified on real Basilisk and SPICE with the full P0 scenario (HOST-4 #28, HOST-5 #29; target-architecture G4)

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
| `BasiliskSensorSample` | One sensor device's output map (`BasiliskDeviceSample.output`) |
| `BasiliskStepState` | The `StepResponse` state fields; `BasiliskEngine` checks `run_id` and `sequence` itself |
| `BasiliskStateMapper` | `MapEnvironment`, `MapSpacecraft`, `MapMeasurements` |

## Mapping

- `EnvironmentState` comes from Basilisk's SPICE and eclipse outputs only (D3): `q_EN` from
  `J20002Pfix`, Earth's rate from the antisymmetric part of `J20002Pfix_dot · J20002Pfixᵀ`,
  the Sun rotated into ITRF93, and the shadow factor from the eclipse `illuminationFactor`.
- `SpacecraftState` is Earth-fixed and tagged `Itrf93`:
  `r_E = q_EN (r_BN_N − r_Earth)`, `v_E = q_EN (v_BN_N − v_Earth) − ω × r_E`,
  `BodyToEcef = q_EN * q_NB`. The body rate passes through.
- Sensor samples become a `SensorMeasurementSet` (D10). The mapper is given the run's
  `SimulationConfiguration.BackendSensors`, worked out once per run, and rejects a list that
  holds any other device: one that isn't `DeviceConfiguration.IsBackendSensor`, a
  Basilisk-modelled `IBackendSensorKind` (CORE-22). Each sensor must report
  exactly on the steps its period divides, at the step time, with exactly its kind's output
  keys; the kind (`IBackendSensorKind.ReadOutput`) reads them into its measurement type, and
  an empty map is Unavailable. Sensors Argus computes never reach Basilisk (CORE-18), so a
  sample naming one is rejected as an unknown sensor.
- Every inconsistent input throws; nothing is repaired or approximated.

## Rules

- Every type here is `internal`, so Unity cannot see it (D8). `Core/AssemblyInfo.cs`
  exposes them only to `Argus.Simulation.Host` and the EditMode tests.
- No Protobuf types here. `headless/Host` maps Protobuf messages to these mirrors.
- `BasiliskStateMapperTests` pins the frame math to a literal SPICE sample.
