# Unity cameras

`UnityImageRenderer` implements Core `IImageRenderer`. A request is an exposure at a fixed
Earth-fixed pose, with pixel intrinsics, capture time and optional Sun position. `RenderAsync`
queues it and returns a task; `Update` processes requests serially on Unity's main thread.
The Foundation scene wires the renderer to its rig, georeference, directional Sun light and
GIBS controller. Other callers can use `Configure` or resolve these scene components at runtime.

## Capture and display

Dashboard feeds remain live **display previews**, at 768×432 by default. This preserves the
mission-control UI while Core exposures become on-request. `livePreviewEnabled` can disable
automatic preview rendering. Body preview FOV and full-resolution calibration come from Core's
`CameraKind.Profile` in the host run configuration, not duplicated IMX708 constants.

The rig owns a separate, disabled request camera. The renderer allocates a target at the
request's exact dimensions, adds the exposure view to Cesium, and renders it once. Dashboard,
observer, nadir and look-ahead views participate in tile selection except for bounded readiness
probes. Cesium exposes only combined progress, so the renderer selects only the exposure camera
for **one frame per two frames** (configurable, minimum interval two frames), samples its progress,
then immediately restores all views for the remaining frames. The request view stays registered
throughout. Other views continue rendering; their tile-selection interruption is at most one
frame per probe, rather than the whole capture. This prevents unrelated views from blocking an
otherwise ready exposure. The request
camera is unregistered and its target released on every exit path. A capture starting during
nadir warm-up immediately restores the temporarily deferred dashboard views.

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

The readiness check finds tilesets once at the start of each wait and skips destroyed or disabled
entries. Tilesets created after that snapshot join the next capture. No enabled tileset means ready. Defaults are 100% progress for
10 consecutive frames, then a 1-second settle. Loading times out after 60 seconds without a
progress change of at least 0.5 percentage points, or a 1200-second total deadline. Progress can
drop when Cesium discovers finer tiles; that change also restarts the inactivity timer. The total
deadline still bounds refinement churn. This lets large captures finish while they are making
progress, rather than aborting them at 60 seconds regardless of progress.
It yields Unity frames throughout; it never blocks the main thread waiting for tiles.
`NadirTilePriority` uses the same progress helper and retains its own cached tileset list.

Each real-terrain readiness probe restores the view list and main/scene flags before yielding
another live-view frame, including on timeout, cancellation and disable. No-tileset synthetic
scenes do not need exclusive probes.

A request with a Sun position applies the night-side override and directional-light rotation only
around synchronous render/readback. Both are restored before the frame task succeeds. All cleanup
operations are attempted even when another restore fails; cleanup failures fault the render task.
The display is unchanged during loading and settling. Without a Sun position, existing scene
lighting is used and no Sun model is approximated.

## Runtime ownership and cancellation

