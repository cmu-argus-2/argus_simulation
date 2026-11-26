# AGENTS.md: headless

The `dotnet` build of Core and its EditMode tests without Unity, and the headless simulation host.
Nothing here is copied from `Assets/`: the projects compile the Unity sources in place. The full
guide is [README.md](README.md).

| Project | Compiles | Notes |
|---|---|---|
| `Core/Argus.Simulation.Core.Headless.csproj` | `Assets/ArgusSimulation/Core/**/*.cs` | netstandard2.1; proves Core has no Unity dependency (D1) |
| `Tests/Argus.Simulation.Tests.EditMode.Headless.csproj` | `Assets/ArgusSimulation/Tests/EditMode/**/*.cs` | NUnit |
| `Host/Argus.Simulation.Host.csproj` | `Host/**/*.cs` + code generated from `Argus.Contracts/proto` | net8.0 exe |
| `Host.Tests/Argus.Simulation.Host.Tests.csproj` | `Host.Tests/**/*.cs` | NUnit |

`Directory.Build.props` pins C# 9 to match Unity 6: don't use newer language features in Core or tests.

## Build and test

From the repository root, with the .NET 8 SDK:

```bash
dotnet build headless/Host/Argus.Simulation.Host.csproj
dotnet test headless/Tests/Argus.Simulation.Tests.EditMode.Headless.csproj
dotnet test headless/Host.Tests/Argus.Simulation.Host.Tests.csproj
```

Run the host against the Basilisk service (needs `Argus.Basilisk/.venv`; the host starts and
stops the service itself):

```bash
dotnet run --project headless/Host/Argus.Simulation.Host.csproj -- --start-basilisk --duration-s 60 --real-time-factor 0
dotnet run --project headless/Host/Argus.Simulation.Host.csproj -- --validate --run-config headless/Host/runs/p0-default.json --duration-s 3600
```

- Exit codes: 0 success or Ctrl+C, 1 run failure, 2 bad arguments or an invalid run file or
  profile. `--validate` checks a run file and kernel coverage without connecting to anything.
- `--record <dir>` writes dataset v1 (`Assets/ArgusSimulation/Core/Recording/README.md`).
- `[Explicit]` tests need a running service or recorded inputs and run only when named with
  `--filter`; CI runs `AGatewayOverTheServiceAppliesATorqueOverItsStep` that way.

## Layout

| Path | What |
|---|---|
| `Host/Program.cs`, `HostOptions.cs` | Entry point and command-line options |
| `Host/RunConfigurationFile.cs` | Reads a run file and its spacecraft profile into Core `SimulationConfiguration` |
| `Host/Basilisk/` | `BasiliskEngine` (gRPC client to the service), `BasiliskProtoMapper`, `BasiliskServiceProcess` (`--start-basilisk`), `KernelSetCoverage` |
| `Host/Streaming/` | State stream server to Unity (`argus/stream/v1`) |
| `Host/Gateway/` | Controller gateway server (`argus/gateway/v1`); a placeholder until HOST-6 lands |
| `Host/Replay/` | `--replay` of a recorded dataset over the state stream (HOST-7) |
| `Host/runs/` | Run files: `p0-default.json` is the P0 run; `reference/` holds HOST-8's frozen cases |
| `Host/spacecraft/` | Spacecraft profiles (body and devices); `argus-p0.json` is P0 |

## Rules

- **Core must compile here.** If a Core change breaks this build, fix Core; don't add Unity
  references or stubs here.
- **Tests go where their code is.** EditMode tests reference only Core and live in
  `Assets/ArgusSimulation/Tests/EditMode/` (compiled by `Tests/`). Tests of `Host/` go in
  `Host.Tests/`. Tests that need Unity go in `Assets/ArgusSimulation/Tests/PlayMode/`.
- **gRPC and Protobuf stay in `Host/`.** Generated code is `Access="Internal"`, lives in `obj/`
  and is never committed. `BasiliskEngine` owns the client and the envelopes; mappers convert
  payloads to and from Core types, once.
- **Run files and profiles.** JSON field names are Core property names in camelCase, SI units
  with the unit in the name (`semiMajorAxisMeters`), quaternions as `{"x","y","z","w"}` objects,
  `epochUtc` ending in `Z`. The run file holds orbit, epoch, seed and kernel set; the profile
  holds the body and devices (D11). Don't put devices back in the run file. The format is in
  the README's "Run configuration files".
- `runs/reference/*.json` are frozen inputs for HOST-8's checks: change a value only by adding a
  new case (`runs/reference/README.md`). Each case keeps its spacecraft profile inline.
- Endpoint and rate precedence is always flag, then environment variable, then default; all
  servers bind loopback only.
