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
cd /Users/sidqian/Downloads/Code/argus-visual

UNITY_EDITOR="/Applications/Unity/Hub/Editor/6000.6.0f1/Unity.app/Contents/MacOS/Unity"
"$UNITY_EDITOR" -projectPath "$PWD"
```

When Unity opens for the first time:

1. Wait for the Cesium package and other dependencies to finish importing.
2. Select **Argus Simulation > Create Foundation Scene**.
3. Press the **Play** button.

The generated scene is saved at `Assets/ArgusSimulation/Scenes/Foundation.unity`. On later runs, open that scene and press **Play**; it does not need to be regenerated.

NASA imagery defaults to `MODIS_Terra_CorrectedReflectance_TrueColor` for `2025-01-15`. To change it, select **Cesium World Terrain + NASA GIBS** in the Unity hierarchy and edit the layer or date in the Inspector.

## Game view controls

While the game is running, interact with the Game view:

- Left-drag to turn the camera.
- Middle-drag to pan the camera.
- Use the scroll wheel to zoom.
- Daytime imagery and cloud coverage remain visible, while NASA `VIIRS_Night_Lights` follows the anti-solar hemisphere.
