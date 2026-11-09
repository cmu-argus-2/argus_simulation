using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class CameraModelTests
    {
        private const string RunId = "run-1";
        private const string CameraId = "cam.test";
        private const double StepSeconds = 0.1;
        private static readonly Vector3d SunItrf93 = new Vector3d(1.47e11, 2.0e10, -6.0e10);

        [Test]
        public void LateFrameKeepsItsCaptureStateAndTime()
        {
            TestRenderer renderer = new TestRenderer();
            SensorManager manager = Manager(new TestCamera(1.0, 1.0, renderer));

            Assert.That(manager.Sample(Context(0)).Frames, Is.Empty, "The exposure starts; nothing is published yet.");
            Assert.That(renderer.Requests.Count, Is.EqualTo(1));
            Assert.That(manager.Sample(Context(1)).Frames, Is.Empty);
            Assert.That(manager.Sample(Context(2)).Frames, Is.Empty);

            renderer.Complete(0);
            SensorOutputSet arrival = manager.Sample(Context(3));

            Assert.That(arrival.SpacecraftSequence, Is.EqualTo(3));
            Assert.That(arrival.TryGetFrame(CameraId, out SensorFrame<ImageFrame> frame), Is.True);
            Assert.That(frame.Status, Is.EqualTo(SensorFrameStatus.Valid));
            Assert.That(frame.Sequence, Is.EqualTo(0));
            Assert.That(frame.SimulationTimeSeconds, Is.EqualTo(0.0));
            Assert.That(frame.TimestampUtc, Is.EqualTo(Utc(0)));
            Assert.That(frame.Payload.IsValid, Is.True);
            Assert.That(frame.Payload.StateSequence, Is.EqualTo(0));
            Assert.That(frame.Payload.SimulationTimeSeconds, Is.EqualTo(0.0));
            Assert.That(frame.Payload.Request.SunPositionEcefMeters, Is.EqualTo(SunItrf93));
            Assert.That(frame.Payload.Renderer, Is.EqualTo("test-renderer"));
        }

        // Captured at state 10 and published at state 13, whether rendered or timed out.
        [TestCase(false)]
        [TestCase(true)]
        public void LateOrTimedOutFrameKeepsItsCaptureState(bool timesOut)
        {
            TestRenderer renderer = new TestRenderer();
            SensorManager manager = Manager(new TestCamera(1.0, timesOut ? 0.25 : double.PositiveInfinity, renderer));
            manager.Sample(Context(0));
            renderer.Complete(0);
            Single(manager.Sample(Context(1)));
            for (long step = 2; step <= 12; step++)
            {
                Assert.That(manager.Sample(Context(step)).Frames, Is.Empty, $"step {step}");
            }

            if (!timesOut)
            {
                renderer.Complete(1);
            }

            SensorOutputSet arrival = manager.Sample(Context(13));
            Assert.That(arrival.IsValid, Is.True);
            Assert.That(arrival.SpacecraftSequence, Is.EqualTo(13));
            SensorFrame<ImageFrame> frame = Single(arrival);
            Assert.That(frame.Status, Is.EqualTo(timesOut ? SensorFrameStatus.Unavailable : SensorFrameStatus.Valid));
            Assert.That(frame.Sequence, Is.EqualTo(1));
            Assert.That(frame.StateSequence, Is.EqualTo(10));
            Assert.That(frame.SimulationTimeSeconds, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(frame.Payload.CameraId, Is.EqualTo(CameraId));
            Assert.That(frame.Payload.StateSequence, Is.EqualTo(10));
            Assert.That(frame.Payload.Data, timesOut ? Is.Null : Is.Not.Null);
        }

        [Test]
        public void FramesArePublishedInSequenceOrder()
        {
            TestRenderer renderer = new TestRenderer();
            SensorManager manager = Manager(new TestCamera(0.2, double.PositiveInfinity, renderer));

            manager.Sample(Context(0));
            manager.Sample(Context(1));
            manager.Sample(Context(2));
            Assert.That(renderer.Requests.Count, Is.EqualTo(2));

            // The second exposure finishes first; it waits behind the first.
            renderer.Complete(1);
            Assert.That(manager.Sample(Context(3)).Frames, Is.Empty);

            renderer.Complete(0);
            Assert.That(Single(manager.Sample(Context(4))).Sequence, Is.EqualTo(0));
            SensorFrame<ImageFrame> second = Single(manager.Sample(Context(5)));
            Assert.That(second.Sequence, Is.EqualTo(1));
            Assert.That(second.SimulationTimeSeconds, Is.EqualTo(0.2).Within(1e-12));
            Assert.That(second.Payload.StateSequence, Is.EqualTo(2));
        }

        [Test]
        public void RenderPastTheLatencyLimitIsUnavailableAndCancelled()
        {
            TestRenderer renderer = new TestRenderer();
            SensorManager manager = Manager(new TestCamera(1.0, 0.25, renderer));

            manager.Sample(Context(0));
            Assert.That(manager.Sample(Context(1)).Frames, Is.Empty);
            Assert.That(manager.Sample(Context(2)).Frames, Is.Empty);

            SensorFrame<ImageFrame> frame = Single(manager.Sample(Context(3)));
            Assert.That(frame.Status, Is.EqualTo(SensorFrameStatus.Unavailable));
            Assert.That(frame.Sequence, Is.EqualTo(0));
            Assert.That(frame.SimulationTimeSeconds, Is.EqualTo(0.0));
            Assert.That(renderer.Tokens[0].IsCancellationRequested, Is.True);

            // A render that finishes after it was given up is never published.
            renderer.Complete(0);
            Assert.That(manager.Sample(Context(4)).Frames, Is.Empty);
        }

        [Test]
        public void WithoutARendererTheCameraIsUnavailableAtEachExposure()
        {
            SensorManager manager = Manager(new TestCamera(0.5, 1.0, null));

            SensorFrame<ImageFrame> first = Single(manager.Sample(Context(0)));
            Assert.That(first.Status, Is.EqualTo(SensorFrameStatus.Unavailable));
            Assert.That(first.Payload.Data, Is.Null);
            Assert.That(first.Payload.CameraId, Is.EqualTo(CameraId));
            Assert.That(manager.Sample(Context(1)).Frames, Is.Empty);
            Assert.That(Single(manager.Sample(Context(5))).Sequence, Is.EqualTo(1));
        }

        [Test]
        public void WithoutAnEnvironmentTheCameraRendersWithNoSunPosition()
        {
            TestRenderer renderer = new TestRenderer();
            SensorManager manager = Manager(new TestCamera(1.0, 1.0, renderer));

            manager.Sample(Context(0, withEnvironment: false));
            Assert.That(renderer.Requests[0].HasSunPosition, Is.False);
            Assert.That(renderer.Requests[0].IsValid, Is.True);

            renderer.Complete(0);
            SensorFrame<ImageFrame> frame = Single(manager.Sample(Context(1, withEnvironment: false)));
            Assert.That(frame.Status, Is.EqualTo(SensorFrameStatus.Valid));
            Assert.That(frame.Payload.Request.HasSunPosition, Is.False);
        }

        [TestCase("fault")]
        [TestCase("cancel")]
        [TestCase("throw")]
        [TestCase("wrong sequence")]
        [TestCase("wrong size")]
        public void FailedOrMismatchedRenderIsUnavailable(string failure)
        {
            TestRenderer renderer = new TestRenderer { ThrowOnRender = failure == "throw" };
            SensorManager manager = Manager(new TestCamera(1.0, 1.0, renderer));
            SensorOutputSet started = manager.Sample(Context(0));
            if (failure == "throw")
            {
                // Nothing to wait for: the exposure is Unavailable at its own step.
                Assert.That(Single(started).Status, Is.EqualTo(SensorFrameStatus.Unavailable));
                Assert.That(Single(started).Payload.CameraId, Is.EqualTo(CameraId));
                return;
            }

            Assert.That(started.Frames, Is.Empty);
            switch (failure)
            {
                case "fault":
                    renderer.Fail(0);
                    break;
                case "cancel":
                    renderer.Cancel(0);
                    break;
                case "wrong sequence":
                    renderer.Complete(0, sequenceOffset: 1);
                    break;
                case "wrong size":
                    renderer.Complete(0, pixelBytes: 5);
                    break;
            }

            SensorFrame<ImageFrame> frame = Single(manager.Sample(Context(1)));
            Assert.That(frame.Status, Is.EqualTo(SensorFrameStatus.Unavailable));
            Assert.That(frame.Payload.Data, Is.Null);
            Assert.That(frame.Payload.CameraId, Is.EqualTo(CameraId));
        }

        [Test]
        public void BodyMountedPoseAppliesTheMount()
        {
            TestRenderer renderer = new TestRenderer();
            // Sensor z along body +x, mounted 0.05 m out on body +x.
            SensorMount mount = new SensorMount(
                new Vector3d(0.05, 0.0, 0.0),
                Quaterniond.FromBasis(new Vector3d(0.0, 1.0, 0.0), new Vector3d(0.0, 0.0, 1.0), new Vector3d(1.0, 0.0, 0.0)));
            SensorManager manager = Manager(new TestCamera(1.0, 1.0, renderer, mount));

            manager.Sample(Context(0));

            RenderRequest request = renderer.Requests[0];
            SpacecraftState state = State(0, ReferenceFrame.Itrf93);
            Vector3d expectedPosition = state.PositionEcefMeters + state.BodyToEcef.Rotate(mount.PositionBodyMeters);
            Assert.That((request.CameraPositionEcefMeters - expectedPosition).Magnitude, Is.LessThan(1e-9));
            Vector3d boresight = request.CameraToEcef.Rotate(new Vector3d(0.0, 0.0, 1.0));
            Vector3d bodyX = state.BodyToEcef.Rotate(new Vector3d(1.0, 0.0, 0.0));
            Assert.That((boresight - bodyX).Magnitude, Is.LessThan(1e-12));
        }

        [Test]
        public void ResetAbandonsPendingExposuresAndRestartsTheSequence()
        {
            TestRenderer renderer = new TestRenderer();
            TestCamera camera = new TestCamera(1.0, double.PositiveInfinity, renderer);
            SensorManager manager = Manager(camera);
            manager.Sample(Context(0));
            Assert.That(camera.PendingExposureCount, Is.EqualTo(1));

            manager.Reset(new SensorResetContext("run-2", DateTimeOffset.UnixEpoch, 2));
            Assert.That(camera.PendingExposureCount, Is.EqualTo(0));
            Assert.That(renderer.Tokens[0].IsCancellationRequested, Is.True);

            manager.Sample(new SensorSampleContext("run-2", StateWithEnvironment("run-2", 0)));
            renderer.Complete(1);
            SensorOutputSet output = manager.Sample(new SensorSampleContext("run-2", StateWithEnvironment("run-2", 1)));
            SensorFrame<ImageFrame> frame = Single(output);
            Assert.That(frame.RunId, Is.EqualTo("run-2"));
            Assert.That(frame.Sequence, Is.EqualTo(0));
        }

        private static SensorManager Manager(CameraModel camera)
        {
            SensorManager manager = new SensorManager();
            manager.Register(camera);
            manager.Reset(new SensorResetContext(RunId, DateTimeOffset.UnixEpoch, 0));
            return manager;
        }

        private static SensorFrame<ImageFrame> Single(SensorOutputSet output)
        {
            Assert.That(output.Frames.Count, Is.EqualTo(1));
            Assert.That(output.TryGetFrame(CameraId, out SensorFrame<ImageFrame> frame), Is.True);
            return frame;
        }

        private static SensorSampleContext Context(long step, bool withEnvironment = true) =>
            withEnvironment
                ? new SensorSampleContext(RunId, StateWithEnvironment(RunId, step))
                : new SensorSampleContext(RunId, new SimulationState(
                    RunId,
                    "analytic",
                    State(step, ReferenceFrame.AnalyticEarthFixed),
                    null));

        private static SimulationState StateWithEnvironment(string runId, long step) =>
            new SimulationState(
                runId,
                "basilisk",
                State(step, ReferenceFrame.Itrf93),
                null,
                new EnvironmentState(
                    SunItrf93,
                    new Quaterniond(0.0, 0.0, 0.0, 1.0),
                    new Vector3d(0.0, 0.0, 7.2921150e-5),
                    1.0));

        private static SpacecraftState State(long step, ReferenceFrame frame) =>
            new SpacecraftState(
                step,
                step * StepSeconds,
                Utc(step),
                new Vector3d(6_878_137.0, 0.0, 0.0),
                new Vector3d(0.0, 7_600.0, 0.0),
                new Quaterniond(0.0, 0.0, 0.38268343236508978, 0.92387953251128674),
                new Vector3d(0.0, 0.0, 0.0),
                frame);

        private static DateTimeOffset Utc(long step) =>
            new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero).AddSeconds(step * StepSeconds);

        private sealed class TestCamera : CameraModel
        {
            public static readonly CameraIntrinsics TestIntrinsics = new CameraIntrinsics(4, 2, 3.0, 3.0, 2.0, 1.0);

            public TestCamera(double periodSeconds, double maximumLatencySeconds, IImageRenderer renderer, SensorMount? mount = null)
                : base(
                    new SensorDefinition(CameraId, "test-camera", "cam_test", periodSeconds, "simulation", mount ?? SensorMount.Identity),
                    TestIntrinsics,
                    0.01,
                    maximumLatencySeconds,
                    renderer)
            {
            }
        }

        // Each render completes only when the test says so.
        private sealed class TestRenderer : IImageRenderer
        {
            private readonly List<TaskCompletionSource<ImageFrame>> _renders = new List<TaskCompletionSource<ImageFrame>>();

            public string RendererName => "test-renderer";
            public bool ThrowOnRender { get; set; }
            public List<RenderRequest> Requests { get; } = new List<RenderRequest>();
            public List<CancellationToken> Tokens { get; } = new List<CancellationToken>();

            public Task<ImageFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
            {
                if (ThrowOnRender)
                {
                    throw new InvalidOperationException("Renderer is not ready.");
                }

                Requests.Add(request);
                Tokens.Add(cancellationToken);
                TaskCompletionSource<ImageFrame> render = new TaskCompletionSource<ImageFrame>();
                _renders.Add(render);
                return render.Task;
            }

            public void Complete(int index, long sequenceOffset = 0, int pixelBytes = -1)
            {
                RenderRequest request = Requests[index];
                if (sequenceOffset != 0)
                {
                    request = new RenderRequest(
                        request.CameraId,
                        request.Sequence + sequenceOffset,
                        request.StateSequence,
                        request.ExposureStartSimulationTimeSeconds,
                        request.ExposureStartUtc,
                        request.ExposureDurationSeconds,
                        request.CameraPositionEcefMeters,
                        request.CameraToEcef,
                        request.Intrinsics,
                        request.SunPositionEcefMeters);
                }

                int length = pixelBytes >= 0
                    ? pixelBytes
                    : request.Intrinsics.WidthPixels * request.Intrinsics.HeightPixels * 3;
                byte[] pixels = new byte[length];
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = 128;
                }

                _renders[index].SetResult(new ImageFrame(request, ImageEncoding.Rgb8, pixels, RendererName));
            }

            public void Fail(int index) => _renders[index].SetException(new InvalidOperationException("GPU lost."));

            public void Cancel(int index) => _renders[index].SetCanceled();
        }
    }
}
