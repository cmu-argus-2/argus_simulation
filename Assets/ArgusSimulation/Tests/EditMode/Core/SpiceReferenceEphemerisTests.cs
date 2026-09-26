using System;
using System.Collections.Generic;
using System.IO;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class SpiceReferenceEphemerisTests
    {
        private const string ReferencePath = "Assets/StreamingAssets/Argus/Spice/foundation_one_orbit.json";
        private const double AstronomicalUnitMeters = 149_597_870_700.0;
        private const double LeoRadiusMeters = 6_878_137.0;

        private static string _json;
        private static TabulatedEphemerisProvider _provider;

        [OneTimeSetUp]
        public void LoadReference()
        {
            _json = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), ReferencePath));
            _provider = SpiceReferenceFile.Load(_json);
        }

        [Test]
        public void Load_ReadsFoundationReferenceHeader()
        {
            Assert.That(_provider.EpochUtc, Is.EqualTo(new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero)));
            Assert.That(_provider.StepSeconds, Is.EqualTo(10.0));
            Assert.That(_provider.KnotCount, Is.EqualTo(569));
            Assert.That(_provider.CoverageStartSeconds, Is.EqualTo(0.0));
            Assert.That(_provider.CoverageEndSeconds, Is.EqualTo(5680.0));
            Assert.That(_provider.SourceName, Does.Contain("foundation_one_orbit").And.Contain("de440s.bsp"));
        }

        [Test]
        public void TryGetSample_AtKnot_ReproducesFileValuesExactly()
        {
            Assert.That(_provider.TryGetSample(0.0, out EphemerisSample sample), Is.True);

            Assert.That(sample.EphemerisTimeSeconds, Is.EqualTo(790171269.1843309));
            Assert.That(sample.TimestampUtc, Is.EqualTo(_provider.EpochUtc));
            Assert.That(sample.J2000ToItrf93.ToRowMajor(), Is.EqualTo(new[]
            {
                -0.41275331697402873, 0.9108423308589297, 0.0009734692644210158,
                -0.9108395986434633, -0.4127544635018774, 0.0022312333567098,
                0.0024341055752874208, 3.42746148280378e-05, 0.9999970369732598,
            }));
            Assert.That(sample.SunPositionJ2000Meters,
                Is.EqualTo(new Vector3d(61571146677.18358, -122621156753.13065, -53154573837.481125)));

            Assert.That(_provider.TryGetSample(1230.0, out EphemerisSample later), Is.True);
            Assert.That(later.J2000ToItrf93.M01, Is.EqualTo(0.8702094226242161));
            Assert.That(later.SunPositionJ2000Meters.Z, Is.EqualTo(-53148424307.82466));
        }

        [Test]
        public void SunPositionItrf93_AgreesWithSpiceFrameTransform()
        {
            _provider.TryGetSample(0.0, out EphemerisSample sample);
            Vector3d spiceItrf93 = new Vector3d(-137153979594.43587, -5587609016.053531, -53008748460.57119);

            Assert.That((sample.SunPositionItrf93Meters - spiceItrf93).Magnitude, Is.LessThan(1.0));
            Assert.That(sample.SunPositionJ2000Meters.Magnitude / AstronomicalUnitMeters,
                Is.EqualTo(0.98362).Within(1e-5));
        }

        [TestCase(-0.001)]
        [TestCase(5680.001)]
        [TestCase(double.NaN)]
        public void TryGetSample_OutsideCoverage_ReturnsFalse(double simulationTimeSeconds)
        {
            Assert.That(_provider.TryGetSample(simulationTimeSeconds, out _), Is.False);
        }

        [TestCase(0.0)]
        [TestCase(5680.0)]
        public void TryGetSample_AtCoverageBounds_ReturnsTrue(double simulationTimeSeconds)
        {
            Assert.That(_provider.TryGetSample(simulationTimeSeconds, out EphemerisSample sample), Is.True);
            Assert.That(sample.IsValid, Is.True);
        }

        [TestCase(5.0)]
        [TestCase(1234.567)]
        [TestCase(5679.9)]
        public void TryGetSample_BetweenKnots_StaysOrthonormal(double simulationTimeSeconds)
        {
            _provider.TryGetSample(simulationTimeSeconds, out EphemerisSample sample);
            Matrix3d rotation = sample.J2000ToItrf93;
            double[] product = (rotation * rotation.Transposed).ToRowMajor();
            double[] identity = Matrix3d.Identity.ToRowMajor();

            for (int index = 0; index < 9; index++)
            {
                Assert.That(product[index], Is.EqualTo(identity[index]).Within(1e-12));
            }

            Assert.That(sample.TimestampUtc, Is.EqualTo(_provider.EpochUtc.AddSeconds(simulationTimeSeconds)));
        }

        [Test]
        public void Interpolation_AtHalfResolution_MatchesSkippedKnots()
        {
            // Rebuild the table from every other knot (20 s) and predict the omitted 10 s knots.
            List<EphemerisSample> sparse = new List<EphemerisSample>();
            for (double t = 0.0; t <= _provider.CoverageEndSeconds; t += 20.0)
            {
                _provider.TryGetSample(t, out EphemerisSample knot);
                sparse.Add(knot);
            }

            TabulatedEphemerisProvider coarse = new TabulatedEphemerisProvider("sparse", _provider.EpochUtc, sparse);
            Vector3d leoPoint = new Vector3d(LeoRadiusMeters, 0.0, 0.0);
            double worstPositionMeters = 0.0;
            double worstSunMeters = 0.0;

            for (double t = 10.0; t < coarse.CoverageEndSeconds; t += 20.0)
            {
                _provider.TryGetSample(t, out EphemerisSample truth);
                coarse.TryGetSample(t, out EphemerisSample predicted);
                Vector3d truthPoint = truth.J2000ToItrf93.Transposed.Transform(leoPoint);
                Vector3d predictedPoint = predicted.J2000ToItrf93.Transposed.Transform(leoPoint);
                worstPositionMeters = Math.Max(worstPositionMeters, (truthPoint - predictedPoint).Magnitude);
                worstSunMeters = Math.Max(
                    worstSunMeters,
                    (truth.SunPositionJ2000Meters - predicted.SunPositionJ2000Meters).Magnitude);
            }

            Assert.That(worstPositionMeters, Is.LessThan(1e-4), "Earth orientation error at LEO radius");
            Assert.That(worstSunMeters, Is.LessThan(5.0), "Sun position error");
        }

        [TestCase(1234.567)]
        [TestCase(4000.25)]
        public void RotationRate_MatchesNumericalDerivativeAndEarthSpin(double t)
        {
            const double h = 0.5;
            _provider.TryGetSample(t, out EphemerisSample sample);
            _provider.TryGetSample(t - h, out EphemerisSample before);
            _provider.TryGetSample(t + h, out EphemerisSample after);
            double[] numerical = ((after.J2000ToItrf93 + before.J2000ToItrf93 * -1.0) * (0.5 / h)).ToRowMajor();
            double[] analytic = sample.J2000ToItrf93Rate.ToRowMajor();

            for (int index = 0; index < 9; index++)
            {
                Assert.That(analytic[index], Is.EqualTo(numerical[index]).Within(1e-12));
            }

            // dR/dt = -[w]x R, so [w]x = -dR/dt R^T.
            Matrix3d skew = sample.J2000ToItrf93Rate * sample.J2000ToItrf93.Transposed * -1.0;
            Vector3d earthRate = new Vector3d(skew.M21, skew.M02, skew.M10);
            Assert.That(earthRate.Magnitude, Is.EqualTo(CircularOrbitModel.EarthRotationRadiansPerSecond).Within(1e-8));
        }

        [Test]
        public void TransformState_GroundPointIsStationaryInItrf93()
        {
            _provider.TryGetSample(2000.0, out EphemerisSample sample);
            Vector3d groundItrf93 = new Vector3d(CircularOrbitModel.EarthEquatorialRadiusMeters, 0.0, 0.0);

            // A point fixed on Earth moves in J2000 with velocity d(R^T)/dt * p.
            Vector3d positionJ2000 = sample.J2000ToItrf93.Transposed.Transform(groundItrf93);
            Vector3d velocityJ2000 = sample.J2000ToItrf93Rate.Transposed.Transform(groundItrf93);
            sample.TransformStateToItrf93(
                positionJ2000,
                velocityJ2000,
                out Vector3d positionItrf93,
                out Vector3d velocityItrf93);

            Assert.That(velocityJ2000.Magnitude, Is.EqualTo(465.1).Within(0.1), "equatorial surface speed");
            Assert.That((positionItrf93 - groundItrf93).Magnitude, Is.LessThan(1e-6));
            Assert.That(velocityItrf93.Magnitude, Is.LessThan(1e-9));
        }

        [TestCase("\"positions\": \"m\"", "\"positions\": \"km\"")]
        [TestCase("\"earth_fixed\": \"ITRF93\"", "\"earth_fixed\": \"IAU_EARTH\"")]
        [TestCase("\"format_version\": 1", "\"format_version\": 2")]
        [TestCase("\"sample_count\": 569", "\"sample_count\": 570")]
        public void Load_RejectsIncompatibleHeader(string original, string replacement)
        {
            Assert.That(_json, Does.Contain(original));
            Assert.Throws<FormatException>(() => SpiceReferenceFile.Load(_json.Replace(original, replacement)));
        }

        [Test]
        public void Load_RejectsTruncatedFile()
        {
            Assert.Throws<FormatException>(() => SpiceReferenceFile.Load(_json.Substring(0, _json.Length / 2)));
        }
    }
}
