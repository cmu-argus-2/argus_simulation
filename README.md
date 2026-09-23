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

## Simulator controls

- Left-drag to turn the globe, middle-drag to pan, and scroll to zoom.
- Use the top toolbar to pause, reset, or change simulation speed.
- Images are not exported automatically. Press **CAPTURE** in the top toolbar to pause
  the simulation, wait for Cesium to finish the current view, and save one synchronized
  set of camera images. A timed-out load is cancelled instead of exporting incomplete imagery.
- Use the mock pose panel to change orbit phase or altitude and apply pitch, yaw, or roll offsets while watching the four side-camera feeds.
- **RESET POSE** restores the initial mock orbit position and removes all attitude offsets.
- Toggle telemetry groups from the right-side sensor settings panel.
- The lower camera strip shows the four body-mounted +X, -X, +Y, and -Y cameras plus a virtual north-up nadir ground-truth feed. The GT camera follows orbital position but ignores CubeSat attitude changes.
- Daytime imagery and cloud coverage remain visible, while NASA `VIIRS_Night_Lights` follows the anti-solar hemisphere.

Captured images and `navigation_metadata.jsonl` are written to:

```text
~/Library/Application Support/DefaultCompany/argus-visual/NavigationEpisodes/
```
