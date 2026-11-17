using System;
using System.Linq;
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
        // controller (D7) until CORE-23 (#125) gives it an observation without truth; other kinds
        // may be visible.
        [Test]
        public void ControllerVisibleCameraIsInvalid()
        {
            Assert.That(Run(Camera(0.1, isControllerVisible: true)).IsValid, Is.False);
            Assert.That(Run(Camera(0.1, isControllerVisible: false)).IsValid, Is.True);
            Assert.That(Run(Imu(0.1, isControllerVisible: true)).IsValid, Is.True);
        }

        // A profile ID names one parameter set (intrinsics, distortion and noise), so two cameras that
        // share one must share those values; otherwise the manifest's profile_id would name two sets
        // (CORE-6). Exposure and the mount belong to each camera.
        [Test]
        public void CamerasSharingAProfileIdShareItsParameterSet()
        {
            DeviceConfiguration px = Camera(0.1, sensorId: "cam.px");
            CameraIntrinsics raspberryPiLens = new CameraIntrinsics(4608, 2592, 3386.0, 3386.0, 2304.0, 1296.0);
            CameraProfile imx708 = TestCameras.Imx708Profile();
            CameraProfile recalibratedSource = new CameraProfile(
                imx708.ProfileId, imx708.Intrinsics, imx708.IntrinsicsSource, imx708.IsCalibrated,
                CameraDistortion.Zero("checkerboard"), imx708.NoiseSource, imx708.ExposureDurationSeconds, imx708.Extrinsics);
            CameraProfile otherMount = new CameraProfile(
                imx708.ProfileId, imx708.Intrinsics, imx708.IntrinsicsSource, imx708.IsCalibrated,
                imx708.Distortion, imx708.NoiseSource, imx708.ExposureDurationSeconds,
                new CameraExtrinsicsProvenance("cad", "geometric-centre", isOrientationVerified: true));

            Assert.That(Run(px, Camera(0.1, sensorId: "cam.py")).IsValid, Is.True, "same set");
            Assert.That(Run(px, Camera(0.1, sensorId: "cam.py", profile: TestCameras.Profile(raspberryPiLens, 0.0005))).IsValid, Is.False, "same ID, other focal length");
            Assert.That(Run(px, Camera(0.1, sensorId: "cam.py", profile: recalibratedSource)).IsValid, Is.False, "same ID, other distortion source");
            Assert.That(Run(px, Camera(0.1, sensorId: "cam.py", profile: TestCameras.Profile(TestCameras.Imx708Intrinsics, 0.002))).IsValid, Is.True, "same ID, own exposure");
            Assert.That(Run(px, Camera(0.1, sensorId: "cam.py", profile: otherMount)).IsValid, Is.True, "same ID, own mount provenance");
            Assert.That(Run(px, Imu(0.1), Camera(0.1, sensorId: "cam.py", profile: TestCameras.Profile(raspberryPiLens, 0.0005))).IsValid, Is.False, "another device between them");
        }

        // The devices the backend models are worked out once, when the configuration is built, in
        // configuration order (CORE-22). Cameras, which Argus computes, are in neither list.
        [Test]
        public void BackendListsHoldTheBasiliskModelledDevicesInOrder()
        {
            DeviceConfiguration magnetometer = MagnetometerKind.Instance.Configure(
                new SensorDefinition("mag.main", "test-magnetometer", "mag_main", 0.1, "basilisk", SensorMount.Identity),
                new MagnetometerProfile(0.0, new Vector3d(0.0, 0.0, 0.0), 1.0, 1.3e-3));
            SimulationConfiguration run = Run(Camera(1.0), Imu(0.1), Camera(1.0, sensorId: "cam.py"), magnetometer);

            Assert.That(run.BackendDevices.Select(device => device.Definition.SensorId), Is.EqualTo(new[] { "imu.main", "mag.main" }));
            Assert.That(run.BackendSensors.Select(device => device.Definition.SensorId), Is.EqualTo(new[] { "imu.main", "mag.main" }));
            Assert.That(default(SimulationConfiguration).BackendDevices, Is.Empty);
            Assert.That(default(SimulationConfiguration).BackendSensors, Is.Empty);
        }

        // CORE-9: the heartbeat timeout is finite and >= 0, and 0 turns it off.
        [TestCase(0.0, true)]
        [TestCase(0.5, true)]
        [TestCase(1e12, true)]
        [TestCase(-0.1, false)]
        [TestCase(double.NaN, false)]
        [TestCase(double.PositiveInfinity, false)]
        public void TheHeartbeatTimeoutIsFiniteAndNotNegative(double timeoutSeconds, bool isValid)
        {
            Assert.That(Timed(timeoutSeconds, default).IsValid, Is.EqualTo(isValid));
        }

        // CORE-9: physical actuators are inhibited unless a run lifts the inhibit with a test procedure,
        // and a run that lifts it needs a heartbeat failsafe, at most 60 s.
        [TestCase(false, null, 0.0, true)]
        [TestCase(true, "bench-coil-1", 1.0, true)]
        [TestCase(true, "bench-coil-1", 60.0, true)]
        [TestCase(true, null, 1.0, false)]
        [TestCase(true, " ", 1.0, false)]
        [TestCase(false, "bench-coil-1", 1.0, false)]
        [TestCase(true, "bench-coil-1", 0.0, false)]
        [TestCase(true, "bench-coil-1", 60.5, false)]
        public void LiftingTheInhibitNeedsAProcedureAndAHeartbeat(bool enabled, string procedure, double timeoutSeconds, bool isValid)
        {
            Assert.That(Timed(timeoutSeconds, new PhysicalActuatorSetting(enabled, procedure)).IsValid, Is.EqualTo(isValid));
        }

        [Test]
        public void PhysicalActuatorsAreInhibitedByDefault()
        {
            Assert.That(Run().PhysicalActuators.Enabled, Is.False);
            Assert.That(Run().HeartbeatTimeoutWallSeconds, Is.EqualTo(0.0));
        }

        // The gateway's Reset(seed, scenario) runs WithRandomSeed: every member but the seed survives,
        // including any added later (CORE-9 design review), so a dropped member fails here.
        [Test]
        public void WithRandomSeedKeepsEveryOtherMember()
        {
            SimulationConfiguration run = new SimulationConfiguration(
                "run-1",
                Epoch,
                0.1,
                new ClassicalOrbitElements(6_878_137.0, 0.001, 97.4, 10.0, 20.0, 30.0),
                randomSeed: 3,
                kernelSetId: "kernels-1",
                spacecraft: new SpacecraftConfiguration(
                    1.33,
                    new Matrix3d(new Vector3d(2.5e-3, 0.0, 0.0), new Vector3d(0.0, 2.5e-3, 0.0), new Vector3d(0.0, 0.0, 2.2e-3)),
                    new Quaterniond(0.0, 0.0, 0.0, 1.0),
                    new Vector3d(0.0, 0.0, 0.0)),
                devices: new[] { Imu(0.1) },
                heartbeatTimeoutWallSeconds: 2.5,
                physicalActuators: new PhysicalActuatorSetting(true, "bench-coil-1"));

            SimulationConfiguration seeded = run.WithRandomSeed(11);

            Assert.That(seeded.RandomSeed, Is.EqualTo(11));
            foreach (System.Reflection.PropertyInfo property in typeof(SimulationConfiguration).GetProperties())
            {
                if (property.Name == nameof(SimulationConfiguration.RandomSeed))
                {
                    continue;
                }

                object before = property.GetValue(run);
                object after = property.GetValue(seeded);
                if (before is System.Collections.IEnumerable items && !(before is string))
                {
                    Assert.That(((System.Collections.IEnumerable)after).Cast<object>(), Is.EqualTo(items.Cast<object>()), property.Name);
                    Assert.That(items.Cast<object>(), Is.Not.Empty, property.Name + " should be non-empty, so the comparison means something");
                }
                else
                {
                    Assert.That(after, Is.EqualTo(before), property.Name);
                }
            }
        }

        private static SimulationConfiguration Timed(double timeoutSeconds, PhysicalActuatorSetting physical) =>
            new SimulationConfiguration("run-1", Epoch, 0.1, heartbeatTimeoutWallSeconds: timeoutSeconds, physicalActuators: physical);

        private static SimulationConfiguration Run(params DeviceConfiguration[] devices) =>
            new SimulationConfiguration("run-1", Epoch, 0.1, devices: devices);

        private static DeviceConfiguration Imu(double samplePeriodSeconds, bool isControllerVisible = true)
        {
            return ImuKind.Instance.Configure(
                new SensorDefinition("imu.main", "test-imu", "imu_main", samplePeriodSeconds, "basilisk", SensorMount.Identity, isControllerVisible),
                TestImu.Profile());
        }

        private static DeviceConfiguration Camera(
            double samplePeriodSeconds,
            bool isControllerVisible = false,
            string sensorId = "cam.px",
            CameraProfile? profile = null) =>
            CameraKind.Instance.Configure(
                new SensorDefinition(sensorId, "test-camera", sensorId.Replace('.', '_'), samplePeriodSeconds, "argus", SensorMount.Identity, isControllerVisible),
                profile ?? TestCameras.Imx708Profile());
    }
}
