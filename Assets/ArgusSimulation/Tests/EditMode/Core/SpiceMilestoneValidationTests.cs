using System;
using System.Collections.Generic;
using System.IO;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    // Milestone checks for the SPICE integration: frame round trips, velocity consistency,
    // units, and attitude conventions. Coverage, reset, and eclipse checks live alongside the
    // classes they exercise.
    public sealed class SpiceMilestoneValidationTests
    {
        private const string EphemerisPath = "Assets/StreamingAssets/Argus/Spice/foundation_one_orbit.json";
        private const double AltitudeMeters = 500_000.0;
        private const double InclinationDegrees = 51.6;

        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero);

        private static string _json;
        private static TabulatedEphemerisProvider _ephemeris;
        private static AnalyticSimulationEngine _engine;
        private static CircularOrbitModel _orbit;

        [OneTimeSetUp]
        public void Load()
        {
            _json = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), EphemerisPath));
            _ephemeris = SpiceReferenceFile.Load(_json);
            _engine = new AnalyticSimulationEngine(AltitudeMeters, InclinationDegrees, 0.0, 0.0, _ephemeris);
            _engine.Initialize(new SimulationConfiguration("validation", Epoch, 0.1));
            _orbit = new CircularOrbitModel(Epoch, AltitudeMeters, InclinationDegrees, 0.0, 0.0);
        }

        [TestCase(0.0)]
        [TestCase(5.3)]
        [TestCase(2837.25)]
        [TestCase(5680.0)]
        public void FrameRoundTrip_J2000ToItrf93AndBack_RecoversState(double t)
        {
            _ephemeris.TryGetSample(t, out EphemerisSample environment);
            _orbit.SampleJ2000(t, out Vector3d position, out Vector3d velocity);

            environment.TransformStateToItrf93(position, velocity, out Vector3d positionItrf93, out Vector3d velocityItrf93);
            environment.TransformStateToJ2000(positionItrf93, velocityItrf93, out Vector3d positionBack, out Vector3d velocityBack);

            // The velocity round trip is exact only when R and dR/dt are mutually consistent. At knots
            // (SPICE values) it is < 1e-10 m/s; between knots the Hermite-interpolated dR/dt differs
            // from d(R)/dt of an exactly orthonormal R by ~1e-12 relative, i.e. a few nm/s here.
            Assert.That((positionBack - position).Magnitude, Is.LessThan(1e-7), "position round trip (m)");
            Assert.That((velocityBack - velocity).Magnitude, Is.LessThan(1e-8), "velocity round trip (m/s)");
        }

        [TestCase(12.34)]
        [TestCase(2837.25)]
        [TestCase(5000.05)]
        public void Itrf93Velocity_MatchesDerivativeOfItrf93Position(double t)
        {
            const double h = 0.05;
            SpacecraftState state = Step(t);
            Vector3d numerical = (Step(t + h).PositionEcefMeters - Step(t - h).PositionEcefMeters) / (2.0 * h);

            Assert.That((state.VelocityEcefMetersPerSecond - numerical).Magnitude, Is.LessThan(1e-4));

            // Omitting the rotating-frame term (dR/dt * r) would be off by hundreds of m/s.
            _ephemeris.TryGetSample(t, out EphemerisSample environment);
            _orbit.SampleJ2000(t, out _, out Vector3d velocityJ2000);
            Vector3d rotationOnly = environment.J2000ToItrf93.Transform(velocityJ2000);
            Assert.That((rotationOnly - numerical).Magnitude, Is.GreaterThan(100.0));
        }

        [Test]
        public void Units_AltitudeInclinationPeriodAndTime()
        {
            Assert.That(Step(1234.5).PositionEcefMeters.Magnitude - CircularOrbitModel.EarthEquatorialRadiusMeters,
                Is.EqualTo(AltitudeMeters).Within(1e-6), "altitude in meters");

            double maxLatitude = 0.0;
            for (double t = 0.0; t <= 5680.0; t += 1.0)
            {
                _orbit.SampleJ2000(t, out Vector3d position, out _);
                maxLatitude = Math.Max(maxLatitude, Math.Asin(position.Z / position.Magnitude) * 180.0 / Math.PI);
            }

            Assert.That(maxLatitude, Is.EqualTo(InclinationDegrees).Within(0.01), "inclination in degrees");

            Dictionary<string, object> time = (Dictionary<string, object>)
                ((Dictionary<string, object>)JsonReader.Parse(_json))["time"];
            Assert.That(2.0 * Math.PI / _orbit.MeanMotionRadiansPerSecond,
                Is.EqualTo((double)time["orbit_period_seconds"]).Within(1e-9), "C# and Python agree on the period (s)");

            _ephemeris.TryGetSample(100.0, out EphemerisSample first);
            _ephemeris.TryGetSample(1234.567, out EphemerisSample second);
            Assert.That(second.EphemerisTimeSeconds - first.EphemerisTimeSeconds, Is.EqualTo(1134.567).Within(1e-6),
                "ephemeris time advances in SI seconds");
            Assert.That(second.TimestampUtc - first.TimestampUtc,
                Is.EqualTo(TimeSpan.FromSeconds(1134.567)).Within(TimeSpan.FromMilliseconds(1)));
        }

        [TestCase(0.0)]
        [TestCase(2000.0)]
        public void NadirAttitude_BodyAxesFollowDocumentedConvention(double t)
        {
            SpacecraftState state = Step(t);
            Quaterniond bodyToEcef = state.BodyToEcef;
            Vector3d x = bodyToEcef.Rotate(new Vector3d(1.0, 0.0, 0.0));
            Vector3d y = bodyToEcef.Rotate(new Vector3d(0.0, 1.0, 0.0));
            Vector3d z = bodyToEcef.Rotate(new Vector3d(0.0, 0.0, 1.0));

            AssertClose(z, (-state.PositionEcefMeters).Normalized(), 1e-12, "+Z is nadir");
            AssertClose(x, state.VelocityEcefMetersPerSecond.Normalized(), 1e-9, "+X along Earth-fixed velocity");
            AssertClose(Vector3d.Cross(x, y), z, 1e-12, "right-handed body frame");
            Assert.That(bodyToEcef.IsUnit, Is.True);
        }

        private static SpacecraftState Step(double t)
        {
            Assert.That(_engine.TryStep(
                new SimulationStepInput(0, t, ActuatorCommandSet.None(0, t)),
                out SimulationSnapshot snapshot), Is.True);
            return snapshot.Spacecraft;
        }

        private static void AssertClose(Vector3d actual, Vector3d expected, double tolerance, string message)
        {
            Assert.That((actual - expected).Magnitude, Is.LessThan(tolerance), $"{message}: {actual} vs {expected}");
        }
    }
}
