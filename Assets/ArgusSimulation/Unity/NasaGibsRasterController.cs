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
        [SerializeField, Range(0, 8)] private int maximumLevel = 8;

        private CesiumUrlTemplateRasterOverlay _baseOverlay;
        private CesiumUrlTemplateRasterOverlay _observationOverlay;

        public string ActiveTemplateUrl =>
            _observationOverlay == null ? string.Empty : _observationOverlay.templateUrl;

        private void OnEnable()
        {
            ApplySettings();
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
                    66.0);
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
    }
}
