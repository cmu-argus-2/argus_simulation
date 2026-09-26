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
        private const string EphemerisPath = "Assets/StreamingAssets/Argus/Spice/foundation_one_orbit.json";
        private const string SpacecraftReferencePath =
            "Assets/ArgusSimulation/Tests/Fixtures/foundation_one_orbit_spacecraft_reference.json";

        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero);

        private static TabulatedEphemerisProvider _ephemeris;
        private static List<ReferenceState> _reference;

        [OneTimeSetUp]
        public void LoadData()
        {
            _ephemeris = SpiceReferenceFile.Load(ReadProjectFile(EphemerisPath));
            _reference = ReferenceState.Load(ReadProjectFile(SpacecraftReferencePath));
        }

        [Test]
        public void Engine_MatchesExactSpiceTransformAtKnotAndBetweenKnotTimes()
        {
            AnalyticSimulationEngine engine = BuildEngine(_ephemeris);
            double worstPosition = 0.0;
            double worstVelocity = 0.0;

            foreach (ReferenceState expected in _reference)
            {
                Assert.That(engine.TryStep(Input(0, expected.T), out SimulationSnapshot snapshot), Is.True);
                SpacecraftState state = snapshot.Spacecraft;

                worstPosition = Math.Max(worstPosition, (state.PositionEcefMeters - expected.PositionItrf93).Magnitude);
                worstVelocity = Math.Max(
                    worstVelocity,
                    (state.VelocityEcefMetersPerSecond - expected.VelocityItrf93).Magnitude);
                Assert.That(state.TimestampUtc, Is.EqualTo(Epoch.AddSeconds(expected.T)));
            }

            TestContext.WriteLine($"Worst error vs exact SPICE: {worstPosition:E2} m, {worstVelocity:E2} m/s");
            Assert.That(worstPosition, Is.LessThan(1e-4), $"worst position error {worstPosition:E2} m");
            Assert.That(worstVelocity, Is.LessThan(1e-7), $"worst velocity error {worstVelocity:E2} m/s");
        }

        [Test]
        public void OrbitModel_J2000StateMatchesReference()
        {
            CircularOrbitModel model = new CircularOrbitModel(Epoch, 500_000.0, 51.6, 0.0, 0.0);

            foreach (ReferenceState expected in _reference)
            {
                model.SampleJ2000(expected.T, out Vector3d position, out Vector3d velocity);
                Assert.That((position - expected.PositionJ2000).Magnitude, Is.LessThan(1e-6));
                Assert.That((velocity - expected.VelocityJ2000).Magnitude, Is.LessThan(1e-9));
            }
        }

        [Test]
        public void SpiceConversion_ShiftsLongitudeByEarthRotationAngleAtEpoch()
        {
            // The simplified model assumes a zero Earth rotation angle at the epoch; SPICE gives ~114.38 deg.
            SpacecraftState simplified = BuildEngineStep(null, 0.0);
            SpacecraftState spice = BuildEngineStep(_ephemeris, 0.0);

            double shift = Longitude(spice.PositionEcefMeters) - Longitude(simplified.PositionEcefMeters);
            double wrapped = ((shift % 360.0) + 540.0) % 360.0 - 180.0;

            Assert.That(wrapped, Is.EqualTo(-114.378).Within(0.01));
            Assert.That(spice.PositionEcefMeters.Magnitude, Is.EqualTo(simplified.PositionEcefMeters.Magnitude).Within(1e-6));
        }

        [Test]
        public void Engine_PreservesOutputContract()
        {
            SpacecraftState state = BuildEngineStep(_ephemeris, 1234.5);

            Assert.That(state.IsValid, Is.True);
            Assert.That(state.BodyToEcef.IsUnit, Is.True);
            Assert.That(state.PositionEcefMeters.Magnitude, Is.EqualTo(6_878_137.0).Within(1e-6));
            // Earth-fixed LEO speed is inertial speed minus roughly omega x r.
            Assert.That(state.VelocityEcefMetersPerSecond.Magnitude, Is.InRange(7_000.0, 8_000.0));
        }

        [Test]
        public void Gateway_ResetReproducesSpiceStatesExactly()
        {
            SimulationGateway gateway = new SimulationGateway(
                new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0, _ephemeris));
            gateway.Initialize(new SimulationConfiguration("spice-reset", Epoch, 0.1));

            List<SpacecraftState> first = Enumerable.Range(0, 250).Select(_ => gateway.Step().Spacecraft).ToList();
            gateway.Reset();
            List<SpacecraftState> second = Enumerable.Range(0, 250).Select(_ => gateway.Step().Spacecraft).ToList();

            for (int index = 0; index < first.Count; index++)
            {
                Assert.That(second[index].Sequence, Is.EqualTo(first[index].Sequence));
                Assert.That(second[index].SimulationTimeSeconds, Is.EqualTo(first[index].SimulationTimeSeconds));
                Assert.That(second[index].PositionEcefMeters, Is.EqualTo(first[index].PositionEcefMeters));
                Assert.That(second[index].VelocityEcefMetersPerSecond, Is.EqualTo(first[index].VelocityEcefMetersPerSecond));
            }
        }

        [Test]
        public void Initialize_RejectsEphemerisWithDifferentEpoch()
        {
            AnalyticSimulationEngine engine = new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0, _ephemeris);

            Assert.Throws<ArgumentException>(() =>
                engine.Initialize(new SimulationConfiguration("wrong-epoch", Epoch.AddSeconds(1.0), 0.1)));
        }

        [Test]
        public void Step_PastEphemerisCoverage_FailsInsteadOfExtrapolating()
        {
            AnalyticSimulationEngine engine = BuildEngine(_ephemeris);
            Assert.That(engine.TryStep(Input(0, _ephemeris.CoverageEndSeconds), out _), Is.True);
            Assert.That(engine.TryStep(Input(1, _ephemeris.CoverageEndSeconds + 0.1), out _), Is.False);

            SimulationGateway gateway = new SimulationGateway(
                new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0, _ephemeris));
            gateway.Initialize(new SimulationConfiguration("coverage", Epoch, 5680.0));
            gateway.Step();
            gateway.Step();
            Assert.Throws<InvalidOperationException>(() => gateway.Step());
        }

        private static AnalyticSimulationEngine BuildEngine(IEphemerisProvider ephemeris)
        {
            AnalyticSimulationEngine engine = new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0, ephemeris);
            engine.Initialize(new SimulationConfiguration("spice-test", Epoch, 0.1));
            return engine;
        }

        private static SpacecraftState BuildEngineStep(IEphemerisProvider ephemeris, double t)
        {
            BuildEngine(ephemeris).TryStep(Input(0, t), out SimulationSnapshot snapshot);
            return snapshot.Spacecraft;
        }

        private static SimulationStepInput Input(long sequence, double t) =>
            new SimulationStepInput(sequence, t, ActuatorCommandSet.None(sequence, t));

        private static double Longitude(Vector3d ecef) => Math.Atan2(ecef.Y, ecef.X) * 180.0 / Math.PI;

        private static string ReadProjectFile(string relativePath) =>
            File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), relativePath));

        private readonly struct ReferenceState
        {
            private ReferenceState(Dictionary<string, object> row)
            {
                T = (double)row["t"];
                PositionJ2000 = Vector(row["position_j2000_m"]);
                VelocityJ2000 = Vector(row["velocity_j2000_m_s"]);
                PositionItrf93 = Vector(row["position_itrf93_m"]);
                VelocityItrf93 = Vector(row["velocity_itrf93_m_s"]);
            }

            public double T { get; }
            public Vector3d PositionJ2000 { get; }
            public Vector3d VelocityJ2000 { get; }
            public Vector3d PositionItrf93 { get; }
            public Vector3d VelocityItrf93 { get; }

            public static List<ReferenceState> Load(string json)
            {
                Dictionary<string, object> root = (Dictionary<string, object>)JsonReader.Parse(json);
                Assert.That(root["format"], Is.EqualTo("argus.spice.spacecraft_reference"));
                return ((List<object>)root["samples"])
                    .Select(row => new ReferenceState((Dictionary<string, object>)row))
                    .ToList();
            }

            private static Vector3d Vector(object value)
            {
                List<object> values = (List<object>)value;
                return new Vector3d((double)values[0], (double)values[1], (double)values[2]);
            }
        }
    }
}
