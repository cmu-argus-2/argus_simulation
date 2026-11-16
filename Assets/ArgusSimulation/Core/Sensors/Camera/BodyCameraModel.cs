namespace Argus.Simulation.Core
{
    // A camera fixed to the spacecraft body, such as the +X, -X, +Y and -Y body cameras (D5). CameraKind
    // builds it from a camera device configuration (CORE-18, CORE-19). The kind is the physics model and
    // the part is a parameter set (D11): the P0 run file's Arducam IMX708 values are its parameters, not
    // code here. CameraModel does the exposure timing, pose and rendering. The P0 run file configures
    // the four body cameras with the DATA-6 (#71) profile and mounts (CORE-6).
    public sealed class BodyCameraModel : CameraModel
    {
        // Never gives up on a render. Frames are published in order, so a render that hangs stops this
        // camera, and one that falls behind the step loop delays every later frame; either way one more
        // exposure stays pending per period until Reset. Safe only while no caller supplies a renderer
        // (every exposure is then Unavailable at once). A caller cannot wait for renders yet:
        // PendingExposureCount drops only when the next step samples the camera.
        // TODO(G3): take the limit, or a way to wait, from the first caller that supplies a renderer (UNITY-4).
        private const double RenderLatencyLimitSeconds = double.PositiveInfinity;

        // CameraKind.Profile checks the configuration once, before the base constructor, so a wrong
        // one names the cause.
        public BodyCameraModel(DeviceConfiguration configuration, IImageRenderer renderer)
            : this(configuration.Definition, CameraKind.Instance.Profile(configuration), renderer)
        {
        }

        private BodyCameraModel(SensorDefinition definition, CameraProfile profile, IImageRenderer renderer)
            : base(definition, profile.Intrinsics, profile.ExposureDurationSeconds, RenderLatencyLimitSeconds, renderer)
        {
        }
    }
}
