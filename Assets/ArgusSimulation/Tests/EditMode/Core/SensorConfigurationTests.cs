using System;
using System.Linq;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class SensorConfigurationTests
    {
        // Each kind declares whether the backend models it, so Argus never computes a kind the
        // backend models (D10, CORE-18). Each kind also declares whether a controller may see its
        // frames, which a kind whose frames carry truth may not (D7). A new kind fails here until it
        // has a row with both answers, and both properties throw for a kind they do not list.
        [Test]
        public void EveryKindDeclaresWhoModelsItAndWhetherAControllerMaySeeIt()
        {
            (SensorConfiguration Sensor, bool IsBackendModelled, bool MayBeControllerVisible)[] kinds =
            {
                (SensorConfiguration.ForImu(Definition("imu.main"), default), true, true),
                (SensorConfiguration.ForMagnetometer(Definition("mag.main"), default), true, true),
                (SensorConfiguration.ForLightSensor(Definition("light.px"), default), true, true),
                (Camera(4608, 3548.0, 0.0005), false, false)
            };
            SensorKind[] declared = Enum.GetValues(typeof(SensorKind))
                .Cast<SensorKind>()
                .Where(kind => kind != SensorKind.Unknown)
                .ToArray();

            Assert.That(kinds.Select(row => row.Sensor.Kind), Is.EquivalentTo(declared));
            foreach ((SensorConfiguration sensor, bool isBackendModelled, bool mayBeControllerVisible) in kinds)
            {
                Assert.That(sensor.IsBackendModelled, Is.EqualTo(isBackendModelled), sensor.Kind.ToString());
                Assert.That(sensor.MayBeControllerVisible, Is.EqualTo(mayBeControllerVisible), sensor.Kind.ToString());
            }

            Assert.That(default(SensorConfiguration).IsBackendModelled, Is.False);
            Assert.That(default(SensorConfiguration).MayBeControllerVisible, Is.False);
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
