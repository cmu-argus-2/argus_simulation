using System.Linq;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class DeviceKindsTests
    {
        // Each kind declares whether the backend models it, so Argus never computes a kind the
        // backend models (D10, CORE-18), and whether a controller may see its frames, which a kind
        // whose frames carry truth may not (D7), and whether it is a camera (ICameraKind: the
        // recorder's cameras[] and the shared profile-ID rule, CORE-6). A new kind fails here until it
        // has a row.
        // CORE-10: each actuator kind holds a command to its device's limits. Within them, the values
        // come back as given and excess is null; past them, each value is at its nearest limit, with its
        // sign, and excess names it. At the limit is within.
        [TestCase("ideal_body_torque", 1e-3, 1e-3, null)]
        [TestCase("ideal_body_torque", 2e-3, 1e-3, "values[\"torque_body_n_m\"] (0, 0.002, -0.0005) is past max_torque_body_n_m (0.001, 0.001, 0.001)")]
        [TestCase("ideal_body_torque", -2e-3, -1e-3, "values[\"torque_body_n_m\"] (0, -0.002, -0.0005) is past max_torque_body_n_m (0.001, 0.001, 0.001)")]
        [TestCase("magnetic_torquer", -0.0814, -0.0814, null)]
        [TestCase("magnetic_torquer", -0.1, -0.0814, "values[\"dipole_a_m2\"] -0.1 is past max_dipole_a_m2 0.0814")]
        [TestCase("momentum_wheel", 2e-3, 1e-3, "values[\"motor_torque_n_m\"] 0.002 is past max_torque_n_m 0.001")]
        [TestCase("momentum_wheel", -5e-4, -5e-4, null)]
        [TestCase("thruster", 0.02, 0.01, "values[\"thrust_n\"] 0.02 is past max_thrust_n 0.01")]
        [TestCase("thruster", 0.0, 0.0, null)]
        public void EachActuatorKindHoldsACommandToItsDevicesLimits(string kindName, double command, double expected, string expectedExcess)
        {
            (IActuatorKind kind, DeviceValues parameters, string key) = kindName switch
            {
                "ideal_body_torque" => ((IActuatorKind)IdealBodyTorqueKind.Instance,
                    Parameters(IdealBodyTorqueKind.Instance.Configure(Actuator(), new IdealBodyTorqueProfile(new Vector3d(1e-3, 1e-3, 1e-3), ActuatorLimitPolicy.Clamp))),
                    IdealBodyTorqueKind.TorqueKey),
                "magnetic_torquer" => (MagneticTorquerKind.Instance,
                    Parameters(MagneticTorquerKind.Instance.Configure(Actuator(), new MagneticTorquerProfile(new Vector3d(1.0, 0.0, 0.0), 0.0814, ActuatorLimitPolicy.Clamp))),
                    MagneticTorquerKind.DipoleKey),
                "momentum_wheel" => (MomentumWheelKind.Instance,
                    Parameters(MomentumWheelKind.Instance.Configure(Actuator(), new MomentumWheelProfile(new Vector3d(0.0, 0.0, 1.0), 3.39e-6, 1e-3, 600.0, ActuatorLimitPolicy.Reject), 0.0)),
                    MomentumWheelKind.MotorTorqueKey),
                _ => (ThrusterKind.Instance,
                    Parameters(ThrusterKind.Instance.Configure(Actuator(), new ThrusterProfile(new Vector3d(1.0, 0.0, 0.0), 0.01, ActuatorLimitPolicy.Reject))),
                    ThrusterKind.ThrustKey)
            };
            // The torque's other axes are within, so only the y component changes.
            bool isVector = kindName == "ideal_body_torque";
            DeviceValues values = isVector
                ? new DeviceValues.Builder().Add(key, new Vector3d(0.0, command, -5e-4)).Build()
                : new DeviceValues.Builder().Add(key, command).Build();
            Assert.That(kind.FindCommandProblem(values), Is.Null);

            DeviceValues limited = kind.Limit(parameters, values, out string excess);

            Assert.That(excess, Is.EqualTo(expectedExcess));
            if (isVector)
            {
                Assert.That(limited.Vector3(key), Is.EqualTo(new Vector3d(0.0, expected, -5e-4)));
            }
            else
            {
                Assert.That(limited.Number(key), Is.EqualTo(expected));
            }

            Assert.That(ReferenceEquals(limited, values), Is.EqualTo(expectedExcess == null), "unchanged values come back as given");
        }

        [Test]
        public void AnActuatorsLimitPolicyIsItsParameter()
        {
            DeviceConfiguration clamp = MagneticTorquerKind.Instance.Configure(
                Actuator(), new MagneticTorquerProfile(new Vector3d(1.0, 0.0, 0.0), 0.0814, ActuatorLimitPolicy.Clamp));
            DeviceConfiguration reject = MagneticTorquerKind.Instance.Configure(
                Actuator(), new MagneticTorquerProfile(new Vector3d(1.0, 0.0, 0.0), 0.0814, ActuatorLimitPolicy.Reject));

            Assert.That(MagneticTorquerKind.Instance.LimitPolicyOf(clamp.Parameters), Is.EqualTo(ActuatorLimitPolicy.Clamp));
            Assert.That(MagneticTorquerKind.Instance.LimitPolicyOf(reject.Parameters), Is.EqualTo(ActuatorLimitPolicy.Reject));
        }

        private static SensorDefinition Actuator() =>
            new SensorDefinition("act.test", "test-actuator", "act_test", 0.0, "basilisk", SensorMount.Identity, isControllerVisible: false);

        private static DeviceValues Parameters(DeviceConfiguration configuration) => configuration.Parameters;

        [Test]
        public void EveryKindDeclaresWhoModelsItWhetherAControllerMaySeeItAndWhetherItIsACamera()
        {
            const DeviceRole sensor = DeviceRole.Sensor;
            const DeviceRole actuator = DeviceRole.Actuator;
            (string Name, DeviceRole Role, bool IsBackendModelled, bool MayBeControllerVisible, bool IsCamera)[] kinds =
            {
                ("imu", sensor, true, true, false),
                ("magnetometer", sensor, true, true, false),
                ("light_sensor", sensor, true, true, false),
                ("camera", sensor, false, false, true),
                ("nadir_ground_truth_camera", sensor, false, false, true),
                ("ideal_body_torque", actuator, true, false, false),
                ("momentum_wheel", actuator, true, false, false),
                ("magnetic_torquer", actuator, true, false, false),
                ("thruster", actuator, true, false, false)
            };

            Assert.That(DeviceKinds.All.Select(kind => kind.Name), Is.EquivalentTo(kinds.Select(row => row.Name)));
            foreach ((string name, DeviceRole role, bool isBackendModelled, bool mayBeControllerVisible, bool isCamera) in kinds)
            {
                IDeviceKind kind = DeviceKinds.Find(name);
                Assert.That(kind.Role, Is.EqualTo(role), name);
                Assert.That(kind.IsBackendModelled, Is.EqualTo(isBackendModelled), name);
                Assert.That(kind.MayBeControllerVisible, Is.EqualTo(mayBeControllerVisible), name);
                Assert.That(kind is IBackendSensorKind, Is.EqualTo(isBackendModelled && role == sensor), name);
                Assert.That(kind is IActuatorKind, Is.EqualTo(role == actuator), name);
                Assert.That(kind is ICameraKind, Is.EqualTo(isCamera), name);
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
                Is.EqualTo("initialState[\"speed\"]: is not a magnetometer key"));
            Assert.That(
                Problem("magnetometer", With(Without(valid, "range_tesla"), "range_tesla", DeviceValue.FromText(null))),
                Is.EqualTo("parameters[\"range_tesla\"]: must be a Number, not a Text"));
            Assert.That(Problem("sun_sensor", valid), Does.StartWith("kind: 'sun_sensor' is not a registered device kind"));
        }

        // The profile accepts exactly what the CameraModel constructor accepts, for both camera kinds:
        // the body camera and the nadir ground-truth camera share their keys and checks.
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
            CameraProfile profile = TestCameras.Profile(
                new CameraIntrinsics(widthPixels, 2592, focalLengthPixels, focalLengthPixels, 2304.0, 1296.0),
                exposureDurationSeconds);
            foreach (CameraProfileKind kind in CameraKinds)
            {
                DeviceConfiguration camera = kind.Configure(Definition("cam.px", DeviceSource.Argus), profile);

                Assert.That(camera.Kind, Is.EqualTo(kind.Name));
                Assert.That(camera.IsValid, Is.EqualTo(isValid), kind.Name);
            }
        }

        // The DATA-6 (#71) profile as camera parameters (CORE-6): every value with where it came from.
        // Distortion is Brown-Conrady, one key per coefficient in OpenCV's order (there is no array
        // value type). Keys carry their units (docs/device-model.md §3).
        [Test]
        public void CameraParametersAreTheDataSixProfile()
        {
            (string Name, DeviceValueType Type)[] keys =
            {
                ("profile_id", DeviceValueType.Text),
                ("intrinsics.width_px", DeviceValueType.Number),
                ("intrinsics.height_px", DeviceValueType.Number),
                ("intrinsics.focal_length_x_px", DeviceValueType.Number),
                ("intrinsics.focal_length_y_px", DeviceValueType.Number),
                ("intrinsics.principal_point_x_px", DeviceValueType.Number),
                ("intrinsics.principal_point_y_px", DeviceValueType.Number),
                ("intrinsics.source", DeviceValueType.Text),
                ("intrinsics.calibrated", DeviceValueType.Flag),
                ("distortion.model", DeviceValueType.Text),
                ("distortion.k1", DeviceValueType.Number),
                ("distortion.k2", DeviceValueType.Number),
                ("distortion.p1", DeviceValueType.Number),
                ("distortion.p2", DeviceValueType.Number),
                ("distortion.k3", DeviceValueType.Number),
                ("distortion.source", DeviceValueType.Text),
                ("noise.source", DeviceValueType.Text),
                ("exposure_duration_s", DeviceValueType.Number),
                ("extrinsics.source", DeviceValueType.Text),
                ("extrinsics.origin", DeviceValueType.Text),
                ("extrinsics.orientation_verified", DeviceValueType.Flag)
            };

            foreach (CameraProfileKind kind in CameraKinds)
            {
                Assert.That(kind.ParameterKeys.Select(key => (key.Name, key.Type)), Is.EquivalentTo(keys), kind.Name);
            }

            // The parameters round-trip to the same profile, every field.
            CameraProfile profile = new CameraProfile(
                "imx708-cal-1",
                new CameraIntrinsics(4608, 2592, 3540.5, 3541.5, 2300.25, 1290.75),
                "checkerboard",
                isCalibrated: true,
                CameraDistortion.Zero("checkerboard-2026-11"),
                CameraProfile.NoNoise,
                0.002,
                new CameraExtrinsicsProvenance("cad-2026", "geometric-centre", isOrientationVerified: true));
            DeviceConfiguration camera = CameraKind.Instance.Configure(Definition("cam.px", DeviceSource.Argus), profile);
            Assert.That(camera.IsValid, Is.True, camera.FindProblem());
            Assert.That(CameraKind.Instance.Profile(camera), Is.EqualTo(profile));
            Assert.That(camera.Parameters["profile_id"].Text, Is.EqualTo("imx708-cal-1"));
            Assert.That(camera.Parameters["intrinsics.focal_length_y_px"].Number, Is.EqualTo(3541.5));
            Assert.That(camera.Parameters["intrinsics.calibrated"].Flag, Is.True);
            Assert.That(camera.Parameters["distortion.source"].Text, Is.EqualTo("checkerboard-2026-11"));
            Assert.That(camera.Parameters["extrinsics.origin"].Text, Is.EqualTo("geometric-centre"));
            Assert.That(camera.Parameters["extrinsics.orientation_verified"].Flag, Is.True);

            // Each coefficient lands under its own name. Non-zero distortion stays invalid until a
            // renderer applies it (UNITY-4), so it is checked on the parameters, not read back.
            DeviceConfiguration distorted = CameraKind.Instance.Configure(
                Definition("cam.px", DeviceSource.Argus),
                new CameraProfile(
                    profile.ProfileId, profile.Intrinsics, profile.IntrinsicsSource, profile.IsCalibrated,
                    new CameraDistortion(CameraDistortion.BrownConrady5, 1e-3, 2e-3, 3e-3, 4e-3, 5e-3, "calibration"),
                    profile.NoiseSource, profile.ExposureDurationSeconds, profile.Extrinsics));
            Assert.That(
                new[] { "distortion.k1", "distortion.k2", "distortion.p1", "distortion.p2", "distortion.k3" }
                    .Select(key => distorted.Parameters[key].Number),
                Is.EqualTo(new[] { 1e-3, 2e-3, 3e-3, 4e-3, 5e-3 }));
            Assert.That(distorted.FindProblem(), Does.StartWith("parameters[\"distortion.*\"]: must be all zero"));
        }

        // An invalid camera profile names the narrowest key (CORE-6). Distortion must be zero until a
        // renderer applies it (UNITY-4), and there is no noise model yet (DATA-6), so both are closed.
        [TestCase("profile_id", "IMX708", "parameters[\"profile_id\"]: must match [a-z0-9][a-z0-9._-]*")]
        [TestCase("profile_id", "", "parameters[\"profile_id\"]: must match [a-z0-9][a-z0-9._-]*")]
        [TestCase("intrinsics.source", " ", "parameters[\"intrinsics.source\"]: must not be blank")]
        [TestCase("intrinsics.width_px", 0.0, "parameters[\"intrinsics.width_px\"]: must be > 0")]
        [TestCase("intrinsics.height_px", 0.0, "parameters[\"intrinsics.height_px\"]: must be > 0")]
        [TestCase("intrinsics.focal_length_x_px", 0.0, "parameters[\"intrinsics.focal_length_x_px\"]: must be > 0")]
        [TestCase("intrinsics.focal_length_y_px", -1.0, "parameters[\"intrinsics.focal_length_y_px\"]: must be > 0")]
        [TestCase("distortion.model", "kannala-brandt", "parameters[\"distortion.model\"]: must be \"brown-conrady-5\"")]
        [TestCase("distortion.k1", 1e-3, "parameters[\"distortion.*\"]: must be all zero until a renderer applies distortion (UNITY-4 #57)")]
        [TestCase("distortion.p2", -1e-3, "parameters[\"distortion.*\"]: must be all zero until a renderer applies distortion (UNITY-4 #57)")]
        [TestCase("distortion.k3", 1e-3, "parameters[\"distortion.*\"]: must be all zero until a renderer applies distortion (UNITY-4 #57)")]
        [TestCase("distortion.source", "", "parameters[\"distortion.source\"]: must not be blank")]
        [TestCase("noise.source", "dark-frames", "parameters[\"noise.source\"]: must be \"none\": there is no camera noise model yet (DATA-6 #71)")]
        [TestCase("exposure_duration_s", -1.0, "parameters[\"exposure_duration_s\"]: must be finite and >= 0")]
        [TestCase("extrinsics.source", " ", "parameters[\"extrinsics.source\"]: must not be blank")]
        [TestCase("extrinsics.origin", "", "parameters[\"extrinsics.origin\"]: must not be blank")]
        public void InvalidCameraProfileNamesTheKey(string key, object value, string expectedProblem)
        {
            DeviceValues valid = TestCameras.Configuration(Definition("cam.px", DeviceSource.Argus)).Parameters;
            DeviceValue changed = value is string text ? DeviceValue.FromText(text) : DeviceValue.FromNumber((double)value);

            foreach (CameraProfileKind kind in CameraKinds)
            {
                Assert.That(Problem(kind.Name, With(Without(valid, key), key, changed)), Does.StartWith(expectedProblem), kind.Name);
            }
        }

        // Which kinds are cameras is the registry's ICameraKind, not a list of names, so a camera kind
        // added later (CORE-8's nadir camera, #44) must declare it: every sensor kind builds a
        // CameraModel exactly when it is an ICameraKind, and no other role is a camera. A new kind
        // fails here until it has a sample.
        [Test]
        public void EverySensorKindBuildsACameraModelExactlyWhenItIsACameraKind()
        {
            DeviceConfiguration[] samples = RegistrySamples();

            Assert.That(samples.Select(sample => sample.Kind), Is.EquivalentTo(DeviceKinds.All.Select(kind => kind.Name)));
            foreach (DeviceConfiguration sample in samples)
            {
                IDeviceKind kind = sample.KindDefinition;
                Assert.That(sample.IsValid, Is.True, sample.Kind + ": " + sample.FindProblem());
                if (kind.Role != DeviceRole.Sensor)
                {
                    Assert.That(kind is ICameraKind, Is.False, sample.Kind);
                    continue;
                }

                Assert.That(kind.CreateSensor(sample, null) is CameraModel, Is.EqualTo(kind is ICameraKind), sample.Kind);
            }

            DeviceConfiguration camera = samples[3];
            Assert.That(((ICameraKind)camera.KindDefinition).Profile(camera), Is.EqualTo(TestCameras.Imx708Profile()));
        }

        // Which side models a device belongs to its kind, not to the device (D10,
        // docs/device-model.md §1), so a configured device's source must be its kind's (CORE-22).
        // Every registered kind is covered through its registry sample.
        [Test]
        public void ADevicesSourceIsItsKindsModeller()
        {
            foreach (DeviceConfiguration sample in RegistrySamples())
            {
                bool basilisk = sample.KindDefinition.IsBackendModelled;
                string expected = basilisk ? "basilisk" : "argus";
                string expectedProblem =
                    $"source: must be \"{expected}\", because {sample.Kind} is {(basilisk ? "Basilisk-modelled" : "Argus-computed")} (D10)";

                Assert.That(DeviceSource.For(sample.KindDefinition), Is.EqualTo(expected), sample.Kind);
                Assert.That(WithSource(sample, expected).FindProblem(), Is.Null, sample.Kind);
                foreach (string wrong in new[] { basilisk ? "argus" : "basilisk", "simulation", "test" })
                {
                    DeviceConfiguration mislabelled = WithSource(sample, wrong);
                    Assert.That(mislabelled.FindProblem(), Is.EqualTo(expectedProblem), sample.Kind + " labelled " + wrong);
                    Assert.That(mislabelled.IsValid, Is.False, sample.Kind + " labelled " + wrong);
                }
            }
        }

        // Configure sets that source from the kind, whatever the definition carries, so code that
        // builds a device needn't know it (CORE-22 review). A device built with the constructor is
        // still checked, as above.
        [TestCase("test")]
        [TestCase("simulation")]
        [TestCase(DeviceSource.Argus)]
        public void ConfigureSetsTheSourceFromTheKind(string given)
        {
            DeviceConfiguration imu = ImuKind.Instance.Configure(Definition("imu.main", given), TestImu.Profile());
            DeviceConfiguration camera = TestCameras.Configuration(Definition("cam.px", given));

            Assert.That(imu.Definition.Source, Is.EqualTo(DeviceSource.Basilisk));
            Assert.That(imu.IsValid, Is.True);
            Assert.That(camera.Definition.Source, Is.EqualTo(DeviceSource.Argus));
            Assert.That(camera.IsValid, Is.True);
        }

        // MapMeasurements reads the output of every sensor the backend models, so a Basilisk-modelled
        // sensor kind must say how (IBackendSensorKind). Otherwise it would be sent to the service
        // and its samples rejected as unknown (CORE-22).
        [Test]
        public void EveryBasiliskModelledSensorKindReadsBackendOutput()
        {
            foreach (IDeviceKind kind in DeviceKinds.All)
            {
                if (kind.IsBackendModelled && kind.Role == DeviceRole.Sensor)
                {
                    Assert.That(kind, Is.InstanceOf<IBackendSensorKind>(), kind.Name);
                }
            }
        }

        // One camera can't start an exposure before its last one ends, so an exposure longer than
        // the sample period is invalid; equal is continuous exposure (CORE-22). Both camera kinds.
        [TestCase(1.0, true)]
        [TestCase(1.5, false)]
        public void ACameraMayNotExposeLongerThanItsSamplePeriod(double exposureSeconds, bool isValid)
        {
            foreach (CameraProfileKind kind in CameraKinds)
            {
                DeviceConfiguration camera = kind.Configure(
                    new SensorDefinition("cam.px", "test-model", "cam_px", 1.0, DeviceSource.Argus, SensorMount.Identity, isControllerVisible: false),
                    TestCameras.Profile(TestCameras.Imx708Intrinsics, exposureSeconds));

                Assert.That(camera.IsValid, Is.EqualTo(isValid), kind.Name);
                if (!isValid)
                {
                    Assert.That(
                        camera.FindProblem(),
                        Is.EqualTo("parameters[\"exposure_duration_s\"]: must not exceed samplePeriodSeconds (1.5 s > 1 s): one camera can't start an exposure before the last one ends"),
                        kind.Name);
                }
            }
        }

        // The message is the same on every machine: a German locale would write "1,5 s" (CORE-22
        // review).
        [Test]
        [SetCulture("de-DE")]
        public void TheExposureErrorIsTheSameInEveryLocale()
        {
            DeviceConfiguration camera = CameraKind.Instance.Configure(
                new SensorDefinition("cam.px", "test-model", "cam_px", 1.0, DeviceSource.Argus, SensorMount.Identity, isControllerVisible: false),
                TestCameras.Profile(TestCameras.Imx708Intrinsics, 1.5));

            Assert.That(camera.FindProblem(), Does.Contain("(1.5 s > 1 s)"));
        }

        // The kind's own parameter checks run before the exposure check, which reads the camera
        // profile and would otherwise throw on invalid intrinsics (CORE-22 design review).
        [Test]
        public void AnInvalidCameraProfileIsReportedBeforeItsExposure()
        {
            foreach (CameraProfileKind kind in CameraKinds)
            {
                DeviceConfiguration camera = kind.Configure(
                    new SensorDefinition("cam.px", "test-model", "cam_px", 1.0, DeviceSource.Argus, SensorMount.Identity, isControllerVisible: false),
                    TestCameras.Profile(new CameraIntrinsics(0, 2592, 3548.0, 3548.0, 2304.0, 1296.0), 1.5));

                Assert.That(camera.FindProblem(), Is.EqualTo("parameters[\"intrinsics.width_px\"]: must be > 0"), kind.Name);
                Assert.That(camera.IsValid, Is.False, kind.Name);
            }
        }

        // Both camera kinds share CameraProfileKind's keys and checks.
        private static readonly CameraProfileKind[] CameraKinds = { CameraKind.Instance, NadirGroundTruthCameraKind.Instance };

        // WriteOutput is ReadOutput's inverse under each Basilisk-modelled kind's keys, so the state
        // stream sends the keys the service sent (HOST-3).
        [Test]
        public void BackendSensorKindsWriteTheOutputTheyRead()
        {
            object[] measurements =
            {
                new ImuMeasurement(new Vector3d(1e-3, -2e-3, 3e-3), new Vector3d(0.0, 0.1, -0.2)),
                new MagnetometerMeasurement(new Vector3d(2.1e-5, -1.0e-6, 4.4e-5)),
                new LightSensorMeasurement(1361.0)
            };
            IBackendSensorKind[] kinds = { ImuKind.Instance, MagnetometerKind.Instance, LightSensorKind.Instance };
            for (int i = 0; i < kinds.Length; i++)
            {
                DeviceValues output = kinds[i].WriteOutput(measurements[i]);
                Assert.That(output.Keys, Is.EquivalentTo(kinds[i].OutputKeys.Select(key => key.Name)), kinds[i].Name);
                Assert.That(kinds[i].ReadOutput(output), Is.EqualTo(measurements[i]), kinds[i].Name);
            }

            Assert.Throws<System.ArgumentException>(() => ImuKind.Instance.WriteOutput(measurements[1]));
        }

        // A spacecraft whose only actuator is one ideal body torque device is valid. Each actuator
        // kind rejects bad limits, axes and keys, an actuator has no period and an unrotated mount,
        // and device IDs are unique across sensors and actuators (CON-2).
        [Test]
        public void ActuatorDevicesCheckTheirLimitsAxesAndIds()
        {
            DeviceConfiguration torque = IdealBodyTorque("torque.body");
            Assert.That(Run(torque).IsValid, Is.True, torque.FindProblem());
            Assert.That(Run(torque, Magnetometer()).IsValid, Is.True);

            Assert.That(
                WheelProblem(WheelParameters(new Vector3d(2.0, 0.0, 0.0), 1e-3)),
                Is.EqualTo("parameters[\"spin_axis_body\"]: must have unit length within 1e-6 (ActuatorGeometry.UnitTolerance)"));
            Assert.That(
                WheelProblem(WheelParameters(new Vector3d(1.0, 0.0, 0.0), 0.0)),
                Is.EqualTo("parameters[\"max_torque_n_m\"]: must be > 0"));
            Assert.That(
                WheelProblem(WheelParameters(new Vector3d(1.0, 0.0, 0.0), double.PositiveInfinity)),
                Is.EqualTo("parameters[\"max_torque_n_m\"]: must be finite"));
            Assert.That(
                Problem(IdealBodyTorqueKind.KindName, new DeviceValues.Builder()
                    .Add("max_torque_body_n_m", new Vector3d(1e-3, 1e-3, 1e-3))
                    .Add("limit_policy", DeviceValue.FromText("saturate"))
                    .Build()),
                Is.EqualTo("parameters[\"limit_policy\"]: must be clamp or reject"));
            Assert.That(
                Problem(ThrusterKind.KindName, new DeviceValues.Builder().Add("max_thrust_n", 0.01).Build()),
                Is.EqualTo("parameters[\"thrust_direction_body\"]: is required"));

            SensorDefinition sampled = new SensorDefinition("torque.body", "test-model", "torque_body", 0.1, "test", SensorMount.Identity, isControllerVisible: false);
            SensorDefinition rotated = new SensorDefinition(
                "torque.body", "test-model", "torque_body", 0.0, "test",
                new SensorMount(new Vector3d(0.0, 0.0, 0.0), new Quaterniond(0.0, 0.0, 0.7071067811865476, 0.7071067811865476)),
                isControllerVisible: false);
            Assert.That(new DeviceConfiguration(sampled, torque.Kind, torque.Parameters).IsValid, Is.False);
            Assert.That(new DeviceConfiguration(rotated, torque.Kind, torque.Parameters).IsValid, Is.False);

            Assert.That(
                ThrusterKind.Instance.FindCommandProblem(new DeviceValues.Builder().Add(ThrusterKind.ThrustKey, -0.01).Build()),
                Is.EqualTo("values[\"thrust_n\"]: must be >= 0"));

            DeviceConfiguration sameIdAsSensor = IdealBodyTorque("mag.main");
            Assert.That(sameIdAsSensor.IsValid, Is.True);
            Assert.That(Run(Magnetometer(), sameIdAsSensor).IsValid, Is.False);
        }

        private static SimulationConfiguration Run(params DeviceConfiguration[] devices) =>
            new SimulationConfiguration("actuators", System.DateTimeOffset.UnixEpoch, 0.1, devices: devices);

        private static string WheelProblem(DeviceValues parameters) => new DeviceConfiguration(
            ActuatorDefinition("wheel.x"),
            MomentumWheelKind.KindName,
            parameters,
            new DeviceValues.Builder().Add(MomentumWheelKind.SpeedKey, 0.0).Build()).FindProblem();

        private static DeviceValues WheelParameters(Vector3d spinAxis, double maxTorque) => new DeviceValues.Builder()
            .Add("spin_axis_body", spinAxis)
            .Add("spin_inertia_kg_m2", 1e-5)
            .Add("max_torque_n_m", maxTorque)
            .Add("max_speed_rad_per_s", 600.0)
            .Add("limit_policy", DeviceValue.FromText("clamp"))
            .Build();

        private static DeviceConfiguration Magnetometer() =>
            MagnetometerKind.Instance.Configure(
                Definition("mag.main"),
                new MagnetometerProfile(0.0, new Vector3d(0.0, 0.0, 0.0), 1.0, 1.3e-3));

        private static string Problem(string kind, DeviceValues parameters) =>
            new DeviceConfiguration(Definition("mag.main", SourceOf(kind)), kind, parameters).FindProblem();

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

        private static SensorDefinition Definition(string sensorId, string source = DeviceSource.Basilisk) =>
            new SensorDefinition(sensorId, "test-model", sensorId.Replace('.', '_'), 0.1, source, SensorMount.Identity);

        // A kind's own source, or Basilisk's for a name the registry doesn't know.
        private static string SourceOf(string kind) =>
            DeviceKinds.Find(kind) is IDeviceKind registered ? DeviceSource.For(registered) : DeviceSource.Basilisk;

        private static DeviceConfiguration WithSource(DeviceConfiguration device, string source)
        {
            SensorDefinition definition = device.Definition;
            return new DeviceConfiguration(
                new SensorDefinition(
                    definition.SensorId,
                    definition.ModelName,
                    definition.FrameId,
                    definition.SamplePeriodSeconds,
                    source,
                    definition.Mount,
                    definition.IsControllerVisible),
                device.Kind,
                device.Parameters,
                device.InitialState);
        }

        private static DeviceConfiguration[] RegistrySamples() => new[]
        {
            ImuKind.Instance.Configure(Definition("imu.main"), TestImu.Profile()),
            Magnetometer(),
            LightSensorKind.Instance.Configure(Definition("light.px"), new LightSensorProfile(1.0, 1.0, 0.0, 0.0, 1500.0)),
            TestCameras.Configuration(Definition("cam.px", DeviceSource.Argus)),
            TestCameras.NadirConfiguration(Definition("cam.nadir_gt", DeviceSource.Argus)),
            IdealBodyTorque("torque.body"),
            MomentumWheelKind.Instance.Configure(
                ActuatorDefinition("wheel.x"),
                new MomentumWheelProfile(new Vector3d(1.0, 0.0, 0.0), 1e-5, 1e-3, 600.0, ActuatorLimitPolicy.Clamp),
                0.0),
            MagneticTorquerKind.Instance.Configure(
                ActuatorDefinition("mtq.x"),
                new MagneticTorquerProfile(new Vector3d(1.0, 0.0, 0.0), 0.2, ActuatorLimitPolicy.Reject)),
            ThrusterKind.Instance.Configure(
                ActuatorDefinition("thruster.px"),
                new ThrusterProfile(new Vector3d(-1.0, 0.0, 0.0), 0.01, ActuatorLimitPolicy.Reject))
        };

        // An actuator has no sample period and publishes no frames (docs/device-model.md §1).
        private static SensorDefinition ActuatorDefinition(string deviceId) =>
            new SensorDefinition(deviceId, "test-model", deviceId.Replace('.', '_'), 0.0, DeviceSource.Basilisk, SensorMount.Identity, isControllerVisible: false);

        private static DeviceConfiguration IdealBodyTorque(string deviceId) => IdealBodyTorqueKind.Instance.Configure(
            ActuatorDefinition(deviceId),
            new IdealBodyTorqueProfile(new Vector3d(1e-3, 1e-3, 1e-3), ActuatorLimitPolicy.Clamp));
    }
}
