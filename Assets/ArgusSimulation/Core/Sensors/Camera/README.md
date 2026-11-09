# Core/Sensors/Camera — camera sensor models

**Diagram block:** Sensor models · **Assembly:** `Argus.Simulation.Core` · **Status:** `CameraModel` base implemented (CORE-3); `BodyCameraModel` built from the run configuration (CORE-18), with the four P0 body cameras and their DATA-6 profile (CORE-6); `NadirGroundTruthCameraModel` built from the run configuration (CORE-8)

Cameras are Core sensor models; Unity only renders (decision D5 in
[target-architecture.md](../../../../../docs/target-architecture.md)).

## Classes

- `CameraModel`: an abstract `SensorModel<ImageFrame>` that owns exposure timing, pose,
  intrinsics (`Core/Imaging/CameraIntrinsics.cs`) and exposure duration. At each exposure
  it sends a `RenderRequest` (`Core/Imaging/RenderRequest.cs`) to an `IImageRenderer`
  (`Core/Abstractions/IImageRenderer.cs`) and publishes the result as a
  `SensorFrame<ImageFrame>`. Distortion and noise are in `CameraProfile`; no renderer applies
  distortion yet.
- `BodyCameraModel`: a camera fixed to the body, used for the +X, -X, +Y and -Y body
  cameras. The kind is the physics model and the part is a parameter set (D11): the P0 run
  file's Arducam IMX708 values are its parameters. `CameraKind` builds it from a `camera`
  device configuration, whose parameters read into a `CameraProfile` (CORE-18, CORE-6).
  Cameras are computed by Argus, so they are never sent to Basilisk. The renderer comes from
  the host or Unity; `headless/Host` has none yet, so its camera frames are `Unavailable`. A
  camera frame carries the true camera pose, so a run may not make a camera
  controller-visible (`IDeviceKind.MayBeControllerVisible`, D7). The four body cameras stay
  non-visible; a controller-safe camera observation without pose is CORE-23 (#125,
  `TODO(D7)`). `runs/p0-default.json` configures them as `cam.px`, `cam.py`, `cam.mx` and
  `cam.my` (flight's cam1–cam4, ids 0–3) with the DATA-6 profile and mounts, sampled every
  15 s, flight's capture interval. Their exposure is 0.5 ms, the driver's minimum: a v0
  estimate for sunlit Earth, where flight uses auto exposure between 0.5 and 65.5 ms (#42
  comment, 27 Oct).
- `NadirGroundTruthCameraModel` (CORE-8): the north-up nadir ground-truth camera.
  `NadirGroundTruthCameraKind` builds it from a `nadir_ground_truth_camera` device
  configuration with the same DATA-6 parameters as a `camera`, and its own profile ID (for
  example `nadir-gt-768x432-9deg`, the Unity rig's 768 x 432 px, 9 degree ground-truth view).
  It overrides `ComputePose`: the camera sits at the spacecraft position (the mount is
  ignored) and ignores attitude. In the camera frame (x right, y down, z out of the lens;
  DATA-6, #71) z is geodetic down, perpendicular to the WGS84 ellipsoid through the geodetic
  point beneath the spacecraft, -y is north and x is east, so the image is north-up and
  centred on that point. Exactly on the polar axis east is taken as Earth-fixed +y. Cesium's
  East-North-Up frame, which the Unity rig's ground-truth camera uses today, takes the
  ellipsoid gradient at the spacecraft instead; at 500 km the two differ by about 0.01
  degrees. It is ground truth, so it is never controller-visible (D7): its frames reach the
  recorder, never a gateway observation. `runs/p0-default.json` does not include it.

A kind whose sensor is a camera implements `ICameraKind` (`Core/Contracts/ICameraKind.cs`):
the recorder lists it in the manifest's `cameras`, and cameras that share a profile ID must
share its parameter set. `DeviceKindsTests` fails for a kind that builds a `CameraModel`
without it. `CameraKind` and `NadirGroundTruthCameraKind` both derive from `CameraProfileKind`,
which implements it and holds the camera parameter keys and their checks, so the two kinds
cannot drift apart.

## Camera profile

A `camera` device's parameters (`CameraKind`, docs/device-model.md §3) are the DATA-6 (#71)
profile, each value with where it came from:

| keys | what |
|---|---|
| `profile_id` | names the part's parameter set: the intrinsics with their source, the distortion and the noise. Cameras that share one must share those values (`CameraProfile.IsSameParameterSetAs`); a calibration gets a new ID. |
| `intrinsics.*` | `width_px`, `height_px`, `focal_length_x_px`, `focal_length_y_px`, `principal_point_x_px`, `principal_point_y_px`, plus `source` (e.g. `spec-estimate`) and `calibrated` |
| `distortion.*` | `model`, which must be `brown-conrady-5`; `k1`, `k2`, `p1`, `p2`, `k3` in OpenCV's order, all zero until a renderer applies distortion (UNITY-4, `TODO(D5)` in `CameraProfile`); `source` |
| `noise.source` | `none`: there is no camera noise model yet |
| `exposure_duration_s` | this camera's exposure |
| `extrinsics.*` | where this camera's mount came from: `source`, `origin` (the body point the position is measured from), `orientation_verified` |

Exposure and extrinsics belong to each camera, not to the profile ID.

## Camera frame

OpenCV's: +x is image right, +y is image down and +z points out of the lens. Pixel (0, 0) is
the centre of the top-left pixel. A mount's `deviceToBody` takes camera-frame vectors to the
body (the DATA-6 R matrices, camera to body, CORE-6). A request's `CameraToEcef` is the body
attitude applied after the mount. The Argus flight and GNC code use the same frame
(`FSW-Payload` `od_alg.cpp`, `GNC-Payload` `camera_model.py`). A renderer converts it to its
own axes in one place (UNITY-4). The DATA-6 principal point (2304, 1296) is the flight
config's (W / 2, H / 2), half a pixel right of and below the geometric centre (2303.5, 1295.5)
under this convention (about 1.4e-4 rad); it is kept as given, since it is an uncalibrated
estimate.

## How an exposure works

1. When a sample slot is due, the camera computes its pose from the state (by default the
   mount's lever arm and rotation applied to the spacecraft pose) and sends a
   `RenderRequest` with the capture state's sequence and time and, when the state has an
   `EnvironmentState`, the Sun position. Nothing is published yet.
2. On each later `TrySample` it checks the oldest exposure, without waiting:
   - **Rendered:** published `Valid`, stamped with its capture state and time (G3), even if
     it arrives several steps later. The published `ImageFrame` carries this camera's
     request, so the renderer supplies only pixels.
   - **Failed, cancelled, or a frame that does not match the request:** `Unavailable`.
   - **Still rendering past `MaximumLatencySeconds` of simulation time:** cancelled and
     published `Unavailable`. `PositiveInfinity` waits for every render.
3. Frames are published in sequence order, one per `TrySample`; a later exposure that
   finishes first waits for the earlier one.

Every frame, `Valid` or `Unavailable`, carries its capture state in
`SensorFrame.StateSequence`, which the recorder writes as `state_sequence`. An `Unavailable`
frame's `ImageFrame` keeps the request (camera ID, pose, capture state) with no pixels.

A camera never publishes a black or approximated image. With no renderer every exposure is
`Unavailable`. With no `EnvironmentState` (analytic runs, D9) it still renders and
`RenderRequest.SunPositionEcefMeters` is `null`, which records that the renderer lit the
scene its own way.

An exposure still pending when the run ends or the camera resets is abandoned, not published.
`TrySample` is a frame's only way out, and neither case samples that run again; `Reset` also
cancels the render. Frames are published in sequence order, so abandoned exposures are always
the last ones: a camera's frames in a run directory can stop short of its last slot, but their
`sequence` has no gaps. The manifest does not count them.

`PendingExposureCount` is a queue depth, not something to wait on. It falls only inside
`TrySample`, so a step loop that waits for it to reach 0 before stepping never finishes. Core
has no way to wait for a render yet: `PositiveInfinity` stops a slow render from timing out but
does not hold the step loop back, so exposures queue up. Lockstep waiting belongs with the
first renderer. The Unity renderer that implements `IImageRenderer` lives in `Unity/Cameras/`
(UNITY-4, #57).
