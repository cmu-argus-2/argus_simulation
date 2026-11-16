# Argus Visual

Unity visualization for the CubeSat simulation. The current scene displays Cesium World Terrain with dated NASA GIBS true-color imagery and visible cloud coverage.

The long-term simulator is designed as a Unity-independent headless core with Unity as
an optional GUI and image renderer. See [the system architecture](docs/system-architecture.md)
for the flight-hardware, sensor, rendering, and Basilisk integration design.
See [code organization](docs/code-organization.md) for the class and folder map, and
[the target architecture](docs/target-architecture.md) for the agreed target design, decisions, and open gaps.

## Components

The repository has three parts. Each has its own toolchain, and you only need the ones you
work on:

| Part | Folder | Toolchain | What it is |
|---|---|---|---|
| Unity visualization | `Assets/`, `ProjectSettings/` | Unity `6000.6.0f1`, a Cesium ion token, internet access for Cesium ion and NASA GIBS | The GUI and image renderer |
| Headless core and host | `headless/` | .NET SDK 8 or newer | The Unity-independent core, its EditMode tests, and the simulation host |
| Basilisk service | `Argus.Basilisk/` | [uv](https://docs.astral.sh/uv/getting-started/installation/) 0.13.4 or later; it installs Python 3.12 itself | The Python gRPC service that runs Basilisk and SPICE |

`Argus.Basilisk/` has the repository's only Python environment: `Argus.Basilisk/.venv`, built
from the committed lock file. The optional imagery-pack script in `Argus.Imagery/` uses only
the Python 3 standard library and needs no environment. The .NET and Unity parts don't use
Python.

## Set up and run the Basilisk service

Supported platforms are the ones with a `bsk==2.12.2` wheel: macOS on Apple silicon, Linux
x86_64 and aarch64, and Windows x86_64. Intel Macs aren't supported.

From `Argus.Basilisk/`:

```bash
uv sync --frozen --all-extras
uv run --frozen python scripts/generate_protos.py
uv run --frozen python scripts/fetch_kernels.py argus-2026-11-02
uv run --frozen python -m argus_basilisk --endpoint 127.0.0.1:50051
```

1. `uv sync --frozen --all-extras` creates `.venv` with exactly the versions in `uv.lock`, the
   same on every machine. Without uv, use the hashed `requirements.txt` in a Python 3.12 venv
   instead (see [Argus.Basilisk/README.md §2](Argus.Basilisk/README.md#2-install-and-run)).
2. `generate_protos.py` writes the gRPC code from `Argus.Contracts/` into `generated/`.
3. `fetch_kernels.py` downloads the P0 kernel set's SPICE kernels, `WMM2025.COF` and the
   CERES albedo map into `kernels/`, and checks their SHA-256 and coverage. A run's epoch must
   lie inside the set's coverage: `argus-2026-11-02` ends on 2026-08-06, its Earth PCK's last
   measured day, so a later `epochUtc` is refused with OUT_OF_RANGE until a newer kernel set is
   published ([Argus.Basilisk/README.md §8](Argus.Basilisk/README.md#8-spice-and-kernels)).
4. The last command serves on loopback until Ctrl+C.

Run the service tests with `uv run --frozen python -m pytest -q`. Tests that need the real
kernels are skipped until `kernels/` is filled.

The service is built in steps ([Argus.Basilisk/README.md §12](Argus.Basilisk/README.md#12-build-order)).
It runs the full P0 sensor set (IMU, magnetometer and light sensors), so the host's P0 run below
runs end to end, including magnetometer and light-sensor noise.

## Build and test the headless core

From the repository root:

```bash
dotnet test headless/Tests/Argus.Simulation.Tests.EditMode.Headless.csproj
dotnet run --project headless/Host/Argus.Simulation.Host.csproj -- --duration-s 60 --basilisk-endpoint 127.0.0.1:50051
```

The first command needs only the .NET SDK. The second runs the host against a Basilisk
service that is already serving, with the P0 run file `headless/Host/runs/p0-default.json`.
To keep the run, add `--record <dir>`:

```bash
dotnet run --project headless/Host/Argus.Simulation.Host.csproj -- --duration-s 600 --basilisk-endpoint 127.0.0.1:50051 --record runs-out
```

It writes dataset v1 to `runs-out/<run_id>_0/`: `manifest.json` (the configuration and its
devices), then `states.jsonl` (truth), `sensors.jsonl` (every sensor frame) and
`commands.jsonl` (every command), one JSON object per line. The fields are described in
[Core/Recording/README.md](Assets/ArgusSimulation/Core/Recording/README.md). See
[headless/README.md](headless/README.md) for the other options, the exit codes and the run-file
format.

## Run the Unity visualization

Requirements: Unity `6000.6.0f1`, internet access for Cesium ion and NASA GIBS, and a Cesium
ion access token.

### Configure the Cesium token

Copy `.env.example` to `.env` in this directory and fill in the token:

```dotenv
CESIUM_ION_ACCESS_TOKEN=your_token_here
CESIUM_ION_ASSET_ID=1
```

The `.env` file is ignored by Git.

### Open the project

Unity's state-stream client needs its gRPC plugin, which is generated and never committed.
Build it once after cloning, and again when the contracts change (it needs the .NET SDK):

```bash
dotnet build Argus.Contracts/Unity/Argus.Stream.Contracts.csproj
```

It writes `Assets/ArgusSimulation/Plugins/Stream/` (git-ignored). Without it, Unity reports
missing `Grpc`, `Google.Protobuf` and `Argus.Contracts` namespaces.

From a terminal:

```bash
cd argus-visual

UNITY_EDITOR="/Applications/Unity/Hub/Editor/6000.6.0f1/Unity.app/Contents/MacOS/Unity"
"$UNITY_EDITOR" -projectPath "$PWD"
```

When Unity opens for the first time:

1. Wait for the Cesium package and other dependencies to finish importing.
2. Open `Assets/ArgusSimulation/Scenes/Foundation.unity`. It is tracked in Git; **Argus Simulation > Create Foundation Scene** regenerates it and overwrites the tracked file.
3. Start the simulation: the Basilisk service and `headless/Host`, as in
   [headless/README.md](headless/README.md). The host's run file is the only simulation; Unity
   runs none of its own and follows the host's state stream on `127.0.0.1:50052`.
4. Press the **Play** button. The toolbar shows **FOLLOWING HOST** once states arrive, or
   **CONNECTING TO HOST** / **HOST DISCONNECTED** while the host is not streaming; Unity keeps
   retrying, so the host can start before or after Play.

The scene is at `Assets/ArgusSimulation/Scenes/Foundation.unity`. On later runs, open that scene and press **Play**; it does not need to be regenerated.

NASA imagery defaults to `VIIRS_SNPP_CorrectedReflectance_TrueColor` for `2025-06-21`, a date with daylight imagery up to the north pole. To change it, use the **SETTINGS** panel in the top toolbar, pass `-gt-date YYYY-MM-DD` and `-gt-layer LAYER` on the command line, or select **Cesium World Terrain + NASA GIBS** in the Unity hierarchy and edit the layer or date in the Inspector.

For tests and dataset generation you can optionally download the full-resolution imagery once
(~31 GB per date as PNG, ~5 GB as JPEG) and have Unity read it from disk instead of streaming.
See [Argus.Imagery/README.md](Argus.Imagery/README.md).

### Simulator controls

- Left-drag to turn the globe, middle-drag to pan, and scroll to zoom.
- The host owns the run, so Unity has no pause, reset, speed, orbit or attitude controls: change
  the run in the host's run file. The stream panel under the toolbar shows the endpoint, the
  stream's status and how fast the run advances (×1 when paced in real time). The CubeSat is
  interpolated between the streamed states, and the orbit trail draws the last orbit or so flown
  (one point per 10 simulated seconds).
- Images are not exported automatically. Press **CAPTURE** in the top toolbar to hold the CubeSat
  at the latest state, wait for Cesium to finish that view, and save one synchronized set of
  camera images; the host keeps running meanwhile. A timed-out load is cancelled instead of
  exporting incomplete imagery.
- Toggle telemetry groups from the right-side sensor settings panel.
- The lower camera strip shows the four body-mounted +X, -X, +Y, and -Y cameras plus a virtual north-up nadir ground-truth feed. The GT camera follows orbital position but ignores CubeSat attitude changes.
- The nadir GT feed loads first. When a run starts or Unity reconnects, the toolbar shows
  **LOADING NADIR** while Cesium loads only the nadir view (up to 30 s); the run is never held.
  A hidden look-ahead camera preloads imagery about 4 wall-clock seconds ahead of the nadir camera
  at the rate the host's run advances.
- Daytime imagery and cloud coverage remain visible, while NASA `VIIRS_Night_Lights` follows the anti-solar side of the scene's fixed Sun light, including the part past the antimeridian. The Sun itself is still fixed; see the known issues in [the target architecture](docs/target-architecture.md#7-gaps).

Captured images and `navigation_metadata.jsonl` are written to:

```text
~/Library/Application Support/DefaultCompany/argus-visual/NavigationEpisodes/
```
