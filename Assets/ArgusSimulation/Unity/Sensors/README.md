# Unity/Sensors — sensor runtime bridge

**Diagram block:** Sensor runtime · **Assembly:** `Argus.Simulation.Unity` · **Status:** exists, temporary

`SimulationSensorRuntime` owns a Core `SensorManager` and samples it on every
`SimulationRunner.StateProduced`, which carries a whole `SimulationState`. Sensor behaviour
itself lives in `Core/Sensors/`; this folder holds only the Unity-side wiring.

This bridge exists because the core currently runs inside Unity. `headless/Host` already
runs a `SensorManager` over `BasiliskEngine`; once it streams states and sensor frames to
Unity (gap G2 in [target-architecture.md](../../../../docs/target-architecture.md)), Unity
stops running its own `SensorManager` and only displays the frames it receives.
