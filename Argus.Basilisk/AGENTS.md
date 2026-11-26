# AGENTS.md: Argus.Basilisk

The Python gRPC service that runs Basilisk with SPICE inside it. It owns simulation time and,
in paced runs, wall-clock pacing. Its only client is `BasiliskEngine` in `headless/Host`.

The full brief is [README.md](README.md). Section numbers below (§n) refer to it; read the
section for the area you change before you change it.

## Setup

All commands run from this folder. Python 3.12 only; bsk 2.12.2 has no Intel-Mac wheel.

```bash
uv sync --frozen --all-extras                       # .venv from uv.lock, exactly
.venv/bin/python scripts/generate_protos.py         # Argus.Contracts -> generated/
.venv/bin/python scripts/fetch_kernels.py argus-2026-11-02   # SPICE, WMM and albedo -> kernels/
```

- Without uv: `python3.12 -m venv .venv && .venv/bin/pip install --require-hashes -r requirements.txt`.
- The P0 kernel set ID is `kernelSetId` in `headless/Host/runs/p0-default.json`.
- **Rerun `generate_protos.py` after any `.proto` change or pull.** `tests/conftest.py` only
  generates when `generated/` is missing, so stale code otherwise survives.

## Test and run

```bash
.venv/bin/python -m pytest -q                       # all tests
.venv/bin/python -m pytest -q tests/test_validation.py   # no bsk or kernels needed
.venv/bin/python -m argus_basilisk --endpoint 127.0.0.1:50051
```

- Tests that need kernels skip until `kernels/` is filled. CI fills it and fails on any skip
  except the `argus-2026-09-25` tests, so run with kernels before you call a change done.
- End to end: `dotnet run --project headless/Host/Argus.Simulation.Host.csproj -- --start-basilisk --duration-s 60`
  from the repository root must exit 0.

## Code map

| File | Role |
|---|---|
| `argus_basilisk/servicer.py` | The three RPCs (ConfigureRun, Reset, Step) and the lifecycle (§3) |
| `argus_basilisk/validation.py` | Every request check; one error per field path (§3, §11) |
| `argus_basilisk/scenario.py` | Builds spacecraft, SPICE, eclipse, environment, devices, ClockSynch (§4–§8) |
| `argus_basilisk/devices/` | One module per device kind, registered in `devices/__init__.py` `KINDS` (§9) |
| `argus_basilisk/seeds.py` | Per-device RNG seeds and the service's own noise draws (§10) |
| `argus_basilisk/kernels.py` | Kernel-set manifests, SHA-256 and coverage checks (§8) |
| `kernel_sets/*.json` | Pinned kernel manifests. A set's ID is immutable: publish a new ID, never edit one |
| `scripts/` | `generate_protos.py`, `fetch_kernels.py`, `check_reference_runs.py` (HOST-8 checker) |

## Rules

- **Return Basilisk-native data.** J2000 state and SPICE matrices only. Never convert to ECEF
  or ITRF93 here; Core's `BasiliskStateMapper` converts once (D8).
- **SPICE only through `spiceInterface` (D3).** `argus_basilisk` never imports spiceypy; only the
  offline scripts may.
- **Tick order and freshness are required (§6).** Modules run ClockSynch, `spiceInterface`,
  actuators, dynamics, environment, then sensors. Read every message through a reader
  subscribed at build time and assert it was written at this t_n.
- **Adding a device kind** is one module in `devices/` plus one `KINDS` entry; `validation.py`
  and `scenario.py` walk `KINDS` and must not switch on kind names. Import numpy and Basilisk
  only inside `build`, so validation runs without the `basilisk` extra. Add the kind's
  parameter and output tables to §9 and the matching Core kind (`docs/device-model.md`).
- **Seeds (§10).** Derive every module's `RNGSeed` from `(random_seed, device_id, stream)` with
  SHA-256. Never use Python `hash()` or Basilisk's default seed. Changing one device's seed must
  change only that device's noise.
- **IMU:** one Gauss-Markov state per axis, so noise density and bias random walk exclude each
  other per channel. Never call `setErrorBounds*`.
- **Errors (§11).** Map each failure to the gRPC code in the table, with the field path and bad
  value in the details. A call that reaches the servicer while another runs is RESOURCE_EXHAUSTED.
- **Dependencies.** Edit `pyproject.toml`, then regenerate and commit both lock files:
  `uv lock` and `uv export --frozen --all-extras --no-emit-project --format requirements-txt -o requirements.txt`.
  CI fails if they disagree.
- **Schema changes** go through `Argus.Contracts/` with the matching Core change in one PR.
- Never commit `generated/`, `kernels/` or `.venv/`.
