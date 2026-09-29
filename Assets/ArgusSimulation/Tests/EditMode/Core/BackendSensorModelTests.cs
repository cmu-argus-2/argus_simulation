using System;
using System.Collections.Generic;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class BackendSensorModelTests
    {
        [Test]
        public void BackendSensor_PublishesOnlyWhatTheBackendSampled()
        {
            SensorManager manager = new SensorManager();
            manager.Register(new ImuSensor(ImuConfiguration()));
            manager.Reset(new SensorResetContext("run-1", DateTimeOffset.UnixEpoch, 0));
            ImuMeasurement measured = new ImuMeasurement(
                new Vector3d(0.01, 0.02, 0.03),
                new Vector3d(0.0, 0.0, 1e-6));
            ImuMeasurement notFinite = new ImuMeasurement(
                new Vector3d(double.NaN, 0.0, 0.0),
                new Vector3d(0.0, 0.0, 0.0));

            SensorOutputSet unavailable = manager.Sample(Context(0, new SensorMeasurementSet(
                new Dictionary<string, object>(),
                new[] { "imu.main" })));
            SensorOutputSet notSampled = manager.Sample(Context(1, SensorMeasurementSet.Empty));
            SensorOutputSet valid = manager.Sample(Context(2, Measurements(measured)));
            SensorOutputSet invalid = manager.Sample(Context(3, Measurements(notFinite)));

            Assert.That(unavailable.TryGetFrame("imu.main", out SensorFrame<ImuMeasurement> unavailableFrame), Is.True);
            Assert.That(unavailableFrame.Status, Is.EqualTo(SensorFrameStatus.Unavailable));
            Assert.That(unavailableFrame.Sequence, Is.EqualTo(0));
            Assert.That(notSampled.Frames, Is.Empty);
            Assert.That(valid.TryGetFrame("imu.main", out SensorFrame<ImuMeasurement> validFrame), Is.True);
            Assert.That(validFrame.Status, Is.EqualTo(SensorFrameStatus.Valid));
            Assert.That(validFrame.Sequence, Is.EqualTo(1));
            Assert.That(
                validFrame.Payload.AngularVelocitySensorRadiansPerSecond,
                Is.EqualTo(measured.AngularVelocitySensorRadiansPerSecond));
            Assert.That(
                validFrame.Payload.SpecificForceSensorMetersPerSecondSquared,
                Is.EqualTo(measured.SpecificForceSensorMetersPerSecondSquared));
            Assert.That(invalid.TryGetFrame("imu.main", out SensorFrame<ImuMeasurement> invalidFrame), Is.True);
            Assert.That(invalidFrame.Status, Is.EqualTo(SensorFrameStatus.Invalid));
            Assert.That(invalidFrame.Sequence, Is.EqualTo(2));
        }

        private static SensorConfiguration ImuConfiguration()
        {
            Vector3d zero = new Vector3d(0.0, 0.0, 0.0);
            Vector3d unitScale = new Vector3d(1.0, 1.0, 1.0);
            ImuProfile profile = new ImuProfile(
                new GyroscopeProfile(1e-4, 0.0, 0.0, zero, unitScale, 4.0, 1e-4),
                new AccelerometerProfile(1e-3, 0.0, 0.0, zero, unitScale, 20.0, 1e-3));
            SensorDefinition definition = new SensorDefinition(
                "imu.main",
                "test-imu",
                "imu_main",
                0.1,
                "basilisk",
                SensorMount.Identity);
            return SensorConfiguration.ForImu(definition, profile);
        }

        private static SensorMeasurementSet Measurements(ImuMeasurement measurement) =>
            new SensorMeasurementSet(new Dictionary<string, object> { { "imu.main", measurement } });

        private static SensorSampleContext Context(long sequence, SensorMeasurementSet measurements)
        {
            double time = sequence * 0.1;
            SpacecraftState state = new SpacecraftState(
                sequence,
                time,
                DateTimeOffset.UnixEpoch.AddSeconds(time),
                new Vector3d(6_878_137.0, 0.0, 0.0),
                new Vector3d(0.0, 7_600.0, 0.0),
                new Quaterniond(0.0, 0.0, 0.0, 1.0),
                new Vector3d(0.0, 0.0, 0.0),
                ReferenceFrame.AnalyticEarthFixed);
            SimulationSnapshot snapshot = new SimulationSnapshot(
                "run-1",
                "test-backend",
                state,
                ActuatorCommandSet.None(sequence, time),
                sensorMeasurements: measurements);
            return new SensorSampleContext("run-1", snapshot);
        }
    }
}
