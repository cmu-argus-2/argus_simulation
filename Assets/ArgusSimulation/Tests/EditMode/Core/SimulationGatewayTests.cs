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

        [TestCase(SnapshotMismatch.RunId)]
        [TestCase(SnapshotMismatch.StateSequence)]
        [TestCase(SnapshotMismatch.StateTime)]
        [TestCase(SnapshotMismatch.Timestamp)]
        [TestCase(SnapshotMismatch.CommandTime)]
        public void Step_RejectsOutOfSyncBackendSnapshotWithoutAdvancing(
            SnapshotMismatch mismatch)
        {
            OutOfSyncSimulationEngine engine = new OutOfSyncSimulationEngine(mismatch);
            SimulationGateway gateway = new SimulationGateway(engine);
            gateway.Initialize(new SimulationConfiguration(
                "test-run",
                DateTimeOffset.UnixEpoch,
                0.1));
            bool snapshotPublished = false;
            gateway.SnapshotProduced += _ => snapshotPublished = true;

            Assert.Throws<InvalidOperationException>(() => gateway.Step());

            Assert.That(gateway.NextSequence, Is.EqualTo(0));
            Assert.That(gateway.NextSimulationTimeSeconds, Is.EqualTo(0.0));
            Assert.That(snapshotPublished, Is.False);
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

        public enum SnapshotMismatch
        {
            RunId,
            StateSequence,
            StateTime,
            Timestamp,
            CommandTime
        }

        private sealed class OutOfSyncSimulationEngine : ISimulationEngine
        {
            private readonly SnapshotMismatch _mismatch;
            private SimulationConfiguration _configuration;

            public OutOfSyncSimulationEngine(SnapshotMismatch mismatch)
            {
                _mismatch = mismatch;
            }

            public string BackendName => "out-of-sync-test";
            public bool IsInitialized { get; private set; }

            public void Initialize(SimulationConfiguration configuration)
            {
                _configuration = configuration;
                IsInitialized = true;
            }

            public void Reset()
            {
            }

            public bool TryStep(SimulationStepInput input, out SimulationSnapshot snapshot)
            {
                long stateSequence = input.Sequence;
                double stateTime = input.SimulationTimeSeconds;
                DateTimeOffset timestamp =
                    _configuration.EpochUtc.AddSeconds(input.SimulationTimeSeconds);
                ActuatorCommandSet appliedCommands = input.Commands;
                string runId = _configuration.RunId;

                switch (_mismatch)
                {
                    case SnapshotMismatch.RunId:
                        runId = "different-run";
                        break;
                    case SnapshotMismatch.StateSequence:
                        stateSequence++;
                        appliedCommands = ActuatorCommandSet.None(
                            stateSequence,
                            input.SimulationTimeSeconds);
                        break;
                    case SnapshotMismatch.StateTime:
                        stateTime += 0.1;
                        break;
                    case SnapshotMismatch.Timestamp:
                        timestamp = timestamp.AddSeconds(1.0);
                        break;
                    case SnapshotMismatch.CommandTime:
                        appliedCommands = ActuatorCommandSet.None(
                            input.Sequence,
                            input.SimulationTimeSeconds + 0.1);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                SpacecraftState state = new SpacecraftState(
                    stateSequence,
                    stateTime,
                    timestamp,
                    new Vector3d(6_878_137.0, 0.0, 0.0),
                    new Vector3d(0.0, 7_600.0, 0.0),
                    new Quaterniond(0.0, 0.0, 0.0, 1.0),
                    new Vector3d(0.0, 0.0, 0.0));
                snapshot = new SimulationSnapshot(
                    runId,
                    BackendName,
                    state,
                    appliedCommands);
                return true;
            }
        }
    }
}
