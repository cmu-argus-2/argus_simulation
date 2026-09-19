# Argus Visual

Unity visualization for the CubeSat simulation. The current scene displays Cesium World Terrain with dated NASA GIBS true-color imagery and visible cloud coverage.

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
- Use the mock pose panel to change orbit phase or altitude and apply pitch, yaw, or roll offsets while watching the four side-camera feeds.
- **RESET POSE** restores the initial mock orbit position and removes all attitude offsets.
- Toggle telemetry groups from the right-side sensor settings panel.
- The four lower feeds show the outward-facing cameras mounted on the 1U CubeSat's +X, -X, +Y, and -Y side faces.
- Daytime imagery and cloud coverage remain visible, while NASA `VIIRS_Night_Lights` follows the anti-solar hemisphere.
