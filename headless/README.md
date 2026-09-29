# Headless build of the Argus core

`Argus.Simulation.Core` must run without Unity (see
[target architecture](../docs/target-architecture.md), decision D1 and gap G2). This
folder builds the same source files outside Unity, so that rule can be checked with plain
`dotnet` and the core tests can run without a Unity licence. No CI runs it yet.

Nothing here is copied from `Assets/`. The projects compile the Unity source in place:

| Project | Compiles | Output assembly |
|---|---|---|
| `Core/Argus.Simulation.Core.Headless.csproj` | `Assets/ArgusSimulation/Core/**/*.cs` (netstandard2.1) | `Argus.Simulation.Core` |
| `Tests/Argus.Simulation.Tests.EditMode.Headless.csproj` | `Assets/ArgusSimulation/Tests/EditMode/**/*.cs` (NUnit) | `Argus.Simulation.Tests.EditMode` |

The assembly names match the Unity assemblies, so `InternalsVisibleTo` entries work in
both builds. `Directory.Build.props` pins C# 9 to match Unity 6.

## Run

Requires the .NET SDK 8 or newer.

```bash
dotnet test headless/Tests/Argus.Simulation.Tests.EditMode.Headless.csproj
```

## Rules

- Code under `Assets/ArgusSimulation/Core/` must compile here. The Core `.asmdef` sets
  `noEngineReferences`, so Unity rejects `UnityEngine` usage in Core as well.
- EditMode tests reference only Core. Tests that need Unity belong in
  `Assets/ArgusSimulation/Tests/PlayMode/`.
- Later, the headless simulation host process (gateway, `BasiliskEngine`, sensors,
  recorder) will live next to these projects. It does not exist yet.
