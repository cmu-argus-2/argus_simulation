# HOST-8 reference cases (#32)

Frozen run files for `Argus.Basilisk/scripts/check_reference_runs.py`, which checks the Basilisk path against
independent references. Their values don't follow P0 or DATA-1 updates: change a value only by adding a new case.
Run-file format changes, such as HOST-9's profile split or the removal of a device `source`, are applied here too.
`RunConfigurationFileTests` parses every file.

Common to all files:
- **Epoch:** 2026-06-01T12:00:00Z.
- **Kernel set:** `argus-2026-11-02`.
- **Seed:** 1.
- **Mass and inertia:** DATA-1 #67's 2U estimates (100 × 100 × 227 mm), which DATA-1 marks "estimates until
  Mechanical has numbers". The cases don't depend on their accuracy: a torque-free body is blind to mass and to the
  inertia's scale.
- **Mass:** 2.6 kg.
- **Orbit:** DATA-1's 580 km SSO, a = 6,958,137 m, e = 0, i = 97.75°, RAAN 234.06°, ω = 0 (#67, 4 Nov 2026), except
  where noted.
- **2U inertia:** diag(1.906e-2, 1.931e-2, 8.24e-3) kg·m², with +Y the unique major axis.
- **Duration:** the host takes it from `--duration-s`, and the checker's case table sets it.

| file | case | step | duration | what differs |
|---|---|---|---|---|
| `r1-orbit-dt0.1.json`, `r1-orbit-dt1.json` | R1 orbit | 0.1 s, 1 s | 86,400 s | no devices; identity attitude, zero rate |
| `r1e-eccentric.json` | R1e | 0.1 s | 7,200 s | e = 0.01, RAAN 251°, ω 37°, f 113°, so that anomaly and argument mix-ups show |
| `r2-spin.json` | R2 | 0.1 s | 600 s | start 40° about (1, −2, 0.5); 20.17°/s about +Y, an off-round rate so samples never land on whole half-turns. One IMU, `imu.ref`, with noise and resolution 0, mounted 25° about (0.3, −0.5, 0.8) at (0.03, 0, 0.04) m |
| `r2-long-q0-dt0.1.json`, `r2-long-q0-dt1.json` | R2-long | 0.1 s, 1 s | 86,400 s | R2's start, 20°/s about +Y; no devices |
| `r2-long-identity-dt0.1.json`, `r2-long-identity-dt1.json` | R2-long | 0.1 s, 1 s | 86,400 s | identity start, 20°/s about +Y; no devices |
| `r3-tumble.json` | R3 | 0.1 s | 600 s | inertia [[3, 0.2, −0.1], [0.2, 4, 0.15], [−0.1, 0.15, 2]] × 1e-3 kg·m²; ω0 = (5, 15, −10)°/s; R2's start |
| `r5-sun-pointing.json` | R5 | 0.1 s | 60 s | f = 155.7°, which puts the spacecraft 10.8° from the subsolar direction (the limit for the whole Earth to sit behind the sensor is 23.6°). One light sensor, `light.pz` with P0's parameters, mounted 35° about (1, 2, −0.5), so an ignored or inverted mount turns it 34° or 68° off the Sun. The start points its boresight (device +Z) at the Sun: q0 = q_s ⊗ q_mount*, where q_s is the smallest rotation from body +Z to the Sun's J2000 direction at the epoch (RA 69.058°, Dec 22.040°, from SPICE with `argus-2026-11-02`). Zero rate |

R5's start came from a one-off script that used SPICE and the checker's own elements-to-state code; the script isn't
part of the repo. To reproduce it, take the Sun's J2000 unit vector ŝ from Earth's centre at the epoch
(`spkpos("SUN", et, "J2000", "NONE", "EARTH")`). q_s is then the rotation by acos(ŝ_z) about +Z × ŝ, and the start is q_s ⊗ q_mount*. The values are frozen here.

Tolerances, and what each case measured on 8 Nov 2026, are in `docs/target-architecture.md`, "G4 results".
