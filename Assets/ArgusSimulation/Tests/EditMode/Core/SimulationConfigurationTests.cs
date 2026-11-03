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
                devices: new[] { Camera(samplePeriodSeconds) });

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
                devices: new[] { Imu(samplePeriodSeconds) });

            Assert.That(configuration.IsValid, Is.EqualTo(isValid));
        }

        // A camera frame carries the true camera pose, so a run may not show a camera to the
        // controller (D7) until CORE-6 decides what it may see; other kinds may be visible.
        [Test]
        public void ControllerVisibleCameraIsInvalid()
        {
            Assert.That(Run(Camera(0.1, isControllerVisible: true)).IsValid, Is.False);
            Assert.That(Run(Camera(0.1, isControllerVisible: false)).IsValid, Is.True);
            Assert.That(Run(Imu(0.1, isControllerVisible: true)).IsValid, Is.True);
        }

        private static SimulationConfiguration Run(DeviceConfiguration sensor) =>
            new SimulationConfiguration("run-1", Epoch, 0.1, devices: new[] { sensor });

        private static DeviceConfiguration Imu(double samplePeriodSeconds, bool isControllerVisible = true)
        {
            Vector3d zero = new Vector3d(0.0, 0.0, 0.0);
            Vector3d unitScale = new Vector3d(1.0, 1.0, 1.0);
            return ImuKind.Instance.Configure(
                new SensorDefinition("imu.main", "test-imu", "imu_main", samplePeriodSeconds, "basilisk", SensorMount.Identity, isControllerVisible),
                new ImuProfile(
                    new GyroscopeProfile(1e-4, 0.0, 0.0, zero, unitScale, 4.0, 1e-4),
                    new AccelerometerProfile(1e-3, 0.0, 0.0, zero, unitScale, 20.0, 1e-3)));
        }

        private static DeviceConfiguration Camera(double samplePeriodSeconds, bool isControllerVisible = false) =>
            CameraKind.Instance.Configure(
                new SensorDefinition("cam.px", "test-camera", "cam_px", samplePeriodSeconds, "argus", SensorMount.Identity, isControllerVisible),
                new CameraProfile(new CameraIntrinsics(4608, 2592, 3548.0, 3548.0, 2304.0, 1296.0), 0.0005));
    }
}
