using Argus.Simulation.Core;
using Argus.Simulation.Unity;
using NUnit.Framework;
using UnityEngine;

namespace Argus.Simulation.Tests
{
    public sealed class SunGeometryPlayModeTests
    {
        private GameObject _simulation;
        private AnalyticOrbitStateSource _source;
        private SimulationRunner _runner;
        private MockSensorSuite _sensors;

        [SetUp]
        public void BuildPipeline()
        {
            _simulation = new GameObject("Simulation");
            _source = _simulation.AddComponent<AnalyticOrbitStateSource>();
            _runner = _simulation.AddComponent<SimulationRunner>();
            _runner.Configure(_source);
            _runner.IsRunning = false;
            _sensors = _simulation.AddComponent<MockSensorSuite>();
            _sensors.Configure(_runner);
        }

        [TearDown]
        public void DestroyPipeline()
        {
            Object.DestroyImmediate(_simulation);
        }

        [Test]
        public void Sensors_ReportUmbraDuringEclipseAndSunlightAfter()
        {
            _runner.Configure(_source, 100.0);
            _runner.IsRunning = false;

            Assert.That(_runner.StepOnce(), Is.True);
            Assert.That(_sensors.Latest.SunlightCondition, Is.EqualTo(SunlightCondition.Sunlit));

            while (_runner.SimulationTimeSeconds <= 1_800.0)
            {
                Assert.That(_runner.StepOnce(), Is.True);
            }

            Assert.That(_runner.LastState.SimulationTimeSeconds, Is.EqualTo(1_800.0).Within(1e-6));
            Assert.That(_sensors.Latest.SunlightCondition, Is.EqualTo(SunlightCondition.Umbra));
            Assert.That(_sensors.Latest.SunVectorBody.Magnitude, Is.EqualTo(0.0));
            Assert.That(_sensors.Latest.SolarPowerWatts, Is.EqualTo(0.0));

            while (_runner.SimulationTimeSeconds <= 4_000.0)
            {
                Assert.That(_runner.StepOnce(), Is.True);
            }

            Assert.That(_sensors.Latest.SunlightCondition, Is.EqualTo(SunlightCondition.Sunlit));
        }
    }
}
