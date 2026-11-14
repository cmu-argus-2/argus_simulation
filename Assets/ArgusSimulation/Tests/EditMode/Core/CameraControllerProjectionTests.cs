using System;
using System.Runtime.InteropServices;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    // The camera kind's controller projection (CORE-23): a controller gets the pixels and timing of
    // a camera frame, never its pose or Sun (D7). Only a valid, lit, raw RGB render reaches it with
    // pixels; anything else reaches it Unavailable, while the recorder keeps the original frame.
    public sealed class CameraControllerProjectionTests
    {
        private static readonly CameraIntrinsics Intrinsics = new CameraIntrinsics(4, 2, 3.0, 3.0, 2.0, 1.0);
        private static readonly Vector3d Sun = new Vector3d(1.47e11, 2.0e10, -6.0e10);
        private static readonly DateTimeOffset ExposureStart = new DateTimeOffset(2025, 1, 15, 0, 0, 1, 500, TimeSpan.Zero);

        [Test]
        public void ItTakesImageFramesAndGivesCameraObservations()
        {
            Assert.That(CameraControllerProjection.Instance.InputPayloadType, Is.EqualTo(typeof(ImageFrame)));
            Assert.That(CameraControllerProjection.Instance.OutputPayloadType, Is.EqualTo(typeof(CameraObservation)));
        }

        // The same bytes in a copy of their own: ReadOnlyMemory's array can still be written through
        // MemoryMarshal.TryGetArray, and the recorder queues the frame by reference (RunRecorder,
        // #99), so a controller must not hold the recorder's array (D6, D7).
        [Test]
        public void AValidLitRgb8FrameKeepsItsPixelsAndTiming()
        {
            byte[] pixels = Pixels(3);
            SensorFrame<ImageFrame> frame = Frame(SensorFrameStatus.Valid, new ImageFrame(Request(Sun), ImageEncoding.Rgb8, pixels, "test-renderer"));

            SensorFrame<CameraObservation> projected = Project(frame);

            AssertSameEnvelope(projected, frame);
            Assert.That(projected.Status, Is.EqualTo(SensorFrameStatus.Valid));
            CameraObservation observation = projected.Payload;
            Assert.That((observation.CameraId, observation.Sequence), Is.EqualTo(("cam.px", 7L)));
            Assert.That(
                (observation.ExposureStartSimulationTimeSeconds, observation.ExposureStartUtc, observation.ExposureDurationSeconds),
                Is.EqualTo((1.5, ExposureStart, 0.0005)));
            Assert.That((observation.WidthPixels, observation.HeightPixels), Is.EqualTo((4, 2)));
            Assert.That(observation.Pixels.ToArray(), Is.EqualTo(pixels));
            Assert.That(MemoryMarshal.TryGetArray(observation.Pixels, out ArraySegment<byte> copy), Is.True);
            Assert.That(copy.Array, Is.Not.SameAs(pixels));
            Assert.That(observation.IsValid, Is.True);
        }

        // A controller gets only raw RGB from a valid render lit by the run's Sun. Any other encoding
        // could carry more than pixels (an alpha channel, PNG text chunks); an image with no Sun was
        // lit by a light that isn't the run's (G3: never approximated); an invalid one is not an image.
        [TestCase("png")]
        [TestCase("rgba8")]
        [TestCase("no sun")]
        [TestCase("wrong length")]
        [TestCase("no renderer name")]
        public void AValidFrameTheControllerCannotTakeBecomesUnavailable(string problem)
        {
            ImageFrame image;
            switch (problem)
            {
                case "png":
                    image = new ImageFrame(Request(Sun), ImageEncoding.Png, new byte[] { 137, 80, 78, 71 }, "test-renderer");
                    break;
                case "rgba8":
                    image = new ImageFrame(Request(Sun), ImageEncoding.Rgba8, Pixels(4), "test-renderer");
                    break;
                case "no sun":
                    image = new ImageFrame(Request(null), ImageEncoding.Rgb8, Pixels(3), "test-renderer");
                    break;
                case "wrong length":
                    image = new ImageFrame(Request(Sun), ImageEncoding.Rgb8, new byte[5], "test-renderer");
                    break;
                default:
                    image = new ImageFrame(Request(Sun), ImageEncoding.Rgb8, Pixels(3), null);
                    break;
            }

            SensorFrame<ImageFrame> frame = Frame(SensorFrameStatus.Valid, image);
            SensorFrame<CameraObservation> projected = Project(frame);

            AssertSameEnvelope(projected, frame);
            Assert.That(projected.Status, Is.EqualTo(SensorFrameStatus.Unavailable));
            AssertNoPixels(projected.Payload);
            Assert.That(projected.Payload.IsValid, Is.True);
            Assert.That((projected.Payload.CameraId, projected.Payload.Sequence), Is.EqualTo(("cam.px", 7L)));
            Assert.That(projected.Payload.ExposureStartSimulationTimeSeconds, Is.EqualTo(1.5));
        }

        // A frame that is not Valid never carries pixels, even if it has some, and a frame with no
        // pixels is Unavailable. Only Invalid stays Invalid, so a controller can tell a fault from
        // a frame it may not see.
        [TestCase(SensorFrameStatus.Unavailable, SensorFrameStatus.Unavailable)]
        [TestCase(SensorFrameStatus.Stale, SensorFrameStatus.Unavailable)]
        [TestCase(SensorFrameStatus.Invalid, SensorFrameStatus.Invalid)]
        public void AFrameThatIsNotValidHasNoPixelsAndIsUnavailableUnlessInvalid(SensorFrameStatus status, SensorFrameStatus expected)
        {
            SensorFrame<ImageFrame> frame = Frame(status, new ImageFrame(Request(Sun), ImageEncoding.Rgb8, Pixels(3), "test-renderer"));

            SensorFrame<CameraObservation> projected = Project(frame);

            AssertSameEnvelope(projected, frame);
            Assert.That(projected.Status, Is.EqualTo(expected));
            AssertNoPixels(projected.Payload);
            Assert.That(projected.Payload.IsValid, Is.True);
            Assert.That(projected.Payload.ExposureDurationSeconds, Is.EqualTo(0.0005));
        }

        // An Unavailable camera frame with no request at all (CameraModel.Measure's fallback) gives
        // the request's default values, which name no camera, so the observation is not valid.
        [Test]
        public void ADefaultImageGivesDefaultValues()
        {
            SensorFrame<CameraObservation> projected = Project(Frame(SensorFrameStatus.Unavailable, default));

            Assert.That(projected.Payload.CameraId, Is.Null);
            Assert.That((projected.Payload.Sequence, projected.Payload.ExposureStartSimulationTimeSeconds), Is.EqualTo((0L, 0.0)));
            AssertNoPixels(projected.Payload);
            Assert.That(projected.Payload.IsValid, Is.False);
        }

        private static SensorFrame<CameraObservation> Project(SensorFrame<ImageFrame> frame)
        {
            ISensorFrame projected = CameraControllerProjection.Instance.Project(frame);
            Assert.That(projected, Is.TypeOf<SensorFrame<CameraObservation>>());
            return (SensorFrame<CameraObservation>)projected;
        }

        private static void AssertSameEnvelope(ISensorFrame projected, ISensorFrame frame)
        {
            Assert.That(
                (projected.RunId, projected.SensorId, projected.FrameId, projected.Sequence, projected.StateSequence),
                Is.EqualTo((frame.RunId, frame.SensorId, frame.FrameId, frame.Sequence, frame.StateSequence)));
            Assert.That(
                (projected.SimulationTimeSeconds, projected.TimestampUtc, projected.Source),
                Is.EqualTo((frame.SimulationTimeSeconds, frame.TimestampUtc, frame.Source)));
        }

        private static void AssertNoPixels(CameraObservation observation)
        {
            Assert.That(observation.Pixels.IsEmpty, Is.True);
            Assert.That((observation.WidthPixels, observation.HeightPixels), Is.EqualTo((0, 0)));
        }

        // Frame 7 of cam.px, captured at state 15 and t = 1.5 s.
        private static SensorFrame<ImageFrame> Frame(SensorFrameStatus status, ImageFrame image) =>
            new SensorFrame<ImageFrame>("run-1", "cam.px", "cam_px", 7, 15, 1.5, ExposureStart, status, "argus", image);

        private static RenderRequest Request(Vector3d? sun) =>
            new RenderRequest(
                "cam.px",
                7,
                15,
                1.5,
                ExposureStart,
                0.0005,
                new Vector3d(6_878_137.0, 0.0, 0.0),
                new Quaterniond(0.0, 0.0, 0.0, 1.0),
                Intrinsics,
                sun);

        // A distinct byte per position, bytesPerPixel per pixel.
        private static byte[] Pixels(int bytesPerPixel)
        {
            byte[] pixels = new byte[Intrinsics.WidthPixels * Intrinsics.HeightPixels * bytesPerPixel];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (byte)(i + 1);
            }

            return pixels;
        }
    }
}
