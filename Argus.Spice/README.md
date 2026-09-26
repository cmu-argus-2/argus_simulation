# Argus.Spice

Offline generator for SPICE-derived environment data: Earth orientation
(J2000 -> ITRF93) and Sun position. Unity and the simulation core read the generated
JSON; neither depends on Python or SPICE at runtime.

## Regenerate

Requires Homebrew `python3.12` (override with `PYTHON=...`).

```bash
Argus.Spice/regenerate.sh
```

The script:

1. Creates `.venv/` and installs the pinned `requirements.txt`.
2. Downloads any missing kernels into `kernels/` (gitignored) and checks their SHA-256
   against `kernels.json`.
3. Writes two files:
   - `Assets/StreamingAssets/Argus/Spice/foundation_one_orbit.json` is the ephemeris
     Unity loads at runtime: Earth orientation and Sun position every 10 s.
   - `Assets/ArgusSimulation/Tests/Fixtures/foundation_one_orbit_spacecraft_reference.json`
     is the test reference: the analytic orbit transformed with exact SPICE calls, plus
     eclipse windows from SPICE's `gfoclt` occultation search. The C# tests compare the
     simulator against it.

To confirm the committed files are current without rewriting them:

```bash
Argus.Spice/regenerate.sh --check
```

## Files

| File | Role |
|---|---|
| `kernels.json` | Pinned kernels: URLs (with `a_old_versions/` fallbacks) and SHA-256. |
| `scenarios/*.json` | Epoch, sample step, and the analytic orbit used to size one period. |
| `generate_reference.py` | Coverage checks, sampling, per-sample validation, deterministic output. |

The output header documents frames, units, the simulation-time -> ET -> UTC mapping,
kernel hashes, and kernel coverage. To add a scenario, copy the JSON in `scenarios/`
and pass its path to `regenerate.sh`. A scenario outside the Earth-orientation kernel's
coverage (2000-01-01 to 2026-12-22) fails with a coverage error; pin a newer
`earth_*.bpc` in `kernels.json` and rerun with `--pin-kernels`.
