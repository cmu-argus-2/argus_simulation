using System;

namespace Argus.Simulation.Core
{
    // The "camera" kind, which Argus computes and never sends to Basilisk (CORE-18). A specific part,
    // such as the Arducam IMX708, is a parameter set for it (D11): its parameters are the DATA-6 (#71)
    // profile, each value with where it came from (CORE-6). Its frames carry the true pose they were
    // rendered for, so a controller may not see them (D7).
    // TODO(D7): a controller-safe camera observation without pose or Sun is CORE-23 (#125); CORE-8
    // (#44)'s nadir ground-truth camera kind stays not visible.
    public sealed class CameraKind : DeviceKind<CameraProfile>, ICameraKind
    {
        public const string KindName = "camera";

        public static readonly CameraKind Instance = new CameraKind();

        private const string ProfileId = "profile_id";
        private const string Width = "intrinsics.width_px";
        private const string Height = "intrinsics.height_px";
        private const string FocalLengthX = "intrinsics.focal_length_x_px";
        private const string FocalLengthY = "intrinsics.focal_length_y_px";
        private const string PrincipalPointX = "intrinsics.principal_point_x_px";
        private const string PrincipalPointY = "intrinsics.principal_point_y_px";
        private const string IntrinsicsSource = "intrinsics.source";
        private const string Calibrated = "intrinsics.calibrated";
        // Brown-Conrady in OpenCV's order, one key per coefficient: there is no array value type.
        private const string DistortionModel = "distortion.model";
        private const string K1 = "distortion.k1";
        private const string K2 = "distortion.k2";
        private const string P1 = "distortion.p1";
        private const string P2 = "distortion.p2";
        private const string K3 = "distortion.k3";
        private const string DistortionSource = "distortion.source";
        private const string NoiseSource = "noise.source";
        private const string ExposureDuration = "exposure_duration_s";
        private const string ExtrinsicsSource = "extrinsics.source";
        private const string ExtrinsicsOrigin = "extrinsics.origin";
        private const string OrientationVerified = "extrinsics.orientation_verified";

        private CameraKind()
            : base(
                KindName,
                DeviceRole.Sensor,
                isBackendModelled: false,
                mayBeControllerVisible: false,
                new[]
                {
                    new DeviceKey(ProfileId, DeviceValueType.Text),
                    new DeviceKey(Width, DeviceValueType.Number),
                    new DeviceKey(Height, DeviceValueType.Number),
                    new DeviceKey(FocalLengthX, DeviceValueType.Number),
                    new DeviceKey(FocalLengthY, DeviceValueType.Number),
                    new DeviceKey(PrincipalPointX, DeviceValueType.Number),
                    new DeviceKey(PrincipalPointY, DeviceValueType.Number),
                    new DeviceKey(IntrinsicsSource, DeviceValueType.Text),
                    new DeviceKey(Calibrated, DeviceValueType.Flag),
                    new DeviceKey(DistortionModel, DeviceValueType.Text),
                    new DeviceKey(K1, DeviceValueType.Number),
                    new DeviceKey(K2, DeviceValueType.Number),
                    new DeviceKey(P1, DeviceValueType.Number),
                    new DeviceKey(P2, DeviceValueType.Number),
                    new DeviceKey(K3, DeviceValueType.Number),
                    new DeviceKey(DistortionSource, DeviceValueType.Text),
                    new DeviceKey(NoiseSource, DeviceValueType.Text),
                    new DeviceKey(ExposureDuration, DeviceValueType.Number),
                    new DeviceKey(ExtrinsicsSource, DeviceValueType.Text),
                    new DeviceKey(ExtrinsicsOrigin, DeviceValueType.Text),
                    new DeviceKey(OrientationVerified, DeviceValueType.Flag)
                })
        {
        }

        // Without a renderer, every exposure is Unavailable (D5).
        public override ISensor CreateSensor(DeviceConfiguration configuration, IImageRenderer imageRenderer) =>
            new BodyCameraModel(configuration, imageRenderer);

        protected override string FindValueProblem(DeviceValues parameters) =>
            FindPixelCountProblem(parameters, Width) ?? FindPixelCountProblem(parameters, Height);

        protected override CameraProfile Parse(DeviceValues parameters) => new CameraProfile(
            parameters[ProfileId].Text,
            new CameraIntrinsics(
                (int)parameters.Number(Width),
                (int)parameters.Number(Height),
                parameters.Number(FocalLengthX),
                parameters.Number(FocalLengthY),
                parameters.Number(PrincipalPointX),
                parameters.Number(PrincipalPointY)),
            parameters[IntrinsicsSource].Text,
            parameters[Calibrated].Flag,
            new CameraDistortion(
                parameters[DistortionModel].Text,
                parameters.Number(K1),
                parameters.Number(K2),
                parameters.Number(P1),
                parameters.Number(P2),
                parameters.Number(K3),
                parameters[DistortionSource].Text),
            parameters[NoiseSource].Text,
            parameters.Number(ExposureDuration),
            new CameraExtrinsicsProvenance(
                parameters[ExtrinsicsSource].Text,
                parameters[ExtrinsicsOrigin].Text,
                parameters[OrientationVerified].Flag));

