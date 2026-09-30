using System.Collections;
using Argus.Simulation.Core;
using Argus.Simulation.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Argus.Simulation.Tests
{
    public sealed class SimulationSensorRuntimePlayModeTests
    {
        [UnityTest]
        public IEnumerator RuntimePublishesTruthBackedBodyRateAndResetsWithSimulation()
        {
            GameObject simulation = new GameObject("Sensor Runtime Test");
            AnalyticStateSource source = simulation.AddComponent<AnalyticStateSource>();
            SimulationRunner runner = simulation.AddComponent<SimulationRunner>();
            runner.Configure(source);
            SimulationSensorRuntime sensors = simulation.AddComponent<SimulationSensorRuntime>();
            sensors.Configure(runner);

            Assert.That(runner.StepOnce(), Is.True);
            string firstRunId = sensors.RunId;
            Assert.That(firstRunId, Is.Not.Null.And.EqualTo(runner.LastState.RunId));

            Assert.That(sensors.HasOutput, Is.True);
            Assert.That(sensors.Latest.IsValid, Is.True);
            Assert.That(
                sensors.Latest.TryGetFrame(
                    SimulationSensorRuntime.BodyRateSensorId,
                    out SensorFrame<AngularRateMeasurement> firstFrame),
                Is.True);
            Assert.That(firstFrame.Status, Is.EqualTo(SensorFrameStatus.Valid));
            Assert.That(firstFrame.Sequence, Is.EqualTo(0));
            Assert.That(
                firstFrame.Payload.AngularVelocitySensorRadiansPerSecond,
                Is.EqualTo(runner.LastState.Spacecraft.AngularVelocityBodyRadiansPerSecond));

            runner.ResetSimulation();
            Assert.That(sensors.HasOutput, Is.False);
            Assert.That(runner.StepOnce(), Is.True);
            Assert.That(runner.LastState.RunId, Is.Not.EqualTo(firstRunId));
            Assert.That(runner.LastState.Spacecraft.Sequence, Is.EqualTo(0));
            Assert.That(sensors.RunId, Is.EqualTo(runner.LastState.RunId));
            Assert.That(
                sensors.Latest.TryGetFrame(
                    SimulationSensorRuntime.BodyRateSensorId,
                    out SensorFrame<AngularRateMeasurement> resetFrame),
                Is.True);
            Assert.That(resetFrame.Sequence, Is.EqualTo(0));
            Assert.That(resetFrame.RunId, Is.EqualTo(runner.LastState.RunId));

            Object.Destroy(simulation);
            yield return null;

            LogAssert.NoUnexpectedReceived();
        }
    }
}
