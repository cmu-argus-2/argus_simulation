# Argus.Basilisk: Basilisk simulation service

**Diagram block:** Basilisk service · **Process:** Python, outside Unity · **Status:** Basilisk-free handoff skeleton (every RPC returns UNIMPLEMENTED)

The Python gRPC service that runs the Basilisk simulation, with SPICE inside it through
`spiceInterface`. It is the authoritative dynamics backend and owns simulation time in every
Basilisk run, plus pacing in real-time runs (decisions D2, D3 and D10 in
[target-architecture.md](../docs/target-architecture.md)). Its only client is
`BasiliskEngine` in `headless/Host`, over the v1 schemas in
[`Argus.Contracts/`](../Argus.Contracts/README.md).

## 1. Ownership

- This folder holds the skeleton, the pinned kernel manifest and this brief. The
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

## 2. Install and run

```bash
python3.12 -m venv .venv
.venv/bin/pip install -e ".[basilisk]"
.venv/bin/python scripts/generate_protos.py
.venv/bin/python -m argus_basilisk --endpoint 127.0.0.1:50051
```

- The skeleton needs only `pip install -e .`; the `basilisk` extra adds `bsk==2.11.1`.
- The endpoint comes from `--endpoint`, then `ARGUS_BASILISK_ENDPOINT`, then
  `127.0.0.1:50051`. v1 is an insecure channel, so non-loopback hosts are refused.
- Generated code goes to `generated/` and kernel files to `kernels/`; both are gitignored.
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
   - sensor profiles as in `sensors.proto`, plus Basilisk 2.11.1 limits: an IMU channel
     cannot have both noise density and random walk > 0 (one Gauss-Markov state per axis),
     and a walk bound > 0 needs random walk > 0 (the same bound would clip white noise);
   - `noise_std_tesla > 0` is UNIMPLEMENTED: bsk 2.11.1 seeds the magnetometer's private
     noise generator in its constructor with the default seed and never re-seeds it, so
     every seed and every magnetometer would get the same stream (§9);
   - `epoch_utc` is inside the kernel set's `coverage_utc`, and inside WMM2025 validity
     (2025.0 to 2030.0) when a magnetometer is configured; otherwise OUT_OF_RANGE. This
     check must precede the build: `SpiceInterface::Reset` evaluates SPICE during
     `InitializeSimulation`, and CSPICE aborts the whole process outside coverage.
3. Verify the kernels (§8), build the simulation (§7 to §9) and call
   `InitializeSimulation()`. Run no step. A `BasiliskError` raised while loading kernels
   is FAILED_PRECONDITION.
4. Store the request, end READY, and fill every `ConfigureRunResponse` field; the client
   checks each echo.

### Reset

- Requires the stored `run_id`; allowed from FAILED.
- MUST construct a new `SimBaseClass` and new modules from the stored request, then re-arm
  pacing. Reusing modules is wrong in bsk 2.11.1: `ImuSensor::Reset` keeps `NominalReady`,
  `PreviousTime` and the previous state (the next t = 0 sample underflows a uint64 time
  difference), Gauss-Markov states carry over, and the magnetometer is never re-seeded.
- Acceptance: 100 steps, Reset, 100 steps again gives bit-identical responses.

### Step(n)

1. `run_id` matches and n equals the next sequence; otherwise FAILED_PRECONDITION naming
   the expected value.
2. `sim_time_ns == n × fixed_step_ns`; otherwise INVALID_ARGUMENT.
3. `command` has all three vectors and they are finite; otherwise INVALID_ARGUMENT. A
   non-zero command is UNIMPLEMENTED until actuators exist (G1).
4. `epoch + t_n` is inside kernel coverage (and WMM validity with a magnetometer);
   otherwise OUT_OF_RANGE, checked before executing.
5. Run to t_n: for n = 0, `ConfigureStopTime(0)` then `ExecuteSimulation()` (the t = 0
   pass); otherwise `ConfigureStopTime(n × fixed_step_ns)` then `ExecuteSimulation()`.
6. Only after running to t_n, write command n into the actuator input messages, so it acts
   over [t_n, t_n+1). Never write it before, or it would act one step early.
7. Read every returned message through the reader subscribed at build time (§6) and assert
   `isWritten()` and `timeWritten() == t_n`, and that all values are finite. A failure is
   INTERNAL and the run becomes FAILED.
8. Return the `StepResponse`; the next sequence is n + 1.

## 4. Run configuration to Basilisk

| Field | Basilisk target |
|---|---|
| `epoch_utc` | `spiceInterface.UTCCalInit`, formatted from seconds + nanos as `yyyy-MM-ddTHH:mm:ss.fffffffffZ` (NAIF `str2et` accepts the `Z`). The same epoch feeds the WMM `epochInMsg`, never the module default. |
| `initial_orbit` | Degrees to radians, `orbitalMotion.elem2rv(earth.mu, oe)`, then `hub.r_CN_NInit` / `v_CN_NInit`. Earth is `isCentralBody`. RAAN is from J2000 x. |
| `spacecraft` | `hub.mHub`; `hub.IHubPntBc_B` (row-major); `hub.sigma_BNInit = EP2MRP([w, x, y, z])`; `hub.omega_BN_BInit`; `hub.r_BcB_B = 0`. |
| `random_seed` | Module seeds (§10). |
| `kernel_set_id` | SPICE setup (§8). |
| `real_time_factor` | Pacing (§5). |

