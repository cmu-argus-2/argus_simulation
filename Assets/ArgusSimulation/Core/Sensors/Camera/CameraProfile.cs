namespace Argus.Simulation.Core
{
    // What a run configuration sets for a camera that Argus computes (CORE-18). The kind is a physics
    // model and a specific part, such as the Arducam IMX708, is a parameter set for it (D11).
    // Distortion and noise come with the concrete camera profiles (CORE-6).
    public readonly struct CameraProfile
    {
        public CameraProfile(CameraIntrinsics intrinsics, double exposureDurationSeconds)
        {
            Intrinsics = intrinsics;
            ExposureDurationSeconds = exposureDurationSeconds;
        }

        public CameraIntrinsics Intrinsics { get; }
        public double ExposureDurationSeconds { get; }

        // Exactly what the CameraModel constructor accepts.
        public bool IsValid =>
            Intrinsics.IsValid &&
            ExposureDurationSeconds >= 0.0 &&
            !double.IsInfinity(ExposureDurationSeconds);
    }
}
