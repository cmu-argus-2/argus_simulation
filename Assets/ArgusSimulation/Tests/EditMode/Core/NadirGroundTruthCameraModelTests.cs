using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class NadirGroundTruthCameraModelTests
    {
        private const string RunId = "nadir-run";
        private const string CameraId = "cam.nadir_gt";
        private const double StepSeconds = 0.1;
        private const double Tolerance = 1e-12;

        // WGS84, as NadirGroundTruthCameraModel uses.
        private const double EquatorialRadiusMeters = 6_378_137.0;
        private const double EccentricitySquared = 6.69437999014e-3;

        private static readonly Quaterniond Level = new Quaterniond(0.0, 0.0, 0.0, 1.0);
        private static readonly Quaterniond Tumbled = new Quaterniond(0.3, -0.5, 0.1, 0.8).Normalized();

        private string _root;

        [SetUp]
        public void CreateRoot()
        {
            _root = Path.Combine(Path.GetTempPath(), "argus-nadir-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void DeleteRoot()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        // Acceptance (#44): two states with the same position and different attitudes give the same
        // RenderRequest pose.
        [Test]
        public void PoseFollowsPositionAndIgnoresAttitude()
        {
            Vector3d position = Geodetic(40.0, -80.0, 500_000.0);

            RenderRequest a = Request(position, Level);
            RenderRequest b = Request(position, Tumbled);

            Assert.That(Distance(a.CameraPositionEcefMeters, position), Is.LessThan(Tolerance));
            Assert.That(Distance(b.CameraPositionEcefMeters, position), Is.LessThan(Tolerance));
            AssertSameRotation(a.CameraToEcef, b.CameraToEcef);
        }

        // The mount is ignored too. With a lever arm and a 90 degree rotation, the pose is the same
        // across attitudes (a lever arm or rotation applied through the attitude would differ) and
        // the same as with the identity mount (a rotation applied on its own would differ).
        [Test]
        public void PoseIgnoresTheMount()
        {
            Vector3d position = Geodetic(40.0, -80.0, 500_000.0);
            double half = Math.Sqrt(0.5);
            SensorMount mounted = new SensorMount(new Vector3d(0.05, 0.0, 0.0), new Quaterniond(0.0, 0.0, half, half));

            RenderRequest level = Request(position, Level, mounted);
            RenderRequest tumbled = Request(position, Tumbled, mounted);
            RenderRequest unmounted = Request(position, Level);

            Assert.That(Distance(level.CameraPositionEcefMeters, tumbled.CameraPositionEcefMeters), Is.LessThan(Tolerance));
            AssertSameRotation(level.CameraToEcef, tumbled.CameraToEcef);
            Assert.That(Distance(level.CameraPositionEcefMeters, unmounted.CameraPositionEcefMeters), Is.LessThan(Tolerance));
            AssertSameRotation(level.CameraToEcef, unmounted.CameraToEcef);
        }

        // Camera frame x right, y down, z out of the lens (DATA-6): looking straight down with north
        // up, image right is east and image down is south.
        [Test]
        public void LooksDownWithNorthUpAndEastRight()
        {
            RenderRequest request = Request(new Vector3d(6_878_137.0, 0.0, 0.0), Level);

            AssertDirection(request.CameraToEcef.Rotate(new Vector3d(0.0, 0.0, 1.0)), new Vector3d(-1.0, 0.0, 0.0));
            AssertDirection(request.CameraToEcef.Rotate(new Vector3d(1.0, 0.0, 0.0)), new Vector3d(0.0, 1.0, 0.0));
            AssertDirection(request.CameraToEcef.Rotate(new Vector3d(0.0, 1.0, 0.0)), new Vector3d(0.0, 0.0, -1.0));
        }

        // "Down" is geodetic down: perpendicular to the WGS84 ellipsoid, through the geodetic point
        // beneath the spacecraft (built here from latitude, longitude and height), not the direction
        // to Earth's centre.
        [TestCase(45.0, 30.0)]
        [TestCase(-62.5, 171.0)]
        [TestCase(10.0, -100.0)]
        public void DownIsTheGeodeticNormalNotTheGeocentricDirection(double latitudeDegrees, double longitudeDegrees)
        {
            Vector3d position = Geodetic(latitudeDegrees, longitudeDegrees, 500_000.0);
            RenderRequest request = Request(position, Level);

            double phi = latitudeDegrees * Math.PI / 180.0;
            double lambda = longitudeDegrees * Math.PI / 180.0;
            Vector3d geodeticDown = new Vector3d(-Math.Cos(phi) * Math.Cos(lambda), -Math.Cos(phi) * Math.Sin(lambda), -Math.Sin(phi));
            Vector3d boresight = request.CameraToEcef.Rotate(new Vector3d(0.0, 0.0, 1.0));
            AssertDirection(boresight, geodeticDown);

            // Image up (-y) is north: it points along the meridian, towards the pole.
            Vector3d geodeticNorth = new Vector3d(-Math.Sin(phi) * Math.Cos(lambda), -Math.Sin(phi) * Math.Sin(lambda), Math.Cos(phi));
            AssertDirection(request.CameraToEcef.Rotate(new Vector3d(0.0, -1.0, 0.0)), geodeticNorth);

            Vector3d geocentricDown = new Vector3d(-position.X, -position.Y, -position.Z).Normalized();
            Assert.That(Vector3d.Dot(boresight, geocentricDown), Is.LessThan(1.0 - 1e-7), "the geocentric direction differs off the equator");
        }

        // On the polar axis north is undefined; the pose is still a proper rotation looking down.
        [TestCase(1.0)]
        [TestCase(-1.0)]
        public void PolePoseStillLooksDown(double sign)
        {
            RenderRequest request = Request(new Vector3d(0.0, 0.0, sign * 6_856_752.0), Level);

            Assert.That(request.CameraToEcef.IsUnit, Is.True);
            AssertDirection(request.CameraToEcef.Rotate(new Vector3d(0.0, 0.0, 1.0)), new Vector3d(0.0, 0.0, -sign));
            AssertDirection(request.CameraToEcef.Rotate(new Vector3d(1.0, 0.0, 0.0)), new Vector3d(0.0, 1.0, 0.0));
        }

        // Acceptance (#44): the camera is not controller-visible. Its rendered frames reach the
        // recorder (sensors.jsonl and images/) at every step, but never a gateway observation.
        [Test]
        public void FramesReachTheRecorderAndImagesButNeverAnObservation()
        {
            const int steps = 3;
            SolidRenderer renderer = new SolidRenderer();
            List<Observation> observations = new List<Observation>();
            using (RunRecorder recorder = new RunRecorder(_root, "test"))
            {
                AnalyticSimulationEngine engine = new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0);
                SimulationGateway gateway = new SimulationGateway(engine, device => SensorFactory.Create(device, renderer));
                gateway.RunStarted += (run, sensors) => recorder.BeginRun(run, sensors, analyticOrbit: engine.Orbit);
                gateway.StateProduced += recorder.RecordState;
                gateway.SensorOutputProduced += recorder.RecordSensorOutput;

                observations.Add(gateway.Reset(0, Scenario(NadirCamera(isControllerVisible: false))));
                for (int i = 0; i < steps; i++)
                {
                    observations.Add(gateway.Step());
                }

                recorder.Close();
            }

            Assert.That(renderer.Requests, Is.GreaterThan(0));
            Assert.That(observations, Has.All.Matches<Observation>(observation => observation.Frames.Count == 0));

            string run = Path.Combine(_root, RunId + "_0");
            string[] frames = File.ReadAllLines(Path.Combine(run, "sensors.jsonl"));
            Assert.That(frames, Has.Length.GreaterThan(0));
            Assert.That(frames, Has.All.Contains($"\"sensor_id\":\"{CameraId}\""));
            Assert.That(frames, Has.Some.Contains("\"status\":\"Valid\""));
            Assert.That(Directory.GetFiles(Path.Combine(run, "images", CameraId), "*.png"), Is.Not.Empty);
            string manifest = File.ReadAllText(Path.Combine(run, "manifest.json"));
            Assert.That(manifest, Does.Contain("\"kind\":\"nadir_ground_truth_camera\""));
            Assert.That(manifest, Does.Contain($"\"cameras\":[{{\"sensor_id\":\"{CameraId}\",\"profile_id\":\"nadir-gt-768x432-9deg\"}}]"));
        }

        // Cameras that share a profile ID share its parameters (CORE-6), so a nadir camera beside the
        // body cameras needs its own ID: with theirs, the run is invalid.
        [Test]
        public void ANadirCameraBesideTheBodyCamerasNeedsItsOwnProfileId()
        {
            DeviceConfiguration body = TestCameras.Configuration(
                new SensorDefinition("cam.px", "arducam-imx708", "cam_px", 1.0, "argus", SensorMount.Identity, isControllerVisible: false));
            DeviceConfiguration nadir = NadirCamera(isControllerVisible: false);
            CameraProfile own = TestCameras.NadirProfile();
            DeviceConfiguration sharedId = NadirGroundTruthCameraKind.Instance.Configure(
                nadir.Definition,
                new CameraProfile(
                    TestCameras.Imx708Profile().ProfileId, own.Intrinsics, own.IntrinsicsSource, own.IsCalibrated,
                    own.Distortion, own.NoiseSource, own.ExposureDurationSeconds, own.Extrinsics));

            Assert.That(Scenario(body, nadir).IsValid, Is.True);
            Assert.That(sharedId.IsValid, Is.True);
            Assert.That(Scenario(body, sharedId).IsValid, Is.False);
        }

        [Test]
        public void ResetRejectsAControllerVisibleNadirCamera()
        {
            SimulationGateway gateway = new SimulationGateway(new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0));

            Assert.That(Scenario(NadirCamera(isControllerVisible: true)).IsValid, Is.False);
            Assert.Throws<ArgumentException>(() => gateway.Reset(0, Scenario(NadirCamera(isControllerVisible: true))));
            Assert.That(gateway.IsRunning, Is.False);
        }

        [Test]
        public void TheFactoryBuildsItAndItIsNeverSentToTheBackend()
        {
            DeviceConfiguration configuration = NadirCamera(isControllerVisible: false);

            Assert.That(configuration.Kind, Is.EqualTo("nadir_ground_truth_camera"));
            Assert.That(SensorFactory.Create(configuration), Is.TypeOf<NadirGroundTruthCameraModel>());
            Assert.That(configuration.IsBackendModelled, Is.False);
            Assert.Throws<ArgumentException>(() => new NadirGroundTruthCameraModel(
                CameraKind.Instance.Configure(configuration.Definition, NadirGroundTruthCameraKind.Instance.Profile(configuration)),
                null));
        }

        // The pose of the one exposure a camera takes at t = 0, read from its RenderRequest. With no
        // renderer the frame is Unavailable but still carries the request.
        private static RenderRequest Request(Vector3d positionEcefMeters, Quaterniond bodyToEcef) =>
            Request(positionEcefMeters, bodyToEcef, SensorMount.Identity);

        private static RenderRequest Request(Vector3d positionEcefMeters, Quaterniond bodyToEcef, SensorMount mount)
        {
            SensorManager manager = new SensorManager();
            manager.Register(new NadirGroundTruthCameraModel(NadirCamera(isControllerVisible: false, mount), null));
            manager.Reset(new SensorResetContext(RunId, DateTimeOffset.UnixEpoch, 0));
            SpacecraftState spacecraft = new SpacecraftState(
                0,
                0.0,
                DateTimeOffset.UnixEpoch,
                positionEcefMeters,
                new Vector3d(0.0, 7_600.0, 0.0),
                bodyToEcef,
                new Vector3d(0.0, 0.0, 0.0),
                ReferenceFrame.AnalyticEarthFixed);
            SensorOutputSet output = manager.Sample(new SensorSampleContext(RunId, new SimulationState(RunId, "analytic", spacecraft, null)));

            Assert.That(output.TryGetFrame(CameraId, out SensorFrame<ImageFrame> frame), Is.True);
            return frame.Payload.Request;
        }

        private static SimulationConfiguration Scenario(params DeviceConfiguration[] devices) =>
            new SimulationConfiguration(RunId, DateTimeOffset.UnixEpoch, StepSeconds, devices: devices);

        // TestCameras.NadirProfile, sampled at every step. The mount is ignored.
        private static DeviceConfiguration NadirCamera(bool isControllerVisible) =>
            NadirCamera(isControllerVisible, SensorMount.Identity);

        private static DeviceConfiguration NadirCamera(bool isControllerVisible, SensorMount mount) =>
            TestCameras.NadirConfiguration(
                new SensorDefinition(CameraId, "nadir-ground-truth", "cam_nadir_gt", StepSeconds, "argus", mount, isControllerVisible));

        private static Vector3d Geodetic(double latitudeDegrees, double longitudeDegrees, double heightMeters)
        {
            double phi = latitudeDegrees * Math.PI / 180.0;
            double lambda = longitudeDegrees * Math.PI / 180.0;
            double n = EquatorialRadiusMeters / Math.Sqrt(1.0 - EccentricitySquared * Math.Sin(phi) * Math.Sin(phi));
            return new Vector3d(
                (n + heightMeters) * Math.Cos(phi) * Math.Cos(lambda),
                (n + heightMeters) * Math.Cos(phi) * Math.Sin(lambda),
                (n * (1.0 - EccentricitySquared) + heightMeters) * Math.Sin(phi));
        }

        private static double Distance(Vector3d a, Vector3d b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

        private static void AssertDirection(Vector3d actual, Vector3d expected) =>
            Assert.That(Distance(actual, expected.Normalized()), Is.LessThan(1e-9), $"{actual} vs {expected}");

        private static void AssertSameRotation(Quaterniond a, Quaterniond b)
        {
            foreach (Vector3d axis in new[] { new Vector3d(1.0, 0.0, 0.0), new Vector3d(0.0, 1.0, 0.0), new Vector3d(0.0, 0.0, 1.0) })
            {
                Assert.That(Distance(a.Rotate(axis), b.Rotate(axis)), Is.LessThan(Tolerance));
            }
        }

        // Completes every render at once with a mid-grey image of the requested size.
        private sealed class SolidRenderer : IImageRenderer
        {
            public int Requests { get; private set; }

            public string RendererName => "solid-test";

            public Task<ImageFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
            {
                Requests++;
                byte[] pixels = new byte[request.Intrinsics.WidthPixels * request.Intrinsics.HeightPixels * 3];
                Array.Fill(pixels, (byte)128);
                return Task.FromResult(new ImageFrame(request, ImageEncoding.Rgb8, pixels, RendererName));
            }
        }
    }
}
