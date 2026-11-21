using Argus.Simulation.Unity;
using UnityEditor;
using UnityEngine;

namespace Argus.Simulation.Editor
{
    // Editor capture commands use the production renderer and its terrain-readiness checks.
    public static class CameraCaptureTools
    {
        private const string Root = "Argus Simulation/Cameras/";

        [MenuItem(Root + "Capture Preview")]
        public static void CapturePreview() => Capture(false);

        [MenuItem(Root + "Capture Full Resolution (4 Body Cameras)")]
        public static void CaptureFullResolution() => Capture(true);

        [MenuItem(Root + "Cancel Capture")]
        public static void CancelCapture()
        {
            Object.FindAnyObjectByType<NavigationEpisodeExporter>()?.CancelCapture();
        }

        [MenuItem(Root + "Log Capture Status")]
        public static void LogCaptureStatus()
        {
            var exporter = Object.FindAnyObjectByType<NavigationEpisodeExporter>();
            var renderer = Object.FindAnyObjectByType<UnityImageRenderer>();
            var pose = Object.FindAnyObjectByType<CesiumSpacecraftPoseDriver>();
            Debug.Log($"Camera acceptance: play={EditorApplication.isPlaying}; " +
                $"capturing={exporter?.IsCaptureInProgress}; status={exporter?.LastCaptureStatus}; " +
                $"renderer={renderer?.CaptureStatus}; held={pose?.IsHeld}; " +
                $"readiness_probes={renderer?.ReadinessProbeCount}; max_probe_frames={renderer?.MaximumReadinessProbeFrames}; " +
                $"error={exporter?.LastCaptureError}; directory={exporter?.EpisodeDirectory}");
        }

        private static void Capture(bool fullResolution)
        {
            var exporter = Object.FindAnyObjectByType<NavigationEpisodeExporter>();
            if (!EditorApplication.isPlaying || exporter == null)
            {
                Debug.LogWarning("Open Foundation and enter Play Mode before capturing.");
                return;
            }
            bool started = fullResolution ? exporter.RequestFullResolutionCapture() : exporter.RequestCapture();
            Debug.Log(started ? "Camera acceptance capture started." : "Camera acceptance capture could not start: " + exporter.LastCaptureStatus);
        }
    }
}
