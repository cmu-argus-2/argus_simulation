using System;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class SimulationConfigurationTests
    {
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero);

        // Sensors sample only at step states, so a period off the step grid would sample unevenly
        // while the dataset records a period that never happens. Basilisk needs whole steps too.
        [TestCase(0.1, true)]
        [TestCase(0.3, true)] // 3 x 0.1 is 0.30000000000000004
        [TestCase(0.7, true)]
        [TestCase(1.0, true)]
        [TestCase(0.25, false)]
        [TestCase(0.05, false)]
        public void SensorPeriodsAreWholeNumbersOfSteps(double samplePeriodSeconds, bool isValid)
        {
            SimulationConfiguration configuration = new SimulationConfiguration(
                "run-1",
                Epoch,
                0.1,
                sensors: new[] { Camera(samplePeriodSeconds) });

            Assert.That(configuration.IsValid, Is.EqualTo(isValid));
        }

        // The rule covers every sensor, not only the ones Argus computes.
        [TestCase(0.2, true)]
        [TestCase(0.25, false)]
        public void BackendSensorPeriodsAreWholeNumbersOfStepsToo(double samplePeriodSeconds, bool isValid)
        {
            SimulationConfiguration configuration = new SimulationConfiguration(
                "run-1",
                Epoch,
                0.1,
                sensors: new[] { Imu(samplePeriodSeconds) });

            Assert.That(configuration.IsValid, Is.EqualTo(isValid));
        }

        private static SensorConfiguration Imu(double samplePeriodSeconds)
        {
            Vector3d zero = new Vector3d(0.0, 0.0, 0.0);
            Vector3d unitScale = new Vector3d(1.0, 1.0, 1.0);
            return SensorConfiguration.ForImu(
                new SensorDefinition("imu.main", "test-imu", "imu_main", samplePeriodSeconds, "basilisk", SensorMount.Identity),
                new ImuProfile(
                    new GyroscopeProfile(1e-4, 0.0, 0.0, zero, unitScale, 4.0, 1e-4),
                    new AccelerometerProfile(1e-3, 0.0, 0.0, zero, unitScale, 20.0, 1e-3)));
        }

        private static SensorConfiguration Camera(double samplePeriodSeconds) =>
            SensorConfiguration.ForCamera(
                new SensorDefinition("cam.px", "test-camera", "cam_px", samplePeriodSeconds, "argus", SensorMount.Identity),
                new CameraProfile(new CameraIntrinsics(4608, 2592, 3548.0, 3548.0, 2304.0, 1296.0), 0.0005));
    }
}
