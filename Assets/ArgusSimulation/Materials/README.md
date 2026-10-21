# Four-overlay Cesium tileset material

UNITY-2 (#55) extends Cesium for Unity 1.25.1’s default shader and material with a fourth `CesiumRasterOverlay` slot, preserving the original requests and keys: Blue Marble (`0`), dated VIIRS imagery (`1`), and west/east night lights (`2`/`3`). The project-owned copies retain Cesium’s blending and clipping; the Apache-2.0 license is included in `Cesium-LICENSE.txt`. `FoundationSceneBuilder` assigns the material as `opaqueMaterial`; rebuild via **Argus Simulation > Create Foundation Scene** after changes. When upgrading Cesium, compare against the upstream defaults and rerun the GPU check.

## Reviewing the changes

The shader graph and material are modified copies of Cesium for Unity **1.25.1**'s
`Source/Runtime/Resources/CesiumDefaultTilesetShader.shadergraph` and
`CesiumDefaultTilesetMaterial.mat`. Argus adds overlay slot `3` to the graph and
material; the installed package is unchanged. The complete upstream license is
retained in `Cesium-LICENSE.txt`.

GitHub collapses these upstream-derived resources by default; they remain
expandable. From the repository root, run `python3 tools/review_four_overlay.py`
after Unity resolves its packages. This verifies the upstream graph checksum,
compares parsed objects rather than JSON formatting, prints changed fields and
added object IDs, and shows the short material diff. The current delta preserves
all 450 upstream objects: 448 are unchanged, two update graph/category lists,
and 15 objects are added. The color chain changes from slot `2` → Base Color to
slot `2` → slot `3` → Base Color, with three new slot properties and their inputs.

Review the script's delta alongside `FoundationSceneBuilder.cs`,
`NasaGibsRasterController.cs`, and `FourOverlayMaterialValidation.cs`. The saved
scene must reference the new material and the Sunlight object; rebuilding it also
changes serialized object IDs. This source comparison complements the GPU check
and geographic screenshots; it does not replace either.

## Validation

Run **Argus Simulation > Validate Four Overlay Material** in the Unity editor.
It checks the three required properties for each key, then renders local textures
in a temporary preview scene: a red base must turn green when slot `3` is applied.
It closes the preview scene and destroys temporary textures/materials afterward.
It needs a graphics device and does not depend on Cesium ion or NASA requests.

This offline check is separate from #55's geographic acceptance: with the fixed
Sun, capture actual city lights in the east night rectangle (about 180°–165°W)
in the PR. A successful material check does not establish successful GIBS downloads.
