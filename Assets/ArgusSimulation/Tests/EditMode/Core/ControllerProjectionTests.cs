using System;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    // The gateway shows a controller each visible frame through its kind's projection (D7, CORE-23).
    // Project checks the payload type it takes and the one it returns, and that the envelope is kept,
    // so a frame can't reach a controller as a type its kind did not declare. Its messages reach the
    // controller as a termination reason, so they name sensor IDs and types, never payload values.
    public sealed class ControllerProjectionTests
    {
        private static readonly DateTimeOffset Utc = new DateTimeOffset(2025, 1, 15, 0, 0, 4, TimeSpan.Zero);

        [Test]
        public void IdentityDeclaresItsPayloadOnBothSidesAndReturnsTheFrame()
        {
            ControllerProjection identity = ControllerProjection.Identity<ImuMeasurement>();
            SensorFrame<ImuMeasurement> frame = Frame(Imu(0.125));

            ISensorFrame projected = identity.Project(frame);

            Assert.That((identity.InputPayloadType, identity.OutputPayloadType), Is.EqualTo((typeof(ImuMeasurement), typeof(ImuMeasurement))));
            Assert.That(projected, Is.TypeOf<SensorFrame<ImuMeasurement>>());
            Assert.That(((SensorFrame<ImuMeasurement>)projected).Payload, Is.EqualTo(frame.Payload));
            Assert.That(projected, Is.EqualTo(frame));
        }

        [Test]
        public void AFrameOfAnotherPayloadIsRefusedNamingOnlyTheSensorAndTheTypes()
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => ControllerProjection.Identity<ImuMeasurement>().Project(Frame(new AngularRateMeasurement(new Vector3d(0.125, 0.0, 0.0)))));

            Assert.That(error.Message, Is.EqualTo(
                "Sensor 'imu.main' published a AngularRateMeasurement payload; its kind's controller projection takes ImuMeasurement."));
            Assert.Throws<ArgumentNullException>(() => ControllerProjection.Identity<ImuMeasurement>().Project(null));
        }

        // A projection may drop truth and lower the status (a camera frame without a Sun becomes
        // Unavailable), but not the payload type it declared or the envelope a recorded frame joins on.
        [TestCase("Status", null)]
        [TestCase("payload type", "returned a AngularRateMeasurement payload, not its declared ImuMeasurement.")]
        [TestCase("no frame", "returned no frame, not its declared ImuMeasurement.")]
        [TestCase("RunId", "changed the frame's RunId.")]
        [TestCase("SensorId", "changed the frame's SensorId.")]
        [TestCase("FrameId", "changed the frame's FrameId.")]
        [TestCase("Sequence", "changed the frame's Sequence.")]
        [TestCase("StateSequence", "changed the frame's StateSequence.")]
        [TestCase("SimulationTimeSeconds", "changed the frame's SimulationTimeSeconds.")]
        [TestCase("TimestampUtc", "changed the frame's TimestampUtc.")]
        [TestCase("Source", "changed the frame's Source.")]
        public void AProjectionMayNotChangeItsDeclaredPayloadOrTheEnvelope(string change, string problem)
        {
            ControllerProjection projection = new Changing(change);
            SensorFrame<ImuMeasurement> frame = Frame(Imu(0.125));

            if (problem == null)
            {
                Assert.That(projection.Project(frame).Status, Is.EqualTo(SensorFrameStatus.Unavailable));
                return;
            }

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => projection.Project(frame));
            Assert.That(error.Message, Is.EqualTo("The controller projection of sensor 'imu.main' " + problem));
            Assert.That(error.Message, Does.Not.Contain("0.125"));
        }

        // A projection keeps a frame's status or makes it Unavailable to withhold its payload; any other
        // change would show a controller a payload its sensor didn't produce (Stale and Valid carry one),
        // or hide a fault (D7).
        [TestCase(SensorFrameStatus.Valid, SensorFrameStatus.Unavailable, true)]
        [TestCase(SensorFrameStatus.Stale, SensorFrameStatus.Unavailable, true)]
        [TestCase(SensorFrameStatus.Invalid, SensorFrameStatus.Invalid, true)]
        [TestCase(SensorFrameStatus.Unavailable, SensorFrameStatus.Valid, false)]
        [TestCase(SensorFrameStatus.Stale, SensorFrameStatus.Valid, false)]
        [TestCase(SensorFrameStatus.Invalid, SensorFrameStatus.Valid, false)]
        [TestCase(SensorFrameStatus.Unavailable, SensorFrameStatus.Stale, false)]
        [TestCase(SensorFrameStatus.Valid, SensorFrameStatus.Stale, false)]
        [TestCase(SensorFrameStatus.Invalid, SensorFrameStatus.Unavailable, false)]
        public void AProjectionMayOnlyKeepTheStatusOrMakeItUnavailable(
            SensorFrameStatus input, SensorFrameStatus output, bool allowed)
        {
            SensorFrame<ImuMeasurement> frame = Frame(Imu(0.125), input);
            ControllerProjection projection = new Restating(output);

            if (allowed)
            {
                Assert.That(projection.Project(frame).Status, Is.EqualTo(output));
                return;
            }

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => projection.Project(frame));
            Assert.That(
                error.Message,
                Is.EqualTo($"The controller projection of sensor 'imu.main' changed a {input} frame to {output}."));
            Assert.That(error.Message, Does.Not.Contain("0.125"));
        }

        private static ImuMeasurement Imu(double rate) =>
            new ImuMeasurement(new Vector3d(rate, 0.0, 0.0), new Vector3d(0.0, 0.0, 0.0));

        private static SensorFrame<T> Frame<T>(T payload, SensorFrameStatus status = SensorFrameStatus.Valid) =>
            new SensorFrame<T>("run-1", "imu.main", "imu_main", 4, 40, 4.0, Utc, status, "basilisk", payload);

        // An identity on ImuMeasurement that gives every frame one status.
        private sealed class Restating : ControllerProjection
        {
            private readonly SensorFrameStatus _status;

            public Restating(SensorFrameStatus status)
            {
                _status = status;
            }

            public override Type InputPayloadType => typeof(ImuMeasurement);
            public override Type OutputPayloadType => typeof(ImuMeasurement);

            protected override ISensorFrame ProjectCore(ISensorFrame frame)
            {
                SensorFrame<ImuMeasurement> f = (SensorFrame<ImuMeasurement>)frame;
                return new SensorFrame<ImuMeasurement>(
                    f.RunId, f.SensorId, f.FrameId, f.Sequence, f.StateSequence, f.SimulationTimeSeconds,
                    f.TimestampUtc, _status, f.Source, f.Payload);
            }
        }

        // An identity on ImuMeasurement that changes one thing.
        private sealed class Changing : ControllerProjection
        {
            private readonly string _change;

            public Changing(string change)
            {
                _change = change;
            }

            public override Type InputPayloadType => typeof(ImuMeasurement);
            public override Type OutputPayloadType => typeof(ImuMeasurement);

            protected override ISensorFrame ProjectCore(ISensorFrame frame)
            {
                SensorFrame<ImuMeasurement> f = (SensorFrame<ImuMeasurement>)frame;
                switch (_change)
                {
                    case "payload type":
                        return new SensorFrame<AngularRateMeasurement>(
                            f.RunId, f.SensorId, f.FrameId, f.Sequence, f.StateSequence, f.SimulationTimeSeconds, f.TimestampUtc,
                            f.Status, f.Source, new AngularRateMeasurement(f.Payload.AngularVelocitySensorRadiansPerSecond));
                    case "no frame":
                        return null;
                    default:
                        return new SensorFrame<ImuMeasurement>(
                            _change == "RunId" ? "run-2" : f.RunId,
                            _change == "SensorId" ? "imu.other" : f.SensorId,
                            _change == "FrameId" ? "imu_other" : f.FrameId,
                            _change == "Sequence" ? f.Sequence + 1 : f.Sequence,
                            _change == "StateSequence" ? f.StateSequence - 1 : f.StateSequence,
                            _change == "SimulationTimeSeconds" ? f.SimulationTimeSeconds + 0.1 : f.SimulationTimeSeconds,
                            _change == "TimestampUtc" ? f.TimestampUtc.AddSeconds(0.1) : f.TimestampUtc,
                            _change == "Status" ? SensorFrameStatus.Unavailable : f.Status,
                            _change == "Source" ? "argus" : f.Source,
                            f.Payload);
                }
            }
        }
    }
}