Quaternion rule: the Argus Hamilton active quaternion `a_to_b` (x, y, z, w) equals the
Basilisk Euler-parameter set `[w, x, y, z]` of a relative to b, and `EP2C` of it gives [ab].
Acceptance: `EP2C([√½, 0, 0, √½]) == [[0, 1, 0], [−1, 0, 0], [0, 0, 1]]` and
`EP2C([.5, .5, .5, .5])[2] == [1, 0, 0]`.

Time: `sim_time_ns` is `CurrentSimNanos`. Basilisk's SPICE time is ETInit + t, so Argus
stamps UTC as the epoch plus elapsed ephemeris-time seconds (no leap seconds; up to about
1.7 ms of periodic TDB error).

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
3. spacecraft dynamics;
4. eclipse, `MagneticFieldWMM` and albedo;
5. sensor tasks, one task `sensors_<ns>` per distinct sample period.

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
| `spacecraft_shadow_factor` | `eclipse.eclipseOutMsgs[0].shadowFactor` (1 sunlit, 0 umbra); always set |
| `sensor_samples` | One per sensor whose period divides `sim_time_ns`, with `sample_time_ns` from the subscribed reader's `timeWritten()`; measurement unset when the sensor ran without data |
| `pacing_overrun_count` | `clockSynch.clockOutMsg.overrunCounter`; 0 when unpaced |

## 8. SPICE and kernels

- Kernel manifests live in `kernel_sets/<id>.json`; the first is `argus-2026-09-25`. An ID
  is immutable: changing any file needs a new ID. `coverage_utc` bounds every run.
- Before loading, check that each file exists in `kernels/` and matches its SHA-256;
  otherwise FAILED_PRECONDITION. Never download during ConfigureRun. The fetch/verify tool
  (a port of `origin/spice-integration:Argus.Spice/generate_reference.py`) is SPICE-team
  work.
- Build `spiceInterface.SpiceInterface()` directly and call `addKernelPaths` with the
  manifest files in order. Never use the `createSpiceInterface` defaults, which load
  de430 through pooch. A failed kernel load raises `BasiliskError` from `bskLogger.bskError`
  during `InitializeSimulation`; map it to FAILED_PRECONDITION. The SHA-256 check stays,
  because it also catches a wrong file that loads fine.
- `addPlanetNames(["earth", "sun"])` in gravity-body order; `planetFrames = ["ITRF93", ""]`
  (the default `IAU_EARTH` is silently wrong, and a non-empty frame forces
  `computeOrient`); `referenceBase = "j2000"`; `zeroBase = "Earth"`. Echo the Earth frame in
  `spice_earth_frame`.
- Eclipse: `sunInMsg` from the SPICE Sun, `addPlanetToModel(earth)`,
  `addSpacecraftToModel(scStateOutMsg)`.

## 9. P0 sensors

Common: `C_SB = EP2C([w, x, y, z])` of `mount.sensor_to_body`; `mount.position_body_m` goes
to IMU `sensorPos_B`, CSS `r_B` and albedo `r_IB_B`. **Always set the A matrix
explicitly**: the Basilisk defaults are random walks. Outputs are scale × (true + noise +
bias), then quantised (IMU only), then clipped.

**IMU** (`imuSensor.ImuSensor`):
- bsk 2.11.1 seeds the gyroscope and accelerometer from one `RNGSeed`, so build two modules
  per Argus IMU: one with gyroscope noise only (stream `gyro`) and one with accelerometer
  noise only (stream `accel`), with the same `dcm_PB`, `sensorPos_B` and task.
- Profile fields map as the comments in `sensors.proto` state (`PMatrix*`, `setAMatrix*`,
  `setWalkBounds*`, `senRotBias`/`senTransBias`, `gyroScale`/`accelScale`,
  `senRotMax`/`senTransMax`, `setLSBs(accelerometer, gyroscope)`). Never call
  `setErrorBounds*`: it is the same Gauss-Markov bound.
- The first firing writes an empty message (`NominalReady` is false); send it with the
  measurement unset, which Argus reports as Unavailable.
- `AccelPlatform` includes the lever-arm terms dω/dt × r + ω × (ω × r), so it is zero in
  free fall only at the centre of mass without rotation.

**Magnetometer** (`magnetometer.Magnetometer`):
- `dcm_SB`, `senNoiseStd = [σ] * 3`, `setAMatrix(0)`, `walkBounds = 0`, `senBias`,
  `scaleFactor`, `maxOutput = +R`, `minOutput = −R`.
- `stateInMsg` from `scStateOutMsg`; `magInMsg` from `MagneticFieldWMM.envOutMsgs[k]`, set up
  with `configureWMMFile(<bsk supportData>/MagneticField/WMM2025.COF)`, `epochInMsg` from
  the run epoch, `planetPosInMsg` from the SPICE Earth, and `addSpacecraftToModel`. P0 uses
  WMM2025 only.
- Noise is not seedable in bsk 2.11.1 (§3). Until the team patches upstream or adds seeded
  noise in the service, ConfigureRun rejects `noise_std_tesla > 0`.

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
`0x1badcad1`. Acceptance: changing one seed changes exactly that module's noise stream.

## 11. Error codes

| Code | When |
|---|---|
| INVALID_ARGUMENT | Malformed or invalid fields; an unset message field or oneof |
| NOT_FOUND | No manifest for `kernel_set_id` |
| FAILED_PRECONDITION | Wrong lifecycle state, run ID or sequence; kernel missing, SHA-256 mismatch or failed load; run FAILED |
| OUT_OF_RANGE | Epoch or t_n outside kernel coverage or WMM validity |
| UNIMPLEMENTED | This skeleton; a non-zero command; a sensor kind or magnetometer noise not built yet; `real_time_factor > 0` before ClockSynch |
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
