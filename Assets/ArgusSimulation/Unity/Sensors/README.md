# Unity/Sensors — sensor runtime bridge

**Diagram block:** Sensor runtime · **Assembly:** `Argus.Simulation.Unity` · **Status:** exists, temporary

`SimulationSensorRuntime` owns a Core `SensorManager` and samples it on every
`SimulationRunner.StateProduced`, which carries a whole `SimulationState`. Sensor behaviour
itself lives in `Core/Sensors/`; this folder holds only the Unity-side wiring.

This bridge exists because the core currently runs inside Unity. Once the headless core
process exists (gap G2 in [target-architecture.md](../../../../docs/target-architecture.md)),
`SensorManager` runs there and Unity only displays the frames it receives.
