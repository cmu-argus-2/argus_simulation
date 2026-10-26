using System;
using System.Globalization;
using System.IO;

namespace Argus.Simulation.Core
{
    public static class NasaGibsUrl
    {
        public const string DefaultLayer = "VIIRS_SNPP_CorrectedReflectance_TrueColor";
        public const string DefaultBaseLayer = "BlueMarble_NextGeneration";

        public static bool TryParseDate(string value, out DateTimeOffset date)
        {
            return DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out date);
        }

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
