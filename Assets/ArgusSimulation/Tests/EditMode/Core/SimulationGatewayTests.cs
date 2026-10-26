using System;
using System.Collections.Generic;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class SimulationGatewayTests
    {
        [Test]
        public void ResetReturnsObservationZeroAndStepCommandKReturnsObservationKPlusOne()
        {
            SimulationGateway gateway = new SimulationGateway(Engine());
            List<SimulationState> states = new List<SimulationState>();
            gateway.StateProduced += states.Add;

            Observation first = gateway.Reset(7, Scenario(0.25));
            ActuatorCommandSet command = new ActuatorCommandSet(
                0,
                0.0,
                new Vector3d(0.001, -0.002, 0.003),
                new Vector3d(0.02, 0.0, -0.01),
                new Vector3d(0.0, 0.0, 0.0));
            Observation second = gateway.Step(command);

            Assert.That((first.Sequence, first.SimulationTimeSeconds, first.Status), Is.EqualTo((0L, 0.0, ObservationStatus.Ok)));
            Assert.That((second.Sequence, second.SimulationTimeSeconds), Is.EqualTo((1L, 0.25)));
            Assert.That(second.RunId, Is.EqualTo("test-run"));
            Assert.That(second.Source, Is.EqualTo("analytic-circular-orbit"));
            Assert.That(second.IsTerminal, Is.False);
            Assert.That((gateway.NextSequence, gateway.NextSimulationTimeSeconds), Is.EqualTo((1L, 0.25)));

            // Truth reaches StateProduced, not the observation: state 0 has no command, and
            // command 0 acted over [t_0, t_1) to produce state 1.
            Assert.That(states.Count, Is.EqualTo(2));
            Assert.That(states[0].HasAppliedCommands, Is.False);
            Assert.That(states[1].Spacecraft.Sequence, Is.EqualTo(1));
            Assert.That(states[1].AppliedCommands.Value.ReactionWheelTorqueBodyNewtonMeters,
                Is.EqualTo(command.ReactionWheelTorqueBodyNewtonMeters));
            Assert.That(second.TimestampUtc, Is.EqualTo(states[1].Spacecraft.TimestampUtc));
        }

        [Test]
        public void ObservationsHoldOnlyControllerVisibleFrames()
        {
            SimulationGateway gateway = new SimulationGateway(
                Engine(),
                sensor => new IdealBodyRateSensorModel(
                    sensor.Definition.SensorId,
                    sensor.Definition.FrameId,
                    sensor.Definition.SamplePeriodSeconds));
            List<SensorOutputSet> outputs = new List<SensorOutputSet>();
            gateway.SensorOutputProduced += outputs.Add;
            SimulationConfiguration scenario = Scenario(
                0.1,
                Rate("rate.visible", isControllerVisible: true),
                Rate("rate.truth", isControllerVisible: false));

            Observation[] observations = { gateway.Reset(1, scenario), gateway.Step() };

            for (int index = 0; index < observations.Length; index++)
            {
                Assert.That(observations[index].Frames.Count, Is.EqualTo(1));
                Assert.That(observations[index].TryGetFrame("rate.visible", out SensorFrame<AngularRateMeasurement> frame), Is.True);
                Assert.That(frame.Sequence, Is.EqualTo(observations[index].Sequence));
                Assert.That(observations[index].TryGetFrame("rate.truth", out SensorFrame<AngularRateMeasurement> _), Is.False);
                // The recorder still receives the ground-truth sensor's frame.
                Assert.That(outputs[index].Frames.Count, Is.EqualTo(2));
            }
        }

        [Test]
        public void ResetRestartsSequenceAndTime()
        {
            SimulationGateway gateway = new SimulationGateway(Engine());
            gateway.Reset(0, Scenario(0.5));
            gateway.Step();
            gateway.Step();

            Observation restarted = gateway.Reset(0, Scenario(0.5));

            Assert.That((restarted.Sequence, restarted.SimulationTimeSeconds), Is.EqualTo((0L, 0.0)));
            Assert.That(gateway.Step().Sequence, Is.EqualTo(1));
        }

        [Test]
        public void StepRejectsCommandForWrongSequence()
        {
            SimulationGateway gateway = new SimulationGateway(Engine());
            gateway.Reset(0, Scenario(0.1));

            Assert.Throws<ArgumentException>(() => gateway.Step(ActuatorCommandSet.None(4, 0.4)));
        }

        private static AnalyticSimulationEngine Engine() => new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0);

        private static SimulationConfiguration Scenario(double fixedStepSeconds, params SensorConfiguration[] sensors) =>
            new SimulationConfiguration("test-run", DateTimeOffset.UnixEpoch, fixedStepSeconds, sensors: sensors);

        // Any valid sensor configuration; the test's factory samples it as an ideal body-rate sensor.
        private static SensorConfiguration Rate(string sensorId, bool isControllerVisible)
        {
            Vector3d zero = new Vector3d(0.0, 0.0, 0.0);
            Vector3d unitScale = new Vector3d(1.0, 1.0, 1.0);
            return SensorConfiguration.ForImu(
                new SensorDefinition(sensorId, "test-rate", sensorId, 0.1, "test", SensorMount.Identity, isControllerVisible),
                new ImuProfile(
                    new GyroscopeProfile(1.2e-4, 0.0, 0.0, zero, unitScale, 4.363, 1.33e-4),
                    new AccelerometerProfile(1.8e-3, 0.0, 0.0, zero, unitScale, 19.61, 6.0e-4)));
        }
    }
}
