using System;
using System.IO;
using System.Text.Json.Nodes;
using Argus.Simulation.Core;
using NUnit.Framework;
using PbBasilisk = Argus.Contracts.Basilisk.V1;

namespace Argus.Simulation.Host.Tests
{
    public sealed class RunConfigurationFileTests
    {
        private const string RunId = "host-test";
        private const long FixedStepNanoseconds = 100_000_000;

        private static string P0Text => File.ReadAllText(HostOptions.DefaultRunConfigurationPath);

        [Test]
        public void P0FileSendsTheSameConfigureRunRequestAsTheCompiledP0Default()
        {
            SimulationConfiguration expected = CompiledP0Default(RunId);
            SimulationConfiguration loaded = RunConfigurationFile.Load(HostOptions.DefaultRunConfigurationPath, RunId);

            Assert.That(loaded.IsValid, Is.True);
            Assert.That(Request(loaded), Is.EqualTo(Request(expected)));

            // SensorDefinition fields that stay in Argus and never reach the wire.
            Assert.That(loaded.Sensors.Count, Is.EqualTo(expected.Sensors.Count));
            for (int i = 0; i < expected.Sensors.Count; i++)
            {
                SensorDefinition want = expected.Sensors[i].Definition;
                SensorDefinition got = loaded.Sensors[i].Definition;
                Assert.That(got.ModelName, Is.EqualTo(want.ModelName));
                Assert.That(got.FrameId, Is.EqualTo(want.FrameId));
                Assert.That(got.Source, Is.EqualTo(want.Source));
                Assert.That(got.SamplePeriodSeconds, Is.EqualTo(want.SamplePeriodSeconds));
            }
        }

