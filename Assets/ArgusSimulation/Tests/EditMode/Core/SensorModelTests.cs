using System;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class SensorModelTests
    {
        [Test]
        public void SensorModel_PublishesTypedFrameAtConfiguredCadence()
        {
            SensorManager manager = new SensorManager();
            manager.Register(new AngularRateSensor(0.5));
            manager.Reset(new SensorResetContext("run-1", DateTimeOffset.UnixEpoch, 42));

            SensorOutputSet first = manager.Sample(Context(0, 0.0));
            SensorOutputSet early = manager.Sample(Context(1, 0.25));
            SensorOutputSet second = manager.Sample(Context(2, 0.5));

            Assert.That(first.IsValid, Is.True);
            Assert.That(first.TryGetFrame("imu.gyroscope", out SensorFrame<Vector3d> firstFrame), Is.True);
            Assert.That(firstFrame.Sequence, Is.EqualTo(0));
            Assert.That(firstFrame.FrameId, Is.EqualTo("imu_link"));
            Assert.That(firstFrame.Status, Is.EqualTo(SensorFrameStatus.Valid));
            Assert.That(firstFrame.Payload, Is.EqualTo(new Vector3d(0.01, -0.02, 0.03)));
            Assert.That(early.Frames, Is.Empty);
            Assert.That(second.TryGetFrame("imu.gyroscope", out SensorFrame<Vector3d> secondFrame), Is.True);
            Assert.That(secondFrame.Sequence, Is.EqualTo(1));
        }

        [Test]
        public void SensorModel_ResetRestartsSensorSequence()
        {
            SensorManager manager = new SensorManager();
            manager.Register(new AngularRateSensor(0.1));
            manager.Reset(new SensorResetContext("run-1", DateTimeOffset.UnixEpoch, 1));
            manager.Sample(Context(0, 0.0));

            manager.Reset(new SensorResetContext("run-2", DateTimeOffset.UnixEpoch, 2));
            SensorOutputSet restarted = manager.Sample(new SensorSampleContext(
                "run-2",
                State(0, 0.0)));

            Assert.That(restarted.TryGetFrame("imu.gyroscope", out SensorFrame<Vector3d> frame), Is.True);
            Assert.That(frame.RunId, Is.EqualTo("run-2"));
            Assert.That(frame.Sequence, Is.EqualTo(0));
        }

        [Test]
        public void Register_RejectsDuplicateSensorIds()
        {
            SensorManager manager = new SensorManager();
            manager.Register(new AngularRateSensor(0.1));

            Assert.Throws<ArgumentException>(() =>
                manager.Register(new AngularRateSensor(0.2)));
        }

        [Test]
        public void SensorStatus_CanExplicitlyReportUnavailable()
        {
            SensorManager manager = new SensorManager();
            manager.Register(new UnavailableSensor());
            manager.Reset(new SensorResetContext("run-1", DateTimeOffset.UnixEpoch, 0));

            SensorOutputSet output = manager.Sample(Context(0, 0.0));

            Assert.That(output.TryGetFrame("external.temperature", out SensorFrame<double> frame), Is.True);
            Assert.That(frame.Status, Is.EqualTo(SensorFrameStatus.Unavailable));
            Assert.That(frame.HasValidEnvelope, Is.True);
        }

        private static SensorSampleContext Context(long sequence, double time) =>
            new SensorSampleContext("run-1", State(sequence, time));

        private static SpacecraftState State(long sequence, double time) =>
            new SpacecraftState(
                sequence,
                time,
                DateTimeOffset.UnixEpoch.AddSeconds(time),
                new Vector3d(6_878_137.0, 0.0, 0.0),
                new Vector3d(0.0, 7_600.0, 0.0),
                new Quaterniond(0.0, 0.0, 0.0, 1.0),
                new Vector3d(0.01, -0.02, 0.03));

        private sealed class AngularRateSensor : SensorModel<Vector3d>
        {
            public AngularRateSensor(double samplePeriodSeconds)
                : base(new SensorDefinition(
                    "imu.gyroscope",
                    "test-angular-rate",
                    "imu_link",
                    samplePeriodSeconds,
                    "simulation",
                    SensorMount.Identity))
            {
            }

            protected override SensorFrameStatus Measure(
                SensorSampleContext context,
                out Vector3d payload)
            {
                payload = context.Spacecraft.AngularVelocityBodyRadiansPerSecond;
                return SensorFrameStatus.Valid;
            }
        }

        private sealed class UnavailableSensor : SensorModel<double>
        {
            public UnavailableSensor()
                : base(new SensorDefinition(
                    "external.temperature",
                    "test-unavailable",
                    "sensor_link",
                    1.0,
                    "physical",
                    SensorMount.Identity))
            {
            }

            protected override SensorFrameStatus Measure(
                SensorSampleContext context,
                out double payload)
            {
                payload = default;
                return SensorFrameStatus.Unavailable;
            }
        }
    }
}
