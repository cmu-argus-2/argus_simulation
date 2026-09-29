# Argus.Basilisk — Basilisk simulation service (planned)

**Diagram block:** Basilisk service · **Process:** Python, outside Unity · **Status:** not built

The Python service that runs the Basilisk simulation. It is the authoritative dynamics
backend and owns simulation time in every Basilisk run (decisions D2 and D3 in
[target-architecture.md](../docs/target-architecture.md)):

- spacecraft dynamics, actuator modules, and IMU and other sensor modules
- SPICE through Basilisk's `spiceInterface` module, loading one pinned NAIF kernel set.
  There is no separate SPICE service.
- `clockSynch` pacing in real-time and HIL runs; Argus follows Basilisk's timestamps

It talks only to `BasiliskEngine` (`Assets/ArgusSimulation/Core/Basilisk/`) over gRPC using
the schemas in [`Argus.Contracts/`](../Argus.Contracts/README.md). Keep Argus extensions
outside the Basilisk source tree.

Teammate Basilisk and SPICE work on other branches (`dynamics/basilisk/`, `Argus.Spice/`)
is not on `main`. Port it here only after reconciling it with decisions D2, D3 and D8
(§8 of the target architecture): SPICE only through `spiceInterface`, with no standalone
SPICE worker or offline tables, and live Basilisk runs rather than recording replay.
