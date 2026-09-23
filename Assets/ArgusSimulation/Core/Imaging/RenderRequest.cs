namespace Argus.Simulation.Core
{
    // Renderer-neutral description of one exposure. Unity is one possible implementation.
    public readonly struct RenderRequest
    {
        public RenderRequest(
            string cameraId,
            long sequence,
            double exposureStartSimulationTimeSeconds,
            double exposureDurationSeconds,
            Vector3d cameraPositionEcefMeters,
            Quaterniond cameraToEcef,
            CameraIntrinsics intrinsics)
        {
            CameraId = cameraId;
            Sequence = sequence;
            ExposureStartSimulationTimeSeconds = exposureStartSimulationTimeSeconds;
            ExposureDurationSeconds = exposureDurationSeconds;
            CameraPositionEcefMeters = cameraPositionEcefMeters;
            CameraToEcef = cameraToEcef;
            Intrinsics = intrinsics;
        }

        public string CameraId { get; }
        public long Sequence { get; }
        public double ExposureStartSimulationTimeSeconds { get; }
        public double ExposureDurationSeconds { get; }
        public Vector3d CameraPositionEcefMeters { get; }
        public Quaterniond CameraToEcef { get; }
        public CameraIntrinsics Intrinsics { get; }

        public bool IsValid =>
            !string.IsNullOrWhiteSpace(CameraId) &&
            Sequence >= 0 &&
            ExposureStartSimulationTimeSeconds >= 0.0 &&
            ExposureDurationSeconds >= 0.0 &&
            IsFinite(ExposureStartSimulationTimeSeconds) &&
            IsFinite(ExposureDurationSeconds) &&
            CameraPositionEcefMeters.IsFinite &&
            CameraToEcef.IsUnit &&
            Intrinsics.IsValid;

        private static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
