using System;
using System.Globalization;

namespace Argus.Simulation.Core
{
    public static class NasaGibsUrl
    {
        public const string DefaultLayer = "VIIRS_SNPP_CorrectedReflectance_TrueColor";
        public const string DefaultBaseLayer = "BlueMarble_NextGeneration";

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
