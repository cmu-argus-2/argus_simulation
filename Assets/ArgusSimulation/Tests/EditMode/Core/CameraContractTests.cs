using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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

        // What a controller sees of a camera frame is what flight's own frame record holds (CORE-23
        // C1; FSW-Payload include/vision/frame.hpp): the camera ID, the frame's sequence, the
        // exposure start and length, and raw RGB pixels. No pose, Sun, intrinsics or renderer. Every
        // instance field, compiler backing fields included, is pinned by name and type, so a stored
        // member of any type fails here until the decision changes. The public instance properties
        // are pinned too, to the same set plus the computed IsValid, so a public instance property
        // can't hand a controller truth from elsewhere, such as a static field. Methods and static
        // members are not checked.
        [Test]
        public void CameraObservation_HoldsOnlyWhatFlightsFrameHolds()
        {
            (string Name, Type Type)[] flight =
            {
                ("CameraId", typeof(string)),
                ("Sequence", typeof(long)),
                ("ExposureStartSimulationTimeSeconds", typeof(double)),
                ("ExposureStartUtc", typeof(DateTimeOffset)),
                ("ExposureDurationSeconds", typeof(double)),
                ("WidthPixels", typeof(int)),
                ("HeightPixels", typeof(int)),
                ("Pixels", typeof(ReadOnlyMemory<byte>))
            };
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            Assert.That(
                typeof(CameraObservation).GetFields(instance).Select(field => (BackedMember(field.Name), field.FieldType)),
                Is.EquivalentTo(flight));
            Assert.That(
                typeof(CameraObservation)
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Select(property => (property.Name, property.PropertyType)),
                Is.EquivalentTo(flight.Append(("IsValid", typeof(bool)))));
        }

        // A well-formed observation names its camera, has a non-negative sequence, a finite
        // non-negative exposure start and length (0 is a valid exposure, as in RenderRequest), and
        // either no pixels at size 0 or exactly width x height x 3 bytes of Rgb8.
        [TestCase("with pixels", true)]
        [TestCase("without pixels", true)]
        [TestCase("zero exposure", true)]
        [TestCase("blank camera ID", false)]
        [TestCase("negative sequence", false)]
        [TestCase("negative start", false)]
        [TestCase("start not finite", false)]
        [TestCase("start infinite", false)]
        [TestCase("negative exposure", false)]
        [TestCase("exposure not finite", false)]
        [TestCase("exposure NaN", false)]
        [TestCase("pixels of another size", false)]
        [TestCase("pixels at size 0", false)]
        [TestCase("a size without pixels", false)]
        [TestCase("default", false)]
        public void CameraObservation_IsValidOnlyWhenWellFormed(string shape, bool isValid)
        {
            string cameraId = shape == "blank camera ID" ? " " : "cam.px";
            long sequence = shape == "negative sequence" ? -1 : 7;
            double start = shape == "negative start" ? -0.1
                : shape == "start not finite" ? double.NaN
                : shape == "start infinite" ? double.PositiveInfinity
                : 1.5;
            double exposure = shape == "negative exposure" ? -0.0005
                : shape == "exposure not finite" ? double.PositiveInfinity
                : shape == "exposure NaN" ? double.NaN
                : shape == "zero exposure" ? 0.0
                : 0.0005;
            bool noPixels = shape == "without pixels" || shape == "a size without pixels";
            byte[] pixels = noPixels ? Array.Empty<byte>() : new byte[shape == "pixels of another size" ? 4 * 2 * 4 : 4 * 2 * 3];
            int width = shape == "without pixels" || shape == "pixels at size 0" ? 0 : 4;
            int height = shape == "without pixels" ? 0 : 2;

            CameraObservation observation = shape == "default" ? default : new CameraObservation(
                cameraId,
                sequence,
                start,
                new DateTimeOffset(2025, 1, 15, 0, 0, 1, 500, TimeSpan.Zero),
                exposure,
                width,
                height,
                pixels);

            Assert.That(observation.IsValid, Is.EqualTo(isValid));
        }

        // An auto-property's backing field is named <Property>k__BackingField.
        private static string BackedMember(string fieldName)
        {
            Match backing = Regex.Match(fieldName, "^<(.+)>k__BackingField$");
            return backing.Success ? backing.Groups[1].Value : fieldName;
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
