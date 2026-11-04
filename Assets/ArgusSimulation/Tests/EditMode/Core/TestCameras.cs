using Argus.Simulation.Core;

namespace Argus.Simulation.Tests
{
    // The DATA-6 (#71) v0 camera profile the camera tests share: the Arducam IMX708 spec estimate,
    // no distortion, no noise model, and Argus-2's mounts with the origin assumed at the centre of
    // mass.
    internal static class TestCameras
    {
        public static readonly CameraIntrinsics Imx708Intrinsics =
            new CameraIntrinsics(4608, 2592, 3548.0, 3548.0, 2304.0, 1296.0);

        public static CameraProfile Imx708Profile() => Profile(Imx708Intrinsics, 0.0005);

        public static CameraProfile Profile(CameraIntrinsics intrinsics, double exposureDurationSeconds) =>
            new CameraProfile(
                "imx708-66deg-spec-v0",
                intrinsics,
                "spec-estimate",
                isCalibrated: false,
                CameraDistortion.Zero("none"),
                CameraProfile.NoNoise,
                exposureDurationSeconds,
                new CameraExtrinsicsProvenance("argus-2-config", "assumed-com", isOrientationVerified: false));

        public static DeviceConfiguration Configuration(SensorDefinition definition) =>
            CameraKind.Instance.Configure(definition, Imx708Profile());
    }
}
