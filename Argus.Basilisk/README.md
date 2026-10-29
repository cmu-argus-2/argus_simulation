# Argus.Basilisk: Basilisk simulation service

**Diagram block:** Basilisk service · **Process:** Python, outside Unity · **Status:** build steps 1 to 3 (§12): spacecraft, SPICE, eclipse, the IMU and the magnetometer; the light sensors, pacing and non-zero commands are UNIMPLEMENTED

The Python gRPC service that runs the Basilisk simulation, with SPICE inside it through
`spiceInterface`. It is the authoritative dynamics backend and owns simulation time in every
Basilisk run, plus pacing in real-time runs (decisions D2, D3 and D10 in
[target-architecture.md](../docs/target-architecture.md)). Its only client is
`BasiliskEngine` in `headless/Host`, over the v1 schemas in
[`Argus.Contracts/`](../Argus.Contracts/README.md).

## 1. Ownership

- This folder holds the service, the pinned kernel manifest and this brief. The
  Basilisk/SPICE team implements everything that imports Basilisk.
- Keep the RPC names and field meanings in `argus/basilisk/v1/basilisk_service.proto`.
  Schema changes are additive only and go through `Argus.Contracts` with the matching Core
  change in one PR.
- Never convert to ECEF or ITRF93 in the service. Return Basilisk-native J2000 state and
  SPICE matrices; Argus converts once, in `Core/Basilisk/BasiliskStateMapper`.
- Keep Argus extensions outside the Basilisk source tree.
- Teammate work on other branches (`dynamics/basilisk/`, `Argus.Spice/`) is ported here
  only after reconciling it with D2, D3 and D8: SPICE only through `spiceInterface`, no
  standalone SPICE worker or offline tables, and live runs rather than recording replay.

