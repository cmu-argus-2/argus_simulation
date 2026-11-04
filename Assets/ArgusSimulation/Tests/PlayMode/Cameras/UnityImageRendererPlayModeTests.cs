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

        private RenderRequest Request(bool withSun = false)
        {
            Vector3d position = new Vector3d(6378137 + 200, 0, 0);
            return new RenderRequest("cam.test", 3, 42, 1.25,
                new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), 0.025,
                position, new Quaterniond(0, 0, Math.Sin(0.23), Math.Cos(0.23)),
                new CameraIntrinsics(64, 48, 70, 60, 27, 19),
                withSun ? position + new Vector3d(1e10, 2e10, 3e10) : (Vector3d?)null);
        }

        private void AddMarker(RenderRequest request, Vector3d cameraPosition, Color color)
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
