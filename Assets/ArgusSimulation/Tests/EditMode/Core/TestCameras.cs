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

        // The nadir ground-truth camera (CORE-8): the Unity rig's ground-truth view, 768 x 432 px and
        // 9 degrees vertical, sampled with no exposure time. Its own profile ID, since cameras that
        // share one share their parameters; the pose is computed, not mounted.
        public static readonly CameraIntrinsics NadirIntrinsics =
            new CameraIntrinsics(768, 432, 2744.54, 2744.54, 384.0, 216.0);

        public static CameraProfile NadirProfile() =>
            new CameraProfile(
                "nadir-gt-768x432-9deg",
                NadirIntrinsics,
                "unity-gt-rig",
                isCalibrated: false,
                CameraDistortion.Zero("none"),
                CameraProfile.NoNoise,
                0.0,
                new CameraExtrinsicsProvenance("computed-nadir", "spacecraft-position", isOrientationVerified: false));

        public static DeviceConfiguration NadirConfiguration(SensorDefinition definition) =>
            NadirGroundTruthCameraKind.Instance.Configure(definition, NadirProfile());
    }
}
