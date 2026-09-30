# Argus.Agent: training environment and agent SDK (placeholder)

**Diagram block:** Agents + flight computer · **Process:** outside Unity · **Status:** not started

TODO(D7, G1): a Python SDK that drives the simulation through the gateway link
(`argus/gateway/v1`, served by `headless/Host` `GatewayServer`) with deterministic
`Reset(seed, scenario)` and `Step(commands)`. It receives controller-visible observations
only, never truth (decision D7 in [target-architecture.md](../docs/target-architecture.md)).
Whether lockstep training runs Basilisk unpaced or the analytic fixture is open (§11).
