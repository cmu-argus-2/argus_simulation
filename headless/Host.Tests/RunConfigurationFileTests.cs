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
            Assert.That(loaded.Devices.Count, Is.EqualTo(expected.Devices.Count));
            for (int i = 0; i < expected.Devices.Count; i++)
            {
                SensorDefinition want = expected.Devices[i].Definition;
                SensorDefinition got = loaded.Devices[i].Definition;
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
        public void FileWithASubsetOfTheDevicesLoads(int deviceCount)
        {
            JsonObject root = P0();
            JsonArray devices = root["devices"].AsArray();
            while (devices.Count > deviceCount)
            {
                devices.RemoveAt(devices.Count - 1);
            }

            SimulationConfiguration configuration = RunConfigurationFile.Parse(root.ToJsonString(), RunId);

            Assert.That(configuration.IsValid, Is.True);
            Assert.That(configuration.Devices.Count, Is.EqualTo(deviceCount));
            Assert.That(Request(configuration).Configuration.Devices.Count, Is.EqualTo(deviceCount));
        }

        // A camera is computed by Argus: it loads from the file, but ConfigureRun carries the same
        // backend-modelled devices as without it, so validation.py never sees it (CORE-18).
        [Test]
        public void ArgusComputedCameraLoadsButIsNotSentToBasilisk()
        {
            JsonObject root = P0();
            root["devices"].AsArray().Insert(1, CameraNode("cam.px"));

            SimulationConfiguration withCamera = RunConfigurationFile.Parse(root.ToJsonString(), RunId);
            SimulationConfiguration p0 = RunConfigurationFile.Parse(P0Text, RunId);

            Assert.That(withCamera.IsValid, Is.True);
            Assert.That(withCamera.Devices.Count, Is.EqualTo(p0.Devices.Count + 1));
            DeviceConfiguration camera = withCamera.Devices[1];
            Assert.That(camera.Kind, Is.EqualTo("camera"));
            Assert.That(camera.IsBackendModelled, Is.False);
            Assert.That(camera.Definition.SensorId, Is.EqualTo("cam.px"));
            CameraProfile profile = CameraKind.Instance.Profile(camera);
            Assert.That(profile.Intrinsics.WidthPixels, Is.EqualTo(4608));
            Assert.That(profile.Intrinsics.HeightPixels, Is.EqualTo(2592));
            Assert.That(profile.Intrinsics.FocalLengthXPixels, Is.EqualTo(3548.0));
            Assert.That(profile.Intrinsics.PrincipalPointYPixels, Is.EqualTo(1296.0));
            Assert.That(profile.ExposureDurationSeconds, Is.EqualTo(0.0005));
            Assert.That(Request(withCamera), Is.EqualTo(Request(p0)));
        }

        [TestCase("missing field", "initialOrbit.eccentricity: is required")]
        [TestCase("unknown field", "spacecraft.massKg: is not a known field")]
        [TestCase("invalid orbit", "initialOrbit: needs finite elements")]
        [TestCase("invalid profile", "devices[0].parameters: is not a valid imu profile (see ImuProfile.IsValid)")]
        [TestCase("duplicate device ID", "devices[1].deviceId: duplicates another device's ID")]
        [TestCase("unknown kind", "devices[2].kind: 'sun_sensor' is not a registered device kind")]
        [TestCase("unknown parameter", "devices[1].parameters.gain: is not a magnetometer parameter")]
        [TestCase("missing parameter", "devices[1].parameters[\"range_tesla\"]: is required")]
        [TestCase("wrong parameter type", "devices[2].parameters.scale_factor: must be a finite number")]
        [TestCase("wrong type", "devices[0].mount.positionBodyMeters: must be an array of 3 numbers")]
        [TestCase("epoch without zone", "epochUtc: must be a UTC time")]
        [TestCase("invalid kernel set", "kernelSetId: must match")]
        [TestCase("invalid camera intrinsics", "devices[8].parameters: is not a valid camera profile")]
        [TestCase("fractional pixel count", "devices[8].parameters[\"intrinsics.width_px\"]: must be a whole number of pixels")]
        [TestCase("period off the step grid", "devices[8].samplePeriodSeconds: must be a whole multiple of fixedStepSeconds")]
        [TestCase("controller-visible camera", "devices[8].controllerVisible: must be false")]
        public void InvalidFileNamesTheField(string change, string expectedMessage)
        {
            JsonObject root = P0();
            JsonArray devices = root["devices"].AsArray();
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
                    devices[0]["parameters"]["gyroscope"]["range_rad_per_s"] = -1.0;
                    break;
                case "duplicate device ID":
                    devices[1]["deviceId"] = "imu.main";
                    break;
                case "unknown kind":
                    devices[2]["kind"] = "sun_sensor";
                    break;
                case "unknown parameter":
                    devices[1]["parameters"]["gain"] = 2.0;
                    break;
                case "missing parameter":
                    devices[1]["parameters"].AsObject().Remove("range_tesla");
                    break;
                case "wrong parameter type":
                    devices[2]["parameters"]["scale_factor"] = "1";
                    break;
                case "wrong type":
                    devices[0]["mount"]["positionBodyMeters"] = "0, 0, 0";
                    break;
                case "epoch without zone":
                    root["epochUtc"] = "2025-01-15T00:00:00";
                    break;
                case "invalid kernel set":
                    root["kernelSetId"] = "Argus 2026";
                    break;
                case "invalid camera intrinsics":
                    JsonObject camera = CameraNode("cam.px");
                    camera["parameters"]["intrinsics"]["width_px"] = 0;
                    devices.Add(camera);
                    break;
                case "fractional pixel count":
                    JsonObject fractional = CameraNode("cam.px");
                    fractional["parameters"]["intrinsics"]["width_px"] = 4608.5;
                    devices.Add(fractional);
                    break;
                case "period off the step grid":
                    JsonObject offGrid = CameraNode("cam.px");
                    offGrid["samplePeriodSeconds"] = 0.25;
                    devices.Add(offGrid);
                    break;
                case "controller-visible camera":
                    JsonObject visible = CameraNode("cam.px");
                    visible["controllerVisible"] = true;
                    devices.Add(visible);
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

        [TestCase("device id", "devices[0].deviceId: 'IMU.Main' cannot be recorded")]
        [TestCase("record root", "--record: Cannot write datasets under")]
        public void RecordingThatCannotWorkExitsWithCode2BeforeConnecting(string problem, string expectedMessage)
        {
            string path = Path.Combine(Path.GetTempPath(), $"argus-record-run-{Guid.NewGuid():N}.json");
            string blocker = Path.Combine(Path.GetTempPath(), $"argus-not-a-directory-{Guid.NewGuid():N}");
            JsonObject root = P0();
            string record = Path.Combine(Path.GetTempPath(), $"argus-record-{Guid.NewGuid():N}");
            if (problem == "device id")
            {
                // Valid for the run file, which only needs a non-blank ID, but not a directory name.
                root["devices"][0]["deviceId"] = "IMU.Main";
            }
            else
            {
                // A root under a regular file cannot be created.
                File.WriteAllText(blocker, "");
                record = Path.Combine(blocker, "datasets");
            }

            File.WriteAllText(path, root.ToJsonString());
            TextWriter originalError = Console.Error;
            StringWriter error = new StringWriter();
            try
            {
                Console.SetError(error);
                int exitCode = Program.Main(new[] { "--run-config", path, "--basilisk-endpoint", "127.0.0.1:9", "--record", record });

                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(error.ToString(), Does.Contain(expectedMessage));
                Assert.That(error.ToString(), Does.Not.Contain("Unavailable"));
            }
            finally
            {
                Console.SetError(originalError);
                File.Delete(path);
                File.Delete(blocker);
                if (Directory.Exists(record))
                {
                    Directory.Delete(record, recursive: true);
                }
            }
        }

        private static JsonObject P0() => JsonNode.Parse(P0Text).AsObject();

        // An Argus-computed camera entry with the DATA-6 (#71) v0 profile on the +X mount. Not
        // controller-visible: a camera frame carries truth (D7).
        private static JsonObject CameraNode(string deviceId) => new JsonObject
        {
            ["deviceId"] = deviceId,
            ["kind"] = "camera",
            ["modelName"] = "arducam-imx708",
            ["frameId"] = deviceId.Replace('.', '_'),
            ["samplePeriodSeconds"] = 1.0,
            ["source"] = "argus",
            ["controllerVisible"] = false,
            ["mount"] = new JsonObject
            {
                ["positionBodyMeters"] = new JsonArray(0.05116, -0.000198, -0.02927),
                ["deviceToBody"] = new JsonObject { ["x"] = -0.5, ["y"] = 0.5, ["z"] = -0.5, ["w"] = 0.5 }
            },
            ["parameters"] = new JsonObject
            {
                ["intrinsics"] = new JsonObject
                {
                    ["width_px"] = 4608,
                    ["height_px"] = 2592,
                    ["focal_length_x_px"] = 3548.0,
                    ["focal_length_y_px"] = 3548.0,
                    ["principal_point_x_px"] = 2304.0,
                    ["principal_point_y_px"] = 1296.0
                },
                ["exposure_duration_s"] = 0.0005
            }
        };

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
            DeviceConfiguration Light(string sensorId, string frameId, Vector3d position, Vector3d sensorX, Vector3d sensorY, Vector3d sensorZ) =>
                LightSensorKind.Instance.Configure(
                    Definition(sensorId, "placeholder-face-light-sensor", frameId, new SensorMount(position, Quaterniond.FromBasis(sensorX, sensorY, sensorZ))),
                    new LightSensorProfile(Math.PI / 2.0, 1.0, 0.0, 0.0, 2000.0));

            DeviceConfiguration[] devices =
            {
                ImuKind.Instance.Configure(
                    Definition("imu.main", "placeholder-mems-imu", "imu_main", SensorMount.Identity),
                    new ImuProfile(
                        new GyroscopeProfile(1.2e-4, 0.0, 0.0, zero, unitScale, 4.363, 1.33e-4),
                        new AccelerometerProfile(1.8e-3, 0.0, 0.0, zero, unitScale, 19.61, 6.0e-4))),
                MagnetometerKind.Instance.Configure(
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
                kernelSetId: "argus-2026-11-02",
                spacecraft: new SpacecraftConfiguration(
                    1.33,
                    new Matrix3d(
                        new Vector3d(2.536e-3, 0.0, 0.0),
                        new Vector3d(0.0, 2.536e-3, 0.0),
                        new Vector3d(0.0, 0.0, 2.217e-3)),
                    new Quaterniond(0.0, 0.0, 0.0, 1.0),
                    zero),
                devices: devices);
        }
    }
}
