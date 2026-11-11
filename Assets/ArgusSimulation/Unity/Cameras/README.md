# Unity cameras

`UnityImageRenderer` implements Core `IImageRenderer`. A request is an exposure at a fixed
Earth-fixed pose, with pixel intrinsics, capture time and optional Sun position. `RenderAsync`
queues it and returns a task; `Update` processes requests serially on Unity's main thread.
The Foundation scene wires the renderer to its rig, georeference, directional Sun light and
GIBS controller. Other callers can use `Configure` or resolve these scene components at runtime.

## Capture and display

Dashboard feeds remain live **display previews**, at 768×432 by default. This preserves the
mission-control UI while Core exposures become on-request. `livePreviewEnabled` can disable
automatic preview rendering. Preview FOV defaults to the IMX708 calibration (fy=3548 at
2592 pixels high); these preview textures are not recorded Core exposures.

The rig owns a separate, disabled request camera. The renderer allocates a target at the
request's exact dimensions, registers that view with Cesium, and renders it once.
Only the exposure view participates in Cesium tile selection during capture; dashboard,
observer and look-ahead views are restored afterwards. It does not
move a preview camera or replace a preview target. The request camera is unregistered and its
target released on success, failure, cancellation or renderer disable.

Cesium's tile-selection FOV conservatively encloses the calibrated off-centre projection;
rendering itself uses the exact projection matrix. Cesium 1.25.1 reads these selection fields
in its [CameraManager](https://github.com/CesiumGS/cesium-unity/blob/v1.25.1/native~/src/Runtime/CameraManager.cpp).

Camera coordinates follow OpenCV: x right, y down, z out of the lens. `CameraToEcef` rotates
those axes to Earth-fixed; the renderer converts forward and negative-y through the Cesium
georeference to Unity forward/up. The projection uses fx, fy, cx and cy independently, including
the half-pixel boundary for top-left pixel centre (0,0). Raw `Rgb8` output is top-to-bottom,
left-to-right. `ImageFrame.Request` preserves the input calibration, pose and exposure metadata.
Exposure duration is metadata; this renderer does not integrate motion blur over that interval.

For exposure cameras more than 100 km from Unity's world origin, the synchronous render/readback
temporarily rebases the Cesium georeference at the requested ECEF position. This avoids float
view/projection singularities (`Screen position out of view frustum`) for distant orbital views.
The previous origin, its authority/placement, and display-light rotation are restored in `finally`;
the `ImageFrame` retains the original ECEF request throughout.

## Tile readiness and lighting

The readiness check enumerates current enabled tilesets on every check, including ones created
or activated during the wait. No enabled tileset means ready. Defaults are 100% progress for
10 consecutive frames, then a 1-second settle. Loading times out after 60 seconds without a
progress change of at least 0.5 percentage points, or a 240-second total deadline. Progress can
drop when Cesium discovers finer tiles; that change also restarts the inactivity timer. The total
deadline still bounds refinement churn. This lets large captures finish while they are making
progress, rather than aborting them at 60 seconds regardless of progress.
It yields Unity frames throughout; it never blocks the main thread waiting for tiles.
`NadirTilePriority` uses the same progress helper and refreshes its look-ahead render target
from the nadir preview each frame.

A request with a Sun position temporarily selects its night-side direction while loading the
view, then sets the directional light for the actual render and restores its prior rotation.
The capture override is separate from UNITY-3's display-light input; clearing it preserves that
input. A request without a Sun uses existing scene lighting and keeps a null Sun in its metadata.
A missing directional light for a Sun-lit request, timeout, or render error faults the task;
the Core camera model decides how to publish `Unavailable`. The renderer never substitutes a
black image for an error. Naturally dark rendered pixels are still valid pixels.

## Waiting and lifecycle

The Foundation analytic `SimulationRunner` is configured with this renderer. While a render is
queued or running, `StepOnce` returns false and `Update` yields without accumulating wall time.
After render tasks finish, the next step can call the Core sensors and publish their deferred
frames. This waits on renderer work (`IsRendering`), never on `CameraModel.PendingExposureCount`.
Follower streams have their own upstream clock; this local gate cannot pause a remote Host.

Consumers must await the task or yield frames; do not call `.Wait()` or `.Result` on Unity's
main thread. Cancellation is observed on the next pump frame. Disabling the component cancels
active and queued tasks. Ending a run must cancel its requests through their tokens; this does
not add Core's missing end/reset drain path (CORE-21 #119).

`NavigationEpisodeExporter` temporarily uses the same renderer and PNG conversion until UNITY-9
retires it. Tile-wait and pixel readback live here in one place. Readback currently uses Unity
`ReadPixels` after `Camera.Render`; it synchronizes the GPU at capture time. It is not a promise
of asynchronous GPU readback throughput or a full Host-to-Unity rendering RPC.

## Verification

Run PlayMode tests with a GPU (no `-nographics`). The renderer tests use a small synthetic scene,
not remote Cesium terrain. They verify off-centre intrinsics and image orientation with coloured
markers, capture metadata, light restoration, cancellation/disable cleanup, task ordering after
failure, step-loop gating, and PNG export through the legacy entry point. The separate opt-in scene smoke test covers real Cesium imagery loading and the
full-resolution four-camera workload; synthetic tests alone do not establish that result.

## Manual scene acceptance

In Play Mode, the dashboard CAPTURE button saves all five preview-sized views. It keeps its
CAPTURE label while busy; the adjacent status shows the camera number, loading percentage and
elapsed seconds. Completion and timeouts are also logged in the Console. Outputs are PNGs plus
`navigation_metadata.jsonl` under `Application.persistentDataPath/NavigationEpisodes`.

The editor menu **Argus Simulation > Cameras** also provides Capture Preview, Capture Full
Resolution (4 Body Cameras), Cancel Capture and Log Capture Status. The full-resolution command
sends four native 4608×2592 requests (DATA-6 v0 fx=fy=3548, cx=2304, cy=1296), without enlarging
the preview targets. This manual snapshot keeps exposure duration at zero; it is not the
configured sensor sampling/noise/mount integration owned by CORE-6.

Each new JSONL record has `metadata_version: 2`. At capture start the exporter snapshots the
rendered spacecraft pose and every camera request before waiting for tiles. Manual phase,
altitude, pitch, yaw, roll, camera mount and preview-FOV changes made before Capture are included.
Edits made while capture is loading affect subsequent captures; all views in the current capture
retain their original snapshot. Full-resolution capture uses its documented native calibration.

`position_ecef_m` and `rotation_body_to_ecef_xyzw` describe that rendered body pose, including manual
edits. `source_position_ecef_m`, `source_rotation_body_to_ecef_xyzw` and
`source_angular_velocity_body_truth_rad_per_s` retain the engine state. ECEF velocity remains the
engine velocity; `angular_velocity_body_truth_rad_per_s` expresses the engine's angular velocity
in the rendered body axes. Manual pose edits are instantaneous and do not simulate an actuator or
an angular impulse. Their physical integration remains G1.

`camera_position_ecef_m`, `camera_to_ecef_xyzw`, `camera_to_body_xyzw`, intrinsics, time,
`exposure_duration_s`, `renderer`, `has_sun_position` and `sun_position_ecef_m` describe the actual
render request returned with the pixels. Camera quaternions use OpenCV axes (right, down,
forward); the legacy `rotation_camera_to_body_unity_xyzw` retains Unity's camera convention.
When `has_sun_position` is false the scene provides the lighting. Older records without
`metadata_version` lack these pose snapshots and may omit manual attitude offsets.

`UnityImageRendererCesiumSmokeTests` is opt-in. Set `ARGUS_RUN_CESIUM_SMOKE=1` and select its
`FoundationSavesPreviewAndFourFullResolutionBodyImages` test to exercise the actual Foundation
scene, existing Cesium credentials, preview export, then four full-size exports. It retains
images and metadata for inspection. Normal offline test runs skip this network-dependent test.
The opt-in test requires enabled terrain and checks Earth-facing PNG content as well as file
counts and sizes. It sets a 600-second per-view total loading budget for uncached imagery;
the normal renderer default is 240 seconds, with the same 60-second inactivity limit.

## Local acceptance record — 2026-11-04

On macOS with Unity 6000.6.0f1 and Metal: 14 offline PlayMode tests passed; the real-Cesium
opt-in scene test passed separately in 266 seconds. The scene run saved five 768×432 images,
four native 4608×2592 body images and nine matching metadata records. PNG decoding, dimensions,
unique paths and full-size intrinsics were checked, and Earth-facing images were inspected.
This validates the renderer and manual capture path; it does not validate a remote Host render
RPC or CORE-6's configured sensor/noise integration. Network and uncached full-size tile loading
can take minutes; this is not a real-time throughput result.

## Local follow-up acceptance — 2026-11-11

The metadata-v2 and orbital-projection fixes passed 79 EditMode tests and 19 offline PlayMode
tests on Unity 6000.6.0f1 / Metal. The opt-in network scene test was skipped in that final run.
The added regressions cover manual orbit/attitude/mount/FOV edits, frozen per-camera metadata,
fresh captures after reset, and Dashboard pause followed by preview plus native-resolution
capture with identical state sequence and exposure time across all nine records.

The recorded five-camera reset pose reproduced Unity's frustum error before the precision fix;
the same replay now passes with the georeference and display light restored. A separate
far-origin red/green marker test verifies actual pixel projection after rebasing. The user's
five manual captures produced 24 readable PNGs with matching metadata, including 350 km and
reset-to-500 km states; these files predate the projection fix. The final code was not subjected
to another full network/Cesium capture run.
