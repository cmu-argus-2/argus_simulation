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
        // Waits for every render, as a lockstep run does.
        // TODO(G3): take the limit from the first caller that supplies a renderer (late-frame policy).
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