        [Test]
        public void HostOptionsDefaultToTheCheckedInP0File()
        {
            Assert.That(HostOptions.TryParse(Array.Empty<string>(), out HostOptions defaults, out _), Is.True);
            Assert.That(defaults.RunConfigurationPath, Is.EqualTo(HostOptions.DefaultRunConfigurationPath));
            Assert.That(File.Exists(defaults.RunConfigurationPath), Is.True);

            Assert.That(HostOptions.TryParse(new[] { "--run-config", "staged.json" }, out HostOptions staged, out _), Is.True);
            Assert.That(staged.RunConfigurationPath, Is.EqualTo("staged.json"));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void FileWithASubsetOfTheSensorsLoads(int sensorCount)
        {
            JsonObject root = P0();
            JsonArray sensors = root["sensors"].AsArray();
            while (sensors.Count > sensorCount)
            {
                sensors.RemoveAt(sensors.Count - 1);
            }

            SimulationConfiguration configuration = RunConfigurationFile.Parse(root.ToJsonString(), RunId);

            Assert.That(configuration.IsValid, Is.True);
            Assert.That(configuration.Sensors.Count, Is.EqualTo(sensorCount));
            Assert.That(Request(configuration).Configuration.Sensors.Count, Is.EqualTo(sensorCount));
        }

        [TestCase("missing field", "initialOrbit.eccentricity: is required")]
        [TestCase("unknown field", "spacecraft.massKg: is not a known field")]
        [TestCase("invalid orbit", "initialOrbit: needs finite elements")]
        [TestCase("invalid profile", "sensors[0].imu.gyroscope: is not a valid gyroscope profile")]
        [TestCase("duplicate sensor ID", "sensors[1].sensorId: duplicates another sensor's ID")]
        [TestCase("two profiles", "sensors[2]: needs exactly one of imu, magnetometer, lightSensor")]
        [TestCase("wrong type", "sensors[0].mount.positionBodyMeters: must be an array of 3 numbers")]
        [TestCase("epoch without zone", "epochUtc: must be a UTC time")]
        [TestCase("invalid kernel set", "kernelSetId: must match")]
        public void InvalidFileNamesTheField(string change, string expectedMessage)
        {
            JsonObject root = P0();
            JsonArray sensors = root["sensors"].AsArray();
            switch (change)
            {
                case "missing field":
                    root["initialOrbit"].AsObject().Remove("eccentricity");
                    break;
                case "unknown field":
                    root["spacecraft"]["massKg"] = 1.33;
                    break;
                case "invalid orbit":
                    root["initialOrbit"]["eccentricity"] = 1.5;
                    break;
                case "invalid profile":
                    root["sensors"][0]["imu"]["gyroscope"]["rangeRadiansPerSecond"] = -1.0;
                    break;
                case "duplicate sensor ID":
                    sensors[1]["sensorId"] = "imu.main";
                    break;
                case "two profiles":
                    sensors[2]["magnetometer"] = sensors[1]["magnetometer"].DeepClone();
                    break;
                case "wrong type":
                    sensors[0]["mount"]["positionBodyMeters"] = "0, 0, 0";
                    break;
                case "epoch without zone":
                    root["epochUtc"] = "2025-01-15T00:00:00";
                    break;
                case "invalid kernel set":
                    root["kernelSetId"] = "Argus 2026";
                    break;
            }

            RunConfigurationException error = Assert.Throws<RunConfigurationException>(
                () => RunConfigurationFile.Parse(root.ToJsonString(), RunId));
            Assert.That(error.Message, Does.StartWith(expectedMessage));
        }

        [Test]
        public void HostExitsWithCode2BeforeConnecting()
        {
            string path = Path.Combine(Path.GetTempPath(), $"argus-bad-run-{Guid.NewGuid():N}.json");
            JsonObject root = P0();
            root["initialOrbit"]["eccentricity"] = 1.5;
            File.WriteAllText(path, root.ToJsonString());
            TextWriter originalError = Console.Error;
            StringWriter error = new StringWriter();
            try
            {
                Console.SetError(error);
                // Nothing listens on port 9; reaching the service would exit 1 with Unavailable.
                int exitCode = Program.Main(new[] { "--run-config", path, "--basilisk-endpoint", "127.0.0.1:9" });

                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(error.ToString(), Does.Contain("initialOrbit: needs finite elements"));
            }
            finally
            {
                Console.SetError(originalError);
                File.Delete(path);
            }
        }

        private static JsonObject P0() => JsonNode.Parse(P0Text).AsObject();

        // What BasiliskEngine.Initialize sends with a real-time factor of 0.
        private static PbBasilisk.ConfigureRunRequest Request(SimulationConfiguration configuration) =>
            new PbBasilisk.ConfigureRunRequest
            {
                Configuration = BasiliskProtoMapper.ToProto(configuration, FixedStepNanoseconds),
                RealTimeFactor = 0.0
            };

        // The P0 run configuration as headless/Host compiled it in before runs/p0-default.json.
        private static SimulationConfiguration CompiledP0Default(string runId)
        {
            Vector3d x = new Vector3d(1.0, 0.0, 0.0);
            Vector3d y = new Vector3d(0.0, 1.0, 0.0);
            Vector3d z = new Vector3d(0.0, 0.0, 1.0);
            Vector3d zero = new Vector3d(0.0, 0.0, 0.0);
            Vector3d unitScale = new Vector3d(1.0, 1.0, 1.0);
            Vector3d Negate(Vector3d v) => new Vector3d(-v.X, -v.Y, -v.Z);
            SensorDefinition Definition(string sensorId, string modelName, string frameId, SensorMount mount) =>
                new SensorDefinition(sensorId, modelName, frameId, 0.1, "basilisk", mount);
            SensorConfiguration Light(string sensorId, string frameId, Vector3d position, Vector3d sensorX, Vector3d sensorY, Vector3d sensorZ) =>
                SensorConfiguration.ForLightSensor(
                    Definition(sensorId, "placeholder-face-light-sensor", frameId, new SensorMount(position, Quaterniond.FromBasis(sensorX, sensorY, sensorZ))),
                    new LightSensorProfile(Math.PI / 2.0, 1.0, 1.0, 0.0, 2000.0));

            SensorConfiguration[] sensors =
            {
                SensorConfiguration.ForImu(
                    Definition("imu.main", "placeholder-mems-imu", "imu_main", SensorMount.Identity),
                    new ImuProfile(
                        new GyroscopeProfile(1.2e-4, 0.0, 0.0, zero, unitScale, 4.363, 1.33e-4),
                        new AccelerometerProfile(1.8e-3, 0.0, 0.0, zero, unitScale, 19.61, 6.0e-4))),
                SensorConfiguration.ForMagnetometer(
                    Definition("mag.main", "placeholder-magnetometer", "mag_main", SensorMount.Identity),
                    new MagnetometerProfile(0.0, zero, 1.0, 1.3e-3)),
                Light("light.px", "light_px", new Vector3d(0.05, 0.0, 0.0), y, z, x),
                Light("light.mx", "light_mx", new Vector3d(-0.05, 0.0, 0.0), Negate(y), z, Negate(x)),
                Light("light.py", "light_py", new Vector3d(0.0, 0.05, 0.0), z, x, y),
                Light("light.my", "light_my", new Vector3d(0.0, -0.05, 0.0), Negate(z), x, Negate(y)),
                Light("light.pz", "light_pz", new Vector3d(0.0, 0.0, 0.05675), x, y, z),
                Light("light.mz", "light_mz", new Vector3d(0.0, 0.0, -0.05675), x, Negate(y), Negate(z))
            };

            return new SimulationConfiguration(
                runId,
                new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero),
                0.1,
                initialOrbit: new ClassicalOrbitElements(6_878_137.0, 0.0, 51.6, 0.0, 0.0, 0.0),
                randomSeed: 1,
                kernelSetId: "argus-2026-10-20",
                spacecraft: new SpacecraftConfiguration(
                    1.33,
                    new Matrix3d(
                        new Vector3d(2.536e-3, 0.0, 0.0),
                        new Vector3d(0.0, 2.536e-3, 0.0),
                        new Vector3d(0.0, 0.0, 2.217e-3)),
                    new Quaterniond(0.0, 0.0, 0.0, 1.0),
                    zero),
                sensors: sensors);
        }
    }
}
