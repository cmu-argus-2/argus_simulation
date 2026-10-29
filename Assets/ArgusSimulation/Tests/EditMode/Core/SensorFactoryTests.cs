using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class SensorFactoryTests
    {
        private const string RunId = "run-1";
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero);
        private static readonly CameraIntrinsics Imx708 = new CameraIntrinsics(4608, 2592, 3548.0, 3548.0, 2304.0, 1296.0);

        [Test]
        public void BuildsBackendModelsForBackendKindsAndTheCameraModelForTheCamera()
        {
            Assert.That(SensorFactory.Create(Imu()), Is.InstanceOf<ImuSensor>());

            ISensor sensor = SensorFactory.Create(Camera());

            Assert.That(sensor, Is.InstanceOf<ArducamImx708CameraModel>());
            ArducamImx708CameraModel camera = (ArducamImx708CameraModel)sensor;
            Assert.That(camera.Definition.SensorId, Is.EqualTo("cam.px"));
            Assert.That(camera.Intrinsics, Is.EqualTo(Imx708));
            Assert.That(camera.ExposureDurationSeconds, Is.EqualTo(0.0005));
            Assert.That(camera.MaximumLatencySeconds, Is.EqualTo(double.PositiveInfinity));
            Assert.Throws<ArgumentException>(() => new ArducamImx708CameraModel(Imu(), null));
        }

        // The host or Unity supplies the renderer (CORE-18). Without one, every exposure is
        // Unavailable, never a black image (D5).
        [Test]
        public void CameraRendersThroughTheSuppliedRendererOrPublishesUnavailable()
        {
            RecordingRenderer renderer = new RecordingRenderer();
            ISensor rendered = SensorFactory.Create(Camera(), renderer);
            rendered.Reset(new SensorResetContext(RunId, Epoch, 0));

            Assert.That(rendered.TrySample(Context(0), out _), Is.False, "The exposure starts; nothing is published yet.");
            Assert.That(renderer.Requests.Count, Is.EqualTo(1));
            Assert.That(renderer.Requests[0].CameraId, Is.EqualTo("cam.px"));
            Assert.That(renderer.Requests[0].Intrinsics, Is.EqualTo(Imx708));

            ISensor unrendered = SensorFactory.Create(Camera());
            unrendered.Reset(new SensorResetContext(RunId, Epoch, 0));

            Assert.That(unrendered.TrySample(Context(0), out ISensorFrame frame), Is.True);
            Assert.That(frame.SensorId, Is.EqualTo("cam.px"));
            Assert.That(frame.Status, Is.EqualTo(SensorFrameStatus.Unavailable));
        }

        private static SensorConfiguration Imu()
        {
            Vector3d zero = new Vector3d(0.0, 0.0, 0.0);
            Vector3d unitScale = new Vector3d(1.0, 1.0, 1.0);
            return SensorConfiguration.ForImu(
                new SensorDefinition("imu.main", "test-imu", "imu_main", 0.1, "basilisk", SensorMount.Identity),
                new ImuProfile(
                    new GyroscopeProfile(1e-4, 0.0, 0.0, zero, unitScale, 4.0, 1e-4),
                    new AccelerometerProfile(1e-3, 0.0, 0.0, zero, unitScale, 20.0, 1e-3)));
        }

        private static SensorConfiguration Camera() =>
            SensorConfiguration.ForCamera(
                new SensorDefinition("cam.px", "arducam-imx708", "cam_px", 1.0, "argus", SensorMount.Identity),
                new CameraProfile(Imx708, 0.0005));

        // An analytic state: the camera still renders without an EnvironmentState.
        private static SensorSampleContext Context(long step) =>
            new SensorSampleContext(RunId, new SimulationState(
                RunId,
                "analytic",
                new SpacecraftState(
                    step,
                    step * 0.1,
                    Epoch.AddSeconds(step * 0.1),
                    new Vector3d(6_878_137.0, 0.0, 0.0),
                    new Vector3d(0.0, 7_600.0, 0.0),
                    new Quaterniond(0.0, 0.0, 0.0, 1.0),
                    new Vector3d(0.0, 0.0, 0.0),
                    ReferenceFrame.AnalyticEarthFixed),
                null));

        // Records each request; no render ever completes.
        private sealed class RecordingRenderer : IImageRenderer
        {
            public List<RenderRequest> Requests { get; } = new List<RenderRequest>();

            public string RendererName => "recording-renderer";

            public Task<ImageFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                return new TaskCompletionSource<ImageFrame>().Task;
            }
        }
    }
}
