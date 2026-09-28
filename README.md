# Argus Visual

Unity visualization for the CubeSat simulation. The current scene displays Cesium World Terrain with dated NASA GIBS true-color imagery and visible cloud coverage.

The long-term simulator is designed as a Unity-independent headless core with Unity as
an optional GUI and image renderer. See [the system architecture](docs/system-architecture.md)
for the agent, flight-hardware, sensor, rendering, and future Basilisk integration design.
See [code organization](docs/code-organization.md) for the class and folder map.

## Requirements

- Unity `6000.6.0f1`
- Internet access for Cesium ion and NASA GIBS
- A Cesium ion access token

## Configure the Cesium token

Open `.env` in this directory and fill in the token:

```dotenv
CESIUM_ION_ACCESS_TOKEN=your_token_here
CESIUM_ION_ASSET_ID=1
```

The `.env` file is ignored by Git.

## Run

From a terminal:

```bash
cd argus-visual

UNITY_EDITOR="/Applications/Unity/Hub/Editor/6000.6.0f1/Unity.app/Contents/MacOS/Unity"
"$UNITY_EDITOR" -projectPath "$PWD"
```

When Unity opens for the first time:

1. Wait for the Cesium package and other dependencies to finish importing.
2. Select **Argus Simulation > Create Foundation Scene**.
3. Press the **Play** button.

The generated scene is saved at `Assets/ArgusSimulation/Scenes/Foundation.unity`. On later runs, open that scene and press **Play**; it does not need to be regenerated.

NASA imagery defaults to `VIIRS_SNPP_CorrectedReflectance_TrueColor` for `2025-01-15`. To change it, select **Cesium World Terrain + NASA GIBS** in the Unity hierarchy and edit the layer or date in the Inspector.

## SPICE environment (Earth orientation, Sun, eclipse)

Earth orientation, the Sun direction, and eclipse status come from NASA/JPL SPICE data.
Python generates that data offline, and Unity only reads the resulting JSON file. The
default reference covers one orbit starting at `2025-01-15T00:00:00Z`. The ephemeris
provider returns no sample after 5,680 s instead of extrapolating.

| SPICE-derived | Current limitation |
|---|---|
| Earth orientation (J2000 → ITRF93), and therefore latitude/longitude and the ground track | Analytic circular-orbit dynamics omit drag and perturbations |
| Sun direction in the body frame and eclipse geometry | Magnetometer, GNSS, reaction-wheel, and star-tracker models are unavailable |
| Sunlit / penumbra / umbra status (conical Earth-shadow model) | Power, thermal, and communications models are unavailable |

The committed data file is `Assets/StreamingAssets/Argus/Spice/foundation_one_orbit.json`.
You only need Python to change or verify it (requires Homebrew `python3.12`):

```bash
Argus.Spice/regenerate.sh          # fetch pinned kernels, regenerate data and test fixture
Argus.Spice/regenerate.sh --check  # verify the committed files match
```

See [Argus.Spice/README.md](Argus.Spice/README.md) for kernels, scenarios, and
coverage. For the frames, time mapping, and shadow model, see
[the architecture document](docs/system-architecture.md).

To run the tests headless, close the editor first:

```bash
UNITY_EDITOR="/Applications/Unity/Hub/Editor/6000.6.0f1/Unity.app/Contents/MacOS/Unity"
"$UNITY_EDITOR" -batchmode -projectPath "$PWD" -runTests -testPlatform EditMode -testResults editmode.xml
"$UNITY_EDITOR" -batchmode -projectPath "$PWD" -runTests -testPlatform PlayMode -testResults playmode.xml
```

To compare with the old simplified Earth rotation, clear **Use Spice Ephemeris** on
**Simulation → Analytic Orbit State Source**.

## Simulator controls

- Left-drag to turn the globe, middle-drag to pan, and scroll to zoom.
- Use the top toolbar to pause, reset, or change simulation speed.
- Images are not exported automatically. Press **CAPTURE** in the top toolbar to pause
  the simulation, wait for Cesium to finish the current view, and save one synchronized
  set of camera images. A timed-out load is cancelled instead of exporting incomplete imagery.
- Use the analytic-state panel to change orbit phase or altitude and apply pitch, yaw, or roll offsets while watching the four side-camera feeds.
- **RESET POSE** restores the initial analytic orbit position and removes all attitude offsets.
- Toggle telemetry groups from the right-side sensor settings panel.
- The lower camera strip shows the four body-mounted +X, -X, +Y, and -Y cameras plus a virtual north-up nadir ground-truth feed. The GT camera follows orbital position but ignores CubeSat attitude changes.
- Daytime imagery and cloud coverage remain visible, while NASA `VIIRS_Night_Lights` follows the anti-solar hemisphere.

Captured images and `navigation_metadata.jsonl` are written to:

```text
~/Library/Application Support/DefaultCompany/argus-visual/NavigationEpisodes/
```
