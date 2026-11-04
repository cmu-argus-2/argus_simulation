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
request's exact dimensions, registers that view with Cesium, and renders it once. It does not
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

## Tile readiness and lighting

The readiness check enumerates current enabled tilesets on every check, including ones created
or activated during the wait. No enabled tileset means ready. Defaults are 100% progress for
10 consecutive frames, then a 1-second settle, with a 60-second wall-clock loading timeout.
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
failure, step-loop gating, and PNG export through the legacy entry point. Real Cesium imagery
loading and the full-resolution four-camera workload still need a scene smoke test.
