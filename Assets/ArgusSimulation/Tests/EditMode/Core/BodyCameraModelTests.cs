using System;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class BodyCameraModelTests
    {
        private const string RunId = "run-1";
        private const double Tolerance = 1e-9;
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero);
        private static readonly Vector3d SpacecraftPosition = new Vector3d(6_878_137.0, 0.0, 0.0);

        // The camera frame is OpenCV's: x is image right, y is image down, z points out of the lens.
        // DATA-6 (#71) gives the +X camera R (camera to body) with columns (0,-1,0), (0,0,-1),
        // (1,0,0): it looks along body +X with image up on body +Z. A request's pose is the body
        // attitude applied after the mount, so with the body turned 90 degrees about the Earth-fixed
        // z axis the camera looks along +Y and its mount offset turns with the body.
        [Test]
        public void RequestPoseIsTheMountTurnedByTheBodyAttitude()
        {
            Vector3d mountPositionBody = new Vector3d(0.05116, -0.000198, -0.02927);
            SensorMount plusX = new SensorMount(
                mountPositionBody,
                Quaterniond.FromBasis(new Vector3d(0, -1, 0), new Vector3d(0, 0, -1), new Vector3d(1, 0, 0)));
            Quaterniond bodyTurnedAboutZ = new Quaterniond(0.0, 0.0, Math.Sqrt(0.5), Math.Sqrt(0.5));

            RenderRequest request = Expose(plusX, bodyTurnedAboutZ);

            AssertClose(request.CameraToEcef.Rotate(new Vector3d(0, 0, 1)), new Vector3d(0, 1, 0), "boresight (camera +z)");
            AssertClose(request.CameraToEcef.Rotate(new Vector3d(1, 0, 0)), new Vector3d(1, 0, 0), "image right (camera +x)");
            AssertClose(request.CameraToEcef.Rotate(new Vector3d(0, -1, 0)), new Vector3d(0, 0, 1), "image up (camera -y)");
            AssertClose(
                request.CameraPositionEcefMeters,
                new Vector3d(SpacecraftPosition.X + 0.000198, 0.05116, -0.02927),
                "camera position");
        }

        // #42 acceptance: the intrinsics built from the profile match the datasheet. The Arducam IMX708
        // has 4608 x 2592 pixels and a 66 degree horizontal field of view, so fx = 2304 / tan(33 deg),
        // with square pixels and the flight config's principal point (W / 2, H / 2) (DATA-6 #71). The
        // shipped P0 run file is checked against the same values in the Host tests.
        [Test]
        public void IntrinsicsBuiltFromTheProfileMatchTheDatasheet()
        {
            double focalLengthFromFieldOfView = 4608 / 2.0 / Math.Tan(66.0 / 2.0 * Math.PI / 180.0);

            CameraIntrinsics intrinsics = Expose(SensorMount.Identity, new Quaterniond(0.0, 0.0, 0.0, 1.0)).Intrinsics;

            Assert.That((intrinsics.WidthPixels, intrinsics.HeightPixels), Is.EqualTo((4608, 2592)));
            Assert.That(intrinsics.FocalLengthXPixels, Is.EqualTo(focalLengthFromFieldOfView).Within(0.5));
            Assert.That(intrinsics.FocalLengthYPixels, Is.EqualTo(intrinsics.FocalLengthXPixels));
            Assert.That((intrinsics.PrincipalPointXPixels, intrinsics.PrincipalPointYPixels), Is.EqualTo((2304.0, 1296.0)));
        }

        private static RenderRequest Expose(SensorMount mount, Quaterniond bodyToEcef)
        {
            DeviceConfiguration configuration = TestCameras.Configuration(
                new SensorDefinition("cam.px", "arducam-imx708", "cam_px", 0.1, "argus", mount, isControllerVisible: false));
            BodyCameraModel camera = new BodyCameraModel(configuration, null);
            camera.Reset(new SensorResetContext(RunId, Epoch, 0));

            SimulationState state = new SimulationState(
                RunId,
                "analytic",
                new SpacecraftState(
                    0,
                    0.0,
                    Epoch,
                    SpacecraftPosition,
                    new Vector3d(0.0, 7_600.0, 0.0),
                    bodyToEcef,
                    new Vector3d(0.0, 0.0, 0.0),
                    ReferenceFrame.AnalyticEarthFixed),
                null);
            Assert.That(camera.TrySample(new SensorSampleContext(RunId, state), out ISensorFrame frame), Is.True);
            return ((SensorFrame<ImageFrame>)frame).Payload.Request;
        }

        private static void AssertClose(Vector3d actual, Vector3d expected, string what)
        {
            Assert.That(actual.X, Is.EqualTo(expected.X).Within(Tolerance), what + " x");
            Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(Tolerance), what + " y");
            Assert.That(actual.Z, Is.EqualTo(expected.Z).Within(Tolerance), what + " z");
        }
    }
}
