using System;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class OrbitLookaheadTests
    {
        [TestCase(0.0)]
        [TestCase(4.0)]
        [TestCase(240.0)]
        [TestCase(600.0)]
        public void PredictPositionEcef_MatchesCircularOrbitModel(double leadSeconds)
        {
            CircularOrbitModel model = new CircularOrbitModel(
                DateTimeOffset.UnixEpoch,
                500_000.0,
                51.6,
                30.0,
                10.0);
            const double nowSeconds = 1234.5;

            SpacecraftState now = model.Sample(0, nowSeconds);
            Vector3d expected = model.Sample(1, nowSeconds + leadSeconds).PositionEcefMeters;

            Vector3d predicted = OrbitLookahead.PredictPositionEcef(now, leadSeconds);

            Assert.That((predicted - expected).Magnitude, Is.LessThan(1e-3));
        }
    }
}
