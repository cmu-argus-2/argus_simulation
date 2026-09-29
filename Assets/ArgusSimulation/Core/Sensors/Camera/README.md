# Core/Sensors/Camera — camera sensor models (planned)

**Diagram block:** Sensor models · **Assembly:** `Argus.Simulation.Core` · **Status:** not built

Cameras are Core sensor models; Unity only renders (decision D5 in
[target-architecture.md](../../../../../docs/target-architecture.md)).

## Planned classes

- `CameraModel`: a `SensorModel<ImageFrame>` that owns exposure timing, mounting,
  intrinsics (`Core/Imaging/CameraIntrinsics.cs`), distortion and noise. At each exposure
  it sends a `RenderRequest` (`Core/Imaging/RenderRequest.cs`) to an `IImageRenderer`
  (`Core/Abstractions/IImageRenderer.cs`). The rendered `ImageFrame` comes back as a
  normal `SensorFrame`.
- `ArducamImx708CameraModel`: the physical camera profile, used for the +X, -X, +Y and
  -Y body cameras.
- `NadirGroundTruthCameraModel`: follows spacecraft position with a north-up nadir
  orientation that ignores attitude.

## Open design points

- Rendering is asynchronous (`RenderAsync`), but `SensorModel.TrySample` is synchronous.
  The model needs a pending-frame path and a late-frame policy (gap G3).
- `RenderRequest` needs the Sun direction from the snapshot's environment for lighting.

The Unity renderer that implements `IImageRenderer` will live in `Unity/Cameras/`, next to
`CubeSatCameraRig`.