Code layout: `argus_basilisk/servicer.py` (the three RPCs and the lifecycle, §3) calls
`validation.py` (§3), `kernels.py` (§8) and `scenario.py` (§4 to §8), which builds the
spacecraft, SPICE and eclipse (BSK-3, #16) and adds the sensors that `sensors.py` (§9) builds
with seeds from `seeds.py` (§10); the IMU (BSK-4, #17) and the magnetometer (BSK-5, #18) are built. `scripts/fetch_kernels.py` is the SPICE team's
fetch/verify tool (§8).

## 2. Install and run

```bash
uv sync --frozen --all-extras
.venv/bin/python scripts/generate_protos.py
.venv/bin/python -m argus_basilisk --endpoint 127.0.0.1:50051
```

### Reproducible environment (BSK-12, #86)

Every machine gets the same packages: `uv.lock` pins all 42 direct and transitive
dependencies, for every extra, with the SHA-256 of each wheel, and `.python-version` pins
Python 3.12 (`requires-python = "==3.12.*"`, so the lock has no per-version forks).

- **With uv** (recommended; [install uv](https://docs.astral.sh/uv/getting-started/installation/),
  0.13.4 or later): `uv sync --frozen --all-extras` creates `.venv` from the lock, downloading
  Python 3.12 if needed. `--frozen` installs exactly the lock and never re-resolves.
- **With pip only**: `requirements.txt` is exported from the lock with the same versions and
  hashes. Install it into a Python 3.12 venv; there is no need to install the project itself,
  since everything runs from this directory:

  ```bash
  python3.12 -m venv .venv
  .venv/bin/pip install --require-hashes -r requirements.txt
  ```

- Supported platforms are the ones with a `bsk==2.11.1` wheel: macOS arm64, Linux x86_64 and
  aarch64, and Windows x86_64. Intel Macs have none, so `uv sync` refuses them, and pip installs
  nothing from `requirements.txt` there, since every line's platform marker is false.
- To change a dependency, edit `pyproject.toml`, then regenerate both files and commit them
  together:

  ```bash
  uv lock
  uv export --frozen --all-extras --no-emit-project --format requirements-txt -o requirements.txt
  ```

  `uv lock --upgrade-package <name>` moves one pinned package. CI fails when `pyproject.toml`,
  `uv.lock` and `requirements.txt` disagree.
- The kernels are pinned separately, by SHA-256 in `kernel_sets/` (§8).

- The service needs the `basilisk` extra (`bsk==2.11.1`); `validation.py`, `kernels.py` and
  their tests run without it.
- The endpoint comes from `--endpoint`, then `ARGUS_BASILISK_ENDPOINT`, then
  `127.0.0.1:50051`. v1 is an insecure channel, so non-loopback hosts are refused.
- Generated code goes to `generated/` and kernel files to `kernels/`; both are gitignored.
  Fill `kernels/` with `scripts/fetch_kernels.py` (§8) before starting a run.
- The server runs one worker and `maximum_concurrent_rpcs=1`, so a concurrent call gets
  RESOURCE_EXHAUSTED.

## 3. Lifecycle

States: EMPTY → READY (next sequence 0) → RUNNING, plus FAILED.

### ConfigureRun

1. Discard any existing run.
2. Validate every field before touching Basilisk, repeating Core `IsValid`:
   - orbit: finite, 0 ≤ e < 1, 0 ≤ i ≤ 180°, a(1 − e) > 6 378 137 m;
   - spacecraft: mass > 0; inertia symmetric (within 1e-12 of the trace) and positive
     definite; unit initial quaternion (within 1e-6); finite rate;
   - `kernel_set_id` matches `[a-z0-9][a-z0-9._-]{0,63}`; `fixed_step_ns > 0`;
     `real_time_factor` finite and ≥ 0; unique sensor IDs; each `sample_period_ns` a
     positive multiple of `fixed_step_ns`; every oneof and message field set;
   - sensor profiles as in `sensors.proto` (a light-sensor half-angle is at most π/2, since
     `coarseSunSensor` and `albedo` do not clamp negative cosines), plus Basilisk 2.11.1
     limits: an IMU channel
     cannot have both noise density and random walk > 0 (one Gauss-Markov state per axis),
     and a walk bound > 0 needs random walk > 0 (the same bound would clip white noise);
   - `noise_std_tesla ≥ 0`; noise > 0 is added by the service from the `mag` seed stream,
     because bsk 2.11.1 ignores the magnetometer's `RNGSeed` (§9, BSK-9 #22);
   - `epoch_utc` is inside the kernel set's `coverage_utc`, and inside WMM2025 validity
     (2025.0 to 2030.0) when a magnetometer is configured; otherwise OUT_OF_RANGE. This
     check must precede the build: `SpiceInterface::Reset` evaluates SPICE during
     `InitializeSimulation`, and CSPICE aborts the whole process outside coverage.

   `argus_basilisk.validation` implements this step (BSK-2, #15) without importing
   Basilisk. `validate_configure_run(request, coverage_utc=..., wmm_validity=...,
   supported_sensor_kinds=...)` raises `ValidationError(code, details)`; the servicer maps it
   with `context.abort(grpc.StatusCode[error.code], error.details)`. Problems rank
   INVALID_ARGUMENT, then UNIMPLEMENTED, then OUT_OF_RANGE, and the details join every problem
   of that code. `coverage_utc` is any object with `start` and `end` (such as the kernel
   set's coverage); both windows are inclusive and compared in exact nanoseconds.
   `SUPPORTED_SENSOR_KINDS` holds `"imu"` (BSK-4, #17) and `"magnetometer"` (BSK-5, #18); BSK-6 (#19) adds `"light_sensor"`; pacing
   and non-zero commands stay UNIMPLEMENTED until BSK-7 (#20) and BSK-8 (#21). Step reuses `validate_step_command` (Step 3) and `check_utc_in_range`
   (Step 4). Run the tests from `Argus.Basilisk/` with `python -m pytest tests -q`; they
   generate `generated/` if it is missing.
3. Verify the kernels (§8), build the simulation (§4 to §9) and call
   `InitializeSimulation()`. Run no step. A `BasiliskError` raised while loading kernels
   is FAILED_PRECONDITION.
4. Store the request, end READY, and fill every `ConfigureRunResponse` field; the client
   checks each echo.

### Reset

- Requires the stored `run_id`; allowed from FAILED. The response echoes `run_id`.
- MUST construct a new `SimBaseClass` and new modules from the stored request, then re-arm
  pacing. Reusing modules is wrong in bsk 2.11.1: `ImuSensor::Reset` keeps `NominalReady`,
  `PreviousTime` and the previous state (the next t = 0 sample underflows a uint64 time
  difference), Gauss-Markov states carry over, and the magnetometer's own generator is never
  re-seeded (the service keeps it off and draws magnetometer noise itself, §9).
- Acceptance: 100 steps, Reset, 100 steps again gives bit-identical responses.

### Step(n)

1. `run_id` matches and n equals the next sequence; otherwise FAILED_PRECONDITION naming
   the expected value.
2. `sim_time_ns == n × fixed_step_ns`; otherwise INVALID_ARGUMENT.
3. `command` is command n-1, for [t_n-1, t_n). It has all three vectors and they are
   finite, and for n > 0 `command_sequence == n − 1` and `command_time_ns == (n − 1) ×
   fixed_step_ns`; otherwise INVALID_ARGUMENT, so a command sent in the wrong request is
   rejected instead of acting a step off. Step(0) follows no command: both fields are unset
   and the command is zero, or INVALID_ARGUMENT (no interval ends at t = 0). For n > 0 a
   non-zero command is UNIMPLEMENTED until actuators exist (G1).
4. `epoch + t_n` is inside kernel coverage (and WMM validity with a magnetometer);
   otherwise OUT_OF_RANGE, checked before executing.
5. For n > 0, write the request's command (command n-1) into the actuator input messages
   before running, so it acts over [t_n-1, t_n): the gateway's command k, computed from the
   observation at t_k, arrives as Step(k + 1) and shapes the returned state at t_k+1 with no
   actuation delay (target-architecture §4). Never write it after running, or it would act
   one step late.
6. Run to t_n: for n = 0, `ConfigureStopTime(0)` then `ExecuteSimulation()` (the t = 0
   pass); otherwise `ConfigureStopTime(n × fixed_step_ns)` then `ExecuteSimulation()`.
7. Read every returned message through the reader subscribed at build time (§6) and assert
   `isWritten()` and `timeWritten() == t_n`, and that all values are finite. A failure is
   INTERNAL and the run becomes FAILED.
8. Return the `StepResponse`, echoing `run_id`, `sequence` and `sim_time_ns` (the client
   rejects any other value); the next sequence is n + 1.

## 4. Run configuration to Basilisk

| Field | Basilisk target |
|---|---|
| `epoch_utc` | `spiceInterface.UTCCalInit`, formatted from seconds + nanos as `yyyy-MM-ddTHH:mm:ss.fffffffffZ` (NAIF `str2et` accepts the `Z`). The same epoch feeds the WMM `epochInMsg`, never the module default. |
| `initial_orbit` | Degrees to radians, `orbitalMotion.elem2rv(earth.mu, oe)`, then `hub.r_CN_NInit` / `v_CN_NInit`. Earth is `isCentralBody`. RAAN is from J2000 x. |
| `spacecraft` | `hub.mHub`; `hub.IHubPntBc_B` (row-major); `hub.sigma_BNInit = rbk.EP2MRP(np.array([w, x, y, z]))`; `hub.omega_BN_BInit`; `hub.r_BcB_B = 0`. |
| `random_seed` | Module seeds (§10). |
| `kernel_set_id` | SPICE setup (§8). |
| `real_time_factor` | Pacing (§5). |

Quaternion rule: the Argus Hamilton active quaternion `a_to_b` (x, y, z, w) equals the
Basilisk Euler-parameter set `[w, x, y, z]` of a relative to b, and `EP2C` of it gives [ab].
Pass NumPy arrays: `RigidBodyKinematics.EP2MRP` negates its argument when w < 0, which
raises `TypeError` on a Python list, and Core accepts either sign of w.
Acceptance: `EP2C([√½, 0, 0, √½]) == [[0, 1, 0], [−1, 0, 0], [0, 0, 1]]`,
`EP2C([.5, .5, .5, .5])[2] == [1, 0, 0]` and
`EP2MRP(np.array([−.5, .5, .5, .5])) == [−⅓, −⅓, −⅓]`.

Time: `sim_time_ns` is `CurrentSimNanos`. Basilisk's SPICE time is ETInit + t, so Argus
stamps UTC as the epoch plus elapsed ephemeris-time seconds (no leap seconds; a periodic
TDB-TT error of up to about 3.3 ms, under 30 µs per day of run).

## 5. Pacing

- `real_time_factor == 0`: no ClockSynch; Step returns as fast as Basilisk runs.
- `real_time_factor > 0`: add `simSynch.ClockSynch` with `accelFactor = real_time_factor`
  and `accuracyNanos` set, on its own task at `fixed_step_ns`, highest priority. `Step(n)`
  returns no earlier than about start + t_n / factor, on an absolute schedule from Step(0).
  Report `clockOutMsg.overrunCounter` in `pacing_overrun_count`.
- Until ClockSynch is built, `real_time_factor > 0` is UNIMPLEMENTED at ConfigureRun.

## 6. Tick order and message freshness (required)

At every t_n, modules run in this order, set by task creation order and priorities:

1. ClockSynch (when pacing);
2. `spiceInterface`;
3. actuators (G1, BSK-8 #21), which read command n-1 from their input messages before the
   dynamics integrate [t_n-1, t_n). After the dynamics, the command would act a step late
   (§3 Step 5);
4. spacecraft dynamics;
5. eclipse, `MagneticFieldWMM` and albedo;
6. sensor tasks, one task `sensors_<ns>` per distinct sample period.

Every sensor must read state, SPICE and environment messages written at the same t_n.

Read every returned or upstream message through a reader subscribed at build time
(`msg.addSubscriber()` or `<Payload>Reader().subscribeTo(msg)`). `isWritten()` and
`timeWritten()` exist only on readers, and `coarseSunSensor` writes `cssDataOutMsg` only
when it is linked, so an unread CSS would report a zero payload that still passes a
`timeWritten() == 0` check at t = 0. Assert freshness on the returned messages and on
`MagneticFieldWMM.envOutMsgs[k]`, `albedo.albOutMsgs[i]` and `eclipse.eclipseOutMsgs[0]`.

## 7. StepResponse sources

| Field | Source |
|---|---|
| `spacecraft` | `spacecraft.scStateOutMsg`: `r_BN_N`, `v_BN_N`, `sigma_BN` as written, `omega_BN_B` |
| `earth`, `sun` | `spiceInterface.planetStateOutMsgs`: `PlanetName`, `PositionVector`, `VelocityVector`, `J20002Pfix`, `J20002Pfix_dot`, `computeOrient` |
| `spacecraft_shadow_factor` | `eclipse.eclipseOutMsgs[0]` read through its reader (1 sunlit, 0 umbra); always set. In bsk 2.11.1 the reader returns the `eclipse` module's payload, whose only field is the C name `shadowFactor` and reads without a warning; `illuminationFactor` is a Python alias that exists only on `messaging.EclipseMsgPayload` |
| `sensor_samples` | One per sensor whose period divides `sim_time_ns`, with `sample_time_ns` from the subscribed reader's `timeWritten()`; measurement unset when the sensor ran without data |
| `pacing_overrun_count` | `clockSynch.clockOutMsg.overrunCounter`; 0 when unpaced |

`Matrix3` is row-major: `row0` is the first row of the Basilisk matrix. Argus rejects a
transposed `J20002Pfix` / `J20002Pfix_dot` pair, whose Earth rate points along ITRF93 −z.

## 8. SPICE and kernels

### Local verifier (BSK-1 / #14)

`argus_basilisk.kernels.load_kernel_set(kernel_set_id, *, data_dir=None)` reads and
verifies local files only. The default data directory is this `Argus.Basilisk/` directory,
independent of the process working directory; tests can supply a temporary directory.
The module uses only the Python standard library, never imports Basilisk or the fetch tool,
and does not download or load SPICE data.

It returns a `KernelSet` with:

- `kernel_set_id`: the requested ID, also checked against the manifest's `id`;
- `kernel_paths`: a tuple of absolute `Path` objects in manifest order. The scenario can
  pass `[str(path) for path in result.kernel_paths]` to `addKernelPaths`;
- `coverage_utc`: `KernelCoverage(start, end, reconstructed_end)`, all timezone-aware UTC
  datetimes, with `start <= reconstructed_end <= end`. These are manifest declarations,
  not independently measured SPICE coverage; request/step range checks belong to validation;
- `support_files`: an immutable mapping from each support filename to its verified absolute
  path, separate from `kernel_paths`.

`KernelSetError` exposes `code` and `details`. The future servicer maps these with
`context.abort(grpc.StatusCode[error.code], error.details)`: invalid IDs give
`INVALID_ARGUMENT`, absent manifests give `NOT_FOUND`, and unreadable/malformed manifests,
missing files or SHA-256 mismatches give `FAILED_PRECONDITION`. Errors name the relevant
manifest, field or file. Filenames must be local basenames; paths resolving outside the
kernel directory, duplicates and kernel/support overlap are rejected.

Support files use the agreed format in [Manifest format](#manifest-format); the verifier
reads only `file` and `sha256`. `argus-2026-10-20` pins `WMM2025.COF` this way.
`ConfigureRun` calls `load_kernel_set` before building anything (BSK-3, #16).

Run the synthetic, offline verifier tests from `Argus.Basilisk/`:

```bash
python -m pytest tests -q
```

Install the `test` extra to obtain pytest (`pip install -e '.[test]'`); Basilisk and
generated protobuf modules are not needed by these tests. `tests/test_service.py` needs the
`basilisk` extra. Its live-run tests also need the P0 set `argus-2026-10-20` in `kernels/`,
and the §13 SPICE reference test needs `argus-2026-09-25`; each is skipped without its files.

### Scenario integration

- Kernel manifests live in `kernel_sets/<id>.json`. P0 (`headless/Host/runs/p0-default.json`)
  uses `argus-2026-10-20`; `argus-2026-09-25` stays for the §13 SPICE check. An ID is
  immutable: changing any file needs a new ID. `coverage_utc` bounds every run.
- Before loading, check that each file exists in `kernels/` and matches its SHA-256;
  otherwise FAILED_PRECONDITION. Never download during ConfigureRun; `scripts/fetch_kernels.py`
  (below) puts the files there beforehand.
- Build `spiceInterface.SpiceInterface(auto_configure_kernels=False)` directly and call
  `addKernelPaths` with the manifest files in order. In bsk 2.11.1 the plain constructor
  fetches its default kernels (de430 and others, about 120 MB) through pooch, and the
  `createSpiceInterface` defaults do the same. Do not import `simIncludeGravBody` either: it
  imports `dataFetcher`, which sends a request to github.com at import time; `scenario.py`
  builds the two point-mass `GravBodyData` itself.
- A kernel that cannot be loaded raises `BasiliskError` during `InitializeSimulation`; it is
  FAILED_PRECONDITION. A file that loads but lacks data (no leap seconds, no ITRF93 for the
  epoch) is not an error until CSPICE needs that data, and then CSPICE aborts the process.
  The SHA-256 check and the `fetch_kernels.py` coverage check are what prevent that.
- `addPlanetNames(["earth", "sun"])` in gravity-body order; `planetFrames = ["ITRF93", ""]`
  (the default `IAU_EARTH` is silently wrong, and a non-empty frame forces
  `computeOrient`); `referenceBase = "j2000"`; `zeroBase = "Earth"`. Echo the Earth frame in
  `spice_earth_frame`.
- The empty Sun entry still auto-detects `IAU_SUN`, so `sun.computeOrient` is 1 and every
  step calls `sxform("j2000", "IAU_sun")`. That needs the `BODY10_*` constants from
  `pck00011.tpc`; without it CSPICE aborts the process. (The eclipse radii are hard-coded
  in Basilisk and never read from the PCK.)
- Eclipse: `sunInMsg` from the SPICE Sun, `addPlanetToModel(earth)`,
  `addSpacecraftToModel(scStateOutMsg)`.

### Fetching a kernel set

```bash
.venv/bin/python scripts/fetch_kernels.py argus-2026-10-20
```

- For every `kernels` and `support_files` entry missing from `kernels/`, the tool tries
  each URL in order and keeps the download only if its SHA-256 matches; otherwise nothing
  is left in `kernels/` and it exits non-zero naming the file and both hashes. A file
  already present is hashed and never replaced, so a second run downloads nothing.
- It then reports, with spiceypy, the SPK coverage of the Sun (10) and Earth (399), the
  binary PCK coverage of ITRF93 (3000) and the PCK's "UTC Epoch of last datum" comment,
  and fails unless they cover `coverage_utc` and match `reconstructed_end`.
- spiceypy is a dependency of this tool only (the `tools` extra). `argus_basilisk` never
  imports it or the tool, so the service's only SPICE source stays `spiceInterface` (D3).
- A copy placed in `kernels/` by hand is accepted when its hash matches.

### Choosing the Earth PCK

- NAIF replaces its daily high-precision Earth PCK (`earth_000101_*`) every few days and
  deletes the old one; it is never moved to `a_old_versions/`. A set that pins one stops
  being downloadable within days, as `argus-2026-09-25` has: its PCK must now be copied
  into `kernels/` by hand.
- New sets pin NAIF's yearly historical PCK (`earth_620120_<last datum>.bpc`, published each
  August). Superseded versions move to `a_old_versions/` and stay there, so each entry lists
  both URLs and the set stays downloadable.
- Runs use measured Earth orientation only: `coverage_utc.end` equals `reconstructed_end`
  (the PCK's last datum, 2026-08-06 for `argus-2026-10-20`), not the two extrapolated days
  after it, so a later epoch is OUT_OF_RANGE.
- Not decided yet: who publishes the next set and when, and whether runs will ever need
  epochs after the last datum. If they do, the option is a daily PCK hosted by the team,
  since NAIF does not keep it.

### Manifest format

```json
{
  "id": "<file stem; immutable>",
  "coverage_utc": {"start": "...Z", "end": "...Z", "reconstructed_end": "...Z", "source": "..."},
  "kernels": [
    {"file": "naif0012.tls", "type": "LSK", "purpose": "...", "urls": ["..."], "sha256": "..."}
  ],
  "support_files": [
    {"file": "WMM2025.COF", "purpose": "...", "urls": ["..."], "sha256": "...",
     "validity": {"start": "2025-01-01T00:00:00Z", "end": "2030-01-01T00:00:00Z"}}
  ]
}
```

- `kernels` are passed to `addKernelPaths` in list order. `type` is one of `LSK`, `SPK`,
  `binary PCK`, `text PCK`; the coverage check needs exactly one each of `LSK`, `SPK` and
  `binary PCK`.
- `support_files` (optional) are non-SPICE data files fetched and verified like kernels but
  never passed to `addKernelPaths`; `kernels.py` returns their verified paths by `file`.
  `validity` is a closed UTC interval: a run is inside it when `start <= t <= end`, the
  same rule as `validation.py`'s `UtcWindow`. Neither the tool nor `kernels.py` reads it.
- `purpose` (both lists) is a free-text note that no code reads.
- `file` is a plain filename, unique across both lists; all files share `kernels/`. `urls`
  is a non-empty list tried in order. `sha256` is 64 hex characters.

## 9. P0 sensors

Common: `C_SB = EP2C([w, x, y, z])` of `mount.sensor_to_body`; `mount.position_body_m` goes
to IMU `sensorPos_B`, CSS `r_B` and albedo `r_IB_B`. **Always set the A matrix
explicitly**: the defaults differ per module (IMU gyroscope, magnetometer and CSS default
to a random walk; the IMU accelerometer to white noise). Pass matrices and vectors as NumPy
arrays or nested lists, never scalars: the SWIG Eigen typemaps reject a scalar. The `0`, `I`
and `[b, b, b]` in `sensors.proto` comments are shorthand for `np.zeros((3, 3))`,
`np.eye(3)` and a 3-vector. Outputs are scale × (true + noise + bias), then quantised (IMU
only), then clipped.

Every module's required input messages must be linked, or `InitializeSimulation` raises
`BasiliskError`: IMU (both modules) and CSS `scStateInMsg` / `stateInMsg` from
`scStateOutMsg`; albedo `spacecraftStateInMsg` from `scStateOutMsg` and `sunPositionInMsg`
from the SPICE Sun planet state; plus the per-sensor links below.

**IMU** (`imuSensor.ImuSensor`):
- bsk 2.11.1 seeds the gyroscope and accelerometer from one `RNGSeed`, so build two modules
  per Argus IMU: one with gyroscope noise only (stream `gyro`) and one with accelerometer
  noise only (stream `accel`), with the same `dcm_PB`, `sensorPos_B` and task.
- Profile fields map as the comments in `sensors.proto` state (`PMatrix*`, `setAMatrix*`,
  `setWalkBounds*`, `senRotBias`/`senTransBias`, `gyroScale`/`accelScale`,
  `senRotMax`/`senTransMax`, `setLSBs(accelerometer, gyroscope)`). Never call
  `setErrorBounds*`: it is the same Gauss-Markov bound. A walk bound of 0 leaves the walk
  unbounded, as the module default of −1 does.
- `sensors.Imu` builds the two modules. The channel a module does not report gets P = 0 and
  A = 0, so the gyroscope module's accelerometer and the accelerometer module's gyroscope are
  noise-free; the response takes `AngVelPlatform` from the gyroscope module and
  `AccelPlatform` from the accelerometer module.
- The first firing writes a zero-filled message at t = 0 (`NominalReady` is false until the
  module has run once); send it with the measurement unset, which Argus reports as
  Unavailable.
- `AccelPlatform` includes the lever-arm terms dω/dt × r + ω × (ω × r): in free fall it is
  zero at the centre of mass, or anywhere on a non-rotating body.

**Magnetometer** (`magnetometer.Magnetometer`):
- `dcm_SB`, `senNoiseStd = [0.0, 0.0, 0.0]`, `setAMatrix(np.zeros((3, 3)))`,
  `walkBounds = [0.0, 0.0, 0.0]`, `senBias`, `scaleFactor`, `maxOutput = +inf`,
  `minOutput = −inf`. The service adds the noise and clips to ±R (below).
- `stateInMsg` from `scStateOutMsg`; `magInMsg` from `MagneticFieldWMM.envOutMsgs[k]`, set up
  with `configureWMMFile(<path>/WMM2025.COF)`, `epochInMsg` from the run epoch,
  `planetPosInMsg` from the SPICE Earth, and `addSpacecraftToModel`. P0 uses WMM2025 only.
- The scenario builds one `MagneticFieldWMM` in the dynamics task, after eclipse, only when a
  magnetometer is configured, and asserts freshness on `envOutMsgs[0]` at every step. A kernel
  set without `WMM2025.COF` cannot serve a magnetometer: ConfigureRun returns
  FAILED_PRECONDITION before SPICE loads anything.
- The magnetometer measures on its first firing, at t = 0, since the field is written earlier in
  the same t_n: `tam_S = scale (C_SB C_BN B_N + noise + bias)`, clipped to ±range.
- `WMM2025.COF` is not in the `bsk` wheel (its `supportData` is pruned, and
  `dataFetcher.get_path` downloads it on first use). It is pinned like the kernels, as a
  `support_files` entry of `argus-2026-10-20` checked before the build, and never downloaded
  during ConfigureRun. NOAA ships it only inside `WMM2025COF.zip`, so the entry lists
  Basilisk v2.11.1's byte-identical copy (the file `dataFetcher` would download).
- **Noise is seeded in the service (BSK-9, #22).** bsk 2.11.1 seeds the module's private
  generator in its constructor with the default seed and ignores `RNGSeed` (checked: seeds
  1234 and 99999 give the default stream), so every run and every magnetometer would get the
  same noise. The choice was service-side noise rather than an upstream patch and a bsk pin
  bump: it keeps `bsk==2.11.1` and the protobuf ≤ 7.35.0 constraint, and stays in the backend
  (D10). The module runs noise-free and unclipped; `sensors.Magnetometer.measurement()` takes
  its `tam_S = scale (C_SB C_BN B_N + bias)` and returns
  `clip(tam_S + scale σ z_k, −R, +R)`, which is bsk's formula with σ moved out of the module.
  `z_k` is three standard normals from `numpy.random.default_rng([seed, k])`, with `seed` the
  §10 `mag` seed and `k = t / sample_period_ns` the sample index, so a sample depends only
  on its seed and index. The stream is fixed by the `numpy` pin in `uv.lock`.

**Light sensor** (`coarseSunSensor.CoarseSunSensor`):
- `fov`; `scaleFactor = g × 1361` so the output is W/m²; `senNoiseStd = σ / scaleFactor`
  with `setAMatrix([0.0])`; `senBias = b / scaleFactor`; `maxOutput = saturation`;
  `minOutput = 0`; `kellyFactor = 0`; `nHat_B = C_SB[2, :]` (never
  `setUnitDirectionVectorWithPerturbation`).
- `sunInMsg` from the SPICE Sun; `sunEclipseInMsg` from `eclipseOutMsgs[0]`; `albedoInMsg`
  from `albedo.albOutMsgs[i]`, configured with `addInstrumentConfig(fov, nHat_B, r_B)` and
  `addPlanetandAlbedoAverageModel(earth)`.
- Subscribe a reader to `cssDataOutMsg` at build time (§6).

## 10. Seeds

Every noisy module gets
`RNGSeed = int.from_bytes(sha256(f"{random_seed}:{sensor_id}:{stream}".encode()).digest()[:4], "little")`
with stream `gyro`, `accel`, `mag` or `css`. Never use Python `hash()` or the shared default
`0x1badcad1`. The `mag` seed is not given to bsk: it seeds the service-side magnetometer
noise (§9). Acceptance: changing one seed changes exactly that module's noise stream.

## 11. Error codes

| Code | When |
|---|---|
| INVALID_ARGUMENT | Malformed or invalid fields; an unset message field or oneof; a Step command for the wrong step, or a non-zero command in Step(0) |
| NOT_FOUND | No manifest for `kernel_set_id` |
| FAILED_PRECONDITION | Wrong lifecycle state, run ID or sequence; kernel missing, SHA-256 mismatch or failed load; a magnetometer with no `WMM2025.COF` in the kernel set; run FAILED |
| OUT_OF_RANGE | Epoch or t_n outside kernel coverage or WMM validity |
| UNIMPLEMENTED | A non-zero command after Step(0); a sensor kind not built yet; `real_time_factor > 0` before ClockSynch |
| RESOURCE_EXHAUSTED | A concurrent call |
| INTERNAL | A Basilisk failure, a non-finite output or a stale message |

Details name the field path and the bad value.

## 12. Build order

1. Spacecraft, SPICE and eclipse, with sensors UNIMPLEMENTED.
2. IMU.
3. Magnetometer and WMM.
4. Light sensors and albedo.
5. ClockSynch.
6. Actuators (G1).

## 13. Acceptance checks

- The quaternion checks in §4.
- Every Step n returns `sim_time_ns == n × fixed_step_ns`.
- 100 steps, Reset, 100 steps: bit-identical.
- For 2025-01-15T00:00Z with `argus-2026-09-25`, Step(0) Earth `J20002Pfix` and
  `J20002Pfix_dot` equal the t = 0 `rotation` and `rotation_rate` of
  `origin/spice-integration:Assets/StreamingAssets/Argus/Spice/foundation_one_orbit.json`
  to 1e-12, and Sun − Earth equals its `sun_position_j2000_m` to 1e-3 m. These are the
  inputs of Core `BasiliskStateMapperTests`.
- `dotnet run --project headless/Host/Argus.Simulation.Host.csproj` runs clean against the
  service.
