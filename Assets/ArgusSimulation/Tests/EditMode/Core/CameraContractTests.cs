using System;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class CameraContractTests
    {
        private static readonly CameraIntrinsics Imx708 = new CameraIntrinsics(
            4608,
            2592,
            3200.0,
            3200.0,
            2304.0,
            1296.0);

        [Test]
        public void RenderRequest_IsValidForCalibratedCameraPose()
        {
            Assert.That(Request(new Quaterniond(0.0, 0.0, 0.0, 1.0)).IsValid, Is.True);
        }

        [Test]
        public void CameraIntrinsics_RejectNonPositiveFocalLength()
        {
            CameraIntrinsics intrinsics = new CameraIntrinsics(
                4608,
                2592,
                0.0,
                3200.0,
                2304.0,
                1296.0);

            Assert.That(intrinsics.IsValid, Is.False);
        }

        [Test]
        public void RenderRequest_RejectsNonUnitCameraOrientation()
        {
            Assert.That(Request(new Quaterniond(0.0, 0.0, 0.0, 0.0)).IsValid, Is.False);
        }

        [Test]
        public void RenderRequest_SunPositionIsOptionalButNeverZero()
        {
            Quaterniond identity = new Quaterniond(0.0, 0.0, 0.0, 1.0);

            Assert.That(Request(identity).HasSunPosition, Is.False);
            Assert.That(Request(identity, new Vector3d(1.47e11, 0.0, 0.0)).IsValid, Is.True);
            Assert.That(Request(identity, new Vector3d(0.0, 0.0, 0.0)).IsValid, Is.False);
            Assert.That(Request(identity, new Vector3d(double.NaN, 0.0, 0.0)).IsValid, Is.False);
        }

        [Test]
        public void ImageFrame_TakesItsMetadataFromTheRequest()
        {
            RenderRequest request = Request(new Quaterniond(0.0, 0.0, 0.0, 1.0));
            ImageFrame frame = new ImageFrame(request, ImageEncoding.Png, new byte[] { 1, 2, 3 }, "test");

            Assert.That(frame.IsValid, Is.True);
            Assert.That(frame.CameraId, Is.EqualTo("camera-plus-x"));
            Assert.That(frame.Sequence, Is.EqualTo(12));
            Assert.That(frame.StateSequence, Is.EqualTo(120));
            Assert.That(frame.SimulationTimeSeconds, Is.EqualTo(1.2));
            Assert.That(frame.ExposureDurationSeconds, Is.EqualTo(0.01));
        }

        [Test]
        public void ImageFrame_RawPixelsMustMatchTheIntrinsics()
        {
            RenderRequest request = Request(new Quaterniond(0.0, 0.0, 0.0, 1.0));
            int pixels = Imx708.WidthPixels * Imx708.HeightPixels;

            Assert.That(new ImageFrame(request, ImageEncoding.Rgb8, new byte[pixels * 3], "test").IsValid, Is.True);
            Assert.That(new ImageFrame(request, ImageEncoding.Rgba8, new byte[pixels * 3], "test").IsValid, Is.False);
            Assert.That(new ImageFrame(request, ImageEncoding.Rgb8, new byte[0], "test").IsValid, Is.False);
        }

        private static RenderRequest Request(Quaterniond cameraToEcef, Vector3d? sun = null) =>
            new RenderRequest(
                "camera-plus-x",
                12,
                120,
                1.2,
                new DateTimeOffset(2025, 1, 15, 0, 0, 1, 200, TimeSpan.Zero),
                0.01,
                new Vector3d(6_878_137.0, 0.0, 0.0),
                cameraToEcef,
                Imx708,
                sun);
    }
}
