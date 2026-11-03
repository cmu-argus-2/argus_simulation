using System;

namespace Argus.Simulation.Core
{
    // What a run configuration sets for a camera that Argus computes (CORE-18, CORE-6). The kind is a
    // physics model and a specific part, such as the Arducam IMX708, is a parameter set for it (D11).
    // CameraKind reads it from the camera's parameters, which the manifest records, so a dataset says
    // where each value came from (DATA-6 #71).
    public readonly struct CameraProfile
    {
        // There is no camera noise model yet (DATA-6): rendered images are noise-free.
        public const string NoNoise = "none";

        // The noise sources a camera may declare, only NoNoise so far: IsValid and CameraKind's
        // problem check share this set.
        public static bool IsKnownNoiseSource(string noiseSource) => string.Equals(noiseSource, NoNoise, StringComparison.Ordinal);

        public CameraProfile(
            string profileId,
            CameraIntrinsics intrinsics,
            string intrinsicsSource,
            bool isCalibrated,
            CameraDistortion distortion,
            string noiseSource,
            double exposureDurationSeconds,
            CameraExtrinsicsProvenance extrinsics)
        {
            ProfileId = profileId;
            Intrinsics = intrinsics;
            IntrinsicsSource = intrinsicsSource;
            IsCalibrated = isCalibrated;
            Distortion = distortion;
            NoiseSource = noiseSource;
            ExposureDurationSeconds = exposureDurationSeconds;
            Extrinsics = extrinsics;
        }

        // Names the part's parameter set: intrinsics, distortion and noise. A calibration changes it.
        public string ProfileId { get; }
        public CameraIntrinsics Intrinsics { get; }

        // e.g. "spec-estimate"; IsCalibrated is true only for checkerboard-calibrated values.
        public string IntrinsicsSource { get; }
        public bool IsCalibrated { get; }
        public CameraDistortion Distortion { get; }
        public string NoiseSource { get; }
        public double ExposureDurationSeconds { get; }
        public CameraExtrinsicsProvenance Extrinsics { get; }

        // What ProfileId versions: the intrinsics with their source, the distortion and the noise. Two
        // cameras that share a ProfileId must share these (SimulationConfiguration.IsValid). Exposure
        // and extrinsics belong to each camera. headless/Host's RunConfigurationFile.DifferingParts names
        // the differing parts in its error by the same groups; keep the two in step.
        public bool IsSameParameterSetAs(CameraProfile other) =>
            Intrinsics.Equals(other.Intrinsics) &&
            string.Equals(IntrinsicsSource, other.IntrinsicsSource, StringComparison.Ordinal) &&
            IsCalibrated == other.IsCalibrated &&
            Distortion.Equals(other.Distortion) &&
            string.Equals(NoiseSource, other.NoiseSource, StringComparison.Ordinal);

        // The intrinsics and exposure are what the CameraModel constructor accepts. Distortion must be
        // zero: no renderer applies it yet, so non-zero coefficients would describe images the run
        // never made.
        // TODO(D5): accept non-zero distortion together with a renderer that applies it (UNITY-4 #57).
        public bool IsValid =>
            SimulationConfiguration.IsLowercaseIdentifier(ProfileId) &&
            Intrinsics.IsValid &&
            !string.IsNullOrWhiteSpace(IntrinsicsSource) &&
            Distortion.IsValid &&
            Distortion.IsZero &&
            IsKnownNoiseSource(NoiseSource) &&
            ExposureDurationSeconds >= 0.0 &&
            !double.IsInfinity(ExposureDurationSeconds) &&
            Extrinsics.IsValid;
    }
}
