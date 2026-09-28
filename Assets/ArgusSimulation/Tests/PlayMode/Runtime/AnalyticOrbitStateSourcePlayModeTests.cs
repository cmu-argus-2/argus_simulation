using System.Collections.Generic;
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
        private GameObject _simulation;

        [TearDown]
        public void DestroySimulation()
        {
            if (_simulation != null)
            {
                Object.DestroyImmediate(_simulation);
            }
        }

        [Test]
        public void DefaultSource_UsesLiveSpiceBeyondFormerDatasetBoundary()
        {
            AnalyticOrbitStateSource source = CreateSource();

            Assert.That(source.TryGetState(0, 0.0, out SpacecraftState initial), Is.True);
            Assert.That(source.TryGetState(1, 86_400.0, out SpacecraftState nextDay), Is.True);
            Assert.That(initial.TimestampUtc,
                Is.EqualTo(new System.DateTimeOffset(2025, 1, 15, 0, 0, 0, System.TimeSpan.Zero)));
            Assert.That(nextDay.TimestampUtc, Is.EqualTo(initial.TimestampUtc.AddDays(1.0)));
            Assert.That(source.Ephemeris, Is.Not.Null);
            Assert.That(source.Ephemeris.SourceName, Is.EqualTo("spice-runtime"));
        }

        [Test]
        public void RunnerReset_ReproducesStates_AndFutureQueriesDoNotAdvanceIt()
        {
            AnalyticOrbitStateSource source = CreateSource();
            SimulationRunner runner = _simulation.AddComponent<SimulationRunner>();
            runner.Configure(source);
            runner.IsRunning = false;

            List<SpacecraftState> first = StepMany(runner, 20);
            double timeBeforeQuery = runner.SimulationTimeSeconds;
            Assert.That(source.TryGetState(9_999, 86_400.0, out _), Is.True);
            Assert.That(runner.SimulationTimeSeconds, Is.EqualTo(timeBeforeQuery));

            runner.ResetSimulation();
            List<SpacecraftState> second = StepMany(runner, 20);

            for (int index = 0; index < first.Count; index++)
            {
                Assert.That(second[index].PositionEcefMeters, Is.EqualTo(first[index].PositionEcefMeters));
                Assert.That(second[index].VelocityEcefMetersPerSecond,
                    Is.EqualTo(first[index].VelocityEcefMetersPerSecond));
                Assert.That(second[index].TimestampUtc, Is.EqualTo(first[index].TimestampUtc));
            }
        }

        [Test]
        public void MissingSpiceRuntime_LogsOneErrorAndProducesNoState()
        {
            AnalyticOrbitStateSource source = CreateSource();
            source.SpiceRuntimeLauncher = "Argus.Spice/does-not-exist.sh";

            LogAssert.Expect(LogType.Error, new Regex("Could not start SPICE runtime"));
            Assert.That(source.TryGetState(0, 0.0, out _), Is.False);
            Assert.That(source.TryGetState(1, 0.1, out _), Is.False);
            Assert.That(source.Ephemeris, Is.Null);
        }

        [Test]
        public void SpiceDisabled_FallsBackToSimplifiedEarthRotation()
        {
            AnalyticOrbitStateSource source = CreateSource();
            source.UseSpiceEphemeris = false;

            Assert.That(source.TryGetState(0, 0.0, out SpacecraftState simplified), Is.True);
            Assert.That(source.Ephemeris, Is.Null);
            Assert.That(source.TryGetSunObservation(simplified, out _), Is.False);
            Assert.That(Longitude(simplified.PositionEcefMeters), Is.EqualTo(0.0).Within(1e-9));
        }

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

        private static double Longitude(Vector3d ecef) =>
            System.Math.Atan2(ecef.Y, ecef.X) * 180.0 / System.Math.PI;
    }
}
