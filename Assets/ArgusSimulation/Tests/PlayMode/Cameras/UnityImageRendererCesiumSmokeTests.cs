using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Argus.Simulation.Core;
using Argus.Simulation.Unity;
using CesiumForUnity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Argus.Simulation.Tests
{
    // Opt-in: requires real terrain credentials, network and a GPU. Never part of offline CI.
    public sealed class UnityImageRendererCesiumSmokeTests
    {
        [UnityTest, Timeout(3_600_000)]
        public IEnumerator FoundationSavesPreviewAndFourFullResolutionBodyImages()
        {
            if (Environment.GetEnvironmentVariable("ARGUS_RUN_CESIUM_SMOKE") != "1")
                Assert.Ignore("Set ARGUS_RUN_CESIUM_SMOKE=1 for the real Cesium scene acceptance run.");

            bool previousIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true; // Remote errors are counted; the capture must still succeed.
            var report = new SmokeReport { startedUtc = DateTime.UtcNow.ToString("O") };
            Application.LogCallback log = (message, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception)
                {
                    report.errorLogCount++;
                    if (message.Contains("Screen position out of view frustum") || stack.Contains("UnityImageRenderer"))
                        report.renderErrorLogCount++;
                }
            };
            Application.logMessageReceived += log;
            float startedAt = Time.realtimeSinceStartup;
            try
            {
                yield return SceneManager.LoadSceneAsync("Assets/ArgusSimulation/Scenes/Foundation.unity");
                if (Object.FindAnyObjectByType<SimulatorDashboard>() == null)
                    new GameObject("Smoke test dashboard").AddComponent<SimulatorDashboard>();
                yield return null;
                Cesium3DTileset[] tilesets = Object.FindObjectsByType<Cesium3DTileset>(FindObjectsInactive.Exclude);
                Assert.That(Array.Exists(tilesets, tileset => tileset.isActiveAndEnabled), Is.True,
                    "Real-scene acceptance requires enabled terrain. Check the project .env / Cesium credentials.");
                var runner = Object.FindAnyObjectByType<SimulationRunner>();
                // Real terrain/GPU with a deterministic host stand-in; no Basilisk run is implied.
                if (runner.StateSource is StateStreamClient stream) stream.enabled = false;
                var source = runner.gameObject.AddComponent<TestStateSource>();
                runner.Configure(source);
                SimulationState state = source.Publish(0);
                var pose = Object.FindAnyObjectByType<CesiumSpacecraftPoseDriver>();
                pose.Hold(state);
                pose.transform.localRotation *= Quaternion.Euler(30f, 20f, 0f);
                pose.GetComponent<CesiumGlobeAnchor>().Sync();
                yield return null;
                yield return null;

                if (Environment.GetEnvironmentVariable("ARGUS_CESIUM_LOG_STATS") == "1")
                    foreach (var tileset in Object.FindObjectsByType<Cesium3DTileset>(FindObjectsInactive.Exclude))
                        tileset.logSelectionStats = true;
                var exporter = Object.FindAnyObjectByType<NavigationEpisodeExporter>();
                typeof(NavigationEpisodeExporter).GetField("episodeName", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(exporter, "unity4-cesium-acceptance");
                bool fullOnly = Environment.GetEnvironmentVariable("ARGUS_CESIUM_FULL_ONLY") == "1";
                if (!fullOnly)
                {
                    Assert.That(exporter.RequestCapture(), Is.True);
                    yield return WaitForCapture(exporter, 1800f, report);
                    report.directory = exporter.EpisodeDirectory;
                    report.previewStatus = exporter.LastCaptureStatus;
                    report.error = exporter.LastCaptureError;
                    Assert.That(report.previewStatus, Is.EqualTo("SAVED"), report.error);
                    Assert.That(Directory.GetFiles(report.directory, "*.png").Length, Is.EqualTo(5));
                    AssertImageHasDetail(Directory.GetFiles(report.directory, "*NADIR*.png")[0]);
                }

                Assert.That(exporter.RequestFullResolutionCapture(), Is.True);
                yield return WaitForCapture(exporter, 1700f, report);
                report.directory = exporter.EpisodeDirectory;
                report.fullResolutionStatus = exporter.LastCaptureStatus;
                report.error = exporter.LastCaptureError;
                Assert.That(report.fullResolutionStatus, Is.EqualTo("SAVED"), report.error);
                string[] images = Directory.GetFiles(report.directory, "*.png");
                Assert.That(images.Length, Is.EqualTo(fullOnly ? 4 : 9));
                int fullSize = 0;
                foreach (string path in images)
                {
                    using (var input = File.OpenRead(path))
                    {
                        byte[] header = new byte[24];
                        Assert.That(input.Read(header, 0, header.Length), Is.EqualTo(header.Length));
                        int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                        int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                        if (width == 4608 && height == 2592) fullSize++;
                    }
                }
                Assert.That(fullSize, Is.EqualTo(4));
                string forwardPattern = fullOnly ? "capture_0000*FORWARD*.png" : "capture_0001*FORWARD*.png";
                AssertImageHasDetail(Directory.GetFiles(report.directory, forwardPattern)[0]);
                string[] records = File.ReadAllLines(Path.Combine(report.directory, "navigation_metadata.jsonl"));
                Assert.That(records.Length, Is.EqualTo(images.Length));
                var paths = new HashSet<string>(StringComparer.Ordinal);
                foreach (string json in records)
                {
                    SavedRecord record = JsonUtility.FromJson<SavedRecord>(json);
                    Assert.That(record.metadata_version, Is.EqualTo(3));
                    Assert.That(record.sequence, Is.EqualTo(state.Spacecraft.Sequence));
                    Assert.That(record.simulation_time_seconds, Is.EqualTo(state.Spacecraft.SimulationTimeSeconds));
                    Assert.That(paths.Add(record.image_file), Is.True);
                    Assert.That(File.Exists(Path.Combine(report.directory, record.image_file)), Is.True);
                    Assert.That(record.camera_position_ecef_m.Length, Is.EqualTo(3));
                    Assert.That(record.camera_to_ecef_xyzw.Length, Is.EqualTo(4));
                    Assert.That(record.cx_px, Is.EqualTo(record.rendered_cx_px + 0.5));
                    Assert.That(record.cy_px, Is.EqualTo(record.rendered_cy_px + 0.5));
                }
                Assert.That(report.probeFrames, Is.GreaterThan(0));
                Assert.That(report.maximumExclusiveFrames, Is.LessThanOrEqualTo(1));
                Assert.That(report.renderErrorLogCount, Is.Zero, "Real captures must not log renderer/frustum errors.");
                report.passed = true;
            }
            finally
            {
                report.elapsedSeconds = Time.realtimeSinceStartup - startedAt;
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "argus-unity4-cesium-smoke.json"), JsonUtility.ToJson(report, true));
                Application.logMessageReceived -= log;
                LogAssert.ignoreFailingMessages = previousIgnore;
            }
        }

        private static void AssertImageHasDetail(string path)
        {
            var image = new Texture2D(2, 2, TextureFormat.RGB24, false);
            try
            {
                Assert.That(image.LoadImage(File.ReadAllBytes(path)), Is.True, path);
                Color32[] pixels = image.GetPixels32();
                var colours = new HashSet<Color32>();
                int stride = Math.Max(1, pixels.Length / 4096);
                for (int i = 0; i < pixels.Length && colours.Count <= 16; i += stride)
                    colours.Add(pixels[i]);
                Assert.That(colours.Count, Is.GreaterThan(16),
                    "The Earth-facing image has no terrain detail; saved background pixels do not establish scene acceptance: " + path);
            }
            finally { Object.Destroy(image); }
        }

        private static IEnumerator WaitForCapture(NavigationEpisodeExporter exporter, float seconds, SmokeReport report)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            float nextLog = Time.realtimeSinceStartup + 10f;
            var renderer = Object.FindAnyObjectByType<UnityImageRenderer>();
            while (exporter.IsCaptureInProgress && Time.realtimeSinceStartup < deadline)
            {
                report.probeFrames = renderer.ReadinessProbeCount;
                report.maximumExclusiveFrames = renderer.MaximumReadinessProbeFrames;
                if (Time.realtimeSinceStartup >= nextLog)
                {
                    Debug.Log("Camera smoke progress: " + exporter.LastCaptureStatus);
                    nextLog = Time.realtimeSinceStartup + 10f;
                }
                yield return null;
            }
            if (exporter.IsCaptureInProgress)
            {
                exporter.CancelCapture();
                Assert.Fail("Capture exceeded its overall smoke-test deadline.");
            }
        }

#pragma warning disable CS0649 // JsonUtility reads these exported fields.
        [Serializable]
        private sealed class SavedRecord
        {
            public int metadata_version;
            public long sequence;
            public double simulation_time_seconds;
            public string image_file;
            public double[] camera_position_ecef_m, camera_to_ecef_xyzw;
            public double cx_px, cy_px, rendered_cx_px, rendered_cy_px;
        }
#pragma warning restore CS0649

        [Serializable]
        private sealed class SmokeReport
        {
            public string startedUtc;
            public string directory;
            public string previewStatus;
            public string fullResolutionStatus;
            public string error;
            public int errorLogCount;
            public int renderErrorLogCount;
            public int probeFrames;
            public int maximumExclusiveFrames;
            public float elapsedSeconds;
            public bool passed;
        }
    }
}
