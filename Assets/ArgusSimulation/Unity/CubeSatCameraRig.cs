using System.Collections.Generic;
using CesiumForUnity;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    [DisallowMultipleComponent]
    public sealed class CubeSatCameraRig : MonoBehaviour
    {
        // Four side cameras remain for the dashboard.
        // The fifth, NADIR EARTH -Z, is the navigation camera for EarthLoc.
        private static readonly string[] FeedNames =
        {
            "FORWARD  +X",
            "AFT  -X",
            "STARBOARD  +Y",
            "PORT  -Y",
            "NADIR EARTH  -Z"
        };

        private readonly List<Camera> _cameras = new List<Camera>(5);
        private readonly List<RenderTexture> _renderTextures =
            new List<RenderTexture>(5);

        private CesiumCameraManager _cameraManager;

        public IReadOnlyList<Camera> Cameras => _cameras;
        public IReadOnlyList<RenderTexture> RenderTextures => _renderTextures;
        public IReadOnlyList<string> Names => FeedNames;

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

            // The spacecraft uses NADIR TRACK pointing. In its body frame,
            // -Z is therefore aimed at Earth. This is the navigation camera.
            RegisterNadirEarthCamera(sensorRig);

            _cameraManager = CesiumCameraManager.GetOrCreate(gameObject);

            foreach (Camera camera in _cameras)
            {
                if (!_cameraManager.additionalCameras.Contains(camera))
                {
                    _cameraManager.additionalCameras.Add(camera);
                }
            }
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

            ConfigureNavigationCamera(camera);
            RegisterCamera(camera);
        }

        private void RegisterNadirEarthCamera(Transform sensorRig)
        {
            const float cameraMountOffsetMeters = 0.06f;

            Camera camera = CreateCamera(
                "Nadir Earth -Z Navigation Camera",
                sensorRig,
                Vector3.back * cameraMountOffsetMeters,
                Quaternion.LookRotation(Vector3.back, Vector3.up));

            ConfigureNavigationCamera(camera);
            RegisterCamera(camera);
        }

        private static void ConfigureNavigationCamera(Camera camera)
        {
            int hiddenLayers =
                (1 << CubeSatVisualModel.OrbitMarkerLayer) |
                (1 << CubeSatVisualModel.SpacecraftModelLayer);

            ConfigureCamera(
                camera,
                ~hiddenLayers,
                0.05f,
                10_000_000f,
                9f);
        }

        private void RegisterCamera(Camera camera)
        {
            camera.enabled = true;

            RenderTexture renderTexture = new RenderTexture(
                768,
                432,
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
        }
    }
}