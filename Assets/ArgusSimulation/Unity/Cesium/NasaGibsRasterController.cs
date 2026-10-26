using System;
using System.IO;
using Argus.Simulation.Core;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    [ExecuteAlways]
    [RequireComponent(typeof(CesiumUrlTemplateRasterOverlay))]
    public sealed class NasaGibsRasterController : MonoBehaviour
    {
        private const string LegacyDefaultLayer =
            "MODIS_Terra_CorrectedReflectance_TrueColor";

        public const string DateCommandLineOption = "-gt-date";
        public const string LayerCommandLineOption = "-gt-layer";
        public const string PackCommandLineOption = "-gt-pack";
        private const string StaticPackDate = "static";

        // Northern summer solstice: VIIRS has gap-free daylight imagery all the way to the north
        // pole. GIBS returns opaque black over whichever pole is in polar night, so no date covers
        // both poles, and clipping the overlay short of a pole makes Cesium stretch its edge row.
        public const string DefaultObservationDateUtc = "2025-06-21";

        [SerializeField] private string layer = NasaGibsUrl.DefaultLayer;
        [SerializeField] private string observationDateUtc = DefaultObservationDateUtc;
        [SerializeField] private string nightLayer = "VIIRS_Night_Lights";
        [SerializeField] private string nightDateUtc = "2016-01-01";
        [SerializeField, Range(0, 8)] private int maximumLevel = 8;
        [SerializeField] private Light sunlight;

        [Header("Offline imagery pack")]
        [Tooltip("Read the base and observation layers from a pack made by " +
            "Argus.Imagery/download_gibs_pack.py. Falls back to streaming when the pack is missing.")]
        [SerializeField] private bool useImageryPack;
        [Tooltip("Pack root. Empty uses ImageryPacks/ in the project root.")]
        [SerializeField] private string imageryPackDirectory = "";

        // JsonUtility reads public fields named as in manifest.json.
        [Serializable]
        private sealed class ImageryPackManifest
        {
            public string extension;
            public int tile_size;
            public int root_tiles_x;
            public int root_tiles_y;
            public int max_level;
            public bool complete;
        }

        private CesiumUrlTemplateRasterOverlay _baseOverlay;
        private CesiumUrlTemplateRasterOverlay _observationOverlay;
        private CesiumUrlTemplateRasterOverlay _nightOverlayWest;
        private CesiumUrlTemplateRasterOverlay _nightOverlayEast;
        private CesiumGeoreference _georeference;
        private bool _hasSunDirectionInput;
        private Vector3 _sunDirectionWorld;
        private float _lastNightCenterLongitude = float.NaN;

        public string ActiveTemplateUrl =>
            _observationOverlay == null ? string.Empty : _observationOverlay.templateUrl;

        public string Layer => layer;

        public string ObservationDateUtc => observationDateUtc;

        public void ConfigureSunlight(Light source)
        {
            sunlight = source;
            _lastNightCenterLongitude = float.NaN;
        }

        // Sun input: the direction from the scene towards the Sun, in Unity world axes. SunLightDriver
        // feeds it from EnvironmentState (UNITY-3); until something sets it, the controller reads the
        // scene's directional light.
        public void SetSunDirection(Vector3 towardSunWorld)
        {
            _sunDirectionWorld = towardSunWorld;
            _hasSunDirectionInput = true;
        }

        public void ClearSunDirection()
        {
            _hasSunDirectionInput = false;
        }

        private void OnEnable()
        {
            if (Application.isPlaying)
            {
                ApplyCommandLineOverrides();
            }

            ApplySettings();
        }

        private void Update()
        {
            if (_nightOverlayWest == null || _nightOverlayEast == null)
            {
                return;
            }

            if (!TryGetSunDirectionWorld(out Vector3 towardSunWorld))
            {
                return;
            }

            if (_georeference == null)
            {
                _georeference = GetComponentInParent<CesiumGeoreference>();
            }

            if (_georeference == null)
            {
                return;
            }

            // Unity world -> georeference local -> Earth-fixed. The local axes are East-Up-North only
            // at the georeference origin, so a longitude read from them is off by the origin's
            // offset (about 20 degrees near Denver).
            Vector3 towardSunLocal = _georeference.transform.InverseTransformDirection(towardSunWorld);
            double3 towardSunEcef = _georeference.TransformUnityDirectionToEarthCenteredEarthFixed(
                new double3(towardSunLocal.x, towardSunLocal.y, towardSunLocal.z));
            double antisolarLongitude = NasaGibsNightSide.AntisolarLongitudeDegrees(
                new Vector3d(towardSunEcef.x, towardSunEcef.y, towardSunEcef.z));
            if (double.IsNaN(antisolarLongitude))
            {
                return;
            }

            float nightCenterLongitude = (float)antisolarLongitude;
            if (!float.IsNaN(_lastNightCenterLongitude) &&
                Mathf.Abs(Mathf.DeltaAngle(_lastNightCenterLongitude, nightCenterLongitude)) < 0.5f)
            {
                return;
            }

            _lastNightCenterLongitude = nightCenterLongitude;
            float west = NormalizeLongitude(nightCenterLongitude - 90f);
            float east = NormalizeLongitude(nightCenterLongitude + 90f);
            if (west <= east)
            {
                SetNightRectangle(_nightOverlayWest, west, east, true);
                SetNightRectangle(_nightOverlayEast, -180f, -180f, false);
            }
            else
            {
                SetNightRectangle(_nightOverlayWest, west, 180f, true);
                SetNightRectangle(_nightOverlayEast, -180f, east, true);
            }
        }

        private bool TryGetSunDirectionWorld(out Vector3 towardSunWorld)
        {
            if (_hasSunDirectionInput)
            {
                towardSunWorld = _sunDirectionWorld;
                return towardSunWorld.sqrMagnitude > 0f;
            }

            if (sunlight == null)
            {
                sunlight = FindAnyObjectByType<Light>();
            }

            towardSunWorld = sunlight == null ? Vector3.zero : -sunlight.transform.forward;
            return sunlight != null;
        }

        public bool TryConfigure(string gibsLayer, string dateUtc, out string error)
        {
            if (!TrySetImagery(gibsLayer, dateUtc, out error))
            {
                return false;
            }

            ApplySettings();
            return true;
        }

        private bool TrySetImagery(string gibsLayer, string dateUtc, out string error)
        {
            gibsLayer = gibsLayer?.Trim();
            dateUtc = dateUtc?.Trim();
            if (!NasaGibsUrl.TryParseDate(dateUtc, out DateTimeOffset date))
            {
                error = "Date must use YYYY-MM-DD.";
                return false;
            }

            try
            {
                NasaGibsUrl.BuildTemplate(gibsLayer, date);
            }
            catch (ArgumentException exception)
            {
                error = exception.Message;
                return false;
            }

            layer = gibsLayer;
            observationDateUtc = dateUtc;
            error = null;
            return true;
        }

        [ContextMenu("Apply NASA GIBS Settings")]
        public void ApplySettings()
        {
            EnsureOverlays();
            if (layer == LegacyDefaultLayer)
            {
                layer = NasaGibsUrl.DefaultLayer;
            }

            if (!NasaGibsUrl.TryParseDate(observationDateUtc, out DateTimeOffset date))
            {
                Debug.LogError("NASA GIBS date must use YYYY-MM-DD.", this);
                return;
            }

            try
            {
                ConfigureOverlay(
                    _baseOverlay,
                    ResolvePackTemplate(NasaGibsUrl.DefaultBaseLayer, StaticPackDate) ??
                        NasaGibsUrl.BuildBaseTemplate(),
                    "0",
                    false,
                    90.0);
                ConfigureOverlay(
                    _observationOverlay,
                    ResolvePackTemplate(layer, observationDateUtc) ??
                        NasaGibsUrl.BuildTemplate(layer, date),
                    "1",
                    true,
                    90.0);

                if (!NasaGibsUrl.TryParseDate(nightDateUtc, out DateTimeOffset nightDate))
                {
                    Debug.LogError("NASA GIBS night date must use YYYY-MM-DD.", this);
                    return;
                }

                ConfigureOverlay(
                    _nightOverlayWest,
                    NasaGibsUrl.BuildTemplate(nightLayer, nightDate),
                    "2",
                    true,
                    90.0);
                ConfigureOverlay(
                    _nightOverlayEast,
                    NasaGibsUrl.BuildTemplate(nightLayer, nightDate),
                    // Foundation's project-owned tileset material supports overlay slots 0-3.
                    "3",
                    true,
                    90.0);
            }
            catch (ArgumentException exception)
            {
                Debug.LogError(exception.Message, this);
            }
        }

        // The night layer keeps streaming: its tiles follow the moving night-side rectangle,
        // so they never line up with a pre-downloaded grid.
        private string ResolvePackTemplate(string packLayer, string packDate)
        {
            if (!useImageryPack)
            {
                return null;
            }

            string root = string.IsNullOrWhiteSpace(imageryPackDirectory)
                ? Path.Combine(Application.dataPath, "..", "ImageryPacks")
                : imageryPackDirectory.Trim();
            string directory = Path.Combine(root, packLayer, packDate);
            string manifestPath = Path.Combine(directory, "manifest.json");
            string problem = null;
            ImageryPackManifest manifest = null;
            if (!File.Exists(manifestPath))
            {
                problem = "has not been downloaded";
            }
            else
            {
                try
                {
                    manifest = JsonUtility.FromJson<ImageryPackManifest>(File.ReadAllText(manifestPath));
                }
                catch (ArgumentException)
                {
                    // Invalid JSON leaves the manifest null, which is reported below.
                }

                if (manifest == null)
                {
                    problem = "has an unreadable manifest.json";
                }
                else if (!manifest.complete)
                {
                    problem = "is incomplete; rerun the download to finish it";
                }
                else if (manifest.max_level < maximumLevel || manifest.tile_size != 512 ||
                         manifest.root_tiles_x != 2 || manifest.root_tiles_y != 1)
                {
                    problem = $"does not match this overlay (needs level {maximumLevel}, 512 px, 2x1 roots)";
                }
            }

            if (problem != null)
            {
                Debug.LogWarning(
                    $"Imagery pack {directory} {problem}. Streaming {packLayer} from NASA GIBS instead. " +
                    "See Argus.Imagery/README.md.",
                    this);
                return null;
            }

            try
            {
                return NasaGibsUrl.BuildPackTemplate(directory, manifest.extension);
            }
            catch (ArgumentException exception)
            {
                Debug.LogWarning($"Imagery pack {directory}: {exception.Message}", this);
                return null;
            }
        }

        private void ApplyCommandLineOverrides()
        {
            string[] args = Environment.GetCommandLineArgs();
            string requestedPack = GetCommandLineValue(args, PackCommandLineOption);
            if (requestedPack != null)
            {
                useImageryPack = true;
                imageryPackDirectory = requestedPack;
            }

            string requestedDate = GetCommandLineValue(args, DateCommandLineOption);
            string requestedLayer = GetCommandLineValue(args, LayerCommandLineOption);
            if ((requestedDate != null || requestedLayer != null) &&
                !TrySetImagery(requestedLayer ?? layer, requestedDate ?? observationDateUtc, out string error))
            {
                Debug.LogError($"Ignoring NASA GIBS command-line override: {error}", this);
            }
        }

        private static string GetCommandLineValue(string[] args, string option)
        {
            for (int index = 0; index < args.Length - 1; index++)
            {
                if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
                {
                    return args[index + 1];
                }
            }

            return null;
        }

        private void EnsureOverlays()
        {
            CesiumUrlTemplateRasterOverlay[] overlays =
                GetComponents<CesiumUrlTemplateRasterOverlay>();
            _baseOverlay = overlays[0];
            _observationOverlay = overlays.Length > 1
                ? overlays[1]
                : gameObject.AddComponent<CesiumUrlTemplateRasterOverlay>();
            _nightOverlayWest = overlays.Length > 2
                ? overlays[2]
                : gameObject.AddComponent<CesiumUrlTemplateRasterOverlay>();
            _nightOverlayEast = overlays.Length > 3
                ? overlays[3]
                : gameObject.AddComponent<CesiumUrlTemplateRasterOverlay>();
        }

        private void ConfigureOverlay(
            CesiumUrlTemplateRasterOverlay overlay,
            string templateUrl,
            string materialKey,
            bool showCredits,
            double northLatitude)
        {
            overlay.enabled = false;
            overlay.materialKey = materialKey;
            overlay.templateUrl = templateUrl;
            overlay.projection = CesiumUrlTemplateRasterOverlayProjection.Geographic;
            overlay.specifyTilingScheme = true;
            overlay.rootTilesX = 2;
            overlay.rootTilesY = 1;
            overlay.rectangleWest = -180.0;
            overlay.rectangleSouth = -90.0;
            overlay.rectangleEast = 180.0;
            overlay.rectangleNorth = northLatitude;
            overlay.minimumLevel = 0;
            overlay.maximumLevel = maximumLevel;
            overlay.tileWidth = 512;
            overlay.tileHeight = 512;
            overlay.showCreditsOnScreen = showCredits;
            overlay.enabled = true;
        }

        private static float NormalizeLongitude(float longitude)
        {
            return Mathf.Repeat(longitude + 180f, 360f) - 180f;
        }

        private static void SetNightRectangle(
            CesiumUrlTemplateRasterOverlay overlay,
            float west,
            float east,
            bool enabled)
        {
            overlay.rectangleWest = west;
            overlay.rectangleEast = east;
            overlay.enabled = enabled;
        }
    }
}
