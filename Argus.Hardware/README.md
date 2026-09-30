# Argus.Hardware: flight-computer adapters (placeholder)

**Diagram block:** Flight computer (HIL) · **Process:** outside Unity · **Status:** not started

TODO(D7, G1): hardware-in-the-loop adapters that connect a physical flight computer to the
gateway link (`argus/gateway/v1`): sensor observations out in the flight software's formats,
actuator commands in, with the gateway's authority, heartbeat and failsafe rules (see
[target-architecture.md](../docs/target-architecture.md)). Physical sensor input publishes
the same `SensorFrame` envelopes as the simulated sensors.
