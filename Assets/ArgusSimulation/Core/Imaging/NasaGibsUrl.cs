using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Argus.Simulation.Core
{
    public static class NasaGibsUrl
    {
        public const string DefaultLayer = "VIIRS_SNPP_CorrectedReflectance_TrueColor";
        public const string DefaultBaseLayer = "BlueMarble_NextGeneration";

        // GIBS's pre-rendered EPSG:4326 tiles (WMTS): a quadtree of 512 px tiles whose two root
        // tiles are 288 degrees wide, anchored at 180 W, 90 N. The grid extends past the globe to
        // 396 E and 198 S; the tiles there hold no data, and the globe never asks for them. These
        // tiles come from GIBS's cache, where its WMS renders each request and sometimes answers
        // with an HTML error page instead of an image (seen on 2026-11-23 for about half of all
        // requests). Cesium's tiling rectangle in degrees.
        public const double TileGridWest = -180.0;
        public const double TileGridNorth = 90.0;
        public const double TileGridEast = 396.0;
        public const double TileGridSouth = -198.0;

        private const string TileEndpoint = "https://gibs.earthdata.nasa.gov/wmts/epsg4326/best/";

        // The layers served as WMTS tiles here, with their tile matrix set, image format and deepest
        // level, from GIBS's WMTS GetCapabilities. Other layers stream through WMS (BuildTemplate).
        private static readonly Dictionary<string, TileLayer> TileLayers = new Dictionary<string, TileLayer>(StringComparer.Ordinal)
        {
            { DefaultBaseLayer, new TileLayer("500m", "jpeg", 7, false) },
            { DefaultLayer, new TileLayer("250m", "jpg", 8, true) },
            { "VIIRS_NOAA20_CorrectedReflectance_TrueColor", new TileLayer("250m", "jpg", 8, true) },
            { "VIIRS_NOAA21_CorrectedReflectance_TrueColor", new TileLayer("250m", "jpg", 8, true) },
            { "MODIS_Terra_CorrectedReflectance_TrueColor", new TileLayer("250m", "jpg", 8, true) },
            { "MODIS_Aqua_CorrectedReflectance_TrueColor", new TileLayer("250m", "jpg", 8, true) }
        };

        private readonly struct TileLayer
        {
            public TileLayer(string matrixSet, string extension, int maximumLevel, bool dated)
            {
                MatrixSet = matrixSet;
                Extension = extension;
                MaximumLevel = maximumLevel;
                Dated = dated;
            }

            public string MatrixSet { get; }
            public string Extension { get; }
            public int MaximumLevel { get; }
            public bool Dated { get; }
        }

        // The WMTS tile template of a listed layer, for Cesium's {z}/{reverseY}/{x} over the tile
        // grid above, and its deepest level; false for a layer that is not listed. JPEG has no
        // transparency, so where a day's swaths leave a gap the tiles are black rather than
        // showing the base layer, as GIBS's polar night already is.
        public static bool TryBuildTileTemplate(
            string layer, DateTimeOffset observationDateUtc, out string template, out int maximumLevel)
        {
            ValidateIdentifier(layer, nameof(layer));
            if (!TileLayers.TryGetValue(layer, out TileLayer tiles))
            {
                template = null;
                maximumLevel = 0;
                return false;
            }

            string time = tiles.Dated
                ? observationDateUtc.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "/"
                : "";
            template = TileEndpoint + layer + "/default/" + time + tiles.MatrixSet + "/{z}/{reverseY}/{x}." + tiles.Extension;
            maximumLevel = tiles.MaximumLevel;
            return true;
        }

        public static bool TryParseDate(string value, out DateTimeOffset date)
        {
            return DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out date);
        }

        // Argus.Imagery/download_gibs_pack.py build_url repeats both WMS requests; keep them in
        // step so pack tiles match the WMS ones. The pack keeps this 2 x 1, 180-degree grid; the
        // listed layers stream as WMTS tiles (TryBuildTileTemplate) when there is no pack.
        public static string BuildBaseTemplate()
        {
            return "https://gibs.earthdata.nasa.gov/wms/epsg4326/best/wms.cgi" +
                "?SERVICE=WMS&REQUEST=GetMap&VERSION=1.1.1" +
                "&LAYERS=" + DefaultBaseLayer +
                "&STYLES=default&FORMAT=image/jpeg" +
                "&SRS=EPSG:4326" +
                "&BBOX={westDegrees},{southDegrees},{eastDegrees},{northDegrees}" +
                "&WIDTH={width}&HEIGHT={height}";
        }

        public static string BuildTemplate(
            string layer,
            DateTimeOffset observationDateUtc)
        {
            ValidateIdentifier(layer, nameof(layer));
            string date = observationDateUtc.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            return "https://gibs.earthdata.nasa.gov/wms/epsg4326/best/wms.cgi" +
                "?SERVICE=WMS&REQUEST=GetMap&VERSION=1.1.1" +
                "&LAYERS=" + layer +
                "&STYLES=default&FORMAT=image/png&TRANSPARENT=TRUE" +
                "&TIME=" + date + "&SRS=EPSG:4326" +
                "&BBOX={westDegrees},{southDegrees},{eastDegrees},{northDegrees}" +
                "&WIDTH={width}&HEIGHT={height}";
        }

        // Local tiles written by Argus.Imagery/download_gibs_pack.py. Only the directory is
        // URI-escaped so Cesium still sees its {z}/{x}/{reverseY} placeholders.
        public static string BuildPackTemplate(string packDirectory, string extension)
        {
            if (string.IsNullOrWhiteSpace(packDirectory))
            {
                throw new ArgumentException("An imagery pack directory is required.", nameof(packDirectory));
            }

            if (extension != "png" && extension != "jpg")
            {
                throw new ArgumentException("Imagery pack tiles must be png or jpg.", nameof(extension));
            }

            if (packDirectory.IndexOfAny(new[] { '#', '%', '?' }) >= 0)
            {
                throw new ArgumentException(
                    "An imagery pack directory may not contain '#', '%' or '?'; move or rename the pack.",
                    nameof(packDirectory));
            }

            string directory = Path.GetFullPath(packDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return new Uri(directory + Path.DirectorySeparatorChar).AbsoluteUri +
                "{z}/{x}/{reverseY}." + extension;
        }

        private static void ValidateIdentifier(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("A NASA GIBS identifier is required.", parameterName);
            }

            foreach (char character in value)
            {
                if (!(char.IsLetterOrDigit(character) || character == '_' || character == '-'))
                {
                    throw new ArgumentException(
                        "NASA GIBS identifiers may contain only letters, numbers, underscores, and hyphens.",
                        parameterName);
                }
            }
        }
    }
}
