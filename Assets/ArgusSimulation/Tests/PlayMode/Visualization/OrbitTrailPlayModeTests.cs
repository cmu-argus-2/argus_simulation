using Argus.Simulation.Core;
using Argus.Simulation.Unity;
using CesiumForUnity;
using NUnit.Framework;
using UnityEngine;

namespace Argus.Simulation.Tests
{
    public sealed class OrbitTrailPlayModeTests
    {
        private GameObject _world;
        private GameObject _simulation;
        private GameObject _trailObject;

        [TearDown]
        public void DestroyObjects()
        {
            Object.DestroyImmediate(_trailObject);
            Object.DestroyImmediate(_simulation);
            Object.DestroyImmediate(_world);
        }

        [Test]
        public void BuildTrail_SamplesFutureStatesWithoutAdvancingLiveSimulation()
        {
            _world = new GameObject("Geospatial World");
            _world.AddComponent<CesiumGeoreference>();
            _simulation = new GameObject("Simulation");
            AnalyticOrbitStateSource source = _simulation.AddComponent<AnalyticOrbitStateSource>();
            SimulationRunner runner = _simulation.AddComponent<SimulationRunner>();
            runner.Configure(source);
            runner.IsRunning = false;

            int published = 0;
            runner.StateProduced += _ => published++;
            for (int index = 0; index < 5; index++)
            {
                Assert.That(runner.StepOnce(), Is.True);
            }

            SpacecraftState liveState = runner.LastState;
            double liveTime = runner.SimulationTimeSeconds;

            _trailObject = new GameObject("Orbit Trail");
            OrbitTrailRenderer trail = _trailObject.AddComponent<OrbitTrailRenderer>();
            trail.Configure(runner);
            trail.BuildTrail();

            LineRenderer line = _trailObject.GetComponent<LineRenderer>();
            Assert.That(line.positionCount, Is.EqualTo(240), "one full orbit fits in ephemeris coverage");
            Assert.That(runner.SimulationTimeSeconds, Is.EqualTo(liveTime));
            Assert.That(runner.LastState.Sequence, Is.EqualTo(liveState.Sequence));
            Assert.That(runner.LastState.PositionEcefMeters, Is.EqualTo(liveState.PositionEcefMeters));
            Assert.That(published, Is.EqualTo(5), "trail queries must not publish states to sensors or pose");

            // A higher orbit's period exceeds the one-orbit ephemeris: uncovered points are dropped.
            source.AltitudeMeters = 700_000.0;
            trail.BuildTrail();
            Assert.That(line.positionCount, Is.GreaterThan(0).And.LessThan(240));
            Vector3[] points = new Vector3[line.positionCount];
            line.GetPositions(points);
            Assert.That(points, Has.None.EqualTo(Vector3.zero));
        }
    }
}
