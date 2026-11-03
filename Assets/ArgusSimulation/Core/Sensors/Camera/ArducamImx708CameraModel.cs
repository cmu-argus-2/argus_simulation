using System;

namespace Argus.Simulation.Core
{
    // The physical camera for the +X, -X, +Y and -Y body cameras (D5), built by SensorFactory from a
    // Camera sensor configuration (CORE-18). CameraModel does the exposure timing, pose and rendering.
    // TODO(D5): CORE-6 (#42) adds the DATA-6 (#71) distortion and noise, and the four mounts in the run
    // configuration. Until UNITY-4, CubeSatCameraRig renders these cameras every frame without this
    // model.
    public sealed class ArducamImx708CameraModel : CameraModel
    {
        // Never gives up on a render. Frames are published in order, so a render that hangs stops this
        // camera, and one that falls behind the step loop delays every later frame; either way one more
        // exposure stays pending per period until Reset. Safe only while no caller supplies a renderer
        // (every exposure is then Unavailable at once). A caller cannot wait for renders yet:
        // PendingExposureCount drops only when the next step samples the camera.
        // TODO(G3): take the limit, or a way to wait, from the first caller that supplies a renderer (UNITY-4).
        private const double RenderLatencyLimitSeconds = double.PositiveInfinity;

        public ArducamImx708CameraModel(SensorConfiguration configuration, IImageRenderer renderer)
            : base(
                Checked(configuration).Definition,
                configuration.CameraProfile.Intrinsics,
                configuration.CameraProfile.ExposureDurationSeconds,
                RenderLatencyLimitSeconds,
                renderer)
        {
        }

        // Runs before the base constructor, so a wrong configuration names the cause.
        private static SensorConfiguration Checked(SensorConfiguration configuration)
        {
            if (configuration.Kind != SensorKind.Camera || !configuration.IsValid)
            {
                throw new ArgumentException(
                    "A valid Camera sensor configuration is required.",
                    nameof(configuration));
            }

            return configuration;
        }
    }
}
