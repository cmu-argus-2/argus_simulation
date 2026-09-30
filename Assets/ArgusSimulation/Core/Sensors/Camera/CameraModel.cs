namespace Argus.Simulation.Core
{
    // PLACEHOLDER: not implemented, not referenced. See README.md in this folder.
    // TODO(D5, G3): base camera sensor.
    // - Becomes a SensorModel<ImageFrame> that owns exposure timing, mount, intrinsics
    //   (CameraIntrinsics), distortion and noise.
    // - At each exposure it sends a RenderRequest to an IImageRenderer and publishes the returned
    //   ImageFrame as a SensorFrame. Rendering is asynchronous and TrySample is not, so it needs a
    //   pending-frame path and a late-frame policy (G3).
    // - RenderRequest needs the Sun direction from the snapshot's EnvironmentState.
    internal abstract class CameraModel
    {
    }
}
