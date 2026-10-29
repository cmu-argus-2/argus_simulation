using System;
using System.Linq;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class SensorConfigurationTests
    {
        // Each kind declares whether the backend models it; Argus never computes a kind the backend
        // models (D10). A new kind fails here until it has a row with its expected answer, and
        // IsBackendModelled throws for a kind it does not list (CORE-18).
        [Test]
        public void EveryKindDeclaresWhetherTheBackendModelsIt()
        {
            (SensorConfiguration Sensor, bool IsBackendModelled)[] kinds =
            {
                (SensorConfiguration.ForImu(Definition("imu.main"), default), true),
                (SensorConfiguration.ForMagnetometer(Definition("mag.main"), default), true),
                (SensorConfiguration.ForLightSensor(Definition("light.px"), default), true),
                (Camera(4608, 3548.0, 0.0005), false)
            };
            SensorKind[] declared = Enum.GetValues(typeof(SensorKind))
                .Cast<SensorKind>()
                .Where(kind => kind != SensorKind.Unknown)
                .ToArray();

            Assert.That(kinds.Select(row => row.Sensor.Kind), Is.EquivalentTo(declared));
            foreach ((SensorConfiguration sensor, bool isBackendModelled) in kinds)
            {
                Assert.That(sensor.IsBackendModelled, Is.EqualTo(isBackendModelled), sensor.Kind.ToString());
            }

            Assert.That(default(SensorConfiguration).IsBackendModelled, Is.False);
        }

        // The profile accepts exactly what the CameraModel constructor accepts.
        [TestCase(4608, 3548.0, 0.0005, true)]
        [TestCase(4608, 3548.0, 0.0, true)]
        [TestCase(0, 3548.0, 0.0005, false)]
        [TestCase(4608, 0.0, 0.0005, false)]
        [TestCase(4608, -3548.0, 0.0005, false)]
        [TestCase(4608, 3548.0, -0.0005, false)]
        [TestCase(4608, 3548.0, double.NaN, false)]
        [TestCase(4608, 3548.0, double.PositiveInfinity, false)]
        public void CameraConfigurationIsValidOnlyWithAValidProfile(
            int widthPixels,
            double focalLengthPixels,
            double exposureDurationSeconds,
            bool isValid)
        {
            SensorConfiguration camera = Camera(widthPixels, focalLengthPixels, exposureDurationSeconds);

            Assert.That(camera.Kind, Is.EqualTo(SensorKind.Camera));
            Assert.That(camera.IsValid, Is.EqualTo(isValid));
        }

        private static SensorConfiguration Camera(int widthPixels, double focalLengthPixels, double exposureDurationSeconds) =>
            SensorConfiguration.ForCamera(
                Definition("cam.px"),
                new CameraProfile(
                    new CameraIntrinsics(widthPixels, 2592, focalLengthPixels, focalLengthPixels, 2304.0, 1296.0),
                    exposureDurationSeconds));

        private static SensorDefinition Definition(string sensorId) =>
            new SensorDefinition(sensorId, "test-model", sensorId.Replace('.', '_'), 0.1, "test", SensorMount.Identity);
    }
}
