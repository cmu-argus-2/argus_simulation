// Creates a small, labelled Cesium reference map for visual navigation.
//
// Each exported image is rendered by a separate 9-degree nadir camera at a
// known latitude / longitude / altitude. These Cesium-rendered reference
// images will later be indexed in Colab and matched against Unity flight images.
//
// Start with a 1x1 test. Then change rows / columns to 5x5.

using System;
using System.Collections;
using System.Globalization;
using System.IO;
using Argus.Simulation.Core;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    [DisallowMultipleComponent]
    public sealed class CesiumReferenceMapExporter : MonoBehaviour
    {
        [Header("Cesium")]
        [SerializeField] private CesiumGeoreference georeference;

        [Header("Reference-map center")]
        [SerializeField] private double centerLatitudeDegrees = 43.6;
        [SerializeField] private double centerLongitudeDegrees = 48.5;
        [SerializeField, Min(1.0f)] private float altitudeMeters = 500000f;

        [Header("Grid")]
        [Tooltip("Use 1 x 1 for the first test, then change to 5 x 5.")]
        [SerializeField, Min(1)] private int rows = 1;
        [SerializeField, Min(1)] private int columns = 1;
        [SerializeField, Min(0.01f)] private float gridSpacingDegrees = 0.25f;

        [Header("Camera")]
        [SerializeField, Range(1f, 120f)] private float fieldOfViewDegrees = 9f;
        [SerializeField, Min(1)] private int widthPixels = 768;
        [SerializeField, Min(1)] private int heightPixels = 432;

        [Header("Capture")]
        [SerializeField, Range(90f, 100f)] private float minimumLoadProgress = 100f;
        [SerializeField, Min(1)] private int stableFramesRequired = 10;
        [SerializeField, Min(0f)] private float postLoadSettleSeconds = 1f;
        [SerializeField, Min(1f)] private float loadTimeoutSeconds = 60f;
        [SerializeField] private bool runOnStart;
        [SerializeField] private string mapName = "cesium_reference_map";

        private Camera _referenceCamera;
        private CesiumGlobeAnchor _anchor;
        private CesiumCameraManager _cameraManager;
        private RenderTexture _renderTexture;
        private Texture2D _readbackTexture;
        private string _outputDirectory;
        private string _metadataPath;
        private bool _lastLoadSucceeded;

        private void Start()
        {
            if (runOnStart)
            {
                StartCoroutine(ExportGrid());
            }
        }

        [ContextMenu("Export Reference Grid")]
        public void StartReferenceExport()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning(
                    "Enter Play mode first, then use this component's export setting.");
                return;
            }

            StartCoroutine(ExportGrid());
        }

        private IEnumerator ExportGrid()
        {
            if (georeference == null)
            {
                georeference = FindAnyObjectByType<CesiumGeoreference>();
            }

            if (georeference == null)
            {
                Debug.LogError(
                    "CesiumReferenceMapExporter needs a CesiumGeoreference.");
                yield break;
            }

            BuildReferenceCameraIfNeeded();
            CreateOutputDirectory();

            int total = rows * columns;
            int sequence = 0;

            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    // Center the grid around the configured latitude/longitude.
                    double latitude = centerLatitudeDegrees +
                        (row - (rows - 1) * 0.5) * gridSpacingDegrees;

                    double longitude = centerLongitudeDegrees +
                        (column - (columns - 1) * 0.5) * gridSpacingDegrees;

                    MoveReferenceCamera(latitude, longitude);

                    yield return WaitForCesiumReady();
                    if (!_lastLoadSucceeded)
                    {
                        Debug.LogWarning(
                            "Reference-map export stopped rather than saving an incomplete tile.",
                            this);
                        yield break;
                    }

                    yield return new WaitForEndOfFrame();

                    string imageFile = string.Format(
                        CultureInfo.InvariantCulture,
                        "tile_{0:D4}_lat_{1:F5}_lon_{2:F5}.png",
                        sequence,
                        latitude,
                        longitude);

                    string imagePath = Path.Combine(_outputDirectory, imageFile);
                    CapturePng(imagePath);

                    ReferenceTileRecord record = new ReferenceTileRecord
                    {
                        sequence = sequence,
                        latitude_degrees = latitude,
                        longitude_degrees = longitude,
                        altitude_meters = altitudeMeters,
                        image_file = imageFile,
                        image_width_px = widthPixels,
                        image_height_px = heightPixels,
                        field_of_view_degrees = fieldOfViewDegrees,
                        fx_px = FocalLengthPixels(heightPixels, fieldOfViewDegrees),
                        fy_px = FocalLengthPixels(heightPixels, fieldOfViewDegrees),
                        cx_px = widthPixels * 0.5,
                        cy_px = heightPixels * 0.5
                    };

                    File.AppendAllText(
                        _metadataPath,
                        JsonUtility.ToJson(record) + Environment.NewLine);

                    sequence++;

                    Debug.Log(
                        $"Cesium reference tile {sequence}/{total}: " +
                        $"{imageFile}");
                }
            }

            Debug.Log(
                "Cesium reference-map export complete.\n" +
                $"Directory: {_outputDirectory}\n" +
                $"Metadata: {_metadataPath}");
        }

        private void BuildReferenceCameraIfNeeded()
        {
            if (_referenceCamera != null)
            {
                return;
            }

            GameObject cameraObject = new GameObject(
                "Cesium Reference Nadir Camera");

            // Globe anchors should be beneath the CesiumGeoreference.
            cameraObject.transform.SetParent(georeference.transform, false);

            _referenceCamera = cameraObject.AddComponent<Camera>();
            _referenceCamera.fieldOfView = fieldOfViewDegrees;
            _referenceCamera.nearClipPlane = 0.05f;
            _referenceCamera.farClipPlane = 10000000f;
            _referenceCamera.clearFlags = CameraClearFlags.SolidColor;
            _referenceCamera.backgroundColor = Color.black;
            _referenceCamera.allowHDR = true;
            _referenceCamera.useOcclusionCulling = false;
            _referenceCamera.enabled = true;

            _anchor = cameraObject.AddComponent<CesiumGlobeAnchor>();

            _renderTexture = new RenderTexture(
                widthPixels,
                heightPixels,
                24,
                RenderTextureFormat.ARGB32)
            {
                name = "Cesium Reference Nadir Render Texture",
                antiAliasing = 1,
                useMipMap = false
            };

            _renderTexture.Create();
            _referenceCamera.targetTexture = _renderTexture;

            _readbackTexture = new Texture2D(
                widthPixels,
                heightPixels,
                TextureFormat.RGB24,
                false);

            // Register the extra camera with Cesium.
            _cameraManager = CesiumCameraManager.GetOrCreate(gameObject);
            if (!_cameraManager.additionalCameras.Contains(_referenceCamera))
            {
                _cameraManager.additionalCameras.Add(_referenceCamera);
            }
        }

        private void MoveReferenceCamera(double latitude, double longitude)
        {
            // Cesium longitudeLatitudeHeight ordering is:
            // X = longitude, Y = latitude, Z = WGS84 ellipsoid height.
            _anchor.longitudeLatitudeHeight = new double3(
                longitude,
                latitude,
                altitudeMeters);

            // Camera local +Z looks down toward Earth (-Up in EUN).
            // Camera local +Y points North, keeping all references consistently oriented.
            Quaternion nadirRotation = Quaternion.LookRotation(
                Vector3.down,
                Vector3.forward);

            _anchor.rotationEastUpNorth = new quaternion(
                nadirRotation.x,
                nadirRotation.y,
                nadirRotation.z,
                nadirRotation.w);

            _anchor.Sync();
        }

        private void CapturePng(string imagePath)
        {
            _referenceCamera.Render();

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _renderTexture;

            _readbackTexture.ReadPixels(
                new Rect(0, 0, widthPixels, heightPixels),
                0,
                0);

            _readbackTexture.Apply();
            RenderTexture.active = previous;

            File.WriteAllBytes(imagePath, _readbackTexture.EncodeToPNG());
        }

        private IEnumerator WaitForCesiumReady()
        {
            Cesium3DTileset[] tilesets = FindObjectsByType<Cesium3DTileset>(
                FindObjectsInactive.Exclude);
            int requiredStableFrames = Math.Max(1, stableFramesRequired);
            float timeout = Math.Max(1f, loadTimeoutSeconds);
            float startedAt = Time.realtimeSinceStartup;
            int stableFrames = 0;
            _lastLoadSucceeded = false;

            while (stableFrames < requiredStableFrames)
            {
                float progress = MinimumEnabledTilesetProgress(tilesets);
                stableFrames = progress >= minimumLoadProgress
                    ? stableFrames + 1
                    : 0;

                if (Time.realtimeSinceStartup - startedAt >= timeout)
                {
                    Debug.LogWarning(
                        $"Cesium did not reach {minimumLoadProgress:0}% load progress " +
                        $"within {timeout:0} seconds.",
                        this);
                    yield break;
                }

                yield return null;
            }

            if (postLoadSettleSeconds > 0f)
            {
                yield return new WaitForSecondsRealtime(postLoadSettleSeconds);
            }

            _lastLoadSucceeded = true;
        }

        private static float MinimumEnabledTilesetProgress(Cesium3DTileset[] tilesets)
        {
            float progress = 100f;
            bool foundEnabledTileset = false;

            foreach (Cesium3DTileset tileset in tilesets)
            {
                if (tileset == null || !tileset.isActiveAndEnabled)
                {
                    continue;
                }

                foundEnabledTileset = true;
                progress = Math.Min(progress, tileset.ComputeLoadProgress());
            }

            return foundEnabledTileset ? progress : 100f;
        }

        private void CreateOutputDirectory()
        {
            string timestamp = DateTime.UtcNow.ToString(
                "yyyyMMddTHHmmssZ",
                CultureInfo.InvariantCulture);
            string safeMapName = OutputNameSanitizer.Sanitize(mapName, "map");

            _outputDirectory = Path.Combine(
                Application.persistentDataPath,
                "CesiumReferenceMaps",
                safeMapName + "_" + timestamp);

            Directory.CreateDirectory(_outputDirectory);

            _metadataPath = Path.Combine(
                _outputDirectory,
                "reference_tiles_metadata.jsonl");

            Debug.Log(
                "Cesium reference-map output directory:\n" +
                _outputDirectory);
        }

        private static double FocalLengthPixels(
            int imageHeightPixels,
            float verticalFieldOfViewDegrees)
        {
            return 0.5 * imageHeightPixels /
                Math.Tan(verticalFieldOfViewDegrees *
                    Math.PI / 360.0);
        }

        private void OnDestroy()
        {
            if (_cameraManager != null && _referenceCamera != null)
            {
                _cameraManager.additionalCameras.Remove(_referenceCamera);
            }

            if (_renderTexture != null)
            {
                _renderTexture.Release();
                Destroy(_renderTexture);
            }

            if (_readbackTexture != null)
            {
                Destroy(_readbackTexture);
            }
        }

        [Serializable]
        private sealed class ReferenceTileRecord
        {
            public int sequence;
            public double latitude_degrees;
            public double longitude_degrees;
            public float altitude_meters;
            public string image_file;
            public int image_width_px;
            public int image_height_px;
            public float field_of_view_degrees;
            public double fx_px;
            public double fy_px;
            public double cx_px;
            public double cy_px;
        }
    }
}