        protected override DeviceValues ToParameters(CameraProfile profile) => new DeviceValues.Builder()
            .Add(ProfileId, DeviceValue.FromText(profile.ProfileId))
            .Add(Width, profile.Intrinsics.WidthPixels)
            .Add(Height, profile.Intrinsics.HeightPixels)
            .Add(FocalLengthX, profile.Intrinsics.FocalLengthXPixels)
            .Add(FocalLengthY, profile.Intrinsics.FocalLengthYPixels)
            .Add(PrincipalPointX, profile.Intrinsics.PrincipalPointXPixels)
            .Add(PrincipalPointY, profile.Intrinsics.PrincipalPointYPixels)
            .Add(IntrinsicsSource, DeviceValue.FromText(profile.IntrinsicsSource))
            .Add(Calibrated, DeviceValue.FromFlag(profile.IsCalibrated))
            .Add(DistortionModel, DeviceValue.FromText(profile.Distortion.Model))
            .Add(K1, profile.Distortion.K1)
            .Add(K2, profile.Distortion.K2)
            .Add(P1, profile.Distortion.P1)
            .Add(P2, profile.Distortion.P2)
            .Add(K3, profile.Distortion.K3)
            .Add(DistortionSource, DeviceValue.FromText(profile.Distortion.Source))
            .Add(NoiseSource, DeviceValue.FromText(profile.NoiseSource))
            .Add(ExposureDuration, profile.ExposureDurationSeconds)
            .Add(ExtrinsicsSource, DeviceValue.FromText(profile.Extrinsics.Source))
            .Add(ExtrinsicsOrigin, DeviceValue.FromText(profile.Extrinsics.Origin))
            .Add(OrientationVerified, DeviceValue.FromFlag(profile.Extrinsics.IsOrientationVerified))
            .Build();

        protected override bool IsValid(CameraProfile profile) => profile.IsValid;

        // CameraProfile.IsValid is the rule; this only names the narrowest key it fails on, so the two
        // never disagree about validity.
        protected override string FindProfileProblem(CameraProfile profile)
        {
            if (profile.IsValid)
            {
                return null;
            }

            if (!SimulationConfiguration.IsLowercaseIdentifier(profile.ProfileId))
            {
                return $"parameters[\"{ProfileId}\"]: must match [a-z0-9][a-z0-9._-]*";
            }

            if (!profile.Intrinsics.IsValid)
            {
                return "parameters[\"intrinsics.*\"]: are not valid intrinsics (see CameraIntrinsics.IsValid)";
            }

            if (IsBlank(profile.IntrinsicsSource))
            {
                return Blank(IntrinsicsSource);
            }

            CameraDistortion distortion = profile.Distortion;
            if (!CameraDistortion.IsKnownModel(distortion.Model))
            {
                return $"parameters[\"{DistortionModel}\"]: must be \"{CameraDistortion.BrownConrady5}\"";
            }

            if (!distortion.IsZero)
            {
                return "parameters[\"distortion.*\"]: must be all zero until a renderer applies distortion (UNITY-4 #57)";
            }

            if (IsBlank(distortion.Source))
            {
                return Blank(DistortionSource);
            }

            if (!CameraProfile.IsKnownNoiseSource(profile.NoiseSource))
            {
                return $"parameters[\"{NoiseSource}\"]: must be \"{CameraProfile.NoNoise}\": there is no camera noise model yet (DATA-6 #71)";
            }

            if (IsBlank(profile.Extrinsics.Source))
            {
                return Blank(ExtrinsicsSource);
            }

            if (IsBlank(profile.Extrinsics.Origin))
            {
                return Blank(ExtrinsicsOrigin);
            }

            // Exposure, or a rule CameraProfile.IsValid gained without a message here.
            return !(profile.ExposureDurationSeconds >= 0.0) || double.IsInfinity(profile.ExposureDurationSeconds)
                ? $"parameters[\"{ExposureDuration}\"]: must be finite and >= 0 (see CameraProfile.IsValid)"
                : "parameters: is not a valid camera profile (see CameraProfile.IsValid)";
        }

        private static bool IsBlank(string value) => string.IsNullOrWhiteSpace(value);

        private static string Blank(string key) => $"parameters[\"{key}\"]: must not be blank";

        // Pixel counts are whole numbers that fit CameraIntrinsics' int.
        private static string FindPixelCountProblem(DeviceValues parameters, string key)
        {
            double value = parameters.Number(key);
            return Math.Floor(value) == value && value >= int.MinValue && value <= int.MaxValue
                ? null
                : $"parameters[\"{key}\"]: must be a whole number of pixels";
        }
    }
}
