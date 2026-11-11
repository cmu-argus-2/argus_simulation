using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Argus.Simulation.Core;
using Argus.Simulation.Unity;
using CesiumForUnity;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Argus.Simulation.Tests
{
    public sealed class UnityImageRendererPlayModeTests
    {
        private GameObject _world;
        private GameObject _simulation;
        private readonly List<Object> _owned = new List<Object>();
        private CesiumGeoreference _reference;
        private CubeSatCameraRig _rig;
        private UnityImageRenderer _renderer;
        private Light _light;

        [SetUp]
        public void SetUp()
        {
            _world = new GameObject("Renderer test world");
            _world.transform.SetPositionAndRotation(new Vector3(7, 3, -2), Quaternion.Euler(0, 17, 0));
            _reference = _world.AddComponent<CesiumGeoreference>();
            _reference.SetOriginLongitudeLatitudeHeight(0, 0, 0);
            _world.AddComponent<CesiumCameraManager>();
            var spacecraft = new GameObject("Renderer test spacecraft");
            spacecraft.transform.SetParent(_world.transform, false);
            spacecraft.AddComponent<CesiumGlobeAnchor>();
            _rig = spacecraft.AddComponent<CubeSatCameraRig>();
            _simulation = new GameObject("Renderer test simulation");
            _renderer = _simulation.AddComponent<UnityImageRenderer>();
            _light = new GameObject("Test sunlight").AddComponent<Light>();
            _light.type = LightType.Directional;
            _light.transform.rotation = Quaternion.Euler(20, 30, 40);
            _owned.Add(_light.gameObject);
            _renderer.Configure(_rig, _reference, _light);
            SetField("stableFramesRequired", 1);
            SetField("postLoadSettleSeconds", 0f);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Object.Destroy(_simulation);
            Object.Destroy(_world);
            foreach (Object item in _owned) Object.Destroy(item);
            _owned.Clear();
            yield return null;
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator RendersRequestedPoseCalibrationAndTopDownPixelsWithCaptureMetadata()
        {
            RenderRequest request = Request();
            AddMarker(request, new Vector3d(-0.8, -0.6, 10), Color.red);
            AddMarker(request, new Vector3d(0.8, 0.6, 10), Color.green);
            RenderTexture preview = _rig.RenderTextures[0];
            Task<ImageFrame> task = _renderer.RenderAsync(request, CancellationToken.None);
            yield return Wait(task);
            ImageFrame frame = task.GetAwaiter().GetResult();
            Assert.That(frame.IsValid, Is.True);
            Assert.That(frame.Intrinsics.WidthPixels, Is.EqualTo(64));
            Assert.That(frame.Intrinsics.HeightPixels, Is.EqualTo(48));
            Assert.That(frame.Data.Length, Is.EqualTo(64 * 48 * 3));
            Assert.That(frame.Sequence, Is.EqualTo(3));
            Assert.That(frame.StateSequence, Is.EqualTo(42));
            Assert.That(frame.TimestampUtc, Is.EqualTo(request.ExposureStartUtc));
            Assert.That(frame.ExposureDurationSeconds, Is.EqualTo(0.025));
            Assert.That(frame.CameraToEcef.W, Is.EqualTo(request.CameraToEcef.W));
            AssertPixel(frame, 21, 15, 0); // cx + fx*x/z; cy + fy*y/z
            AssertPixel(frame, 33, 23, 1);
            Assert.That(_rig.Cameras[0].targetTexture, Is.SameAs(preview));
            Assert.That(_rig.Cameras[0].enabled, Is.True);
            Assert.That(_rig.RequestCamera.enabled, Is.False);
            Assert.That(_rig.RequestCamera.targetTexture, Is.Null);
        }

        [UnityTest]
        public IEnumerator AppliesRequestSunOnlyDuringRenderAndRestoresTheDisplayLight()
        {
            RenderRequest request = Request(withSun: true);
            Quaternion original = _light.transform.rotation;
            Vector3? duringRender = null;
            Camera.CameraCallback callback = camera =>
            {
                if (camera == _rig.RequestCamera) duringRender = _light.transform.forward;
            };
            Camera.onPreRender += callback;
            try
            {
                Task<ImageFrame> task = _renderer.RenderAsync(request, CancellationToken.None);
                yield return Wait(task);
                Assert.That(task.GetAwaiter().GetResult().Request.HasSunPosition, Is.True);
                Assert.That(duringRender.HasValue, Is.True);
                double3 local = _reference.TransformEarthCenteredEarthFixedDirectionToUnity(new double3(1, 2, 3));
                Vector3 towardSun = _reference.transform.TransformDirection(new Vector3((float)local.x, (float)local.y, (float)local.z));
                Assert.That(Vector3.Angle(duringRender.Value, -towardSun), Is.LessThan(0.01f));
                Assert.That(Quaternion.Angle(_light.transform.rotation, original), Is.LessThan(0.01f));
            }
            finally { Camera.onPreRender -= callback; }
        }

        [UnityTest]
        public IEnumerator CancellationAndDisableCompleteTasksAndReleaseTheRequestCamera()
        {
            SetField("stableFramesRequired", 100);
            using (var cancellation = new CancellationTokenSource())
            {
                Task<ImageFrame> active = _renderer.RenderAsync(Request(), cancellation.Token);
                yield return null;
                yield return null;
                Assert.That(active.IsCompleted, Is.False);
                CesiumCameraManager manager = _world.GetComponent<CesiumCameraManager>();
                Assert.That(manager.useMainCamera, Is.False);
                Assert.That(manager.additionalCameras.Count, Is.EqualTo(1));
                Assert.That(manager.additionalCameras[0], Is.SameAs(_rig.RequestCamera));
                cancellation.Cancel();
                yield return Wait(active);
                Assert.That(active.IsCanceled, Is.True);
                Assert.That(manager.useMainCamera, Is.True);
                foreach (Camera view in _rig.Cameras) Assert.That(manager.additionalCameras, Has.Member(view));
                Assert.That(_rig.RequestCamera.targetTexture, Is.Null);
            }
            Task<ImageFrame> first = _renderer.RenderAsync(Request(), CancellationToken.None);
            Task<ImageFrame> second = _renderer.RenderAsync(Request(), CancellationToken.None);
            yield return null;
            _renderer.enabled = false;
            Assert.That(first.IsCanceled, Is.True);
            Assert.That(second.IsCanceled, Is.True);
            Assert.That(_rig.RequestCamera.targetTexture, Is.Null);
            Assert.That(_world.GetComponent<CesiumCameraManager>().additionalCameras,
                Has.No.Member(_rig.RequestCamera));
        }

        [UnityTest]
        public IEnumerator SerialRequestsCompleteAndFailureDoesNotStrandTheNextRequest()
        {
            _renderer.Configure(_rig, _reference); // no light, but ResolveScene can still find ours
            Object.Destroy(_light.gameObject);
            yield return null;
            Task<ImageFrame> failed = _renderer.RenderAsync(Request(withSun: true), CancellationToken.None);
            Task<ImageFrame> next = _renderer.RenderAsync(Request(), CancellationToken.None);
            yield return Wait(next);
            Assert.That(failed.IsFaulted, Is.True);
            Assert.That(failed.Exception.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(next.GetAwaiter().GetResult().IsValid, Is.True);
            Assert.That(_renderer.IsRendering, Is.False);
        }

        [UnityTest]
        public IEnumerator StepLoopYieldsUntilRenderTasksFinishThenCanPublishOnTheNextStep()
        {
            var source = _simulation.AddComponent<AnalyticStateSource>();
            var runner = _simulation.AddComponent<SimulationRunner>();
            runner.Configure(source);
            runner.ConfigureImageRenderer(_renderer);
            runner.IsRunning = false;
            var camera = new SmallCamera(_renderer);
            var manager = new SensorManager();
            manager.Register(camera);
            SensorOutputSet output = null;
            bool initialized = false;
            runner.StateProduced += state =>
            {
                if (!initialized)
                {
                    manager.Reset(new SensorResetContext(state.RunId, state.Spacecraft.TimestampUtc, 0));
                    initialized = true;
                }
                output = manager.Sample(new SensorSampleContext(state.RunId, state));
            };
            Assert.That(runner.StepOnce(), Is.True);
            Assert.That(camera.PendingExposureCount, Is.EqualTo(1));
            Assert.That(runner.IsWaitingForRenderer, Is.True);
            Assert.That(runner.StepOnce(), Is.False);
            Assert.That(runner.LastState.Spacecraft.Sequence, Is.EqualTo(0));
            for (int frame = 0; frame < 180 && _renderer.IsRendering; frame++) yield return null;
            Assert.That(runner.IsWaitingForRenderer, Is.False);
            Assert.That(camera.PendingExposureCount, Is.EqualTo(1), "Publishing still needs another sample call");
            Assert.That(runner.StepOnce(), Is.True);
            Assert.That(runner.LastState.Spacecraft.Sequence, Is.EqualTo(1));
            Assert.That(output.TryGetFrame("cam.test", out SensorFrame<ImageFrame> image), Is.True);
            Assert.That(image.Status, Is.EqualTo(SensorFrameStatus.Valid));
            Assert.That(image.StateSequence, Is.EqualTo(0));
            Assert.That(camera.PendingExposureCount, Is.Zero);
        }

        private sealed class SmallCamera : CameraModel
        {
            public SmallCamera(IImageRenderer renderer)
                : base(new SensorDefinition("cam.test", "test-camera", "camera", 1, "simulation", SensorMount.Identity, false),
                    new CameraIntrinsics(16, 12, 10, 10, 7.5, 5.5), 0.001, double.PositiveInfinity, renderer) { }
        }

        [UnityTest]
        public IEnumerator LegacyExporterUsesRendererAndSavesAPngAndCaptureMetadata()
        {
            var source = _simulation.AddComponent<AnalyticStateSource>();
            var runner = _simulation.AddComponent<SimulationRunner>();
            runner.Configure(source);
            runner.IsRunning = true;
            var exporter = _simulation.AddComponent<NavigationEpisodeExporter>();
            typeof(NavigationEpisodeExporter).GetField("captureAllCameras", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(exporter, false);
            typeof(NavigationEpisodeExporter).GetField("episodeName", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(exporter, "unity4-test-" + Guid.NewGuid().ToString("N"));
            Assert.That(exporter.RequestCapture(), Is.True);
            Assert.That(runner.IsRunning, Is.False);
            try
            {
                for (int frame = 0; frame < 180 && exporter.IsCaptureInProgress; frame++) yield return null;
                Assert.That(exporter.IsCaptureInProgress, Is.False);
                Assert.That(exporter.LastCaptureStatus, Is.EqualTo("SAVED"));
                Assert.That(runner.IsRunning, Is.True);
                string[] images = Directory.GetFiles(exporter.EpisodeDirectory, "*.png");
                Assert.That(images.Length, Is.EqualTo(1));
                byte[] png = File.ReadAllBytes(images[0]);
                Assert.That(png[0], Is.EqualTo(137));
                Assert.That(png[1], Is.EqualTo((byte)'P'));
                Assert.That(File.ReadAllLines(Path.Combine(exporter.EpisodeDirectory, "navigation_metadata.jsonl")).Length,
                    Is.EqualTo(1));
            }
            finally
            {
                if (!string.IsNullOrEmpty(exporter.EpisodeDirectory)) Directory.Delete(exporter.EpisodeDirectory, true);
            }
        }

        [UnityTest]
        public IEnumerator RetryingAPartialCaptureDoesNotOverwriteSavedImagesOrDuplicatePaths()
        {
            var source = _simulation.AddComponent<AnalyticStateSource>();
            var runner = _simulation.AddComponent<SimulationRunner>();
            runner.Configure(source);
            runner.IsRunning = false;
            var exporter = _simulation.AddComponent<NavigationEpisodeExporter>();
            typeof(NavigationEpisodeExporter).GetField("episodeName", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(exporter, "unity4-retry-test-" + Guid.NewGuid().ToString("N"));
            Assert.That(exporter.RequestCapture(), Is.True);
            try
            {
                for (int frame = 0; frame < 180 && string.IsNullOrEmpty(exporter.EpisodeDirectory); frame++) yield return null;
                Assert.That(exporter.EpisodeDirectory, Is.Not.Empty);
                exporter.CancelCapture();
                for (int frame = 0; frame < 180 && exporter.IsCaptureInProgress; frame++) yield return null;
                Assert.That(exporter.LastCaptureStatus, Is.EqualTo("CANCELLED"));
                int partial = Directory.GetFiles(exporter.EpisodeDirectory, "*.png").Length;
                Assert.That(partial, Is.InRange(1, 4));
                Assert.That(exporter.RequestCapture(), Is.True);
                for (int frame = 0; frame < 180 && exporter.IsCaptureInProgress; frame++) yield return null;
                Assert.That(exporter.LastCaptureStatus, Is.EqualTo("SAVED"));
                Assert.That(Directory.GetFiles(exporter.EpisodeDirectory, "*.png").Length, Is.EqualTo(partial + 5));
                string[] metadata = File.ReadAllLines(Path.Combine(exporter.EpisodeDirectory, "navigation_metadata.jsonl"));
                Assert.That(metadata.Length, Is.EqualTo(partial + 5));
                var paths = new HashSet<string>();
                foreach (string row in metadata)
                {
                    // Each record must continue to refer to its own capture, rather than a retry overwriting it.
                    string path = row.Substring(row.IndexOf("capture_", StringComparison.Ordinal)).Split('"')[0];
                    Assert.That(paths.Add(path), Is.True, "Duplicate image path in metadata");
                }
            }
            finally
            {
                if (!string.IsNullOrEmpty(exporter.EpisodeDirectory)) Directory.Delete(exporter.EpisodeDirectory, true);
            }
        }

        [UnityTest]
        public IEnumerator OverallLoadingDeadlineFaultsAndRestoresViews()
        {
            SetField("stableFramesRequired", int.MaxValue);
            SetField("maximumLoadSeconds", 1f);
            Task<ImageFrame> task = _renderer.RenderAsync(Request(), CancellationToken.None);
            yield return new WaitForSecondsRealtime(1.2f);
            yield return Wait(task);
            Assert.That(task.IsFaulted, Is.True);
            Assert.That(task.Exception.InnerException, Is.TypeOf<TimeoutException>());
            Assert.That(_rig.RequestCamera.targetTexture, Is.Null);
            var manager = _world.GetComponent<CesiumCameraManager>();
            Assert.That(manager.useMainCamera, Is.True);
            foreach (Camera view in _rig.Cameras) Assert.That(manager.additionalCameras, Has.Member(view));
        }

        [UnityTest]
        public IEnumerator FullResolutionCaptureSavesFourNativeSizeBodyImages()
        {
            var source = _simulation.AddComponent<AnalyticStateSource>();
            var runner = _simulation.AddComponent<SimulationRunner>();
            runner.Configure(source);
            runner.IsRunning = false;
            var exporter = _simulation.AddComponent<NavigationEpisodeExporter>();
            typeof(NavigationEpisodeExporter).GetField("episodeName", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(exporter, "unity4-full-test-" + Guid.NewGuid().ToString("N"));
            Assert.That(exporter.RequestFullResolutionCapture(), Is.True);
            try
            {
                for (int frame = 0; frame < 600 && exporter.IsCaptureInProgress; frame++) yield return null;
                Assert.That(exporter.IsCaptureInProgress, Is.False);
                Assert.That(exporter.LastCaptureStatus, Is.EqualTo("SAVED"), exporter.LastCaptureError);
                string[] images = Directory.GetFiles(exporter.EpisodeDirectory, "*.png");
                Assert.That(images.Length, Is.EqualTo(4));
                foreach (string path in images)
                {
                    byte[] png = File.ReadAllBytes(path);
                    Assert.That(ReadPngInteger(png, 16), Is.EqualTo(4608));
                    Assert.That(ReadPngInteger(png, 20), Is.EqualTo(2592));
                }
                Assert.That(File.ReadAllLines(Path.Combine(exporter.EpisodeDirectory, "navigation_metadata.jsonl")).Length,
                    Is.EqualTo(4));
                Assert.That(_rig.RenderTextures[0].width, Is.EqualTo(768), "Full-size capture must not resize the live preview");
            }
            finally
            {
                if (!string.IsNullOrEmpty(exporter.EpisodeDirectory)) Directory.Delete(exporter.EpisodeDirectory, true);
            }
        }

        private static int ReadPngInteger(byte[] png, int start) =>
            (png[start] << 24) | (png[start + 1] << 16) | (png[start + 2] << 8) | png[start + 3];

        [UnityTest]
        public IEnumerator PreviewMetadataMatchesManualEditsAndTheFrozenRenderedViews()
        {
            yield return CheckManualCaptureMetadata(false);
        }

        [UnityTest]
        public IEnumerator RecordedResetPoseRendersAllViewsWithoutFrustumErrors()
        {
            // Replay the five views from the manual reset acceptance run at 232.3 seconds.
            // This Earth-fixed pose is far from Foundation's Denver georeference origin.
            _world.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            _reference.SetOriginLongitudeLatitudeHeight(-105.25737, 39.736401, 2250);
            var overlay = new GameObject("Reset pose HUD", typeof(RectTransform), typeof(Canvas),
                typeof(UnityEngine.UI.GraphicRaycaster));
            overlay.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            _owned.Add(overlay);
            var panel = new GameObject("HUD panel", typeof(RectTransform), typeof(UnityEngine.UI.Image));
            panel.transform.SetParent(overlay.transform, false);
            var rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            Quaternion originalLight = _light.transform.rotation;
            var position = new Vector3d(6669499.986476071, 973548.0208930476, 1370672.1029271013);
            var rotations = new[]
            {
                new Quaterniond(0.6382918480800766, 0.6866951498117172, -0.14769006275151164, 0.31499354479741065),
                new Quaterniond(-0.14769004201057487, -0.3149935329333351, -0.6382918646554196, 0.6866951443077183),
                new Quaterniond(0.34690786338135904, 0.2628327692454482, -0.5557731505396608, 0.7083008364206335),
                new Quaterniond(0.5557731349894838, 0.7083008674467147, 0.34690783392611174, -0.2628327573931485),
                new Quaterniond(0.5067443556893242, 0.5860841482498785, -0.4782521512346324, -0.4135098656461203)
            };
            for (int i = 0; i < rotations.Length; i++)
            {
                double focalLength = i == 4 ? 2744.540223013736 : 591.333346142287;
                var request = new RenderRequest("reset-view-" + i, i, 2323, 232.3,
                    new DateTimeOffset(2025, 1, 15, 0, 3, 52, TimeSpan.Zero), 0,
                    position, rotations[i], new CameraIntrinsics(768, 432, focalLength, focalLength, 383.5, 215.5));
                Task<ImageFrame> task = _renderer.RenderAsync(request, CancellationToken.None);
                yield return Wait(task);
                ImageFrame result = task.GetAwaiter().GetResult();
                Assert.That(result.IsValid, Is.True);
                Assert.That(result.CameraPositionEcefMeters, Is.EqualTo(position));
                Assert.That(result.CameraToEcef, Is.EqualTo(rotations[i]));
                Assert.That(_reference.longitude, Is.EqualTo(-105.25737).Within(1e-9));
                Assert.That(_reference.latitude, Is.EqualTo(39.736401).Within(1e-9));
                Assert.That(_reference.height, Is.EqualTo(2250).Within(1e-6));
                Assert.That(_reference.originAuthority, Is.EqualTo(CesiumGeoreferenceOriginAuthority.LongitudeLatitudeHeight));
                Assert.That(Quaternion.Angle(_light.transform.rotation, originalLight), Is.LessThan(0.01));
                Assert.That(overlay.GetComponent<Canvas>().enabled, Is.True, "Restore the display HUD after readback.");
                LogAssert.NoUnexpectedReceived();
            }
        }

        [UnityTest]
        public IEnumerator DashboardPauseKeepsPreviewAndFullResolutionAtTheSameState()
        {
            var source = _simulation.AddComponent<AnalyticStateSource>();
            var runner = _simulation.AddComponent<SimulationRunner>();
            runner.Configure(source);
            var exporter = _simulation.AddComponent<NavigationEpisodeExporter>();
            typeof(NavigationEpisodeExporter).GetField("episodeName", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(exporter, "unity4-paused-test-" + Guid.NewGuid().ToString("N"));
            var dashboardObject = new GameObject("Pause acceptance dashboard");
            _owned.Add(dashboardObject);
            var dashboard = dashboardObject.AddComponent<SimulatorDashboard>();
            // Dashboard creates this outside its own hierarchy.
            var pauseText = (UnityEngine.UI.Text)typeof(SimulatorDashboard)
                .GetField("_pauseButtonText", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(dashboard);
            _owned.Add(pauseText.canvas.gameObject);
            _owned.Add(GameObject.Find("Orbit Visualization"));
            runner.IsRunning = true;
            pauseText.GetComponentInParent<UnityEngine.UI.Button>().onClick.Invoke();
            Assert.That(runner.IsRunning, Is.False);
            Assert.That(runner.StepOnce(), Is.True);
            SpacecraftState paused = runner.LastState.Spacecraft;
            try
            {
                Assert.That(exporter.RequestCapture(), Is.True);
                for (int frame = 0; frame < 900 && exporter.IsCaptureInProgress; frame++) yield return null;
                Assert.That(exporter.LastCaptureStatus, Is.EqualTo("SAVED"), exporter.LastCaptureError);
                Assert.That(runner.IsRunning, Is.False);
                Assert.That(exporter.RequestFullResolutionCapture(), Is.True);
                for (int frame = 0; frame < 900 && exporter.IsCaptureInProgress; frame++) yield return null;
                Assert.That(exporter.LastCaptureStatus, Is.EqualTo("SAVED"), exporter.LastCaptureError);
                Assert.That(runner.IsRunning, Is.False);
                Assert.That(runner.LastState.Spacecraft.Sequence, Is.EqualTo(paused.Sequence));
                string[] rows = File.ReadAllLines(Path.Combine(exporter.EpisodeDirectory, "navigation_metadata.jsonl"));
                Assert.That(rows.Length, Is.EqualTo(9));
                foreach (string json in rows)
                {
                    SavedCapture row = JsonUtility.FromJson<SavedCapture>(json);
                    Assert.That(row.sequence, Is.EqualTo(paused.Sequence));
                    Assert.That(row.simulation_time_seconds, Is.EqualTo(paused.SimulationTimeSeconds));
                    Assert.That(row.timestamp_utc, Is.EqualTo(paused.TimestampUtc.ToString("O")));
                }
            }
            finally
            {
                if (!string.IsNullOrEmpty(exporter.EpisodeDirectory)) Directory.Delete(exporter.EpisodeDirectory, true);
            }
        }

        [UnityTest]
        public IEnumerator FarFromOriginPixelsMatchRequestedPoseAndIntrinsics()
        {
            _reference.SetOriginLongitudeLatitudeHeight(-105.25737, 39.736401, 2250);
            var position = new Vector3d(6669499.986476071, 973548.0208930476, 1370672.1029271013);
            var rotation = new Quaterniond(0.6382918480800766, 0.6866951498117172, -0.14769006275151164, 0.31499354479741065);
            var request = new RenderRequest("far-origin-markers", 0, 2323, 232.3,
                new DateTimeOffset(2025, 1, 15, 0, 3, 52, TimeSpan.Zero), 0.025,
                position, rotation, new CameraIntrinsics(64, 48, 70, 60, 27, 19),
                position + new Vector3d(1e10, 2e10, 3e10));
            Vector3d[] markers = { new Vector3d(-0.8, -0.6, 10), new Vector3d(0.8, 0.6, 10) };
            Color[] colours = { Color.red, Color.green };
            for (int i = 0; i < markers.Length; i++)
            {
                GameObject marker = AddMarker(request, markers[i], colours[i]);
                marker.transform.SetParent(_world.transform, true);
                var anchor = marker.AddComponent<CesiumGlobeAnchor>();
                anchor.adjustOrientationForGlobeWhenMoving = false;
                anchor.detectTransformChanges = false;
                Vector3d ecef = position + rotation.Rotate(markers[i]);
                anchor.positionGlobeFixed = new double3(ecef.X, ecef.Y, ecef.Z);
            }
            Task<ImageFrame> task = _renderer.RenderAsync(request, CancellationToken.None);
            yield return Wait(task);
            ImageFrame image = task.GetAwaiter().GetResult();
            AssertPixel(image, 21, 15, 0);
            AssertPixel(image, 33, 23, 1);
            Assert.That(_reference.longitude, Is.EqualTo(-105.25737).Within(1e-9));
            Assert.That(_reference.latitude, Is.EqualTo(39.736401).Within(1e-9));
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator FullResolutionMetadataMatchesManualEditsAndTheFrozenRenderedViews()
        {
            yield return CheckManualCaptureMetadata(true);
        }

        private IEnumerator CheckManualCaptureMetadata(bool fullResolution)
        {
            var source = _simulation.AddComponent<AnalyticStateSource>();
            var runner = _simulation.AddComponent<SimulationRunner>();
            runner.Configure(source);
            runner.IsRunning = false;
            var anchor = _rig.GetComponent<CesiumGlobeAnchor>();
            var poseDriver = _rig.gameObject.AddComponent<CesiumSpacecraftPoseDriver>();
            poseDriver.Configure(runner, anchor);
            // Exercise the same source/pose operations as Dashboard phase, altitude and P/Y/R.
            source.PhaseDegrees = 15;
            source.AltitudeMeters = 550000;
            Assert.That(runner.StepOnce(), Is.True);
            poseDriver.NudgeAttitude(new Vector3(20, 30, 40));
            _rig.Cameras[0].fieldOfView = 47;
            _rig.Cameras[0].transform.localPosition += new Vector3(0.02f, 0.03f, 0.04f);
            _rig.Cameras[0].transform.localRotation *= Quaternion.Euler(3, 5, 7);
            SpacecraftState sourceState = runner.LastState.Spacecraft;
            // Keep the local float transforms close to the exposure while checking ECEF doubles.
            Vector3d origin = sourceState.PositionEcefMeters;
            _reference.SetOriginEarthCenteredEarthFixed(origin.X, origin.Y, origin.Z);
            float4 bodyRotation = anchor.rotationGlobeFixed.value;
            var expectedBody = new Quaterniond(bodyRotation.x, bodyRotation.y, bodyRotation.z, bodyRotation.w).Normalized();
            Assert.That(Math.Abs(expectedBody.X - sourceState.BodyToEcef.X), Is.GreaterThan(0.01));
            double3 expectedPosition = anchor.positionGlobeFixed;
            var expectedCameraPositions = new List<double3>();
            var expectedCameraForwards = new List<Vector3>();
            var expectedCameraDowns = new List<Vector3>();
            var expectedFocalLengths = new List<double>();
            int count = fullResolution ? 4 : 5;
            // Let LateUpdate align the independent nadir camera before snapshotting it.
            yield return null;
            for (int i = 0; i < count; i++)
            {
                Camera view = _rig.Cameras[i];
                Vector3 local = _reference.transform.InverseTransformPoint(view.transform.position);
                expectedCameraPositions.Add(_reference.TransformUnityPositionToEarthCenteredEarthFixed(
                    new double3(local.x, local.y, local.z)));
                expectedCameraForwards.Add(view.transform.forward);
                expectedCameraDowns.Add(-view.transform.up);
                expectedFocalLengths.Add(0.5 * _rig.RenderTextures[i].height /
                    Math.Tan(0.5 * view.fieldOfView * Math.PI / 180));
            }
            var renderedPositions = new List<Vector3>();
            var renderedForwards = new List<Vector3>();
            var renderedDowns = new List<Vector3>();
            Camera.CameraCallback callback = view =>
            {
                if (view != _rig.RequestCamera) return;
                renderedPositions.Add(view.transform.position);
                renderedForwards.Add(view.transform.forward);
                renderedDowns.Add(-view.transform.up);
            };
            var exporter = _simulation.AddComponent<NavigationEpisodeExporter>();
            typeof(NavigationEpisodeExporter).GetField("episodeName", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(exporter, "unity4-pose-test-" + Guid.NewGuid().ToString("N"));
            Camera.onPreRender += callback;
            try
            {
                Assert.That(fullResolution ? exporter.RequestFullResolutionCapture() : exporter.RequestCapture(), Is.True);
                // Edits during tile waiting must not alter any view in this already-started capture.
                source.PhaseDegrees = 45;
                source.AltitudeMeters = 600000;
                poseDriver.NudgeAttitude(new Vector3(-10, 15, -20));
                _rig.Cameras[0].fieldOfView = 65;
                for (int frame = 0; frame < 900 && exporter.IsCaptureInProgress; frame++) yield return null;
                Assert.That(exporter.LastCaptureStatus, Is.EqualTo("SAVED"), exporter.LastCaptureError);
                Assert.That(renderedForwards.Count, Is.EqualTo(count));
                string[] rows = File.ReadAllLines(Path.Combine(exporter.EpisodeDirectory, "navigation_metadata.jsonl"));
                Assert.That(rows.Length, Is.EqualTo(count));
                for (int i = 0; i < count; i++)
                {
                    SavedCapture row = JsonUtility.FromJson<SavedCapture>(rows[i]);
                    Assert.That(row.metadata_version, Is.EqualTo(2));
                    Assert.That(row.sequence, Is.EqualTo(sourceState.Sequence));
                    Assert.That(row.simulation_time_seconds, Is.EqualTo(sourceState.SimulationTimeSeconds));
                    Assert.That(row.timestamp_utc, Is.EqualTo(sourceState.TimestampUtc.ToString("O")));
                    AssertVector(row.position_ecef_m, new Vector3d(expectedPosition.x, expectedPosition.y, expectedPosition.z), 0.001);
                    AssertVector(row.source_position_ecef_m, sourceState.PositionEcefMeters, 0.001);
                    Quaterniond body = ReadQuaternion(row.rotation_body_to_ecef_xyzw);
                    AssertVector(new[] { body.X, body.Y, body.Z }, new Vector3d(expectedBody.X, expectedBody.Y, expectedBody.Z), 1e-6);
                    Assert.That(body.W, Is.EqualTo(expectedBody.W).Within(1e-6));
                    Quaterniond engineBody = ReadQuaternion(row.source_rotation_body_to_ecef_xyzw);
                    Assert.That(engineBody.X, Is.EqualTo(sourceState.BodyToEcef.X).Within(1e-10));
                    Vector3d rateInEcef = sourceState.BodyToEcef.Rotate(sourceState.AngularVelocityBodyRadiansPerSecond);
                    Vector3d savedRate = new Vector3d(row.angular_velocity_body_truth_rad_per_s[0],
                        row.angular_velocity_body_truth_rad_per_s[1], row.angular_velocity_body_truth_rad_per_s[2]);
                    AssertVector(row.source_angular_velocity_body_truth_rad_per_s, sourceState.AngularVelocityBodyRadiansPerSecond, 1e-10);
                    Vector3d savedRateInEcef = body.Rotate(savedRate);
                    Assert.That((savedRateInEcef - rateInEcef).Magnitude, Is.LessThan(1e-8));
                    Quaterniond camera = ReadQuaternion(row.camera_to_ecef_xyzw);
                    Quaterniond relative = ReadQuaternion(row.camera_to_body_xyzw);
                    Vector3d forward = camera.Rotate(new Vector3d(0, 0, 1));
                    Assert.That(((body * relative).Rotate(new Vector3d(0, 0, 1)) - forward).Magnitude, Is.LessThan(1e-6));
                    Vector3 savedForward = ToWorldDirection(forward);
                    Vector3 savedDown = ToWorldDirection(camera.Rotate(new Vector3d(0, 1, 0)));
                    Assert.That(Vector3.Angle(savedForward, expectedCameraForwards[i]), Is.LessThan(0.05));
                    Assert.That(Vector3.Angle(savedDown, expectedCameraDowns[i]), Is.LessThan(0.05));
                    Assert.That(Vector3.Angle(savedForward, renderedForwards[i]), Is.LessThan(0.05));
                    Assert.That(Vector3.Angle(savedDown, renderedDowns[i]), Is.LessThan(0.05));
                    double3 p = expectedCameraPositions[i];
                    AssertVector(row.camera_position_ecef_m, new Vector3d(p.x, p.y, p.z), 0.001);
                    double3 local = _reference.TransformEarthCenteredEarthFixedPositionToUnity(new double3(
                        row.camera_position_ecef_m[0], row.camera_position_ecef_m[1], row.camera_position_ecef_m[2]));
                    Vector3 worldPosition = _reference.transform.TransformPoint(new Vector3((float)local.x, (float)local.y, (float)local.z));
                    Assert.That(Vector3.Distance(worldPosition, renderedPositions[i]), Is.LessThan(0.01));
                    Assert.That(row.exposure_duration_s, Is.Zero);
                    Assert.That(row.has_sun_position, Is.False);
                    Assert.That(row.renderer, Is.Not.Empty);
                    Assert.That(row.image_width_px, Is.EqualTo(fullResolution ? 4608 : 768));
                    Assert.That(row.image_height_px, Is.EqualTo(fullResolution ? 2592 : 432));
                    double expectedFy = fullResolution ? 3548 : expectedFocalLengths[i];
                    Assert.That(row.fy_px, Is.EqualTo(expectedFy).Within(0.001));
                }

                // The next capture must take fresh state after new orbit edits and a pose reset.
                poseDriver.ResetManualAttitude();
                Assert.That(runner.StepOnce(), Is.True);
                SpacecraftState nextState = runner.LastState.Spacecraft;
                Assert.That((nextState.PositionEcefMeters - sourceState.PositionEcefMeters).Magnitude, Is.GreaterThan(1000));
                Assert.That(fullResolution ? exporter.RequestFullResolutionCapture() : exporter.RequestCapture(), Is.True);
                for (int frame = 0; frame < 900 && exporter.IsCaptureInProgress; frame++) yield return null;
                Assert.That(exporter.LastCaptureStatus, Is.EqualTo("SAVED"), exporter.LastCaptureError);
                rows = File.ReadAllLines(Path.Combine(exporter.EpisodeDirectory, "navigation_metadata.jsonl"));
                Assert.That(rows.Length, Is.EqualTo(count * 2));
                SavedCapture next = JsonUtility.FromJson<SavedCapture>(rows[count]);
                Assert.That(next.sequence, Is.EqualTo(nextState.Sequence));
                AssertVector(next.position_ecef_m, nextState.PositionEcefMeters, 0.01);
                Quaterniond nextBody = ReadQuaternion(next.rotation_body_to_ecef_xyzw);
                Assert.That((nextBody.Rotate(new Vector3d(0, 0, 1)) - nextState.BodyToEcef.Rotate(new Vector3d(0, 0, 1))).Magnitude,
                    Is.LessThan(1e-6));
                if (!fullResolution)
                {
                    SavedCapture nadir = JsonUtility.FromJson<SavedCapture>(rows[count * 2 - 1]);
                    AssertVector(nadir.camera_position_ecef_m, nextState.PositionEcefMeters, 1);
                }
            }
            finally
            {
                Camera.onPreRender -= callback;
                if (!string.IsNullOrEmpty(exporter.EpisodeDirectory)) Directory.Delete(exporter.EpisodeDirectory, true);
            }
        }

        [Serializable]
        private sealed class SavedCapture
        {
            public int metadata_version;
            public long sequence;
            public double simulation_time_seconds;
            public string timestamp_utc;
            public int image_width_px;
            public int image_height_px;
            public double fy_px;
            public double[] position_ecef_m;
            public double[] rotation_body_to_ecef_xyzw;
            public double[] angular_velocity_body_truth_rad_per_s;
            public double[] source_position_ecef_m;
            public double[] source_rotation_body_to_ecef_xyzw;
            public double[] source_angular_velocity_body_truth_rad_per_s;
            public double[] camera_position_ecef_m;
            public double[] camera_to_ecef_xyzw;
            public double[] camera_to_body_xyzw;
            public double exposure_duration_s;
            public string renderer;
            public bool has_sun_position;
        }

        private static Quaterniond ReadQuaternion(double[] values) =>
            new Quaterniond(values[0], values[1], values[2], values[3]);

        private Vector3 ToWorldDirection(Vector3d direction)
        {
            double3 local = _reference.TransformEarthCenteredEarthFixedDirectionToUnity(
                new double3(direction.X, direction.Y, direction.Z));
            return _reference.transform.TransformDirection(new Vector3((float)local.x, (float)local.y, (float)local.z));
        }

        private static void AssertVector(double[] actual, Vector3d expected, double tolerance)
        {
            Assert.That(actual, Has.Length.EqualTo(3));
            Assert.That(actual[0], Is.EqualTo(expected.X).Within(tolerance));
            Assert.That(actual[1], Is.EqualTo(expected.Y).Within(tolerance));
            Assert.That(actual[2], Is.EqualTo(expected.Z).Within(tolerance));
        }

        private RenderRequest Request(bool withSun = false)
        {
            Vector3d position = new Vector3d(6378137 + 200, 0, 0);
            return new RenderRequest("cam.test", 3, 42, 1.25,
                new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), 0.025,
                position, new Quaterniond(0, 0, Math.Sin(0.23), Math.Cos(0.23)),
                new CameraIntrinsics(64, 48, 70, 60, 27, 19),
                withSun ? position + new Vector3d(1e10, 2e10, 3e10) : (Vector3d?)null);
        }

        private GameObject AddMarker(RenderRequest request, Vector3d cameraPosition, Color color)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _owned.Add(marker);
            Vector3d ecef = request.CameraPositionEcefMeters + request.CameraToEcef.Rotate(cameraPosition);
            double3 local = _reference.TransformEarthCenteredEarthFixedPositionToUnity(new double3(ecef.X, ecef.Y, ecef.Z));
            marker.transform.position = _reference.transform.TransformPoint(new Vector3((float)local.x, (float)local.y, (float)local.z));
            marker.transform.localScale = Vector3.one * 0.5f;
            Material material = new Material(Shader.Find("Unlit/Color")) { color = color };
            _owned.Add(material);
            marker.GetComponent<Renderer>().sharedMaterial = material;
            return marker;
        }

        private static void AssertPixel(ImageFrame frame, int x, int y, int channel)
        {
            int offset = (y * frame.Intrinsics.WidthPixels + x) * 3;
            Assert.That(frame.Data[offset + channel], Is.GreaterThan(180), $"Expected marker at ({x},{y})");
            Assert.That(frame.Data[offset + (channel + 1) % 3], Is.LessThan(40));
        }

        private static IEnumerator Wait(Task<ImageFrame> task)
        {
            for (int frame = 0; frame < 180 && !task.IsCompleted; frame++) yield return null;
            Assert.That(task.IsCompleted, Is.True, "Render task did not complete");
        }

        private void SetField(string name, object value) => typeof(UnityImageRenderer)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_renderer, value);
    }
}
