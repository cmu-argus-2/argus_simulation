using System;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class CircularOrbitModelTests
    {
        [Test]
        public void Sample_MaintainsConfiguredOrbitalRadius()
        {
            const double altitudeMeters = 500_000.0;
            CircularOrbitModel model = new CircularOrbitModel(
                DateTimeOffset.UnixEpoch,
                altitudeMeters,
                51.6,
                0.0,
                0.0);

            SpacecraftState state = model.Sample(42, 1234.5);

            Assert.That(state.IsValid, Is.True);
            Assert.That(
                state.PositionEcefMeters.Magnitude,
                Is.EqualTo(CircularOrbitModel.EarthEquatorialRadiusMeters + altitudeMeters).Within(1e-6));
            Assert.That(state.Sequence, Is.EqualTo(42));
        }

        [Test]
        public void Sample_MatchesPreRefactorEarthFixedState()
        {
            // Golden values from the original single-method implementation (commit cf1b129).
            CircularOrbitModel model = new CircularOrbitModel(DateTimeOffset.UnixEpoch, 500_000.0, 51.6, 0.0, 0.0);

            SpacecraftState state = model.Sample(0, 1234.5);

            AssertClose(state.PositionEcefMeters, new Vector3d(1767033.6356731234, 4040845.8381043132, 5278060.783454835), 1e-6);
            AssertClose(state.VelocityEcefMetersPerSecond, new Vector3d(-7042.863117179016, 1497.506792834037, 1211.3884607360433), 1e-9);
        }

        [Test]
        public void SampleJ2000_IsCircularAtOrbitalSpeed()
        {
            const double altitudeMeters = 500_000.0;
            double radius = CircularOrbitModel.EarthEquatorialRadiusMeters + altitudeMeters;
            CircularOrbitModel model = new CircularOrbitModel(DateTimeOffset.UnixEpoch, altitudeMeters, 51.6, 30.0, 10.0);

            model.SampleJ2000(987.6, out Vector3d position, out Vector3d velocity);

            Assert.That(position.Magnitude, Is.EqualTo(radius).Within(1e-6));
            Assert.That(
                velocity.Magnitude,
                Is.EqualTo(Math.Sqrt(CircularOrbitModel.EarthGravitationalParameter / radius)).Within(1e-9));
            Assert.That(Vector3d.Dot(position, velocity), Is.EqualTo(0.0).Within(1e-3));
        }

        [Test]
        public void SampleJ2000_IsUnaffectedByEarthRotation()
        {
            // After one orbital period the inertial position returns to its start; Earth spin plays no part.
            CircularOrbitModel model = new CircularOrbitModel(DateTimeOffset.UnixEpoch, 500_000.0, 51.6, 0.0, 0.0);
            double period = 2.0 * Math.PI / model.MeanMotionRadiansPerSecond;

            model.SampleJ2000(0.0, out Vector3d start, out _);
            model.SampleJ2000(period, out Vector3d afterOneOrbit, out _);

            AssertClose(afterOneOrbit, start, 1e-6);
        }

        private static void AssertClose(Vector3d actual, Vector3d expected, double tolerance)
        {
            Assert.That((actual - expected).Magnitude, Is.LessThan(tolerance), $"{actual} vs {expected}");
        }
    }
}
