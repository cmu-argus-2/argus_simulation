using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Argus.Simulation.Core;
using Argus.Simulation.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Argus.Simulation.Tests
{
    public sealed class AnalyticOrbitStateSourcePlayModeTests
    {
        private const string SpacecraftReferencePath =
            "Assets/ArgusSimulation/Tests/Fixtures/foundation_one_orbit_spacecraft_reference.json";

        private GameObject _simulation;

        [TearDown]
        public void DestroySimulation()
        {
            if (_simulation != null)
            {
                Object.Destroy(_simulation);
            }
        }

        [Test]
        public void DefaultSource_UsesSpiceAndMatchesReferenceStates()
        {
            AnalyticOrbitStateSource source = CreateSource();
            Dictionary<string, object> root = (Dictionary<string, object>)JsonReader.Parse(
                File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), SpacecraftReferencePath)));

            foreach (object item in (List<object>)root["samples"])
            {
                Dictionary<string, object> row = (Dictionary<string, object>)item;
                double t = (double)row["t"];

                Assert.That(source.TryGetState(0, t, out SpacecraftState state), Is.True, $"t = {t}");
                Assert.That((state.PositionEcefMeters - Vector(row["position_itrf93_m"])).Magnitude, Is.LessThan(1e-4));
                Assert.That(
                    (state.VelocityEcefMetersPerSecond - Vector(row["velocity_itrf93_m_s"])).Magnitude,
                    Is.LessThan(1e-7));
            }

            Assert.That(source.Ephemeris, Is.Not.Null);
            Assert.That(source.Ephemeris.SourceName, Does.StartWith("spice-reference:foundation_one_orbit"));
        }

        [Test]
        public void RunnerReset_ReproducesStates_AndFutureQueriesDoNotAdvanceIt()
        {
            AnalyticOrbitStateSource source = CreateSource();
            SimulationRunner runner = _simulation.AddComponent<SimulationRunner>();
            runner.Configure(source);
            runner.IsRunning = false;

            List<SpacecraftState> first = StepMany(runner, 100);
            double timeBeforeQuery = runner.SimulationTimeSeconds;
            Assert.That(source.TryGetState(9_999, 3_000.0, out _), Is.True);
            Assert.That(runner.SimulationTimeSeconds, Is.EqualTo(timeBeforeQuery));

            runner.ResetSimulation();
            List<SpacecraftState> second = StepMany(runner, 100);

            for (int index = 0; index < first.Count; index++)
            {
                Assert.That(second[index].PositionEcefMeters, Is.EqualTo(first[index].PositionEcefMeters));
                Assert.That(second[index].VelocityEcefMetersPerSecond, Is.EqualTo(first[index].VelocityEcefMetersPerSecond));
                Assert.That(second[index].TimestampUtc, Is.EqualTo(first[index].TimestampUtc));
            }
        }

        [Test]
        public void Source_ReturnsNoStateBeyondEphemerisCoverage()
        {
            AnalyticOrbitStateSource source = CreateSource();

            Assert.That(source.TryGetState(0, 5_680.0, out _), Is.True);
            Assert.That(source.TryGetState(1, 5_680.1, out _), Is.False);
        }

        [Test]
        public void MissingSpiceFile_LogsOneErrorAndProducesNoState()
        {
            AnalyticOrbitStateSource source = CreateSource();
            source.SpiceEphemerisFile = "Argus/Spice/does_not_exist.json";

            LogAssert.Expect(LogType.Error, new Regex("Could not load SPICE ephemeris"));
            Assert.That(source.TryGetState(0, 0.0, out _), Is.False);
            // The failure is cached: a second request must not log again (LogAssert fails on extra errors).
            Assert.That(source.TryGetState(1, 0.1, out _), Is.False);
            Assert.That(source.Ephemeris, Is.Null);
        }

        [Test]
        public void SpiceDisabled_FallsBackToSimplifiedEarthRotation()
        {
            AnalyticOrbitStateSource source = CreateSource();
            Assert.That(source.TryGetState(0, 0.0, out SpacecraftState spice), Is.True);

            source.UseSpiceEphemeris = false;
            Assert.That(source.TryGetState(0, 0.0, out SpacecraftState simplified), Is.True);

            Assert.That(source.Ephemeris, Is.Null);
            Assert.That(source.TryGetSunObservation(simplified, out _), Is.False);
            Assert.That(Longitude(simplified.PositionEcefMeters), Is.EqualTo(0.0).Within(1e-9));
            Assert.That(Longitude(spice.PositionEcefMeters), Is.EqualTo(-114.378).Within(0.01));
        }

        private static double Longitude(Vector3d ecef) => System.Math.Atan2(ecef.Y, ecef.X) * 180.0 / System.Math.PI;

        private AnalyticOrbitStateSource CreateSource()
        {
            _simulation = new GameObject("SPICE State Source Test");
            return _simulation.AddComponent<AnalyticOrbitStateSource>();
        }

        private static List<SpacecraftState> StepMany(SimulationRunner runner, int count)
        {
            List<SpacecraftState> states = new List<SpacecraftState>(count);
            for (int index = 0; index < count; index++)
            {
                Assert.That(runner.StepOnce(), Is.True);
                states.Add(runner.LastState);
            }

            return states;
        }

        private static Vector3d Vector(object value)
        {
            List<object> values = (List<object>)value;
            return new Vector3d((double)values[0], (double)values[1], (double)values[2]);
        }
    }
}
