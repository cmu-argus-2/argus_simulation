using System;

namespace Argus.Simulation.Core
{
    // Renderer-neutral description of one exposure. Unity is one possible implementation. The camera
    // model fills every field from the state the exposure was captured at (D5), so a late frame keeps
    // its capture state and time (G3).
    public readonly struct RenderRequest
    {
        public RenderRequest(
            string cameraId,
            long sequence,
            long stateSequence,
            double exposureStartSimulationTimeSeconds,
            DateTimeOffset exposureStartUtc,
            double exposureDurationSeconds,
            Vector3d cameraPositionEcefMeters,
            Quaterniond cameraToEcef,
            CameraIntrinsics intrinsics,
            Vector3d? sunPositionEcefMeters = null)
        {
            CameraId = cameraId;
            Sequence = sequence;
            StateSequence = stateSequence;
            ExposureStartSimulationTimeSeconds = exposureStartSimulationTimeSeconds;
            ExposureStartUtc = exposureStartUtc.ToUniversalTime();
            ExposureDurationSeconds = exposureDurationSeconds;
            CameraPositionEcefMeters = cameraPositionEcefMeters;
            CameraToEcef = cameraToEcef;
            Intrinsics = intrinsics;
            SunPositionEcefMeters = sunPositionEcefMeters;
        }

        public string CameraId { get; }

        // The camera's frame sequence.
        public long Sequence { get; }

        // SpacecraftState.Sequence of the state the exposure was captured at.
        public long StateSequence { get; }

        public double ExposureStartSimulationTimeSeconds { get; }
        public DateTimeOffset ExposureStartUtc { get; }
        public double ExposureDurationSeconds { get; }
        public Vector3d CameraPositionEcefMeters { get; }
        public Quaterniond CameraToEcef { get; }
        public CameraIntrinsics Intrinsics { get; }

        // Earth centre to Sun centre in the same Earth-fixed frame as the camera position, from the
        // state's EnvironmentState. Null when the state has none (analytic runs, D9): the renderer
        // then lights the scene its own way, and this null records that the lighting is not the
        // run's Sun. It is never approximated.
        public Vector3d? SunPositionEcefMeters { get; }
        public bool HasSunPosition => SunPositionEcefMeters.HasValue;

        public bool IsValid =>
            !string.IsNullOrWhiteSpace(CameraId) &&
            Sequence >= 0 &&
            StateSequence >= 0 &&
            ExposureStartSimulationTimeSeconds >= 0.0 &&
            ExposureDurationSeconds >= 0.0 &&
            IsFinite(ExposureStartSimulationTimeSeconds) &&
            IsFinite(ExposureDurationSeconds) &&
            CameraPositionEcefMeters.IsFinite &&
            CameraToEcef.IsUnit &&
            Intrinsics.IsValid &&
            (!SunPositionEcefMeters.HasValue ||
                (SunPositionEcefMeters.Value.IsFinite && SunPositionEcefMeters.Value.Magnitude > 0.0));

        private static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
