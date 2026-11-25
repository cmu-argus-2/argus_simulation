# AGENTS.md

Guidance for coding agents working in this repository. Each component has its own `AGENTS.md`;
read the one nearest the files you change. The human-facing docs are the source of truth, and
these files point into them rather than repeat them.

## What this repository is

The Argus CubeSat simulator: a Unity-independent headless core, a Python Basilisk service that
owns dynamics and simulation time, and Unity as an optional GUI and image renderer.

| Folder | What it is | Toolchain | Guide |
|---|---|---|---|
| `Argus.Basilisk/` | Python gRPC service running Basilisk and SPICE | uv, Python 3.12 | [Argus.Basilisk/AGENTS.md](Argus.Basilisk/AGENTS.md) |
| `Argus.Contracts/` | Protobuf v1 schemas for every cross-process link | protoc | [Argus.Contracts/AGENTS.md](Argus.Contracts/AGENTS.md) |
| `headless/` | `dotnet` build of Core, its tests, and the simulation host | .NET 8 | [headless/AGENTS.md](headless/AGENTS.md) |
| `Assets/ArgusSimulation/` | Core (pure C#) and the Unity adapters, editor tools and tests | Unity 6000.6.0f1 | [Assets/ArgusSimulation/AGENTS.md](Assets/ArgusSimulation/AGENTS.md) |
| `Argus.Hardware/` | HIL flight-computer adapters (not started) | none yet | [Argus.Hardware/AGENTS.md](Argus.Hardware/AGENTS.md) |
| `Argus.Imagery/` | Optional offline NASA GIBS imagery pack | Python 3 stdlib | [Argus.Imagery/AGENTS.md](Argus.Imagery/AGENTS.md) |
| `docs/` | Architecture, decisions (D1–D11), gaps (G1–G5), device model | none | see below |
| `tools/` | One-off review scripts | Python 3 | none |

Read before a non-trivial change:
- [docs/target-architecture.md](docs/target-architecture.md): the decisions table (§3) and run
  modes (§4). Most review comments cite a decision such as D2 or D8.
- [docs/code-organization.md](docs/code-organization.md): where each class belongs.
- [docs/device-model.md](docs/device-model.md): devices, kinds, profiles and run files (D11).

## Verify a change

Run what CI runs ([.github/workflows/headless.yml](.github/workflows/headless.yml)) for the parts
you touched. From the repository root:

```bash
dotnet build headless/Host/Argus.Simulation.Host.csproj
dotnet test headless/Tests/Argus.Simulation.Tests.EditMode.Headless.csproj
dotnet test headless/Host.Tests/Argus.Simulation.Host.Tests.csproj
```

```bash
cd Argus.Basilisk && uv sync --frozen --all-extras && .venv/bin/python scripts/generate_protos.py && .venv/bin/python -m pytest -q
```

CI cannot compile Unity code. A change under `Assets/ArgusSimulation/Unity/`, `Editor/` or
`Tests/PlayMode/` needs a Unity 6000.6 editor to check; say so in the PR if you could not.

## Rules that apply everywhere

- **Core stays headless (D1).** Nothing under `Assets/ArgusSimulation/Core/` may use Unity,
  Cesium, gRPC or Protobuf. `headless/` builds Core with plain `dotnet`, so CI catches it.
- **Basilisk types never reach Unity (D8).** Basilisk data enters through `BasiliskEngine` in
  `headless/Host` and is mapped to Core types once.
- **One clock (D2).** Basilisk owns simulation time; Unity only follows the host's state stream
  and never steps or paces a run.
- **Contracts are additive only.** v1 is frozen: add fields with new numbers, never change a
  field's number, type, unit, frame or meaning. A Core contract change and its `.proto` change
  land in the same PR ([Argus.Contracts/README.md](Argus.Contracts/README.md)).
- **Units and frames in names.** Every field, parameter and variable that holds a physical
  quantity states its unit and frame in its name (`position_ecef_m`, `_rad_per_s`, `_body`).
- **Never commit generated or downloaded files:** `Argus.Basilisk/generated/`,
  `Argus.Basilisk/kernels/`, `Assets/ArgusSimulation/Plugins/`, `bin/`, `obj/`, `ImageryPacks/`,
  `.env`. They are git-ignored; keep it that way.
- **Keep Unity `.meta` files with their assets.** Move or rename a file under `Assets/` together
  with its `.meta`; never delete or regenerate one. A new file under `Assets/` needs a `.meta`
  with a fresh GUID.
- **Planned work** is marked `TODO(<id>)`, where the id is a decision, gap or area from
  `docs/code-organization.md` ("Placeholders and TODOs"). Remove the TODO when you implement it.
- **Docs move with code.** If a change alters behaviour that a README or `docs/` page
  describes, update that page in the same PR.

## Git and pull requests

- Work is tracked in GitHub issues with keys such as `BSK-7`, `HOST-6`, `UNITY-4`, `CORE-19`
  (roadmap: issue #76). Check the issue's assignee before starting.
- Branch from `main`, named like GitHub's issue branch: `<issue number>-<slug>`.
- Conventional commits with a scope and the issue key:
  `feat(basilisk): pace runs with ClockSynch (BSK-7)`, `fix(host): ...`, `docs(contracts): ...`.
- PR bodies are short: `Closes #n`, what changed, how it was tested (test counts). Never push to
  `main`; never force-push a branch someone else is reviewing without saying so.
