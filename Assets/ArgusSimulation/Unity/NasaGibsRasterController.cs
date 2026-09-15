using System;
using System.Globalization;
using Argus.Simulation.Core;
using CesiumForUnity;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    [ExecuteAlways]
    [RequireComponent(typeof(CesiumUrlTemplateRasterOverlay))]
    public sealed class NasaGibsRasterController : MonoBehaviour
    {
        private const string LegacyDefaultLayer =
            "MODIS_Terra_CorrectedReflectance_TrueColor";

        [SerializeField] private string layer = NasaGibsUrl.DefaultLayer;
        [SerializeField] private string observationDateUtc = "2025-01-15";
        [SerializeField] private string nightLayer = "VIIRS_Night_Lights";
        [SerializeField] private string nightDateUtc = "2016-01-01";
        [SerializeField, Range(0, 8)] private int maximumLevel = 8;
        [SerializeField] private Light sunlight;

        private CesiumUrlTemplateRasterOverlay _baseOverlay;
        private CesiumUrlTemplateRasterOverlay _observationOverlay;
        private CesiumUrlTemplateRasterOverlay _nightOverlayWest;
        private CesiumUrlTemplateRasterOverlay _nightOverlayEast;
        private float _lastNightCenterLongitude = float.NaN;

        public string ActiveTemplateUrl =>
            _observationOverlay == null ? string.Empty : _observationOverlay.templateUrl;

        private void OnEnable()
        {
            ApplySettings();
        }

        private void Update()
        {
            if (_nightOverlayWest == null || _nightOverlayEast == null)
            {
                return;
            }

            if (sunlight == null)
            {
                sunlight = FindFirstObjectByType<Light>();
            }

            if (sunlight == null || transform.parent == null)
            {
                return;
            }

            Vector3 sunDirection = transform.parent.InverseTransformDirection(-sunlight.transform.forward);
            float sunLongitude = Mathf.Atan2(sunDirection.z, sunDirection.x) * Mathf.Rad2Deg;
            float nightCenterLongitude = NormalizeLongitude(sunLongitude + 180f);
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

        public void Configure(string gibsLayer, string dateUtc)
        {
            layer = gibsLayer;
            observationDateUtc = dateUtc;
            ApplySettings();
        }

        [ContextMenu("Apply NASA GIBS Settings")]
        public void ApplySettings()
        {
            EnsureOverlays();
            if (layer == LegacyDefaultLayer)
            {
                layer = NasaGibsUrl.DefaultLayer;
            }

            if (!DateTimeOffset.TryParseExact(
                    observationDateUtc,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset date))
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
                    60.0);

                if (!DateTimeOffset.TryParseExact(
                        nightDateUtc,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTimeOffset nightDate))
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
                    "3",
                    true,
                    90.0);
            }
            catch (ArgumentException exception)
            {
                Debug.LogError(exception.Message, this);
            }
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
