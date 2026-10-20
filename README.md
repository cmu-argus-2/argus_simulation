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

`Argus.Basilisk/` holds the repository's only Python code, so it has the only Python
environment: `Argus.Basilisk/.venv`, built from the committed lock file. The .NET and Unity
parts don't use Python.

## Set up and run the Basilisk service

Supported platforms are the ones with a `bsk==2.11.1` wheel: macOS on Apple silicon, Linux
x86_64 and aarch64, and Windows x86_64. Intel Macs aren't supported.

From `Argus.Basilisk/`:

```bash
uv sync --frozen --all-extras
uv run --frozen python scripts/generate_protos.py
uv run --frozen python scripts/fetch_kernels.py argus-2026-09-25
uv run --frozen python -m argus_basilisk --endpoint 127.0.0.1:50051
```

1. `uv sync --frozen --all-extras` creates `.venv` with exactly the versions in `uv.lock`, the
   same on every machine. Without uv, use the hashed `requirements.txt` in a Python 3.12 venv
   instead (see [Argus.Basilisk/README.md §2](Argus.Basilisk/README.md#2-install-and-run)).
2. `generate_protos.py` writes the gRPC code from `Argus.Contracts/` into `generated/`.
3. `fetch_kernels.py` downloads the pinned SPICE kernels into `kernels/` and checks their
   SHA-256. NAIF has since deleted the set's Earth PCK, so until a new kernel set is published
   (#13) that file has to be copied into `kernels/` by hand from someone who has it.
4. The last command serves on loopback until Ctrl+C.

Run the service tests with `uv run --frozen python -m pytest -q`. Tests that need the real
kernels are skipped until `kernels/` is filled.

The service is built in steps ([Argus.Basilisk/README.md §12](Argus.Basilisk/README.md#12-build-order)).
Until the sensors exist, a run that configures sensors, such as the host's P0 run below, is
rejected with UNIMPLEMENTED.

## Build and test the headless core

From the repository root:

```bash
dotnet test headless/Tests/Argus.Simulation.Tests.EditMode.Headless.csproj
dotnet run --project headless/Host/Argus.Simulation.Host.csproj -- --duration-s 60 --basilisk-endpoint 127.0.0.1:50051
```

The first command needs only the .NET SDK. The second runs the host against a Basilisk
service that is already serving. See [headless/README.md](headless/README.md) for the options
and exit codes.

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

From a terminal:

```bash
cd argus-visual

UNITY_EDITOR="/Applications/Unity/Hub/Editor/6000.6.0f1/Unity.app/Contents/MacOS/Unity"
"$UNITY_EDITOR" -projectPath "$PWD"
```

When Unity opens for the first time:

1. Wait for the Cesium package and other dependencies to finish importing.
2. Open `Assets/ArgusSimulation/Scenes/Foundation.unity`. It is tracked in Git; **Argus Simulation > Create Foundation Scene** regenerates it and overwrites the tracked file.
3. Press the **Play** button.

The scene is at `Assets/ArgusSimulation/Scenes/Foundation.unity`. On later runs, open that scene and press **Play**; it does not need to be regenerated.

NASA imagery defaults to `VIIRS_SNPP_CorrectedReflectance_TrueColor` for `2025-06-21`, a date with daylight imagery up to the north pole. To change it, use the **SETTINGS** panel in the top toolbar, pass `-gt-date YYYY-MM-DD` and `-gt-layer LAYER` on the command line, or select **Cesium World Terrain + NASA GIBS** in the Unity hierarchy and edit the layer or date in the Inspector.

### Simulator controls

- Left-drag to turn the globe, middle-drag to pan, and scroll to zoom.
- Use the top toolbar to pause, reset, or change simulation speed.
- Images are not exported automatically. Press **CAPTURE** in the top toolbar to pause
  the simulation, wait for Cesium to finish the current view, and save one synchronized
  set of camera images. A timed-out load is cancelled instead of exporting incomplete imagery.
- Use the analytic-state panel to change orbit phase or altitude and apply pitch, yaw, or roll offsets while watching the four side-camera feeds.
- **RESET POSE** restores the initial analytic orbit position and removes all attitude offsets.
- Toggle telemetry groups from the right-side sensor settings panel.
- The lower camera strip shows the four body-mounted +X, -X, +Y, and -Y cameras plus a virtual north-up nadir ground-truth feed. The GT camera follows orbital position but ignores CubeSat attitude changes.
- Daytime imagery and cloud coverage remain visible, while NASA `VIIRS_Night_Lights` approximately follows the anti-solar side of the scene's fixed Sun light. The band is currently offset from the true night side and its part past the antimeridian does not draw; see the known issues in [the target architecture](docs/target-architecture.md#7-gaps).

Captured images and `navigation_metadata.jsonl` are written to:

```text
~/Library/Application Support/DefaultCompany/argus-visual/NavigationEpisodes/
```
