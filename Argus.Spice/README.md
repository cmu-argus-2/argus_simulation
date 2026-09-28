# Argus.Spice

Persistent runtime SPICE worker for Earth orientation and Sun geometry. The worker
evaluates every requested simulation instant directly from NAIF kernels. It does not
consume or generate a spacecraft trajectory, scenario ephemeris, replay file, or
statistical dataset.

The simulation core starts and stops this worker automatically through
`SpiceRuntimeEphemerisProvider`. SPICE is an environment provider; the selected dynamics
backend remains responsible for spacecraft position, velocity, and attitude.

## Runtime lifecycle

1. The simulation starts `run_runtime.sh` with its UTC epoch.
2. The launcher creates `.venv/` if necessary and installs pinned Python dependencies.
3. `runtime_service.py` downloads missing NAIF kernels, verifies every SHA-256, and
   loads them once.
4. For each simulation step, the core sends the requested simulation time.
5. SPICE returns the exact J2000 → ITRF93 state transform and Sun position for that
   instant.
6. The core sends `QUIT` during shutdown.

No separate generation or preprocessing command is required.

## Manual diagnostic

The simulator runs this automatically. To inspect the protocol manually:

```bash
Argus.Spice/run_runtime.sh --epoch 2025-01-15T00:00:00Z
```

Then send tab-separated commands on standard input:

```text
SAMPLE<TAB>request-id<TAB>simulation-seconds
QUIT
```

Standard output is reserved for the versioned tab-separated protocol. Provisioning and
diagnostic messages are written to standard error.

## Files

| File | Role |
|---|---|
| `run_runtime.sh` | Simulation-owned launcher and automatic Python provisioning. |
| `runtime_service.py` | Persistent live SPICE request worker. |
| `kernels.json` | Pinned NAIF kernel URLs, purposes, and SHA-256 values. |
| `requirements.txt` | Pinned runtime Python dependencies. |
| `kernels/` | Downloaded runtime kernels; verified on startup and ignored by Git. |

The current Earth-orientation kernel covers a finite calendar interval. Runs outside a
loaded kernel's authoritative coverage fail explicitly; they never fall back to a
recorded trajectory or extrapolated environment sample. Update and review
`kernels.json` when a newer official NAIF Earth-orientation kernel is required.
