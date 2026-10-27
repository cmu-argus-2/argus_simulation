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

The workflow also supports manual runs from the Actions tab. New commits replace
outdated runs for the same pull request. It does not launch the Basilisk service
or Unity; Basilisk service tests and Unity PlayMode tests are separate work.

To require a passing result before merge, select **Headless build, tests and
schemas** as a required status check in the repository's branch rules.

## Run

Requires the .NET SDK 8 or newer.

```bash
dotnet test headless/Tests/Argus.Simulation.Tests.EditMode.Headless.csproj
dotnet test headless/Host.Tests/Argus.Simulation.Host.Tests.csproj
```

The host needs a running Basilisk service (`Argus.Basilisk/README.md`). It reads a run
configuration file, runs the gateway and its sensors over `BasiliskEngine`, and prints one
line per simulated second:

```bash
dotnet run --project headless/Host/Argus.Simulation.Host.csproj -- --duration-s 60 --real-time-factor 0 --basilisk-endpoint 127.0.0.1:50051 --run-config headless/Host/runs/p0-default.json
```

The endpoint comes from `--basilisk-endpoint`, then `ARGUS_BASILISK_ENDPOINT`, then
`127.0.0.1:50051`. `--run-config` defaults to the `runs/p0-default.json` copied next to the
host binary. Exit codes: 0 on success or Ctrl+C, 1 on a failure (for example
`Basilisk ConfigureRun failed: Unimplemented: …` against a service that has not built a
configured sensor, or `Unavailable` with no service), 2 on bad arguments or a run
configuration file that is unreadable or invalid. A bad file exits before the host connects
to the service, and the message names the field.

`--record <dir>` writes the run as dataset v1 (`Assets/ArgusSimulation/Core/Recording/README.md`)
into `<dir>/<run_id>_0`. The Core `RunRecorder` receives every state and every sensor frame,
including ground-truth sensors, and every command the gateway logged, applied or rejected
(`commands.jsonl`), from the gateway's events. It writes on its own thread, so a slow disk
never delays a step, and `manifest.json` counts anything it had to drop.

## Run configuration files

A run configuration file defines one run once (G5): epoch, fixed step, seed, kernel set,
orbit, spacecraft and sensors. `RunConfigurationFile` reads it into Core
`SimulationConfiguration`, and `BasiliskEngine` sends it to the service as
`argus.sim.v1.RunConfiguration`. The run ID is not in the file; the host gives each run its
own.

- **Location:** `headless/Host/runs/`. The build copies every `runs/*.json` next to the
  host binary.
- **Format:** JSON whose field names are the Core property names in camelCase, in SI units
  with the unit in the name (`semiMajorAxisMeters`, `samplePeriodSeconds`). Vectors are
  `[x, y, z]`, matrices are three such rows, and quaternions are `{"x", "y", "z", "w"}`
  objects. `epochUtc` is a UTC time ending in `Z`, such as `2025-01-15T00:00:00Z`.
- **Sensors:** each entry holds the `SensorDefinition` fields (`sensorId`, `modelName`,
  `frameId`, `samplePeriodSeconds`, `source`, `controllerVisible`, `mount`) and exactly one
  profile, named by its kind: `imu`, `magnetometer` or `lightSensor`. `controllerVisible` is
  `false` for a ground-truth sensor: the gateway never returns its frames to a controller,
  though the recorder still receives them (D7). The list may hold any subset of the
  sensors, including none, so a staged run against a partly built service needs only a
  different file.
- **Validation:** every field is required and unknown fields are rejected. Each part is
  checked with its Core `IsValid`, so the file accepts exactly what Core accepts.
- **Orbit:** the elements are osculating at the epoch in J2000 axes, so RAAN is measured
  from J2000 x, not from Greenwich as in the Unity analytic fixture.

`runs/p0-default.json` is placeholder data, not confirmed hardware: the values the host
used before run files existed. Its magnetometer noise is 0 because bsk 2.11.1 cannot seed
magnetometer noise and the service rejects it (`Argus.Basilisk/README.md` §9).
TODO(hardware): replace it with confirmed data from DATA-1 (#67): IMU, magnetometer and
light-sensor parts, mounts and rates; spacecraft mass, inertia and initial attitude; orbit
and epoch.

## Rules

- Code under `Assets/ArgusSimulation/Core/` must compile here. The Core `.asmdef` sets
  `noEngineReferences`, so Unity rejects `UnityEngine` usage in Core as well.
- EditMode tests reference only Core. Tests that need Unity belong in
  `Assets/ArgusSimulation/Tests/PlayMode/`, and tests of `Host/` belong in `Host.Tests/`.
- gRPC and Protobuf appear only in `Host/` (today only `Host/Basilisk/` uses them).
  Generated code is `Access="Internal"`, lives in `obj/` and is never committed.
  `BasiliskEngine` owns the client and the request and response envelopes;
  `BasiliskProtoMapper` converts payloads to and from Core types.
