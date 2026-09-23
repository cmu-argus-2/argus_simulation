using System;
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
        public void BuildTemplate_RejectsUrlInjection()
        {
            Assert.Throws<ArgumentException>(() =>
                NasaGibsUrl.BuildTemplate(
                    "invalid/layer",
                    new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero)));
        }
    }
}
