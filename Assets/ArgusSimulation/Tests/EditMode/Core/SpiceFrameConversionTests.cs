using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class SpiceFrameConversionTests
    {
        private static readonly DateTimeOffset Epoch =
            new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero);

        private SpiceRuntimeEphemerisProvider _ephemeris;

        [OneTimeSetUp]
        public void StartRuntime()
        {
            _ephemeris = new SpiceRuntimeEphemerisProvider(
                Epoch,
                Path.Combine(Directory.GetCurrentDirectory(), "Argus.Spice", "run_runtime.sh"));
        }

        [OneTimeTearDown]
        public void StopRuntime() => _ephemeris?.Dispose();

        [TestCase(0.0)]
        [TestCase(5.3)]
        [TestCase(2837.25)]
        [TestCase(86_400.0)]
        public void RuntimeFrameTransform_RoundTripsPositionAndVelocity(double t)
        {
            Assert.That(_ephemeris.TryGetSample(t, out EphemerisSample environment), Is.True,
                _ephemeris.LastError);
            CircularOrbitModel orbit = new CircularOrbitModel(Epoch, 500_000.0, 51.6, 0.0, 0.0);
            orbit.SampleJ2000(t, out Vector3d position, out Vector3d velocity);

            environment.TransformStateToItrf93(
                position,
                velocity,
                out Vector3d positionItrf93,
                out Vector3d velocityItrf93);
            environment.TransformStateToJ2000(
                positionItrf93,
                velocityItrf93,
                out Vector3d positionBack,
                out Vector3d velocityBack);

            Assert.That((positionBack - position).Magnitude, Is.LessThan(1e-7));
            Assert.That((velocityBack - velocity).Magnitude, Is.LessThan(1e-8));
        }

        [Test]
        public void RuntimeProvider_HasNoFormerScenarioBoundary()
        {
            Assert.That(_ephemeris.TryGetSample(5_680.1, out _), Is.True, _ephemeris.LastError);
            Assert.That(_ephemeris.TryGetSample(86_400.0, out _), Is.True, _ephemeris.LastError);
        }

        [Test]
        public void SpiceConversion_ShiftsLongitudeByEarthOrientationAtEpoch()
        {
            SpacecraftState simplified = BuildEngineStep(null, 0.0);
            SpacecraftState spice = BuildEngineStep(_ephemeris, 0.0);

            double shift = Longitude(spice.PositionEcefMeters) - Longitude(simplified.PositionEcefMeters);
            double wrapped = ((shift % 360.0) + 540.0) % 360.0 - 180.0;

            Assert.That(wrapped, Is.EqualTo(-114.378).Within(0.01));
            Assert.That(spice.PositionEcefMeters.Magnitude,
                Is.EqualTo(simplified.PositionEcefMeters.Magnitude).Within(1e-6));
        }

        [Test]
        public void Engine_PreservesOutputContract()
        {
            SpacecraftState state = BuildEngineStep(_ephemeris, 1234.5);

            Assert.That(state.IsValid, Is.True);
            Assert.That(state.BodyToEcef.IsUnit, Is.True);
            Assert.That(state.PositionEcefMeters.Magnitude, Is.EqualTo(6_878_137.0).Within(1e-6));
            Assert.That(state.VelocityEcefMetersPerSecond.Magnitude, Is.InRange(7_000.0, 8_000.0));
        }

        [Test]
        public void Gateway_ResetReproducesRuntimeSpiceStatesExactly()
        {
            SimulationGateway gateway = new SimulationGateway(
                new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0, _ephemeris));
            gateway.Initialize(new SimulationConfiguration("spice-reset", Epoch, 0.1));

            List<SpacecraftState> first = Enumerable.Range(0, 25).Select(_ => gateway.Step().Spacecraft).ToList();
            gateway.Reset();
            List<SpacecraftState> second = Enumerable.Range(0, 25).Select(_ => gateway.Step().Spacecraft).ToList();

            for (int index = 0; index < first.Count; index++)
            {
                Assert.That(second[index].PositionEcefMeters, Is.EqualTo(first[index].PositionEcefMeters));
                Assert.That(second[index].VelocityEcefMetersPerSecond,
                    Is.EqualTo(first[index].VelocityEcefMetersPerSecond));
            }
        }

        [Test]
        public void Initialize_RejectsRuntimeWithDifferentEpoch()
        {
            AnalyticSimulationEngine engine =
                new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0, _ephemeris);

            Assert.Throws<ArgumentException>(() =>
                engine.Initialize(new SimulationConfiguration("wrong-epoch", Epoch.AddSeconds(1.0), 0.1)));
        }

        [Test]
        public void ProtocolParser_RejectsOutOfSequenceResponses()
        {
            const string response =
                "SAMPLE\t9\t1\t1\t0\t0\t0\t1\t0\t0\t0\t1\t0\t0\t0\t0\t0\t0\t0\t0\t0\t1\t2\t3";

            Assert.That(SpiceRuntimeEphemerisProvider.TryParseSampleResponse(
                response, 8, 0.0, Epoch, out _, out string error), Is.False);
            Assert.That(error, Does.Contain("out-of-sequence"));
        }

        private static AnalyticSimulationEngine BuildEngine(IEphemerisProvider ephemeris)
        {
            AnalyticSimulationEngine engine =
                new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0, ephemeris);
            engine.Initialize(new SimulationConfiguration("spice-test", Epoch, 0.1));
            return engine;
        }

        private static SpacecraftState BuildEngineStep(IEphemerisProvider ephemeris, double t)
        {
            Assert.That(BuildEngine(ephemeris).TryStep(Input(0, t), out SimulationSnapshot snapshot), Is.True);
            return snapshot.Spacecraft;
        }

        private static SimulationStepInput Input(long sequence, double t) =>
            new SimulationStepInput(sequence, t, ActuatorCommandSet.None(sequence, t));

        private static double Longitude(Vector3d ecef) =>
            Math.Atan2(ecef.Y, ecef.X) * 180.0 / Math.PI;
    }
}
