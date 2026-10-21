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
        public void Sample_ReportsBodyRateImpliedByAttitude()
        {
            const double time = 1234.5;
            const double halfStep = 0.5;
            CircularOrbitModel model = new CircularOrbitModel(DateTimeOffset.UnixEpoch, 500_000.0, 51.6, 30.0, 10.0);

            SpacecraftState state = model.Sample(0, time);
            SpacecraftState before = model.Sample(0, time - halfStep);
            SpacecraftState after = model.Sample(0, time + halfStep);

            // Body axes in the Earth-fixed frame turn at omega_BE; adding Earth's spin gives omega_BN.
            Vector3d[] axes = BodyAxes(state);
            Vector3d[] axesBefore = BodyAxes(before);
            Vector3d[] axesAfter = BodyAxes(after);
            Vector3d[] rates = new Vector3d[3];
            for (int i = 0; i < 3; i++)
            {
                rates[i] = (axesAfter[i] - axesBefore[i]) / (2.0 * halfStep);
            }

            double earthRate = CircularOrbitModel.EarthRotationRadiansPerSecond;
            Vector3d implied = new Vector3d(
                Vector3d.Dot(rates[1], axes[2]) + earthRate * axes[0].Z,
                Vector3d.Dot(rates[2], axes[0]) + earthRate * axes[1].Z,
                Vector3d.Dot(rates[0], axes[1]) + earthRate * axes[2].Z);

            // Central-difference error is about n^3 h^2 / 6 (~1e-10 rad/s); n is about 1.1e-3 rad/s.
            Vector3d reported = state.AngularVelocityBodyRadiansPerSecond;
            Assert.That((reported - implied).Magnitude, Is.LessThan(1e-9), $"reported {reported}, implied {implied}");
        }

        private static Vector3d[] BodyAxes(SpacecraftState state) => new[]
        {
            state.BodyToEcef.Rotate(new Vector3d(1.0, 0.0, 0.0)),
            state.BodyToEcef.Rotate(new Vector3d(0.0, 1.0, 0.0)),
            state.BodyToEcef.Rotate(new Vector3d(0.0, 0.0, 1.0))
        };
    }
}
