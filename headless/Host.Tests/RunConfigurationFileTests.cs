using System;
using System.IO;
using System.Linq;
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

            // The file's Basilisk-modelled devices are the compiled P0 devices, in order, with the
            // SensorDefinition fields that stay in Argus and never reach the wire. Its cameras are
            // checked in P0FileHasTheFourDataSixBodyCameras.
            DeviceConfiguration[] backend = loaded.Devices.Where(device => device.IsBackendModelled).ToArray();
            Assert.That(backend.Length, Is.EqualTo(expected.Devices.Count));
            for (int i = 0; i < expected.Devices.Count; i++)
            {
                SensorDefinition want = expected.Devices[i].Definition;
                SensorDefinition got = backend[i].Definition;
                Assert.That(got.SensorId, Is.EqualTo(want.SensorId));
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

        // The four body cameras (CORE-6) with the DATA-6 (#71) v0 profile: the Arducam IMX708 spec
        // estimate, and Argus-2's mounts given as R (camera to body, OpenCV camera frame) and t.
        [TestCase("cam.px", 0, -1, 0, 0, 0, -1, 1, 0, 0, 0.05116, -0.000198, -0.02927)]
        [TestCase("cam.py", 1, 0, 0, 0, 0, -1, 0, 1, 0, -0.00018, 0.051362, -0.02927)]
        [TestCase("cam.mx", 0, 1, 0, 0, 0, -1, -1, 0, 0, -0.05214, 0.000022, -0.02927)]
        [TestCase("cam.my", -1, 0, 0, 0, 0, -1, 0, -1, 0, -0.00030, -0.051438, -0.02927)]
        public void P0FileHasTheFourDataSixBodyCameras(
            string deviceId,
            double xx, double xy, double xz,
            double yx, double yy, double yz,
            double zx, double zy, double zz,
            double tx, double ty, double tz)
        {
            SimulationConfiguration p0 = RunConfigurationFile.Load(HostOptions.DefaultRunConfigurationPath, RunId);
            DeviceConfiguration[] cameras = p0.Devices.Where(device => device.KindDefinition is ICameraKind).ToArray();
            Assert.That(cameras.Select(camera => camera.Definition.SensorId), Is.EqualTo(new[] { "cam.px", "cam.py", "cam.mx", "cam.my" }));
            Assert.That(
                Request(p0).Configuration.Devices.Select(device => device.DeviceId),
                Is.EqualTo(p0.Devices.Where(device => device.IsBackendModelled).Select(device => device.Definition.SensorId)));

            DeviceConfiguration camera = cameras.Single(c => c.Definition.SensorId == deviceId);
            SensorDefinition definition = camera.Definition;
            Assert.That(camera.Kind, Is.EqualTo(CameraKind.KindName));
            Assert.That((definition.ModelName, definition.Source, definition.IsControllerVisible), Is.EqualTo(("arducam-imx708", "argus", false)));
            Assert.That(definition.SamplePeriodSeconds, Is.EqualTo(15.0), "flight's capture interval (FSW-Payload dataset_config.toml)");

            // Datasheet: 4608 x 2592 pixels and a 66 degree horizontal field of view.
            CameraProfile profile = CameraKind.Instance.Profile(camera);
            CameraIntrinsics intrinsics = profile.Intrinsics;
            double focalLengthFromFieldOfView = 4608 / 2.0 / Math.Tan(66.0 / 2.0 * Math.PI / 180.0);
            Assert.That((intrinsics.WidthPixels, intrinsics.HeightPixels), Is.EqualTo((4608, 2592)));
            Assert.That(intrinsics.FocalLengthXPixels, Is.EqualTo(focalLengthFromFieldOfView).Within(0.5));
            Assert.That(intrinsics.FocalLengthYPixels, Is.EqualTo(intrinsics.FocalLengthXPixels));
            Assert.That((intrinsics.PrincipalPointXPixels, intrinsics.PrincipalPointYPixels), Is.EqualTo((2304.0, 1296.0)));
            Assert.That(profile.ExposureDurationSeconds, Is.EqualTo(0.0005));
            Assert.That((profile.ProfileId, profile.IntrinsicsSource, profile.IsCalibrated), Is.EqualTo(("imx708-66deg-spec-v0", "spec-estimate", false)));
            Assert.That(profile.Distortion, Is.EqualTo(CameraDistortion.Zero("none")));
            Assert.That(profile.NoiseSource, Is.EqualTo(CameraProfile.NoNoise));
            Assert.That(profile.Extrinsics, Is.EqualTo(new CameraExtrinsicsProvenance("argus-2-config", "assumed-com", false)));

            // The mount takes each camera axis to R's column; q and -q are the same rotation.
            Quaterniond deviceToBody = definition.Mount.SensorToBody;
            AssertClose(deviceToBody.Rotate(new Vector3d(1, 0, 0)), new Vector3d(xx, xy, xz), "camera x (image right)");
            AssertClose(deviceToBody.Rotate(new Vector3d(0, 1, 0)), new Vector3d(yx, yy, yz), "camera y (image down)");
            AssertClose(deviceToBody.Rotate(new Vector3d(0, 0, 1)), new Vector3d(zx, zy, zz), "camera z (boresight)");
            AssertClose(definition.Mount.PositionBodyMeters, new Vector3d(tx, ty, tz), "position");
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
            root["devices"].AsArray().Insert(1, CameraNode("cam.test"));

            SimulationConfiguration withCamera = RunConfigurationFile.Parse(root.ToJsonString(), RunId);
            SimulationConfiguration p0 = RunConfigurationFile.Parse(P0Text, RunId);

            Assert.That(withCamera.IsValid, Is.True);
            Assert.That(withCamera.Devices.Count, Is.EqualTo(p0.Devices.Count + 1));
            DeviceConfiguration camera = withCamera.Devices[1];
            Assert.That(camera.Kind, Is.EqualTo("camera"));
            Assert.That(camera.IsBackendModelled, Is.False);
            Assert.That(camera.Definition.SensorId, Is.EqualTo("cam.test"));
            CameraProfile profile = CameraKind.Instance.Profile(camera);
            Assert.That(profile.Intrinsics.WidthPixels, Is.EqualTo(4608));
            Assert.That(profile.Intrinsics.HeightPixels, Is.EqualTo(2592));
            Assert.That(profile.Intrinsics.FocalLengthXPixels, Is.EqualTo(3548.0));
            Assert.That(profile.Intrinsics.PrincipalPointYPixels, Is.EqualTo(1296.0));
            Assert.That(profile.ExposureDurationSeconds, Is.EqualTo(0.0005));
            Assert.That(profile.ProfileId, Is.EqualTo("imx708-66deg-spec-v0"));
            Assert.That((profile.IntrinsicsSource, profile.IsCalibrated), Is.EqualTo(("spec-estimate", false)));
            Assert.That(profile.Distortion, Is.EqualTo(CameraDistortion.Zero("none")));
            Assert.That(profile.NoiseSource, Is.EqualTo(CameraProfile.NoNoise));
            Assert.That(profile.Extrinsics, Is.EqualTo(new CameraExtrinsicsProvenance("argus-2-config", "assumed-com", false)));
            Assert.That(Request(withCamera), Is.EqualTo(Request(p0)));
        }

        // The nadir ground-truth camera is a devices[] entry of its own kind, with the camera's
        // DATA-6 parameters and its own profile ID next to P0's body cameras. Argus computes it, so it
        // is never sent to Basilisk either (CORE-8).
        [Test]
        public void NadirGroundTruthCameraLoadsButIsNotSentToBasilisk()
        {
            JsonObject root = P0();
            root["devices"].AsArray().Add(NadirCameraNode("cam.nadir_gt"));

            SimulationConfiguration withCamera = RunConfigurationFile.Parse(root.ToJsonString(), RunId);
            SimulationConfiguration p0 = RunConfigurationFile.Parse(P0Text, RunId);

            Assert.That(withCamera.IsValid, Is.True);
            DeviceConfiguration camera = withCamera.Devices[withCamera.Devices.Count - 1];
            Assert.That(camera.Kind, Is.EqualTo("nadir_ground_truth_camera"));
            Assert.That(camera.IsBackendModelled, Is.False);
            Assert.That(camera.MayBeControllerVisible, Is.False);
            Assert.That(camera.Definition.IsControllerVisible, Is.False);
            CameraProfile profile = NadirGroundTruthCameraKind.Instance.Profile(camera);
            Assert.That(profile.ProfileId, Is.EqualTo("nadir-gt-768x432-9deg"));
            Assert.That(profile.Intrinsics.WidthPixels, Is.EqualTo(768));
            Assert.That(profile.Intrinsics.FocalLengthYPixels, Is.EqualTo(2744.54));
            Assert.That(Request(withCamera), Is.EqualTo(Request(p0)));
        }

        [TestCase("missing field", "initialOrbit.eccentricity: is required")]
        [TestCase("unknown field", "spacecraft.massKg: is not a known field")]
        [TestCase("invalid orbit", "initialOrbit: needs finite elements")]
        [TestCase("invalid profile", "devices[0].parameters[\"gyroscope.*\"]: is not a valid gyroscope profile (see GyroscopeProfile.IsValid)")]
        [TestCase("repeated parameter", "devices[0].parameters.gyroscope.range_rad_per_s: repeats devices[0].parameters[\"gyroscope.range_rad_per_s\"]")]
        [TestCase("unknown initial state", "devices[1].initialState.speed_rad_per_s: is not a magnetometer initial-state key")]
        [TestCase("duplicate device ID", "devices[1].deviceId: duplicates another device's ID")]
        [TestCase("unknown kind", "devices[2].kind: 'sun_sensor' is not a registered device kind")]
        [TestCase("unknown parameter", "devices[1].parameters.gain: is not a magnetometer parameter")]
        [TestCase("missing parameter", "devices[1].parameters[\"range_tesla\"]: is required")]
        [TestCase("wrong parameter type", "devices[2].parameters.scale_factor: must be a finite number")]
        [TestCase("wrong type", "devices[0].mount.positionBodyMeters: must be an array of 3 numbers")]
        [TestCase("epoch without zone", "epochUtc: must be a UTC time")]
        [TestCase("invalid kernel set", "kernelSetId: must match")]
        [TestCase("invalid camera intrinsics", "devices[{new}].parameters[\"intrinsics.width_px\"]: must be > 0")]
        [TestCase("exposure longer than the period", "devices[{new}].parameters[\"exposure_duration_s\"]: must not exceed samplePeriodSeconds (1.5 s > 1 s)")]
        [TestCase("invalid intrinsics and a long exposure", "devices[{new}].parameters[\"intrinsics.width_px\"]: must be > 0")]
        [TestCase("fractional pixel count", "devices[{new}].parameters[\"intrinsics.width_px\"]: must be a whole number of pixels")]
        [TestCase("period off the step grid", "devices[{new}].samplePeriodSeconds: must be a whole multiple of fixedStepSeconds")]
        [TestCase("controller-visible camera", "devices[{new}].controllerVisible: must be false")]
        [TestCase("fisheye", "devices[{new}].parameters[\"distortion.model\"]: must be \"brown-conrady-5\"")]
        [TestCase("non-zero distortion", "devices[{new}].parameters[\"distortion.*\"]: must be all zero until a renderer applies distortion")]
        [TestCase("noise model", "devices[{new}].parameters[\"noise.source\"]: must be \"none\"")]
        [TestCase("uppercase profile ID", "devices[{new}].parameters[\"profile_id\"]: must match")]
        [TestCase("blank label", "devices[{new}].parameters[\"extrinsics.origin\"]: must not be blank")]
        [TestCase("calibrated not a flag", "devices[{new}].parameters.intrinsics.calibrated: must be true or false")]
        [TestCase("profile ID reused, other intrinsics", "devices[{new}].parameters[\"profile_id\"]: names another parameter set than cam.px: it differs in intrinsics")]
        [TestCase("profile ID reused, other distortion source", "devices[{new}].parameters[\"profile_id\"]: names another parameter set than cam.px: it differs in distortion")]
        [TestCase("coefficients as an array", "devices[{new}].parameters.distortion.coefficients: is not a camera parameter")]
        [TestCase("invalid nadir camera intrinsics", "devices[{new}].parameters[\"intrinsics.focal_length_y_px\"]: must be > 0")]
        [TestCase("controller-visible nadir camera", "devices[{new}].controllerVisible: must be false")]
        [TestCase("nadir camera with the body cameras' profile ID", "devices[{new}].parameters[\"profile_id\"]: names another parameter set than cam.px: it differs in intrinsics")]
        public void InvalidFileNamesTheField(string change, string expectedMessage)
        {
            JsonObject root = P0();
            JsonArray devices = root["devices"].AsArray();
            // A camera case appends a test camera after P0's devices.
            expectedMessage = expectedMessage.Replace("{new}", devices.Count.ToString());
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
                case "repeated parameter":
                    // Both spellings of one key: dotted, and inside its group.
                    JsonObject imuParameters = devices[0]["parameters"].AsObject();
                    JsonObject gyroscope = imuParameters["gyroscope"].AsObject();
                    imuParameters.Remove("gyroscope");
                    imuParameters["gyroscope.range_rad_per_s"] = 4.363;
                    imuParameters["gyroscope"] = gyroscope;
                    break;
                case "unknown initial state":
                    devices[1]["initialState"] = new JsonObject { ["speed_rad_per_s"] = 1.0 };
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
                    JsonObject camera = CameraNode("cam.test");
                    camera["parameters"]["intrinsics"]["width_px"] = 0;
                    devices.Add(camera);
                    break;
                case "exposure longer than the period":
                    JsonObject longExposure = CameraNode("cam.test");
                    longExposure["parameters"]["exposure_duration_s"] = 1.5;
                    devices.Add(longExposure);
                    break;
                case "invalid intrinsics and a long exposure":
                    JsonObject both = CameraNode("cam.test");
                    both["parameters"]["intrinsics"]["width_px"] = 0;
                    both["parameters"]["exposure_duration_s"] = 1.5;
                    devices.Add(both);
                    break;
                case "fractional pixel count":
                    JsonObject fractional = CameraNode("cam.test");
                    fractional["parameters"]["intrinsics"]["width_px"] = 4608.5;
                    devices.Add(fractional);
                    break;
                case "period off the step grid":
                    JsonObject offGrid = CameraNode("cam.test");
                    offGrid["samplePeriodSeconds"] = 0.25;
                    devices.Add(offGrid);
                    break;
                case "controller-visible camera":
                    JsonObject visible = CameraNode("cam.test");
                    visible["controllerVisible"] = true;
                    devices.Add(visible);
                    break;
                case "fisheye":
                    devices.Add(CameraWith("distortion", "model", "kannala-brandt"));
                    break;
                case "non-zero distortion":
                    devices.Add(CameraWith("distortion", "k1", 0.1));
                    break;
                case "noise model":
                    devices.Add(CameraWith("noise", "source", "dark-frames"));
                    break;
                case "uppercase profile ID":
                    JsonObject uppercase = CameraNode("cam.test");
                    uppercase["parameters"]["profile_id"] = "IMX708";
                    devices.Add(uppercase);
                    break;
                case "blank label":
                    devices.Add(CameraWith("extrinsics", "origin", ""));
                    break;
                case "calibrated not a flag":
                    devices.Add(CameraWith("intrinsics", "calibrated", "no"));
                    break;
                case "profile ID reused, other intrinsics":
                    // P0's cam.px set imx708-66deg-spec-v0 first.
                    devices.Add(CameraWith("intrinsics", "focal_length_y_px", 3386.0));
                    break;
                case "profile ID reused, other distortion source":
                    devices.Add(CameraWith("distortion", "source", "checkerboard"));
                    break;
                case "coefficients as an array":
                    JsonObject asArray = CameraNode("cam.test");
                    JsonObject distortion = asArray["parameters"]["distortion"].AsObject();
                    foreach (string coefficient in new[] { "k1", "k2", "p1", "p2", "k3" })
                    {
                        distortion.Remove(coefficient);
                    }

                    distortion["coefficients"] = new JsonArray(0.0, 0.0, 0.0, 0.0, 0.0);
                    devices.Add(asArray);
                    break;
                case "invalid nadir camera intrinsics":
                    JsonObject invalidNadir = NadirCameraNode("cam.nadir_gt");
                    invalidNadir["parameters"]["intrinsics"]["focal_length_y_px"] = -2744.54;
                    devices.Add(invalidNadir);
                    break;
                case "controller-visible nadir camera":
                    JsonObject visibleNadir = NadirCameraNode("cam.nadir_gt");
                    visibleNadir["controllerVisible"] = true;
                    devices.Add(visibleNadir);
                    break;
                case "nadir camera with the body cameras' profile ID":
                    // Cameras that share a profile ID share its parameters (CORE-6), so the nadir camera
                    // needs its own.
                    JsonObject sharedId = NadirCameraNode("cam.nadir_gt");
                    sharedId["parameters"]["profile_id"] = "imx708-66deg-spec-v0";
                    devices.Add(sharedId);
                    break;
            }

            RunConfigurationException error = Assert.Throws<RunConfigurationException>(
                () => RunConfigurationFile.Parse(root.ToJsonString(), RunId));
            Assert.That(error.Message, Does.StartWith(expectedMessage));
        }

        // Which side models a device belongs to its kind and is not configured per device
        // (docs/device-model.md §1), so the run file has no source field: the host sets it from the
        // kind (CORE-22).
        [Test]
        public void ADevicesSourceComesFromItsKind()
        {
            JsonObject root = P0();
            foreach (JsonNode device in root["devices"].AsArray())
            {
                Assert.That(device.AsObject().ContainsKey("source"), Is.False, "p0-default.json still sets a device's source");
            }

            SimulationConfiguration configuration = RunConfigurationFile.Parse(root.ToJsonString(), RunId);

            Assert.That(configuration.Devices, Is.Not.Empty);
            foreach (DeviceConfiguration device in configuration.Devices)
            {
                Assert.That(device.Definition.Source, Is.EqualTo(device.IsBackendModelled ? "basilisk" : "argus"), device.Definition.SensorId);
            }
        }

        // An old file that still sets source is told why it no longer can (CORE-22).
        [Test]
        public void SourceIsNoLongerARunFileField()
        {
            JsonObject root = P0();
            root["devices"][0]["source"] = "basilisk";

            RunConfigurationException error = Assert.Throws<RunConfigurationException>(
                () => RunConfigurationFile.Parse(root.ToJsonString(), RunId));
            Assert.That(error.Message, Is.EqualTo("devices[0].source: is no longer a run-file field; the device's kind sets it (device-model §1)"));
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
            ["controllerVisible"] = false,
            ["mount"] = new JsonObject
            {
                ["positionBodyMeters"] = new JsonArray(0.05116, -0.000198, -0.02927),
                ["deviceToBody"] = new JsonObject { ["x"] = -0.5, ["y"] = 0.5, ["z"] = -0.5, ["w"] = 0.5 }
            },
            ["parameters"] = new JsonObject
            {
                ["profile_id"] = "imx708-66deg-spec-v0",
                ["intrinsics"] = new JsonObject
                {
                    ["width_px"] = 4608,
                    ["height_px"] = 2592,
                    ["focal_length_x_px"] = 3548.0,
                    ["focal_length_y_px"] = 3548.0,
                    ["principal_point_x_px"] = 2304.0,
                    ["principal_point_y_px"] = 1296.0,
                    ["source"] = "spec-estimate",
                    ["calibrated"] = false
                },
                ["distortion"] = new JsonObject
                {
                    ["model"] = "brown-conrady-5",
                    ["k1"] = 0.0,
                    ["k2"] = 0.0,
                    ["p1"] = 0.0,
                    ["p2"] = 0.0,
                    ["k3"] = 0.0,
                    ["source"] = "none"
                },
                ["noise"] = new JsonObject { ["source"] = "none" },
                ["exposure_duration_s"] = 0.0005,
                ["extrinsics"] = new JsonObject
                {
                    ["source"] = "argus-2-config",
                    ["origin"] = "assumed-com",
                    ["orientation_verified"] = false
                }
            }
        };

        // The Unity ground-truth camera's view (CubeSatCameraRig): 768 x 432 px, 9 degrees vertical,
        // with its own profile ID. The mount is ignored, so it is the identity.
        private static JsonObject NadirCameraNode(string deviceId)
        {
            JsonObject camera = CameraNode(deviceId);
            camera["kind"] = "nadir_ground_truth_camera";
            camera["modelName"] = "nadir-ground-truth";
            camera["mount"] = new JsonObject
            {
                ["positionBodyMeters"] = new JsonArray(0.0, 0.0, 0.0),
                ["deviceToBody"] = new JsonObject { ["x"] = 0.0, ["y"] = 0.0, ["z"] = 0.0, ["w"] = 1.0 }
            };
            JsonNode parameters = camera["parameters"];
            parameters["profile_id"] = "nadir-gt-768x432-9deg";
            parameters["intrinsics"]["width_px"] = 768;
            parameters["intrinsics"]["height_px"] = 432;
            parameters["intrinsics"]["focal_length_x_px"] = 2744.54;
            parameters["intrinsics"]["focal_length_y_px"] = 2744.54;
            parameters["intrinsics"]["principal_point_x_px"] = 384.0;
            parameters["intrinsics"]["principal_point_y_px"] = 216.0;
            parameters["intrinsics"]["source"] = "unity-gt-rig";
            parameters["exposure_duration_s"] = 0.0;
            parameters["extrinsics"]["source"] = "computed-nadir";
            parameters["extrinsics"]["origin"] = "spacecraft-position";
            return camera;
        }

        private static void AssertClose(Vector3d actual, Vector3d expected, string what)
        {
            Assert.That(actual.X, Is.EqualTo(expected.X).Within(1e-12), what + " x");
            Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(1e-12), what + " y");
            Assert.That(actual.Z, Is.EqualTo(expected.Z).Within(1e-12), what + " z");
        }

        // A test camera with one parameter in a group changed.
        private static JsonObject CameraWith(string group, string key, JsonNode value)
        {
            JsonObject camera = CameraNode("cam.test");
            camera["parameters"][group][key] = value;
            return camera;
        }

        // What BasiliskEngine.Initialize sends with a real-time factor of 0.
        private static PbBasilisk.ConfigureRunRequest Request(SimulationConfiguration configuration) =>
            new PbBasilisk.ConfigureRunRequest
            {
                Configuration = BasiliskProtoMapper.ToProto(configuration, FixedStepNanoseconds),
                RealTimeFactor = 0.0
            };

        // The DATA-1 (#67) P0 values, written out so a change to runs/p0-default.json has to be deliberate.
        private static SimulationConfiguration CompiledP0Default(string runId)
        {
            Vector3d zero = new Vector3d(0.0, 0.0, 0.0);
            Vector3d unitScale = new Vector3d(1.0, 1.0, 1.0);
            SensorDefinition Definition(string sensorId, string modelName, string frameId, double samplePeriodSeconds, SensorMount mount) =>
                new SensorDefinition(sensorId, modelName, frameId, samplePeriodSeconds, "basilisk", mount);
            DeviceConfiguration Light(string sensorId, string frameId, Vector3d position, Quaterniond deviceToBody) =>
                LightSensorKind.Instance.Configure(
                    Definition(sensorId, "ti-opt4003-q1-uson", frameId, 1.0, new SensorMount(position, deviceToBody)),
                    new LightSensorProfile(Math.PI / 2.0, 1.0, 1.0, 0.0, 1460.0));

            DeviceConfiguration[] devices =
            {
                ImuKind.Instance.Configure(
                    Definition("imu.main", "bosch-bmx160-gyro125dps-accel8g", "imu_main", 0.1, SensorMount.Identity),
                    new ImuProfile(
                        new GyroscopeProfile(1.2217e-4, 0.0, 0.0, zero, unitScale, 2.18166, 6.6514e-5),
                        new AccelerometerProfile(1.7652e-3, 0.0, 0.0, zero, unitScale, 78.4532, 2.3942e-3))),
                MagnetometerKind.Instance.Configure(
                    Definition("mag.main", "bosch-bmx160-bmm150-high-accuracy", "mag_main", 0.1, SensorMount.Identity),
                    new MagnetometerProfile(3.0e-7, zero, 1.0, 1.3e-3)),
                Light("light.px", "light_px", new Vector3d(0.05, 0.0, 0.0), new Quaterniond(0.5, 0.5, 0.5, 0.5)),
                Light("light.mx", "light_mx", new Vector3d(-0.05, 0.0, 0.0), new Quaterniond(-0.5, 0.5, 0.5, -0.5)),
                Light("light.py", "light_py", new Vector3d(0.0, 0.05, 0.0), new Quaterniond(0.5, 0.5, 0.5, -0.5)),
                Light("light.my", "light_my", new Vector3d(0.0, -0.05, 0.0), new Quaterniond(-0.5, -0.5, 0.5, -0.5)),
                Light("light.mz", "light_mz", new Vector3d(0.0, 0.0, -0.1135), new Quaterniond(1.0, 0.0, 0.0, 0.0)),
                Light("light.zp_xp", "light_zp_xp", new Vector3d(0.01, 0.0, 0.1135), new Quaterniond(0.0, 0.3826834, 0.0, 0.9238795)),
                Light("light.zp_ym", "light_zp_ym", new Vector3d(0.0, -0.01, 0.1135), new Quaterniond(0.3826834, 0.0, 0.0, 0.9238795)),
                Light("light.zp_xm", "light_zp_xm", new Vector3d(-0.01, 0.0, 0.1135), new Quaterniond(0.0, -0.3826834, 0.0, 0.9238795)),
                Light("light.zp_yp", "light_zp_yp", new Vector3d(0.0, 0.01, 0.1135), new Quaterniond(-0.3826834, 0.0, 0.0, 0.9238795)),
                // An actuator has no period and publishes no frames (docs/device-model.md §1).
                IdealBodyTorqueKind.Instance.Configure(
                    new SensorDefinition(
                        "torque.body", "placeholder-ideal-body-torque", "torque_body", 0.0, "basilisk", SensorMount.Identity,
                        isControllerVisible: false),
                    new IdealBodyTorqueProfile(new Vector3d(1e-3, 1e-3, 1e-3), ActuatorLimitPolicy.Clamp))
            };

            return new SimulationConfiguration(
                runId,
                new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
                0.1,
                initialOrbit: new ClassicalOrbitElements(6_958_137.0, 0.0, 97.75, 234.06, 0.0, 0.0),
                randomSeed: 1,
                kernelSetId: "argus-2026-11-02",
                spacecraft: new SpacecraftConfiguration(
                    2.6,
                    new Matrix3d(
                        new Vector3d(1.906e-2, 0.0, 0.0),
                        new Vector3d(0.0, 1.931e-2, 0.0),
                        new Vector3d(0.0, 0.0, 8.24e-3)),
                    new Quaterniond(0.0, 0.0, 0.0, 1.0),
                    new Vector3d(0.1, -0.08, 0.12)),
                devices: devices);
        }
    }
}
