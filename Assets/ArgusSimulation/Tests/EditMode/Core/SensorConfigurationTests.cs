using System;
using System.Collections.Generic;
using System.Linq;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class SensorConfigurationTests
    {
        // Each kind declares whether the backend models it; Argus never computes a kind the backend
        // models (D10). A new kind fails here until it is added to this table (CORE-18).
        [Test]
        public void EveryKindDeclaresWhetherTheBackendModelsIt()
        {
            Dictionary<SensorKind, SensorConfiguration> isBackendModelled = new Dictionary<SensorKind, SensorConfiguration>
            {
                { SensorKind.Imu, SensorConfiguration.ForImu(Definition("imu.main"), default) },
                { SensorKind.Magnetometer, SensorConfiguration.ForMagnetometer(Definition("mag.main"), default) },
                { SensorKind.LightSensor, SensorConfiguration.ForLightSensor(Definition("light.px"), default) },
                { SensorKind.Camera, Camera(4608, 3548.0, 0.0005) }
            };
            SensorKind[] declared = Enum.GetValues(typeof(SensorKind))
                .Cast<SensorKind>()
                .Where(kind => kind != SensorKind.Unknown)
                .ToArray();

            Assert.That(isBackendModelled.Keys, Is.EquivalentTo(declared));
            Assert.That(isBackendModelled[SensorKind.Imu].IsBackendModelled, Is.True);
            Assert.That(isBackendModelled[SensorKind.Magnetometer].IsBackendModelled, Is.True);
            Assert.That(isBackendModelled[SensorKind.LightSensor].IsBackendModelled, Is.True);
            Assert.That(isBackendModelled[SensorKind.Camera].IsBackendModelled, Is.False);
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
