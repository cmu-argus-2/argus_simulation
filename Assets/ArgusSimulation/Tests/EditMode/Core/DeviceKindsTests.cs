using System.Linq;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class DeviceKindsTests
    {
        // Each kind declares whether the backend models it, so Argus never computes a kind the
        // backend models (D10, CORE-18), and whether a controller may see its frames, which a kind
        // whose frames carry truth may not (D7). A new kind fails here until it has a row.
        [Test]
        public void EveryKindDeclaresWhoModelsItAndWhetherAControllerMaySeeIt()
        {
            (string Name, bool IsBackendModelled, bool MayBeControllerVisible)[] kinds =
            {
                ("imu", true, true),
                ("magnetometer", true, true),
                ("light_sensor", true, true),
                ("camera", false, false)
            };

            Assert.That(DeviceKinds.All.Select(kind => kind.Name), Is.EquivalentTo(kinds.Select(row => row.Name)));
            foreach ((string name, bool isBackendModelled, bool mayBeControllerVisible) in kinds)
            {
                IDeviceKind kind = DeviceKinds.Find(name);
                Assert.That(kind.Role, Is.EqualTo(DeviceRole.Sensor), name);
                Assert.That(kind.IsBackendModelled, Is.EqualTo(isBackendModelled), name);
                Assert.That(kind.MayBeControllerVisible, Is.EqualTo(mayBeControllerVisible), name);
                Assert.That(kind is IBackendSensorKind, Is.EqualTo(isBackendModelled), name);
            }

            Assert.That(default(DeviceConfiguration).IsBackendModelled, Is.False);
            Assert.That(default(DeviceConfiguration).MayBeControllerVisible, Is.False);
        }

        // Parameters are exactly the kind's keys, each with its declared type and a finite value, and
        // the problem names the key (docs/device-model.md §2).
        [Test]
        public void ParametersAreExactlyTheKindsKeys()
        {
            DeviceValues valid = Magnetometer().Parameters;
            Assert.That(Magnetometer().IsValid, Is.True);
            Assert.That(MagnetometerKind.Instance.Profile(Magnetometer()).RangeTesla, Is.EqualTo(1.3e-3));

            Assert.That(Problem("magnetometer", Without(valid, "range_tesla")), Is.EqualTo("parameters[\"range_tesla\"]: is required"));
            Assert.That(
                Problem("magnetometer", With(valid, "gain", DeviceValue.FromNumber(2.0))),
                Is.EqualTo("parameters[\"gain\"]: is not a magnetometer key"));
            Assert.That(
                Problem("magnetometer", With(Without(valid, "scale_factor"), "scale_factor", DeviceValue.FromFlag(true))),
                Is.EqualTo("parameters[\"scale_factor\"]: must be a Number, not a Flag"));
            Assert.That(
                Problem("magnetometer", With(Without(valid, "noise_std_tesla"), "noise_std_tesla", DeviceValue.FromNumber(double.NaN))),
                Is.EqualTo("parameters[\"noise_std_tesla\"]: must be finite"));
            Assert.That(
                Problem("magnetometer", With(Without(valid, "range_tesla"), "range_tesla", DeviceValue.FromNumber(-1.0))),
                Is.EqualTo("parameters: is not a valid magnetometer profile (see MagnetometerProfile.IsValid)"));
            Assert.That(
                new DeviceConfiguration(Definition("mag.main"), "magnetometer", valid, With(DeviceValues.Empty, "speed", DeviceValue.FromNumber(1.0))).FindProblem(),
                Is.EqualTo("initialState: must be empty, because the magnetometer kind has no state"));
            Assert.That(Problem("sun_sensor", valid), Does.StartWith("kind: 'sun_sensor' is not a registered device kind"));
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
            DeviceConfiguration camera = CameraKind.Instance.Configure(
                Definition("cam.px"),
                new CameraProfile(
                    new CameraIntrinsics(widthPixels, 2592, focalLengthPixels, focalLengthPixels, 2304.0, 1296.0),
                    exposureDurationSeconds));

            Assert.That(camera.Kind, Is.EqualTo("camera"));
            Assert.That(camera.IsValid, Is.EqualTo(isValid));
        }

        private static DeviceConfiguration Magnetometer() =>
            MagnetometerKind.Instance.Configure(
                Definition("mag.main"),
                new MagnetometerProfile(0.0, new Vector3d(0.0, 0.0, 0.0), 1.0, 1.3e-3));

        private static string Problem(string kind, DeviceValues parameters) =>
            new DeviceConfiguration(Definition("mag.main"), kind, parameters).FindProblem();

        private static DeviceValues With(DeviceValues values, string key, DeviceValue value)
        {
            DeviceValues.Builder builder = new DeviceValues.Builder();
            foreach (var entry in values)
            {
                builder.Add(entry.Key, entry.Value);
            }

            return builder.Add(key, value).Build();
        }

        private static DeviceValues Without(DeviceValues values, string key)
        {
            DeviceValues.Builder builder = new DeviceValues.Builder();
            foreach (var entry in values.Where(entry => entry.Key != key))
            {
                builder.Add(entry.Key, entry.Value);
            }

            return builder.Build();
        }

        private static SensorDefinition Definition(string sensorId) =>
            new SensorDefinition(sensorId, "test-model", sensorId.Replace('.', '_'), 0.1, "test", SensorMount.Identity);
    }
}
