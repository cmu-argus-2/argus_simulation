using System.Collections.Generic;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    [DisallowMultipleComponent]
    public sealed class CubeSatCameraRig : MonoBehaviour
    {
        // Four side cameras plus a nadir ground-truth camera are shown in the dashboard.
        private static readonly string[] FeedNames =
        {
            "FORWARD  +X",
            "AFT  -X",
            "STARBOARD  +Y",
            "PORT  -Y",
            "NADIR GT  NORTH-UP"
        };

        [Header("Physical side cameras")]
        [SerializeField, Range(1f, 120f)]
        private float sideCameraVerticalFieldOfViewDegrees = 75f;

        [Header("Ground-truth camera")]
        [SerializeField, Range(1f, 120f)]
        private float groundTruthVerticalFieldOfViewDegrees = 9f;

        [Header("Shared render settings")]
        [SerializeField, Min(1)] private int imageWidthPixels = 768;
        [SerializeField, Min(1)] private int imageHeightPixels = 432;
        [SerializeField, Min(0.1f)] private float nearClipMeters = 1f;
        [SerializeField, Min(1000f)] private float farClipMeters = 10_000_000f;

        private readonly List<Camera> _cameras = new List<Camera>(5);
        private readonly List<RenderTexture> _renderTextures =
            new List<RenderTexture>(5);

        private CesiumCameraManager _cameraManager;
        private CesiumGlobeAnchor _spacecraftAnchor;
        private CesiumGlobeAnchor _groundTruthAnchor;
        private Camera _groundTruthCamera;

        public IReadOnlyList<Camera> Cameras => _cameras;
        public IReadOnlyList<RenderTexture> RenderTextures => _renderTextures;
        public IReadOnlyList<string> Names => FeedNames;
        public Camera GroundTruthCamera => _groundTruthCamera;

        private void Awake()
        {
            BuildIfNeeded();
        }

        public void BuildIfNeeded()
        {
            if (_cameras.Count > 0)
            {
                return;
            }

            Transform sensorRig = transform.Find("Sensor Rig");
            if (sensorRig == null)
            {
                sensorRig = new GameObject("Sensor Rig").transform;
                sensorRig.SetParent(transform, false);
            }

            foreach (Camera legacyCamera in
                     sensorRig.GetComponentsInChildren<Camera>(true))
            {
                legacyCamera.enabled = false;
                legacyCamera.targetTexture = null;
            }

            // Existing operational side cameras.
            RegisterFaceCamera(sensorRig, "Forward +X Camera", Vector3.right);
            RegisterFaceCamera(sensorRig, "Aft -X Camera", Vector3.left);
            RegisterFaceCamera(sensorRig, "Starboard +Y Camera", Vector3.up);
            RegisterFaceCamera(sensorRig, "Port -Y Camera", Vector3.down);

            // This virtual camera is intentionally not parented to the body. It
            // follows spacecraft position but ignores body attitude, providing a
            // stable north-up nadir image for ground-truth validation.
            RegisterNadirGroundTruthCamera();

            _cameraManager = CesiumCameraManager.GetOrCreate(gameObject);

            foreach (Camera camera in _cameras)
            {
                if (!_cameraManager.additionalCameras.Contains(camera))
                {
                    _cameraManager.additionalCameras.Add(camera);
                }
            }
        }

        private void LateUpdate()
        {
            UpdateGroundTruthPose();
        }

        private void RegisterFaceCamera(
            Transform sensorRig,
            string name,
            Vector3 outwardNormal)
        {
            const float cameraMountOffsetMeters = 0.06f;

            Quaternion localRotation =
                Quaternion.LookRotation(outwardNormal, Vector3.back);

            Camera camera = CreateCamera(
                name,
                sensorRig,
                outwardNormal * cameraMountOffsetMeters,
                localRotation);

            ConfigureSideCamera(camera);
            RegisterCamera(camera);
        }

        private void RegisterNadirGroundTruthCamera()
        {
            _spacecraftAnchor = GetComponent<CesiumGlobeAnchor>();
            Transform geospatialParent = transform.parent;
            if (_spacecraftAnchor == null || geospatialParent == null)
            {
                Debug.LogError(
                    "Nadir GT requires the CubeSat and camera to be under a CesiumGeoreference.",
                    this);
                return;
            }

            GameObject cameraObject = new GameObject("Nadir Ground Truth Camera");
            cameraObject.transform.SetParent(geospatialParent, false);
            _groundTruthCamera = cameraObject.AddComponent<Camera>();
            _groundTruthAnchor = cameraObject.AddComponent<CesiumGlobeAnchor>();
            _groundTruthAnchor.adjustOrientationForGlobeWhenMoving = false;
            _groundTruthAnchor.detectTransformChanges = false;

            ConfigureNadirCamera(_groundTruthCamera);
            RegisterCamera(_groundTruthCamera);
            UpdateGroundTruthPose();
        }

        private void ConfigureSideCamera(Camera camera)
        {
            ConfigureCamera(
                camera,
                VisibleWorldLayers(),
                nearClipMeters,
                farClipMeters,
                sideCameraVerticalFieldOfViewDegrees);
        }

        private void ConfigureNadirCamera(Camera camera)
        {
            ConfigureCamera(
                camera,
                VisibleWorldLayers(),
                nearClipMeters,
                farClipMeters,
                groundTruthVerticalFieldOfViewDegrees);
        }

        private void UpdateGroundTruthPose()
        {
            if (_spacecraftAnchor == null || _groundTruthAnchor == null)
            {
                return;
            }

            _groundTruthAnchor.positionGlobeFixed =
                _spacecraftAnchor.positionGlobeFixed;

            // Cesium East-Up-North: look along local -Up with image-up set to
            // North. This remains independent of CubeSat pitch, yaw, and roll.
            Quaternion northUpNadir = Quaternion.LookRotation(
                Vector3.down,
                Vector3.forward);
            _groundTruthAnchor.rotationEastUpNorth = new quaternion(
                northUpNadir.x,
                northUpNadir.y,
                northUpNadir.z,
                northUpNadir.w);
            _groundTruthAnchor.Sync();
        }

        private static int VisibleWorldLayers()
        {
            int hiddenLayers =
                (1 << CubeSatVisualModel.OrbitMarkerLayer) |
                (1 << CubeSatVisualModel.SpacecraftModelLayer);

            return ~hiddenLayers;
        }

        private void RegisterCamera(Camera camera)
        {
            camera.enabled = true;

            RenderTexture renderTexture = new RenderTexture(
                imageWidthPixels,
                imageHeightPixels,
                24,
                RenderTextureFormat.ARGB32)
            {
                name = camera.name + " Feed",
                antiAliasing = 2,
                useMipMap = false
            };

            renderTexture.Create();
            camera.targetTexture = renderTexture;
            _cameras.Add(camera);
            _renderTextures.Add(renderTexture);
        }

        private static Camera CreateCamera(
            string name,
            Transform parent,
            Vector3 localPosition,
            Quaternion localRotation)
        {
            GameObject cameraObject = new GameObject(name);
            cameraObject.transform.SetParent(parent, false);
            cameraObject.transform.localPosition = localPosition;
            cameraObject.transform.localRotation = localRotation;

            return cameraObject.AddComponent<Camera>();
        }

        private static void ConfigureCamera(
            Camera camera,
            int cullingMask,
            float nearClip,
            float farClip,
            float fieldOfView)
        {
            camera.cullingMask = cullingMask;
            camera.nearClipPlane = nearClip;
            camera.farClipPlane = farClip;
            camera.fieldOfView = fieldOfView;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.005f, 0.008f, 0.015f, 1f);
            camera.allowHDR = true;
            camera.useOcclusionCulling = false;
        }

        private void OnDestroy()
        {
            foreach (Camera camera in _cameras)
            {
                if (_cameraManager != null)
                {
                    _cameraManager.additionalCameras.Remove(camera);
                }
            }

            foreach (RenderTexture renderTexture in _renderTextures)
            {
                if (renderTexture != null)
                {
                    renderTexture.Release();
                    Destroy(renderTexture);
                }
            }

            if (_groundTruthCamera != null)
            {
                Destroy(_groundTruthCamera.gameObject);
            }
        }
    }
}