`SimulationRunner` follows the headless host. Capture never pauses or gates its state stream.
The exporter uses `CesiumSpacecraftPoseDriver.HoldCurrent` while snapshotting the displayed
spacecraft and cameras. A hold that already existed is preserved after capture; otherwise the
exporter releases its hold on completion, failure, disable or cancellation. Requests and host
truth are frozen together before yielding; incoming host states do not rewrite active metadata.
Host-to-Unity Core render transport remains UNITY-10 (#64).

Consumers must await the task or yield frames; do not call `.Wait()` or `.Result` on Unity's
main thread. A queued request's task cancels immediately when its token is cancelled; active
requests observe cancellation at the next yielded frame and restore scene resources before
completing. Disabling the renderer cancels queued and active requests and releases their targets.

`NavigationEpisodeExporter` delegates to the renderer's PNG capture path until UNITY-9 retires
it. This encodes directly from the read texture without allocating/flipping raw RGB copies. Core
`RenderAsync` still returns top-down RGB8; the optional raw-to-PNG helper preserves that contract.
There is one tile-wait/render/readback path. Readback is synchronous `Camera.Render` / `ReadPixels`.

## Camera run configuration

The rig reads the camera subset of `headless/Host/runs/p0-default.json` by default. Supply the
**same file as the host's `--run-config`** through Unity's `--run-config <absolute-path>`,
`ARGUS_RUN_CONFIG`, or the rig's `runConfigurationFile` Inspector field. The stream does not
transport the active run configuration; selecting a custom host file requires selecting it in
Unity too. Missing body cameras or invalid profiles fail explicitly. `ConfigureBodyCameras`
also accepts already parsed Core device configurations. Slots map by ID (`cam.px`, `cam.mx`,
`cam.py`, `cam.my`), independent of configuration order. Full-resolution requests preserve the
profile's exact dimensions, fx/fy/cx/cy and exposure duration. Preview targets retain their size.

## Verification

Run PlayMode tests with a GPU (no `-nographics`). The renderer tests use a small synthetic scene,
not remote Cesium terrain. They verify off-centre intrinsics and image orientation with coloured
markers, capture metadata, light restoration, cancellation/disable cleanup, task ordering after
failure, continued host streaming during capture holds, and PNG export through the legacy entry point. The separate opt-in scene smoke test covers real Cesium imagery loading and the
full-resolution four-camera workload; synthetic tests alone do not establish that result.

## Manual scene acceptance

Start the Basilisk service and headless host as described in [headless setup](../../../../headless/README.md),
using the same camera run configuration selected in the rig. Open `Foundation.unity`, enter Play
Mode and wait for a streamed state. With no host state, capture reports `NO STATE` instead of
starting a local simulation. Unity has no dashboard simulation-pause control after UNITY-6;
capture automatically holds presentation while host states continue arriving.

In Play Mode, the dashboard CAPTURE button saves all five preview-sized views. It keeps its
CAPTURE label while busy; the adjacent status shows the camera number, loading percentage and
elapsed seconds. Completion and timeouts are also logged in the Console. Outputs are PNGs plus
`navigation_metadata.jsonl` under `Application.persistentDataPath/NavigationEpisodes`.

The editor menu **Argus Simulation > Cameras** also provides Capture Preview, Capture Full
Resolution (4 Body Cameras), Cancel Capture and Log Capture Status. The full-resolution command
sends four requests at the configured body-camera native dimensions without enlarging the preview
targets. The default run currently configures 4608×2592. No Unity simulation pause is needed;
Unity's Editor toolbar pause stops coroutine progress and must remain off.

New JSONL records use `metadata_version: 3`. At capture start the exporter snapshots the displayed
spacecraft pose and every camera request. Inspector changes to body position/rotation, camera
mounts and preview FOV made before Capture are included. Changes while loading affect later
captures; all views in the current batch retain their original snapshots.

Legacy `position_ecef_m`, `rotation_body_to_ecef_xyzw` and
`angular_velocity_body_truth_rad_per_s` retain host truth and its body axes. ECEF velocity remains
host truth. `rendered_position_ecef_m`, `rendered_rotation_body_to_ecef_xyzw` and
`rendered_angular_velocity_body_rad_per_s` describe the displayed body; the rate is host angular
velocity re-expressed in the displayed axes, not a physical model of a manual nudge. `source_*`
fields remain aliases of host truth for readers of the short-lived v2 schema.

Legacy `cx_px` / `cy_px` retain edge-origin coordinates (W/2 and H/2 for a centred preview).
`rendered_cx_px` / `rendered_cy_px` explicitly record the exact request's zero-based pixel-centre
coordinates, including off-centre Core profiles. Legacy values are the exact coordinates plus
0.5 pixels, preserving the original coordinate origin without discarding off-centre calibration.
The shared fx/fy values describe the requested image. Consumers reconstructing an exposure must
use the actual camera ECEF pose and the exact rendered principal point, rather than treating host
body truth as the rendered pose after a manual edit.

`camera_position_ecef_m`, `camera_to_ecef_xyzw`, `camera_to_body_xyzw`, dimensions, fx/fy,
`rendered_cx_px`, `rendered_cy_px`, time, `exposure_duration_s`, `renderer`, `has_sun_position` and
`sun_position_ecef_m` describe the actual request returned with the pixels. Camera quaternions
use OpenCV axes. `camera_to_body_xyzw` is relative to the **rendered** body. The legacy
`rotation_camera_to_body_unity_xyzw` retains Unity camera axes. Without a Sun position the scene
provides lighting. Unversioned records lack rendered pose snapshots; v2 changed the legacy pose
fields to rendered values, so those historical files must be read according to their version.

`UnityImageRendererCesiumSmokeTests` is opt-in. Set `ARGUS_RUN_CESIUM_SMOKE=1` and select its
`FoundationSavesPreviewAndFourFullResolutionBodyImages` test to exercise the actual Foundation
scene, existing Cesium credentials, preview export, then four full-size exports. It supplies a
deterministic test state source to the unchanged host-following runner; it validates real Cesium
rendering, not a live Basilisk run or a host-to-Unity render RPC. It retains
images and metadata for inspection. Normal offline test runs skip this network-dependent test.
The opt-in test requires enabled terrain and checks Earth-facing PNG content as well as file
counts and sizes. It uses the production 1200-second per-view total budget and 60-second inactivity
limit. The total budget allows cold native-resolution loads that exceeded the previous 240-second
limit while still advancing. The inactivity limit and explicit Cancel command bound stalled work;
this is a batch capture path, not a real-time throughput guarantee.

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
far-origin red/green marker test verifies actual pixel projection after rebasing. A five-capture
manual validation batch contained 24 readable PNGs with matching metadata, including 350 km and
reset-to-500 km states. These files predate the projection fix. Verification of the latest changes
used offline GPU regression tests. Full network/Cesium validation was pending at that point;
the 2026-11-16 validation below covers the subsequent final renderer.

## Host-following regression validation — 2026-11-16

Integrated main at `f462e7b` (UNITY-5 / UNITY-6 / HOST-3 / CORE-21 / CORE-23). On Unity
6000.6.0f1 / macOS Metal, 261 EditMode and 26 offline PlayMode tests passed; the opt-in
Cesium test is run separately. The headless Core suite passed 261 tests and Host passed
74. New regressions verify continued state delivery during capture holds, preservation of an
existing hold, exact per-camera Core profiles, legacy metadata coordinate origins and truth,
manual edits frozen across all views, immediate queued cancellation, propagation of an injected
origin-restore failure, bounded live-view readiness probes, render-only Sun overrides, export I/O error
reporting, and pixel-for-pixel agreement between raw RGB and the direct PNG capture path.

The final real-Cesium Foundation test passed separately in 784.4 seconds with production defaults
(1200-second per-view total budget, 60-second inactivity limit, alternating readiness/live-view
frames). It saved five 768×432 previews and four 4608×2592 native body images, with nine metadata-v3
records and unique paths. All PNGs decoded and matched their recorded dimensions; every record
retained the same frozen state sequence and exposure start time. The report contained zero error
logs and measured a maximum of one exclusive selection frame across 6201 readiness probes.
Earth-facing nadir and native Forward images contained real terrain/imagery detail. Artifacts
remain under `NavigationEpisodes/unity4-cesium-acceptance_20261116T232817115Z/`.

The scene test uses real Cesium terrain, GIBS imagery, network and Metal, with a deterministic
state-source fixture feeding the host-following runner. It does not validate a live Basilisk
experiment or the pending host render RPC.
