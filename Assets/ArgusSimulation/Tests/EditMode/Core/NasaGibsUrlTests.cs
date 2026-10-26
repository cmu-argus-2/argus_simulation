using System;
using System.IO;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class NasaGibsUrlTests
    {
        [Test]
        public void BuildBaseTemplate_UsesContinuousBlueMarbleLayer()
        {
            Assert.That(
                NasaGibsUrl.BuildBaseTemplate(),
                Is.EqualTo(
                    "https://gibs.earthdata.nasa.gov/wms/epsg4326/best/wms.cgi" +
                    "?SERVICE=WMS&REQUEST=GetMap&VERSION=1.1.1" +
                    "&LAYERS=BlueMarble_NextGeneration" +
                    "&STYLES=default&FORMAT=image/jpeg&SRS=EPSG:4326" +
                    "&BBOX={westDegrees},{southDegrees},{eastDegrees},{northDegrees}" +
                    "&WIDTH={width}&HEIGHT={height}"));
        }

        [Test]
        public void BuildTemplate_UsesTransparentDatedGeographicWmsLayer()
        {
            string result = NasaGibsUrl.BuildTemplate(
                NasaGibsUrl.DefaultLayer,
                new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero));

            Assert.That(
                result,
                Is.EqualTo(
                    "https://gibs.earthdata.nasa.gov/wms/epsg4326/best/wms.cgi" +
                    "?SERVICE=WMS&REQUEST=GetMap&VERSION=1.1.1" +
                    "&LAYERS=VIIRS_SNPP_CorrectedReflectance_TrueColor" +
                    "&STYLES=default&FORMAT=image/png&TRANSPARENT=TRUE" +
                    "&TIME=2025-01-15&SRS=EPSG:4326" +
                    "&BBOX={westDegrees},{southDegrees},{eastDegrees},{northDegrees}" +
                    "&WIDTH={width}&HEIGHT={height}"));
        }

        [Test]
        public void TryParseDate_AcceptsIsoDateOnly()
        {
            Assert.That(NasaGibsUrl.TryParseDate("2025-06-21", out DateTimeOffset date), Is.True);
            Assert.That(date, Is.EqualTo(new DateTimeOffset(2025, 6, 21, 0, 0, 0, TimeSpan.Zero)));
            Assert.That(NasaGibsUrl.TryParseDate("06/21/2025", out _), Is.False);
            Assert.That(NasaGibsUrl.TryParseDate("2025-06-21T00:00:00Z", out _), Is.False);
            Assert.That(NasaGibsUrl.TryParseDate(null, out _), Is.False);
        }

        [Test]
        public void BuildPackTemplate_EscapesDirectoryButKeepsPlaceholders()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "Imagery Packs",
                NasaGibsUrl.DefaultLayer,
                "2025-06-21");

            string result = NasaGibsUrl.BuildPackTemplate(directory, "png");

            Assert.That(result, Does.StartWith("file:///"));
            Assert.That(result, Does.Contain("Imagery%20Packs"));
            Assert.That(
                result,
                Does.EndWith(
                    "/" + NasaGibsUrl.DefaultLayer + "/2025-06-21/{z}/{x}/{reverseY}.png"));
        }

        [Test]
        public void BuildPackTemplate_RejectsUnknownExtension()
        {
            Assert.Throws<ArgumentException>(() =>
                NasaGibsUrl.BuildPackTemplate(Path.GetTempPath(), "tif"));
        }

        [Test]
        public void BuildTemplate_RejectsUrlInjection()
        {
            Assert.Throws<ArgumentException>(() =>
                NasaGibsUrl.BuildTemplate(
                    "invalid/layer",
                    new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero)));
        }
    }
}
