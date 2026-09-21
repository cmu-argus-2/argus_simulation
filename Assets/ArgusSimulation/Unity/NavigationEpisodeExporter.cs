// Exports a navigation episode from the Unity/Cesium simulator.
//
// OUTPUT PER EXPORTED SAMPLE
// - PNG image from one or more CubeSat cameras
// - One JSONL metadata record per PNG:
//
//   timestamp + ECEF truth position/velocity + body attitude +
//   body angular-rate truth + camera intrinsics + camera-to-body rotation.
//
// This is the bridge from the Unity simulator to the Python navigation code:
// Unity images/metadata -> EarthLoc/LightGlue -> FSW-Payload OD.
//
// IMPORTANT:
// The current output angular rate is simulator truth, not yet a noisy gyro.
// Add a gyro bias/noise model after this exporter is working.

using System;
using System.Globalization;
using System.IO;
using Argus.Simulation.Core;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    [DisallowMultipleComponent]
    public sealed class NavigationEpisodeExporter : MonoBehaviour
    {
        [Header("Required scene components")]
        [SerializeField] private SimulationRunner runner;
        [SerializeField] private CubeSatCameraRig cameraRig;

        [Header("Export settings")]
        [SerializeField, Min(0.1f)] private double exportIntervalSeconds = 1.0;
        [SerializeField] private bool captureAllCameras = true;
        [SerializeField, Min(0)] private int selectedCameraIndex = 0;
        [SerializeField] private string episodeName = "unity_navigation_episode";

        private string _episodeDirectory;
        private string _metadataPath;
        private double _lastExportSimulationTime = double.NegativeInfinity;
        private SpacecraftState _pendingState;
        private bool _hasPendingState;

        // JsonUtility serializes public fields, not C# properties.
        [Serializable]
        private sealed class NavigationRecord
        {
            public string timestamp_utc;
            public long sequence;
            public double simulation_time_seconds;

            public string image_file;
            public string camera_name;
            public int image_width_px;
            public int image_height_px;

            // Standard pinhole calibration in pixel units.
            public double fx_px;
            public double fy_px;
            public double cx_px;
            public double cy_px;

            // Unity-coordinate camera-to-body quaternion [x, y, z, w].
            // A later bridge will convert/document the exact Argus convention.
            public double[] rotation_camera_to_body_unity_xyzw;

            // Simulator truth in ECEF.
            public double[] position_ecef_m;
            public double[] velocity_ecef_m_per_s;

            // Simulator truth attitude: body -> ECEF, [x, y, z, w].
            public double[] rotation_body_to_ecef_xyzw;

            // Truth body angular velocity. This is NOT a noisy gyro yet.
            public double[] angular_velocity_body_truth_rad_per_s;
        }

        private void Awake()
        {
            if (runner == null)
            {
                runner = GetComponent<SimulationRunner>();
            }

            if (cameraRig == null)
            {
                cameraRig = FindAnyObjectByType<CubeSatCameraRig>();
            }
        }

        private void OnEnable()
        {
            if (runner != null)
            {
                runner.StateProduced += QueueStateForExport;
            }
        }

        private void OnDisable()
        {
            if (runner != null)
            {
                runner.StateProduced -= QueueStateForExport;
            }
        }

        private void Start()
        {
            if (runner == null)
            {
                Debug.LogError(
                    "NavigationEpisodeExporter needs a SimulationRunner.",
                    this);
                enabled = false;
                return;
            }

            string safeEpisodeName = MakeSafeFileName(episodeName);
            string runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");

            _episodeDirectory = Path.Combine(
                Application.persistentDataPath,
                "NavigationEpisodes",
                safeEpisodeName + "_" + runId);

            Directory.CreateDirectory(_episodeDirectory);

            _metadataPath = Path.Combine(
                _episodeDirectory,
                "navigation_metadata.jsonl");

            File.WriteAllText(_metadataPath, string.Empty);

            Debug.Log(
                "Navigation episode export directory:\n" + _episodeDirectory +
                "\nMetadata file:\n" + _metadataPath,
                this);
        }

        private void LateUpdate()
        {
            // SimulatorDashboard creates the camera rig at runtime.
            // Wait until that rig exists instead of failing during Start().
            if (cameraRig == null)
            {
                cameraRig = FindFirstObjectByType<CubeSatCameraRig>();

                if (cameraRig == null)
                {
                    return;
                }

                Debug.Log("NavigationEpisodeExporter found CubeSatCameraRig.", this);
            }

            if (!_hasPendingState)
            {
                return;
            }

            ExportState(_pendingState);
            _hasPendingState = false;
        }

        private void QueueStateForExport(SpacecraftState state)
        {
            if (state.SimulationTimeSeconds - _lastExportSimulationTime <
                exportIntervalSeconds)
            {
                return;
            }

            _lastExportSimulationTime = state.SimulationTimeSeconds;
            _pendingState = state;
            _hasPendingState = true;
        }

        private void ExportState(SpacecraftState state)
        {
            if (captureAllCameras)
            {
                for (int index = 0; index < cameraRig.Cameras.Count; index++)
                {
                    ExportCamera(state, index);
                }

                return;
            }

            if (selectedCameraIndex < 0 ||
                selectedCameraIndex >= cameraRig.Cameras.Count)
            {
                Debug.LogError(
                    "selectedCameraIndex is outside CubeSatCameraRig.Cameras.",
                    this);
                return;
            }

            ExportCamera(state, selectedCameraIndex);
        }

        private void ExportCamera(SpacecraftState state, int cameraIndex)
        {
            Camera camera = cameraRig.Cameras[cameraIndex];
            RenderTexture renderTexture = cameraRig.RenderTextures[cameraIndex];

            if (camera == null || renderTexture == null)
            {
                Debug.LogWarning("Skipping unavailable camera feed.", this);
                return;
            }

            // Render the camera now, then copy its RenderTexture to a PNG.
            camera.Render();

            string safeCameraName = MakeSafeFileName(cameraRig.Names[cameraIndex]);
            string imageFileName =
                $"frame_{state.Sequence:D6}_{safeCameraName}.png";
            string imagePath = Path.Combine(_episodeDirectory, imageFileName);

            SaveRenderTextureAsPng(renderTexture, imagePath);

            double fy = 0.5 * renderTexture.height /
                Math.Tan(0.5 * camera.fieldOfView * Math.PI / 180.0);
            double fx = fy * renderTexture.width / renderTexture.height;

            // Rotation from this camera frame into the CubeSat body frame.
            Quaternion cameraToBody = Quaternion.Inverse(cameraRig.transform.rotation) *
                camera.transform.rotation;

            NavigationRecord record = new NavigationRecord
            {
                timestamp_utc = state.TimestampUtc.ToString(
                    "O",
                    CultureInfo.InvariantCulture),
                sequence = state.Sequence,
                simulation_time_seconds = state.SimulationTimeSeconds,

                image_file = imageFileName,
                camera_name = cameraRig.Names[cameraIndex],
                image_width_px = renderTexture.width,
                image_height_px = renderTexture.height,

                fx_px = fx,
                fy_px = fy,
                cx_px = 0.5 * renderTexture.width,
                cy_px = 0.5 * renderTexture.height,

                rotation_camera_to_body_unity_xyzw = new[]
                {
                    (double)cameraToBody.x,
                    (double)cameraToBody.y,
                    (double)cameraToBody.z,
                    (double)cameraToBody.w
                },

                position_ecef_m = ToArray(state.PositionEcefMeters),
                velocity_ecef_m_per_s =
                    ToArray(state.VelocityEcefMetersPerSecond),

                rotation_body_to_ecef_xyzw = new[]
                {
                    state.BodyToEcef.X,
                    state.BodyToEcef.Y,
                    state.BodyToEcef.Z,
                    state.BodyToEcef.W
                },

                angular_velocity_body_truth_rad_per_s =
                    ToArray(state.AngularVelocityBodyRadiansPerSecond)
            };

            File.AppendAllText(
                _metadataPath,
                JsonUtility.ToJson(record) + Environment.NewLine);
        }

        private static void SaveRenderTextureAsPng(
            RenderTexture renderTexture,
            string outputPath)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = renderTexture;

            Texture2D image = new Texture2D(
                renderTexture.width,
                renderTexture.height,
                TextureFormat.RGB24,
                false);

            image.ReadPixels(
                new Rect(0, 0, renderTexture.width, renderTexture.height),
                0,
                0);

            image.Apply();

            File.WriteAllBytes(outputPath, image.EncodeToPNG());

            RenderTexture.active = previous;
            Destroy(image);
        }

        private static double[] ToArray(Vector3d vector)
        {
            return new[] { vector.X, vector.Y, vector.Z };
        }

        private static string MakeSafeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "episode";
            }

            char[] characters = value.ToCharArray();
            for (int index = 0; index < characters.Length; index++)
            {
                if (!char.IsLetterOrDigit(characters[index]) &&
                    characters[index] != '_' &&
                    characters[index] != '-')
                {
                    characters[index] = '_';
                }
            }

            return new string(characters);
        }
    }
}