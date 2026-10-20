# Four-overlay Cesium tileset material

UNITY-2 (#55) extends Cesium for Unity 1.25.1’s default shader and material with a fourth `CesiumRasterOverlay` slot, preserving the original requests and keys: Blue Marble (`0`), dated VIIRS imagery (`1`), and west/east night lights (`2`/`3`). The project-owned copies retain Cesium’s blending and clipping; the Apache-2.0 license is included in `Cesium-LICENSE.txt`. `FoundationSceneBuilder` assigns the material as `opaqueMaterial`; rebuild via **Argus Simulation > Create Foundation Scene** after changes. When upgrading Cesium, compare against the upstream defaults and rerun the GPU check.

## Validation

Run **Argus Simulation > Validate Four Overlay Material** in the Unity editor.
It checks the three required properties for each key, then renders local textures
in a temporary preview scene: a red base must turn green when slot `3` is applied.
It closes the preview scene and destroys temporary textures/materials afterward.
It needs a graphics device and does not depend on Cesium ion or NASA requests.

This offline check is separate from #55's geographic acceptance: with the fixed
Sun, capture actual city lights in the east night rectangle (about 180°–145°W)
in the PR. A successful material check does not establish successful GIBS downloads.
