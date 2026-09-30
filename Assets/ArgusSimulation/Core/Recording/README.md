# Core/Recording — run recorder (planned)

**Diagram block:** Run recorder · **Assembly:** `Argus.Simulation.Core` · **Status:** not built

The run recorder will be the single export route (decision D6 in
[target-architecture.md](../../../../docs/target-architecture.md)). It is a passive,
write-only archive. It never feeds data back into the run and never blocks the step loop.

## Inputs

- Every `SimulationSnapshot`: spacecraft truth plus `EnvironmentState` when the backend
  provides it (Basilisk runs; the analytic engine reports none).
- Every `SensorOutputSet` from `SensorManager`, including ground-truth sensors and camera
  `ImageFrame`s once cameras are sensor models. Camera frames carry their capture time;
  late frames follow the late-frame policy (gap G3).
- The gateway's command log: requested, applied and rejected commands.

## Proposed dataset v1 (not final)

```text
<run>/
├── manifest.json        # schema version, run ID, configuration, backend, kernel set, seed
├── snapshots.jsonl      # one line per snapshot
├── sensors.jsonl        # one line per SensorFrame envelope (payload or file reference)
├── commands.jsonl       # one line per gateway command record
└── images/<sensorId>/<sequence>.png
```

## Today

`Unity/Export/NavigationEpisodeExporter.cs` is the current export path. It writes PNGs and
`navigation_metadata.jsonl` under `Application.persistentDataPath`, and the Python
navigation code reads that output. It stays until this recorder covers what that code
reads. Whether dataset v1 must stay compatible with it is an open question (see §11 of
the target architecture).
