using System;
using System.IO;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class SunGeometryTests
    {
        private const double AstronomicalUnitMeters = 149_597_870_700.0;
        private const double LeoRadiusMeters = 6_878_137.0;

        private static readonly DateTimeOffset Epoch =
            new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero);
        private static readonly Vector3d SunOnX = new Vector3d(AstronomicalUnitMeters, 0.0, 0.0);

        private SpiceRuntimeEphemerisProvider _ephemeris;
        private AnalyticSimulationEngine _engine;

        [OneTimeSetUp]
        public void StartRuntime()
        {
            _ephemeris = new SpiceRuntimeEphemerisProvider(
                Epoch,
                Path.Combine(Directory.GetCurrentDirectory(), "Argus.Spice", "run_runtime.sh"));
            _engine = new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0, _ephemeris);
            _engine.Initialize(new SimulationConfiguration("sun-geometry", Epoch, 0.1));
        }

        [OneTimeTearDown]
        public void StopRuntime() => _ephemeris?.Dispose();

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
            Assert.That(EarthShadowModel.IlluminationFraction(
                new Vector3d(LeoRadiusMeters, 0.0, 0.0), SunOnX), Is.EqualTo(1.0));
            Assert.That(EarthShadowModel.IlluminationFraction(
                new Vector3d(-LeoRadiusMeters, 0.0, 0.0), SunOnX), Is.EqualTo(0.0));

            Vector3d edge = new Vector3d(-LeoRadiusMeters, EarthShadowModel.EarthRadiusMeters, 0.0);
            double fraction = EarthShadowModel.IlluminationFraction(edge, SunOnX);
            Assert.That(fraction, Is.GreaterThan(0.0).And.LessThan(1.0));
            Assert.That(EarthShadowModel.Classify(fraction), Is.EqualTo(SunlightCondition.Penumbra));
        }

        [Test]
        public void RuntimeSunGeometry_TracksEclipseAcrossOrbit()
        {
            Assert.That(Observe(100.0).Condition, Is.EqualTo(SunlightCondition.Sunlit));
            Assert.That(Observe(1800.0).Condition, Is.EqualTo(SunlightCondition.Umbra));
            Assert.That(Observe(4000.0).Condition, Is.EqualTo(SunlightCondition.Sunlit));
        }

        [TestCase(100.0)]
        [TestCase(3500.0)]
        public void NadirAttitude_BodyZSunComponentMatchesGeometry(double t)
        {
            SpacecraftState state = Step(t);
            SunObservation sun = Observe(t);

            double expected = -Vector3d.Dot(
                state.PositionEcefMeters.Normalized(),
                sun.SunDirectionItrf93);
            Assert.That(sun.SunDirectionBody.Z, Is.EqualTo(expected).Within(1e-12));
            Assert.That(sun.SunDirectionBody.Magnitude, Is.EqualTo(1.0).Within(1e-12));
        }

        [Test]
        public void RotatingSpacecraft_RotatesBodySunDirectionOnly()
        {
            SpacecraftState state = Step(100.0);
            Assert.That(_ephemeris.TryGetSample(100.0, out EphemerisSample environment), Is.True,
                _ephemeris.LastError);
            Quaterniond offset = Quaterniond.FromAxisAngle(new Vector3d(0.0, 0.0, 1.0), Math.PI / 2.0);

            SunObservation before = SolarGeometry.Observe(state, environment);
            SunObservation after = SolarGeometry.Observe(
                WithAttitude(state, state.BodyToEcef * offset),
                environment);

            AssertClose(after.SunDirectionItrf93, before.SunDirectionItrf93, 0.0);
            AssertClose(
                after.SunDirectionBody,
                new Vector3d(before.SunDirectionBody.Y, -before.SunDirectionBody.X, before.SunDirectionBody.Z),
                1e-12);
            Assert.That(after.IlluminationFraction, Is.EqualTo(before.IlluminationFraction));
        }

        [Test]
        public void Observe_RejectsStateAndEnvironmentFromDifferentTimes()
        {
            Assert.That(_ephemeris.TryGetSample(100.1, out EphemerisSample environment), Is.True,
                _ephemeris.LastError);
            Assert.Throws<ArgumentException>(() => SolarGeometry.Observe(Step(100.0), environment));
        }

        private SpacecraftState Step(double t)
        {
            Assert.That(_engine.TryStep(
                new SimulationStepInput(0, t, ActuatorCommandSet.None(0, t)),
                out SimulationSnapshot snapshot), Is.True);
            return snapshot.Spacecraft;
        }

        private SunObservation Observe(double t)
        {
            Assert.That(_ephemeris.TryGetSample(t, out EphemerisSample environment), Is.True,
                _ephemeris.LastError);
            return SolarGeometry.Observe(Step(t), environment);
        }

        private static SpacecraftState WithAttitude(SpacecraftState state, Quaterniond bodyToEcef) =>
            new SpacecraftState(
                state.Sequence,
                state.SimulationTimeSeconds,
                state.TimestampUtc,
                state.PositionEcefMeters,
                state.VelocityEcefMetersPerSecond,
                bodyToEcef,
                state.AngularVelocityBodyRadiansPerSecond);

        private static void AssertClose(Vector3d actual, Vector3d expected, double tolerance)
        {
            Assert.That((actual - expected).Magnitude, Is.LessThanOrEqualTo(tolerance),
                $"{actual} vs {expected}");
        }
    }
}
