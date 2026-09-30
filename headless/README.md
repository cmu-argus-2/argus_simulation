# Headless build and host

`Argus.Simulation.Core` must run without Unity (see
[target architecture](../docs/target-architecture.md), decision D1 and gap G2). This
folder builds the same source files outside Unity, so that rule can be checked with plain
`dotnet` and the core tests can run without a Unity licence, and it holds the headless
simulation host. No CI runs it yet.

Nothing here is copied from `Assets/`. The projects compile the Unity source in place:

| Project | Compiles | Output assembly |
|---|---|---|
| `Core/Argus.Simulation.Core.Headless.csproj` | `Assets/ArgusSimulation/Core/**/*.cs` (netstandard2.1) | `Argus.Simulation.Core` |
| `Tests/Argus.Simulation.Tests.EditMode.Headless.csproj` | `Assets/ArgusSimulation/Tests/EditMode/**/*.cs` (NUnit) | `Argus.Simulation.Tests.EditMode` |
| `Host/Argus.Simulation.Host.csproj` | `headless/Host/**/*.cs` plus client code generated from `Argus.Contracts/proto` (net8.0 exe) | `Argus.Simulation.Host` |

The assembly names match the Unity assemblies and the `InternalsVisibleTo` entries in
`Assets/ArgusSimulation/Core/AssemblyInfo.cs`, which expose the internal `Core/Basilisk`
mapping to the host and the EditMode tests. `Directory.Build.props` pins C# 9 to match
Unity 6.

## Run

Requires the .NET SDK 8 or newer.

```bash
dotnet test headless/Tests/Argus.Simulation.Tests.EditMode.Headless.csproj
```

The host needs a running Basilisk service (`Argus.Basilisk/README.md`). It builds
`HostScenario.CreateP0Default` (placeholder hardware values), runs the gateway and the P0
sensors over `BasiliskEngine`, and prints one line per simulated second:

```bash
dotnet run --project headless/Host/Argus.Simulation.Host.csproj -- --duration-s 60 --real-time-factor 0 --basilisk-endpoint 127.0.0.1:50051
```

The endpoint comes from `--basilisk-endpoint`, then `ARGUS_BASILISK_ENDPOINT`, then
`127.0.0.1:50051`. Exit codes: 0 on success or Ctrl+C, 1 on a failure (for example
`Basilisk ConfigureRun failed: Unimplemented: …` against the skeleton, or `Unavailable`
with no service), 2 on bad arguments.

## Rules

- Code under `Assets/ArgusSimulation/Core/` must compile here. The Core `.asmdef` sets
  `noEngineReferences`, so Unity rejects `UnityEngine` usage in Core as well.
- EditMode tests reference only Core. Tests that need Unity belong in
  `Assets/ArgusSimulation/Tests/PlayMode/`.
- gRPC and Protobuf appear only in `Host/` (today only `Host/Basilisk/` uses them).
  Generated code is `Access="Internal"`, lives in `obj/` and is never committed.
  `BasiliskEngine` owns the client and the request and response envelopes;
  `BasiliskProtoMapper` converts payloads to and from Core types.
