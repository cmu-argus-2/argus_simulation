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
    }
}
