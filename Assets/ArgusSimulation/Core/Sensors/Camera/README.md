# Core/Sensors/Camera — camera sensor models

**Diagram block:** Sensor models · **Assembly:** `Argus.Simulation.Core` · **Status:** `CameraModel` base implemented (CORE-3); concrete cameras are placeholders (`TODO(D5)`, `TODO(D5, D7)`)

Cameras are Core sensor models; Unity only renders (decision D5 in
[target-architecture.md](../../../../../docs/target-architecture.md)).

## Classes

- `CameraModel`: an abstract `SensorModel<ImageFrame>` that owns exposure timing, pose,
  intrinsics (`Core/Imaging/CameraIntrinsics.cs`) and exposure duration. At each exposure
  it sends a `RenderRequest` (`Core/Imaging/RenderRequest.cs`) to an `IImageRenderer`
  (`Core/Abstractions/IImageRenderer.cs`) and publishes the result as a
  `SensorFrame<ImageFrame>`. Distortion and noise come with the concrete profiles.
- `ArducamImx708CameraModel` (CORE-6): the physical camera profile, used for the +X, -X, +Y
  and -Y body cameras.
- `NadirGroundTruthCameraModel` (CORE-8): follows spacecraft position with a north-up nadir
  orientation that ignores attitude, by overriding `ComputePose`.

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

A step loop that must not outrun the renderer (lockstep runs) waits until
`PendingExposureCount` is 0 before stepping. The Unity renderer that implements
`IImageRenderer` lives in `Unity/Cameras/` (UNITY-4, #57).
