using System;
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
                    NasaGibsUrl.BuildBaseTemplate(),
                    "0",
                    false,
                    90.0);
                ConfigureOverlay(
                    _observationOverlay,
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
                    // TODO(cesium): the default tileset material has overlay slots 0-2 only, so
                    // key "3" is never drawn and night lights past the antimeridian are missing.
                    "3",
                    true,
                    90.0);
            }
            catch (ArgumentException exception)
            {
                Debug.LogError(exception.Message, this);
            }
        }

        private void ApplyCommandLineOverrides()
        {
            string[] args = Environment.GetCommandLineArgs();
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
