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
