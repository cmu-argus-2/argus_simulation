using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
        [UnityTest, Timeout(1_500_000)]
        public IEnumerator FoundationSavesPreviewAndFourFullResolutionBodyImages()
        {
            if (Environment.GetEnvironmentVariable("ARGUS_RUN_CESIUM_SMOKE") != "1")
                Assert.Ignore("Set ARGUS_RUN_CESIUM_SMOKE=1 for the real Cesium scene acceptance run.");

            bool previousIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true; // Remote errors are counted; the capture must still succeed.
            var report = new SmokeReport { startedUtc = DateTime.UtcNow.ToString("O") };
            Application.LogCallback log = (message, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception) report.errorLogCount++;
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
                // Uncached native-resolution imagery can exceed the production default budget.
                // The offline suite separately verifies that the configured deadline is enforced.
                var renderer = Object.FindAnyObjectByType<UnityImageRenderer>();
                typeof(UnityImageRenderer).GetField("maximumLoadSeconds", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(renderer, 600f);
                var runner = Object.FindAnyObjectByType<SimulationRunner>();
                var priority = Object.FindAnyObjectByType<NadirTilePriority>();
                float warmUpDeadline = Time.realtimeSinceStartup + 40f;
                while (priority != null && priority.IsWarmingUp && Time.realtimeSinceStartup < warmUpDeadline)
                    yield return null;
                runner.IsRunning = false;
                if (!runner.HasState) Assert.That(runner.StepOnce(), Is.True);
                Object.FindAnyObjectByType<CesiumSpacecraftPoseDriver>().NudgeAttitude(new Vector3(30f, 20f, 0f));
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
                    yield return WaitForCapture(exporter, 1250f);
                    report.directory = exporter.EpisodeDirectory;
                    report.previewStatus = exporter.LastCaptureStatus;
                    report.error = exporter.LastCaptureError;
                    Assert.That(report.previewStatus, Is.EqualTo("SAVED"), report.error);
                    Assert.That(Directory.GetFiles(report.directory, "*.png").Length, Is.EqualTo(5));
                    AssertImageHasDetail(Directory.GetFiles(report.directory, "*NADIR*.png")[0]);
                }

                Assert.That(exporter.RequestFullResolutionCapture(), Is.True);
                yield return WaitForCapture(exporter, 1000f);
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

        private static IEnumerator WaitForCapture(NavigationEpisodeExporter exporter, float seconds)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            float nextLog = Time.realtimeSinceStartup + 10f;
            while (exporter.IsCaptureInProgress && Time.realtimeSinceStartup < deadline)
            {
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

        [Serializable]
        private sealed class SmokeReport
        {
            public string startedUtc;
            public string directory;
            public string previewStatus;
            public string fullResolutionStatus;
            public string error;
            public int errorLogCount;
            public float elapsedSeconds;
            public bool passed;
        }
    }
}
