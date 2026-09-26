using System;
using System.Collections.Generic;
using System.IO;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class SunGeometryTests
    {
        private const string EphemerisPath = "Assets/StreamingAssets/Argus/Spice/foundation_one_orbit.json";
        private const string SpacecraftReferencePath =
            "Assets/ArgusSimulation/Tests/Fixtures/foundation_one_orbit_spacecraft_reference.json";
        private const double AstronomicalUnitMeters = 149_597_870_700.0;
        private const double LeoRadiusMeters = 6_878_137.0;

        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero);
        private static readonly Vector3d SunOnX = new Vector3d(AstronomicalUnitMeters, 0.0, 0.0);

        private static TabulatedEphemerisProvider _ephemeris;
        private static AnalyticSimulationEngine _engine;

        [OneTimeSetUp]
        public void LoadEphemeris()
        {
            _ephemeris = SpiceReferenceFile.Load(ReadProjectFile(EphemerisPath));
            _engine = new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0, _ephemeris);
            _engine.Initialize(new SimulationConfiguration("sun-geometry", Epoch, 0.1));
        }

        [Test]
        public void Quaternion_RotateMapsBodyAxesAndComposes()
        {
            Vector3d bodyX = new Vector3d(0.0, 1.0, 0.0);
            Vector3d bodyY = new Vector3d(0.0, 0.0, 1.0);
            Vector3d bodyZ = new Vector3d(1.0, 0.0, 0.0);
            Quaterniond bodyToFrame = Quaterniond.FromBasis(bodyX, bodyY, bodyZ);

            AssertClose(bodyToFrame.Rotate(new Vector3d(1.0, 0.0, 0.0)), bodyX, 1e-12);
            AssertClose(bodyToFrame.Rotate(new Vector3d(0.0, 1.0, 0.0)), bodyY, 1e-12);
            AssertClose(bodyToFrame.Rotate(new Vector3d(0.0, 0.0, 1.0)), bodyZ, 1e-12);
            AssertClose(bodyToFrame.Conjugate.Rotate(bodyZ), new Vector3d(0.0, 0.0, 1.0), 1e-12);

            Quaterniond a = Quaterniond.FromAxisAngle(new Vector3d(1.0, 2.0, 3.0), 0.7);
            Quaterniond b = Quaterniond.FromAxisAngle(new Vector3d(-2.0, 0.5, 1.0), -1.1);
            Vector3d v = new Vector3d(0.3, -4.0, 2.5);
            AssertClose((a * b).Rotate(v), a.Rotate(b.Rotate(v)), 1e-12);
        }

        [Test]
        public void ShadowModel_KnownSunlitAndShadowedGeometry()
        {
            Assert.That(EarthShadowModel.IlluminationFraction(new Vector3d(LeoRadiusMeters, 0.0, 0.0), SunOnX),
                Is.EqualTo(1.0), "sub-solar point");
            Assert.That(EarthShadowModel.IlluminationFraction(new Vector3d(-LeoRadiusMeters, 0.0, 0.0), SunOnX),
                Is.EqualTo(0.0), "anti-solar point");
            Assert.That(EarthShadowModel.IlluminationFraction(new Vector3d(0.0, LeoRadiusMeters, 0.0), SunOnX),
                Is.EqualTo(1.0), "terminator plane, above the limb");

            // Behind Earth but offset by exactly one Earth radius: on the shadow boundary (penumbra).
            Vector3d edge = new Vector3d(-LeoRadiusMeters, EarthShadowModel.EarthRadiusMeters, 0.0);
            double fraction = EarthShadowModel.IlluminationFraction(edge, SunOnX);
            Assert.That(fraction, Is.GreaterThan(0.0).And.LessThan(1.0));
            Assert.That(EarthShadowModel.Classify(fraction), Is.EqualTo(SunlightCondition.Penumbra));
        }

        [Test]
        public void ShadowModel_EclipseTimesAgreeWithSpiceOccultationSearch()
        {
            Dictionary<string, object> windows = (Dictionary<string, object>)
                ((Dictionary<string, object>)((Dictionary<string, object>)JsonReader.Parse(
                    ReadProjectFile(SpacecraftReferencePath)))["eclipse"])["windows_t_seconds"];
            double[] spicePartial = Window(windows["penumbra_or_umbra"]);
            double[] spiceUmbra = Window(windows["umbra"]);

            List<double> partialEdges = new List<double>();
            List<double> umbraEdges = new List<double>();
            double previous = 1.0;
            for (int step = 0; step <= 56_800; step++)
            {
                double t = step * 0.1;
                double fraction = Observe(t).IlluminationFraction;
                if (step > 0 && (fraction < 1.0) != (previous < 1.0))
                {
                    partialEdges.Add(t);
                }

                if (step > 0 && (fraction <= 0.0) != (previous <= 0.0))
                {
                    umbraEdges.Add(t);
                }

                previous = fraction;
            }

            TestContext.WriteLine(
                $"Shadow entry/exit vs SPICE: penumbra {partialEdges[0] - spicePartial[0]:+0.0;-0.0} s / " +
                $"{partialEdges[1] - spicePartial[1]:+0.0;-0.0} s, umbra {umbraEdges[0] - spiceUmbra[0]:+0.0;-0.0} s / " +
                $"{umbraEdges[1] - spiceUmbra[1]:+0.0;-0.0} s");

            // Spherical Earth vs SPICE's ellipsoid: a few seconds at LEO.
            Assert.That(partialEdges, Has.Count.EqualTo(2));
            Assert.That(umbraEdges, Has.Count.EqualTo(2));
            Assert.That(partialEdges[0], Is.EqualTo(spicePartial[0]).Within(3.0));
            Assert.That(partialEdges[1], Is.EqualTo(spicePartial[1]).Within(3.0));
            Assert.That(umbraEdges[0], Is.EqualTo(spiceUmbra[0]).Within(3.0));
            Assert.That(umbraEdges[1], Is.EqualTo(spiceUmbra[1]).Within(3.0));

            Assert.That(Observe(100.0).Condition, Is.EqualTo(SunlightCondition.Sunlit));
            Assert.That(Observe(1800.0).Condition, Is.EqualTo(SunlightCondition.Umbra));
            Assert.That(Observe(4000.0).Condition, Is.EqualTo(SunlightCondition.Sunlit));
        }

        [TestCase(100.0)]
        [TestCase(3500.0)]
        public void NadirAttitude_BodyZSunComponentIsMinusCosineOfSunElevationFromZenith(double t)
        {
            SpacecraftState state = Step(t);
            SunObservation sun = Observe(t);

            double expected = -Vector3d.Dot(state.PositionEcefMeters.Normalized(), sun.SunDirectionItrf93);
            Assert.That(sun.SunDirectionBody.Z, Is.EqualTo(expected).Within(1e-12));
            Assert.That(sun.SunDirectionBody.Magnitude, Is.EqualTo(1.0).Within(1e-12));
        }

        [TestCase(0.0, 0.0, 1.0, 90.0)]
        [TestCase(1.0, 0.0, 0.0, -35.0)]
        [TestCase(1.0, -2.0, 0.5, 123.0)]
        public void RotatingSpacecraft_RotatesBodySunDirectionInversely(double x, double y, double z, double degrees)
        {
            SpacecraftState state = Step(100.0);
            _ephemeris.TryGetSample(100.0, out EphemerisSample environment);
            Quaterniond offset = Quaterniond.FromAxisAngle(new Vector3d(x, y, z), degrees * Math.PI / 180.0);

            SunObservation before = SolarGeometry.Observe(state, environment);
            SunObservation after = SolarGeometry.Observe(state.WithBodyToEcef(state.BodyToEcef * offset), environment);

            AssertClose(after.SunDirectionItrf93, before.SunDirectionItrf93, 0.0);
            AssertClose(after.SunDirectionBody, offset.Conjugate.Rotate(before.SunDirectionBody), 1e-12);
            Assert.That(after.IlluminationFraction, Is.EqualTo(before.IlluminationFraction));
        }

        [Test]
        public void RotatingSpacecraft_NinetyDegreesAboutBodyZ_SwapsXAndY()
        {
            SpacecraftState state = Step(100.0);
            _ephemeris.TryGetSample(100.0, out EphemerisSample environment);
            Quaterniond yaw = Quaterniond.FromAxisAngle(new Vector3d(0.0, 0.0, 1.0), Math.PI / 2.0);

            Vector3d before = SolarGeometry.Observe(state, environment).SunDirectionBody;
            Vector3d after = SolarGeometry.Observe(state.WithBodyToEcef(state.BodyToEcef * yaw), environment).SunDirectionBody;

            AssertClose(after, new Vector3d(before.Y, -before.X, before.Z), 1e-12);
        }

        [Test]
        public void Observe_RejectsStateAndEphemerisFromDifferentTimes()
        {
            _ephemeris.TryGetSample(100.1, out EphemerisSample environment);

            Assert.Throws<ArgumentException>(() => SolarGeometry.Observe(Step(100.0), environment));
        }

        private static SpacecraftState Step(double t)
        {
            _engine.TryStep(
                new SimulationStepInput(0, t, ActuatorCommandSet.None(0, t)),
                out SimulationSnapshot snapshot);
            return snapshot.Spacecraft;
        }

        private static SunObservation Observe(double t)
        {
            _ephemeris.TryGetSample(t, out EphemerisSample environment);
            return SolarGeometry.Observe(Step(t), environment);
        }

        private static double[] Window(object windows)
        {
            List<object> first = (List<object>)((List<object>)windows)[0];
            return new[] { (double)first[0], (double)first[1] };
        }

        private static void AssertClose(Vector3d actual, Vector3d expected, double tolerance)
        {
            Assert.That((actual - expected).Magnitude, Is.LessThanOrEqualTo(tolerance), $"{actual} vs {expected}");
        }

        private static string ReadProjectFile(string relativePath) =>
            File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), relativePath));
    }
}
