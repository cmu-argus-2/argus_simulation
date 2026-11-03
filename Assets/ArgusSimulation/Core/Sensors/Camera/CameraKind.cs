using System;

namespace Argus.Simulation.Core
{
    // The "camera" kind, which Argus computes and never sends to Basilisk (CORE-18). A specific part,
    // such as the Arducam IMX708, is a parameter set for it (D11). Its frames carry the true pose
    // they were rendered for, so a controller may not see them (D7).
    // TODO(D7): CORE-6 (#42) decides what a controller sees of a camera; CORE-8 (#44)'s nadir
    // ground-truth camera kind stays not visible.
    public sealed class CameraKind : DeviceKind<CameraProfile>
    {
        public const string KindName = "camera";

        public static readonly CameraKind Instance = new CameraKind();

        private const string Width = "intrinsics.width_px";
        private const string Height = "intrinsics.height_px";
        private const string FocalLengthX = "intrinsics.focal_length_x_px";
        private const string FocalLengthY = "intrinsics.focal_length_y_px";
        private const string PrincipalPointX = "intrinsics.principal_point_x_px";
        private const string PrincipalPointY = "intrinsics.principal_point_y_px";
        private const string ExposureDuration = "exposure_duration_s";

        private CameraKind()
            : base(
                KindName,
                DeviceRole.Sensor,
                isBackendModelled: false,
                mayBeControllerVisible: false,
                new[]
                {
                    new DeviceKey(Width, DeviceValueType.Number),
                    new DeviceKey(Height, DeviceValueType.Number),
                    new DeviceKey(FocalLengthX, DeviceValueType.Number),
                    new DeviceKey(FocalLengthY, DeviceValueType.Number),
                    new DeviceKey(PrincipalPointX, DeviceValueType.Number),
                    new DeviceKey(PrincipalPointY, DeviceValueType.Number),
                    new DeviceKey(ExposureDuration, DeviceValueType.Number)
                })
        {
        }

        // Without a renderer, every exposure is Unavailable (D5).
        public override ISensor CreateSensor(DeviceConfiguration configuration, IImageRenderer imageRenderer) =>
            new ArducamImx708CameraModel(configuration, imageRenderer);

        protected override string FindValueProblem(DeviceValues parameters) =>
            FindPixelCountProblem(parameters, Width) ?? FindPixelCountProblem(parameters, Height);

        protected override CameraProfile Parse(DeviceValues parameters) => new CameraProfile(
            new CameraIntrinsics(
                (int)parameters.Number(Width),
                (int)parameters.Number(Height),
                parameters.Number(FocalLengthX),
                parameters.Number(FocalLengthY),
                parameters.Number(PrincipalPointX),
                parameters.Number(PrincipalPointY)),
            parameters.Number(ExposureDuration));

        protected override DeviceValues ToParameters(CameraProfile profile) => new DeviceValues.Builder()
            .Add(Width, profile.Intrinsics.WidthPixels)
            .Add(Height, profile.Intrinsics.HeightPixels)
            .Add(FocalLengthX, profile.Intrinsics.FocalLengthXPixels)
            .Add(FocalLengthY, profile.Intrinsics.FocalLengthYPixels)
            .Add(PrincipalPointX, profile.Intrinsics.PrincipalPointXPixels)
            .Add(PrincipalPointY, profile.Intrinsics.PrincipalPointYPixels)
            .Add(ExposureDuration, profile.ExposureDurationSeconds)
            .Build();

        protected override bool IsValid(CameraProfile profile) => profile.IsValid;

        protected override string FindProfileProblem(CameraProfile profile)
        {
            if (!profile.Intrinsics.IsValid)
            {
                return "parameters[\"intrinsics.*\"]: are not valid intrinsics (see CameraIntrinsics.IsValid)";
            }

            return profile.IsValid
                ? null
                : $"parameters[\"{ExposureDuration}\"]: must be finite and >= 0 (see CameraProfile.IsValid)";
        }

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
