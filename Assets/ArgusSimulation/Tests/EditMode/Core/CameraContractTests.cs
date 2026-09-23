using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class CameraContractTests
    {
        [Test]
        public void RenderRequest_IsValidForCalibratedCameraPose()
        {
            CameraIntrinsics intrinsics = new CameraIntrinsics(
                4608,
                2592,
                3200.0,
                3200.0,
                2304.0,
                1296.0);
            RenderRequest request = new RenderRequest(
                "camera-plus-x",
                12,
                1.2,
                0.01,
                new Vector3d(6_878_137.0, 0.0, 0.0),
                new Quaterniond(0.0, 0.0, 0.0, 1.0),
                intrinsics);

            Assert.That(request.IsValid, Is.True);
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
            CameraIntrinsics intrinsics = new CameraIntrinsics(
                4608,
                2592,
                3200.0,
                3200.0,
                2304.0,
                1296.0);
            RenderRequest request = new RenderRequest(
                "camera-plus-x",
                12,
                1.2,
                0.01,
                new Vector3d(6_878_137.0, 0.0, 0.0),
                new Quaterniond(0.0, 0.0, 0.0, 0.0),
                intrinsics);

            Assert.That(request.IsValid, Is.False);
        }
    }
}
