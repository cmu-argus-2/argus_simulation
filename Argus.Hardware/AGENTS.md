# AGENTS.md: Argus.Hardware

Hardware-in-the-loop (HIL) adapters that will connect a physical flight computer to the simulator.
**Status: not started.** The folder holds only [README.md](README.md), a placeholder tagged
`TODO(D7, G1)`. The work is tracked as HIL-1 (#66), which depends on DATA-8 (#73) for the flight
computer's interfaces and message formats.

## Before writing code here

- The adapter language and runtime are not chosen yet; DATA-8 records that choice in the README.
  Don't pick one in a code PR without that decision.
- Adapters are clients of the controller gateway, `argus/gateway/v1`
  ([Argus.Contracts/README.md](../Argus.Contracts/README.md), "Controller gateway"), served by
  `headless/Host` (HOST-6, #30). They never talk to the Basilisk service or read
  `SimulationState`.

## Rules for the adapters

- **Observations out, commands in.** Translate controller-visible sensor observations into the
  flight software's formats, and its actuator commands into gateway `DeviceCommandSet`s that
  address actuators by device ID (D11).
- **Follow the gateway's authority rules (D7, CORE-9).** Claim authority before commanding, send
  heartbeats, and treat a lost session as lost authority. The gateway enforces actuator limits
  and failsafe; don't duplicate or bypass them.
- **Physical actuators stay inhibited on the bench.** The adapters enforce the run's
  `physical_actuators` setting (`argus.sim.v1.RunConfiguration`): inhibited unless enabled, and
  enabling names the test procedure that allows it.
- Physical sensor input, if added, publishes the same `SensorFrame` envelopes as the simulated
  sensors, so the recorder and controllers can't tell the source apart.
- Transport code belongs here or in `headless/Host`, never in Unity UI classes or Core
  (`docs/code-organization.md`, "Placement rules").
