# Argus Visual

Unity visualization for the CubeSat simulation. The current scene displays Cesium World Terrain with dated NASA GIBS true-color imagery and visible cloud coverage.

The long-term simulator is designed as a Unity-independent headless core with Unity as
an optional GUI and image renderer. See [the system architecture](docs/system-architecture.md)
for the flight-hardware, sensor, rendering, and Basilisk integration design.
See [code organization](docs/code-organization.md) for the class and folder map, and
[the target architecture](docs/target-architecture.md) for the agreed target design, decisions, and open gaps.

## Requirements

- Unity `6000.6.0f1`
- Internet access for Cesium ion and NASA GIBS
- A Cesium ion access token

## Configure the Cesium token

Copy `.env.example` to `.env` in this directory and fill in the token:

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
2. Open `Assets/ArgusSimulation/Scenes/Foundation.unity`. It is tracked in Git; **Argus Simulation > Create Foundation Scene** regenerates it and overwrites the tracked file.
3. Press the **Play** button.

The scene is at `Assets/ArgusSimulation/Scenes/Foundation.unity`. On later runs, open that scene and press **Play**; it does not need to be regenerated.

NASA imagery defaults to `VIIRS_SNPP_CorrectedReflectance_TrueColor` for `2025-06-21`, a date with daylight imagery up to the north pole. To change it, use the **SETTINGS** panel in the top toolbar, pass `-gt-date YYYY-MM-DD` and `-gt-layer LAYER` on the command line, or select **Cesium World Terrain + NASA GIBS** in the Unity hierarchy and edit the layer or date in the Inspector.

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
- Daytime imagery and cloud coverage remain visible, while NASA `VIIRS_Night_Lights` approximately follows the anti-solar side of the scene's fixed Sun light. The band is currently offset from the true night side and its part past the antimeridian does not draw; see the known issues in [the target architecture](docs/target-architecture.md#7-gaps).

Captured images and `navigation_metadata.jsonl` are written to:

```text
~/Library/Application Support/DefaultCompany/argus-visual/NavigationEpisodes/
```
