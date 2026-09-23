using System;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class SimulationGatewayTests
    {
        [Test]
        public void Step_AdvancesAtConfiguredFixedInterval()
        {
            SimulationGateway gateway = BuildGateway(0.25);

            SimulationSnapshot first = gateway.Step();
            SimulationSnapshot second = gateway.Step();

            Assert.That(first.Spacecraft.Sequence, Is.EqualTo(0));
            Assert.That(first.Spacecraft.SimulationTimeSeconds, Is.EqualTo(0.0));
            Assert.That(second.Spacecraft.Sequence, Is.EqualTo(1));
            Assert.That(second.Spacecraft.SimulationTimeSeconds, Is.EqualTo(0.25));
            Assert.That(gateway.NextSequence, Is.EqualTo(2));
            Assert.That(gateway.NextSimulationTimeSeconds, Is.EqualTo(0.5));
        }

        [Test]
        public void Step_PreservesAppliedControllerCommandInSnapshot()
        {
            SimulationGateway gateway = BuildGateway(0.1);
            ActuatorCommandSet commands = new ActuatorCommandSet(
                0,
                0.0,
                new Vector3d(0.001, -0.002, 0.003),
                new Vector3d(0.02, 0.0, -0.01),
                new Vector3d(0.0, 0.0, 0.0));

            SimulationSnapshot snapshot = gateway.Step(commands);

            Assert.That(snapshot.IsValid, Is.True);
            Assert.That(
                snapshot.AppliedCommands.ReactionWheelTorqueBodyNewtonMeters,
                Is.EqualTo(commands.ReactionWheelTorqueBodyNewtonMeters));
            Assert.That(snapshot.DynamicsBackend, Is.EqualTo("analytic-circular-orbit"));
        }

        [Test]
        public void Reset_RestartsSequenceAndTime()
        {
            SimulationGateway gateway = BuildGateway(0.5);
            gateway.Step();
            gateway.Step();

            gateway.Reset();
            SimulationSnapshot restarted = gateway.Step();

            Assert.That(restarted.Spacecraft.Sequence, Is.EqualTo(0));
            Assert.That(restarted.Spacecraft.SimulationTimeSeconds, Is.EqualTo(0.0));
        }

        [Test]
        public void Step_RejectsCommandForWrongSequence()
        {
            SimulationGateway gateway = BuildGateway(0.1);
            ActuatorCommandSet commands = ActuatorCommandSet.None(4, 0.4);

            Assert.Throws<ArgumentException>(() => gateway.Step(commands));
        }

        private static SimulationGateway BuildGateway(double fixedStepSeconds)
        {
            AnalyticSimulationEngine engine = new AnalyticSimulationEngine(
                500_000.0,
                51.6,
                0.0,
                0.0);
            SimulationGateway gateway = new SimulationGateway(engine);
            gateway.Initialize(new SimulationConfiguration(
                "test-run",
                DateTimeOffset.UnixEpoch,
                fixedStepSeconds));
            return gateway;
        }
    }
}
