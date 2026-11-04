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
using System.Collections;
using System.Globalization;
using System.IO;
using System.Threading;
using Unity.Mathematics;
using Argus.Simulation.Core;
using CesiumForUnity;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    // TODO(D6): retire once Core RunRecorder covers what the Python navigation code reads.
    // This component delegates tile-wait and readback to UnityImageRenderer; remove this component from
    // the scene, FoundationSceneBuilder and the tests in one change.
    [DisallowMultipleComponent]
    public sealed class NavigationEpisodeExporter : MonoBehaviour
    {
        [Header("Required scene components")]
        [SerializeField] private SimulationRunner runner;
        [SerializeField] private CubeSatCameraRig cameraRig;

        [Header("Export settings")]
        [SerializeField] private bool captureAllCameras = true;
        [SerializeField, Min(0)] private int selectedCameraIndex = 0;
        [SerializeField] private string episodeName = "unity_navigation_episode";

        private UnityImageRenderer _renderer;
        private CancellationTokenSource _captureCancellation;
        private bool _resumeSimulation;

        private string _episodeDirectory;
        private string _metadataPath;
        private int _captureNumber;

        public event Action<string> CaptureStatusChanged;

        public bool IsCaptureInProgress { get; private set; }
        public string LastCaptureStatus { get; private set; } = "CAPTURE";
        public string EpisodeDirectory => _episodeDirectory ?? string.Empty;

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
        }

        public bool RequestCapture()
        {
            if (IsCaptureInProgress)
            {
                return false;
            }

            if (runner == null)
            {
                SetCaptureStatus("NO RUNNER");
                return false;
            }

            if (!runner.HasState && !runner.StepOnce())
            {
                SetCaptureStatus("NO STATE");
                return false;
            }

            if (cameraRig == null)
            {
                cameraRig = FindAnyObjectByType<CubeSatCameraRig>();
            }

            if (cameraRig == null || cameraRig.Cameras.Count == 0)
            {
                SetCaptureStatus("NO CAMERAS");
                return false;
            }

            StartCoroutine(CaptureWhenCesiumIsReady(runner.LastState));
            return true;
        }

        private IEnumerator CaptureWhenCesiumIsReady(SimulationState state)
        {
            IsCaptureInProgress = true;
            _resumeSimulation = runner.IsRunning;
            runner.IsRunning = false;
            _captureCancellation = new CancellationTokenSource();
            if (_renderer == null)
            {
                _renderer = GetComponent<UnityImageRenderer>();
                if (_renderer == null) _renderer = gameObject.AddComponent<UnityImageRenderer>();
            }
            runner.ConfigureImageRenderer(_renderer);
            string status = "EXPORT ERROR";
            try
            {
                int first = captureAllCameras ? 0 : selectedCameraIndex;
                int last = captureAllCameras ? cameraRig.Cameras.Count : selectedCameraIndex + 1;
                if (first < 0 || last > cameraRig.Cameras.Count)
                {
                    status = "NO CAMERA";
                    yield break;
                }
                var requests = new RenderRequest[last - first];
                var cameraToBody = new Quaternion[last - first];
                try
                {
                    // Snapshot every view before yielding: manual pose changes or a follower's
                    // later states must not change the exposure while terrain is loading.
                    for (int index = first; index < last; index++)
                    {
                        requests[index - first] = CreateRequest(state, index);
                        cameraToBody[index - first] = Quaternion.Inverse(cameraRig.transform.rotation) *
                            cameraRig.Cameras[index].transform.rotation;
                    }
                }
                catch (Exception exception) { Debug.LogException(exception, this); yield break; }
                for (int index = first; index < last; index++)
                {
                    SetCaptureStatus("LOADING / CAPTURING");
                    System.Threading.Tasks.Task<ImageFrame> task = null;
                    try
                    {
                        task = _renderer.RenderAsync(requests[index - first], _captureCancellation.Token);
                    }
                    catch (Exception exception) { Debug.LogException(exception, this); }
                    if (task == null) yield break;
                    while (!task.IsCompleted) yield return null;
                    try
                    {
                        ImageFrame frame = task.GetAwaiter().GetResult();
                        EnsureOutputDirectory();
                        ExportCamera(state.Spacecraft, index, _captureNumber, frame, cameraToBody[index - first]);
                    }
                    catch (TimeoutException) { status = "LOAD TIMEOUT"; yield break; }
                    catch (OperationCanceledException) { status = "CANCELLED"; yield break; }
                    catch (Exception exception) { Debug.LogException(exception, this); yield break; }
                }
                _captureNumber++;
                status = "SAVED";
            }
            finally
            {
                _captureCancellation.Dispose();
                _captureCancellation = null;
                CompleteCapture(_resumeSimulation, status);
            }
        }

        private RenderRequest CreateRequest(SimulationState state, int index)
        {
            Camera camera = cameraRig.Cameras[index];
            RenderTexture target = cameraRig.RenderTextures[index];
            CesiumGeoreference reference = cameraRig.GetComponentInParent<CesiumGeoreference>();
            Vector3 localPosition = reference.transform.InverseTransformPoint(camera.transform.position);
            double3 position = reference.TransformUnityPositionToEarthCenteredEarthFixed(
                new double3(localPosition.x, localPosition.y, localPosition.z));
            Vector3d right = ToEcefDirection(reference, camera.transform.right);
            Vector3d down = ToEcefDirection(reference, -camera.transform.up);
            Vector3d forward = ToEcefDirection(reference, camera.transform.forward);
            double fy = 0.5 * target.height / Math.Tan(0.5 * camera.fieldOfView * Math.PI / 180.0);
            return new RenderRequest(cameraRig.Names[index], _captureNumber, state.Spacecraft.Sequence,
                state.Spacecraft.SimulationTimeSeconds, state.Spacecraft.TimestampUtc, 0,
                new Vector3d(position.x, position.y, position.z), Quaterniond.FromBasis(right, down, forward),
                new CameraIntrinsics(target.width, target.height, fy, fy, (target.width - 1) / 2.0, (target.height - 1) / 2.0),
                state.Environment?.SunPositionItrf93Meters);
        }

        private static Vector3d ToEcefDirection(CesiumGeoreference reference, Vector3 worldDirection)
        {
            Vector3 local = reference.transform.InverseTransformDirection(worldDirection);
            double3 direction = reference.TransformUnityDirectionToEarthCenteredEarthFixed(new double3(local.x, local.y, local.z));
            return new Vector3d(direction.x, direction.y, direction.z).Normalized();
        }

        private void OnDisable()
        {
            if (!IsCaptureInProgress) return;
            _captureCancellation?.Cancel();
            StopAllCoroutines();
            _captureCancellation?.Dispose();
            _captureCancellation = null;
            CompleteCapture(_resumeSimulation, "CANCELLED");
        }

        private void CompleteCapture(bool resumeSimulation, string status)
        {
            if (runner != null)
            {
                runner.IsRunning = resumeSimulation;
            }

            IsCaptureInProgress = false;
            SetCaptureStatus(status);
        }

        private void SetCaptureStatus(string status)
        {
            LastCaptureStatus = status;
            CaptureStatusChanged?.Invoke(status);
        }

        private void EnsureOutputDirectory()
        {
            if (!string.IsNullOrEmpty(_episodeDirectory))
            {
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
        }

        private void ExportCamera(
            SpacecraftState state,
            int cameraIndex,
            int captureNumber,
            ImageFrame frame,
            Quaternion cameraToBody)
        {
            string safeCameraName = MakeSafeFileName(cameraRig.Names[cameraIndex]);
            string imageFileName =
                $"capture_{captureNumber:D4}_frame_{state.Sequence:D6}_{safeCameraName}.png";
            string imagePath = Path.Combine(_episodeDirectory, imageFileName);

            File.WriteAllBytes(imagePath, UnityImageRenderer.EncodePng(frame));
            CameraIntrinsics intrinsics = frame.Intrinsics;

            NavigationRecord record = new NavigationRecord
            {
                timestamp_utc = state.TimestampUtc.ToString(
                    "O",
                    CultureInfo.InvariantCulture),
                sequence = state.Sequence,
                simulation_time_seconds = state.SimulationTimeSeconds,

                image_file = imageFileName,
                camera_name = cameraRig.Names[cameraIndex],
                image_width_px = frame.Intrinsics.WidthPixels,
                image_height_px = frame.Intrinsics.HeightPixels,

                fx_px = intrinsics.FocalLengthXPixels,
                fy_px = intrinsics.FocalLengthYPixels,
                cx_px = intrinsics.PrincipalPointXPixels,
                cy_px = intrinsics.PrincipalPointYPixels,

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
