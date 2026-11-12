using System;
using System.Collections.Generic;
using System.IO;
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
            DeviceCommandSet command = DeviceCommandSet.None(0, 0.0);
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
            Assert.That(states[1].AppliedCommands.Value.Sequence, Is.EqualTo(0));
            Assert.That(states[1].AppliedCommands.Value.Commands, Is.Empty);
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
                    sensor.Definition.SamplePeriodSeconds,
                    sensor.Definition.Source));
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

        // A camera frame carries the true camera pose (its RenderRequest) even when Unavailable, so
        // a scenario that shows a camera to the controller is rejected before anything runs (D7).
        [Test]
        public void ResetRejectsAControllerVisibleCamera()
        {
            SimulationGateway gateway = new SimulationGateway(Engine());
            int runsStarted = 0;
            gateway.RunStarted += (run, sensors) => runsStarted++;

            Assert.Throws<ArgumentException>(
                () => gateway.Reset(0, Scenario(0.1, Camera("cam.px", isControllerVisible: true))));
            Assert.That(runsStarted, Is.EqualTo(0));
            Assert.That(gateway.IsRunning, Is.False);
        }

        // The default factory builds the camera; with no renderer every exposure is Unavailable.
        // Its frames reach the recorder at every step but never an observation (D7).
        [Test]
        public void CameraFramesReachTheRecorderButNeverTheController()
        {
            SimulationGateway gateway = new SimulationGateway(Engine());
            List<SensorOutputSet> outputs = new List<SensorOutputSet>();
            gateway.SensorOutputProduced += outputs.Add;

            Observation[] observations =
            {
                gateway.Reset(0, Scenario(0.1, Camera("cam.px", isControllerVisible: false))),
                gateway.Step()
            };

            for (int index = 0; index < observations.Length; index++)
            {
                Assert.That(observations[index].Frames, Is.Empty);
                Assert.That(outputs[index].Frames.Count, Is.EqualTo(1));
                Assert.That(outputs[index].Frames[0].SensorId, Is.EqualTo("cam.px"));
                Assert.That(outputs[index].Frames[0].Status, Is.EqualTo(SensorFrameStatus.Unavailable));
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
        public void EveryCommandIsLoggedAppliedOrRejectedBeforeStepThrows()
        {
            SimulationGateway gateway = new SimulationGateway(Engine());
            List<CommandRecord> log = new List<CommandRecord>();
            gateway.CommandLogged += log.Add;
            gateway.Reset(0, Scenario(0.1));

            DeviceCommandSet notFinite = Torque(double.NaN);
            Assert.Throws<ArgumentException>(() => gateway.Step(notFinite));
            Assert.Throws<ArgumentException>(() => gateway.Step(DeviceCommandSet.None(4, 0.4)));
            Assert.Throws<ArgumentException>(() => gateway.Step(DeviceCommandSet.None(0, 0.05)));
            // The run configures no actuator, so a command naming a device never reaches the engine.
            Assert.Throws<ArgumentException>(() => gateway.Step(Torque(0.001)));
            DeviceCommandSet command = DeviceCommandSet.None(0, 0.0);
            gateway.Step(command);

            // Every record is for step 0, the gateway's next step, whatever the command claimed.
            Assert.That(log.Count, Is.EqualTo(5));
            for (int index = 0; index < 4; index++)
            {
                Assert.That((log[index].Outcome, log[index].Sequence, log[index].SimulationTimeSeconds, log[index].Applied.HasValue),
                    Is.EqualTo((CommandOutcome.Rejected, 0L, 0.0, false)));
            }

            Assert.That(log[0].Reason, Is.EqualTo("The command is invalid: device 'torque.body' value \"torque_body_n_m\" is not finite."));
            Assert.That(log[0].Requested.Commands[0].Values.Vector3("torque_body_n_m").X, Is.NaN);
            Assert.That(log[1].Reason, Is.EqualTo("The command is for sequence 4; the next step is 0."));
            Assert.That(log[1].Requested.Sequence, Is.EqualTo(4));
            Assert.That(log[2].Reason, Is.EqualTo("The command is for t = 0.05 s; step 0 is at t = 0 s."));
            Assert.That(log[3].Reason, Is.EqualTo("Device 'torque.body' is not an actuator this run configures."));

            // With no actuator configured, the applied command is the requested one.
            Assert.That((log[4].Outcome, log[4].Reason, log[4].RunId), Is.EqualTo((CommandOutcome.Applied, (string)null, "test-run")));
            Assert.That(log[4].Applied.Value.Commands, Is.Empty);
            Assert.That(gateway.NextSequence, Is.EqualTo(1));
        }

        // Each command has exactly its kind's keys, and only idle values over an engine that does not
        // apply commands, such as the analytic fixture (G1). The applied command adds every actuator
        // the controller left out, at its idle values, once each, while the requested command stays
        // as sent (CON-2).
        [Test]
        public void ActuatorCommandsAreCheckedByKindAndOmittedActuatorsRunIdle()
        {
            SimulationGateway gateway = new SimulationGateway(Engine());
            List<CommandRecord> log = new List<CommandRecord>();
            gateway.CommandLogged += log.Add;
            gateway.Reset(0, Scenario(0.1, IdealBodyTorque("torque.body"), MagneticTorquer("mtq.x")));

            Assert.Throws<ArgumentException>(() => gateway.Step(Command(0, "torque.body", new DeviceValues.Builder()
                .Add("torque_body_n_m", new Vector3d(0.0, 0.0, 0.0))
                .Add("dipole_a_m2", 0.0)
                .Build())));
            Assert.Throws<ArgumentException>(() => gateway.Step(Command(0, "torque.body", DeviceValues.Empty)));
            Assert.Throws<ArgumentException>(() => gateway.Step(Command(0, "torque.body", new DeviceValues.Builder()
                .Add("torque_body_n_m", 0.0)
                .Build())));
            Assert.Throws<ArgumentException>(() => gateway.Step(Torque(0.001)));
            gateway.Step(Torque(0.0));
            gateway.Step(DeviceCommandSet.None(1, 0.1));

            Assert.That(log.Count, Is.EqualTo(6));
            Assert.That(log[0].Reason, Is.EqualTo(
                "Device 'torque.body' command values[\"dipole_a_m2\"]: is not a ideal_body_torque key."));
            Assert.That(log[1].Reason, Is.EqualTo("Device 'torque.body' command values[\"torque_body_n_m\"]: is required."));
            Assert.That(log[2].Reason, Is.EqualTo(
                "Device 'torque.body' command values[\"torque_body_n_m\"]: must be a Vector3, not a Number."));
            Assert.That(log[3].Reason, Is.EqualTo(
                "Device 'torque.body': analytic-circular-orbit does not apply actuator commands."));
            for (int index = 0; index < 4; index++)
            {
                Assert.That((log[index].Outcome, log[index].Applied.HasValue), Is.EqualTo((CommandOutcome.Rejected, false)));
            }

            Assert.That(log[4].Outcome, Is.EqualTo(CommandOutcome.Applied));
            Assert.That(DeviceIds(log[4].Requested), Is.EqualTo(new[] { "torque.body" }));
            Assert.That(DeviceIds(log[4].Applied.Value), Is.EqualTo(new[] { "torque.body", "mtq.x" }));
            Assert.That(log[4].Applied.Value.Commands[1].Values.Number("dipole_a_m2"), Is.EqualTo(0.0));
            Assert.That(DeviceIds(log[5].Requested), Is.Empty);
            Assert.That(DeviceIds(log[5].Applied.Value), Is.EqualTo(new[] { "torque.body", "mtq.x" }));
            Assert.That(log[5].Applied.Value.Commands[0].Values.Vector3("torque_body_n_m"), Is.EqualTo(new Vector3d(0.0, 0.0, 0.0)));
        }

        // Over an engine that applies commands (BasiliskEngine, BSK-8), a non-idle command is applied
        // as sent, with the actuators it leaves out idle.
        [Test]
        public void AnEngineThatAppliesCommandsGetsNonIdleCommands()
        {
            ApplyingEngine engine = new ApplyingEngine(Engine());
            SimulationGateway gateway = new SimulationGateway(engine);
            List<CommandRecord> log = new List<CommandRecord>();
            gateway.CommandLogged += log.Add;
            gateway.Reset(0, Scenario(0.1, IdealBodyTorque("torque.body"), MagneticTorquer("mtq.x")));

            gateway.Step(Torque(0.001));

            Assert.That((log[0].Outcome, log[0].Reason), Is.EqualTo((CommandOutcome.Applied, (string)null)));
            Assert.That(DeviceIds(log[0].Applied.Value), Is.EqualTo(new[] { "torque.body", "mtq.x" }));
            Assert.That(engine.LastCommands.Value.Commands[0].Values.Vector3("torque_body_n_m"), Is.EqualTo(new Vector3d(0.001, 0.0, 0.0)));
            Assert.That(engine.LastCommands.Value.Commands[1].Values.Number("dipole_a_m2"), Is.EqualTo(0.0));
        }

        // CORE-10, clamp policy: a command past its device's limits runs at them. The record keeps the
        // command as sent, what the engine ran, and why; a command at the limits is applied as sent.
        [Test]
        public void AClampPolicyRunsACommandPastItsLimitsAtThem()
        {
            ApplyingEngine engine = new ApplyingEngine(Engine());
            SimulationGateway gateway = new SimulationGateway(engine);
            List<CommandRecord> log = new List<CommandRecord>();
            gateway.CommandLogged += log.Add;
            gateway.Reset(0, Scenario(0.1, IdealBodyTorque("torque.body")));

            gateway.Step(Command(0, "torque.body", TorqueValues(new Vector3d(2e-3, -5e-4, -3e-3))));
            gateway.Step(Command(1, "torque.body", TorqueValues(new Vector3d(1e-3, -1e-3, 0.0))));

            Assert.That((log[0].Outcome, log[0].Reason), Is.EqualTo((CommandOutcome.Clamped,
                "Device 'torque.body' command values[\"torque_body_n_m\"] (0.002, -0.0005, -0.003) is past " +
                "max_torque_body_n_m (0.001, 0.001, 0.001); clamped.")));
            Assert.That(log[0].Requested.Commands[0].Values.Vector3("torque_body_n_m"), Is.EqualTo(new Vector3d(2e-3, -5e-4, -3e-3)));
            Assert.That(log[0].Applied.Value.Commands[0].Values.Vector3("torque_body_n_m"), Is.EqualTo(new Vector3d(1e-3, -5e-4, -1e-3)));
            Assert.That((log[1].Outcome, log[1].Reason), Is.EqualTo((CommandOutcome.Applied, (string)null)));
            Assert.That(engine.LastCommands.Value.Commands[0].Values.Vector3("torque_body_n_m"), Is.EqualTo(new Vector3d(1e-3, -1e-3, 0.0)));
            Assert.That(gateway.NextSequence, Is.EqualTo(2));
        }

        // CORE-10, reject policy: a command past its device's limits is refused whole, the engine runs
        // nothing, and the run goes on at the same step.
        [Test]
        public void ARejectPolicyRefusesACommandPastItsLimits()
        {
            ApplyingEngine engine = new ApplyingEngine(Engine());
            SimulationGateway gateway = new SimulationGateway(engine);
            List<CommandRecord> log = new List<CommandRecord>();
            gateway.CommandLogged += log.Add;
            gateway.Reset(0, Scenario(0.1, IdealBodyTorque("torque.body", ActuatorLimitPolicy.Reject)));

            ArgumentException refused = Assert.Throws<ArgumentException>(() =>
                gateway.Step(Command(0, "torque.body", TorqueValues(new Vector3d(0.0, 1.5e-3, 0.0)))));

            const string reason = "Device 'torque.body' command values[\"torque_body_n_m\"] (0, 0.0015, 0) is past " +
                "max_torque_body_n_m (0.001, 0.001, 0.001), and its limit_policy is reject.";
            Assert.That(refused.Message, Does.StartWith(reason));
            Assert.That((log[0].Outcome, log[0].Reason, log[0].Applied.HasValue), Is.EqualTo((CommandOutcome.Rejected, reason, false)));
            Assert.That(engine.LastCommands.HasValue, Is.False);
            Assert.That(gateway.NextSequence, Is.EqualTo(0));
            gateway.Step(Command(0, "torque.body", TorqueValues(new Vector3d(0.0, 1e-3, 0.0))));
            Assert.That((log[1].Outcome, gateway.NextSequence), Is.EqualTo((CommandOutcome.Applied, 1L)));
        }

        // Each device follows its own policy: a clamp changes only its device, and one device's reject
        // refuses the whole command, a clamp beside it included.
        [Test]
        public void EachDeviceFollowsItsOwnLimitPolicy()
        {
            ApplyingEngine engine = new ApplyingEngine(Engine());
            SimulationGateway gateway = new SimulationGateway(engine);
            List<CommandRecord> log = new List<CommandRecord>();
            gateway.CommandLogged += log.Add;
            gateway.Reset(0, Scenario(0.1, IdealBodyTorque("torque.body"), MagneticTorquer("mtq.x")));
            DeviceCommand torque = new DeviceCommand("torque.body", TorqueValues(new Vector3d(0.0, 0.0, -4e-3)));

            gateway.Step(new DeviceCommandSet(0, 0.0, new[] { torque, new DeviceCommand("mtq.x", Dipole(-0.2)) }));
            Assert.Throws<ArgumentException>(() =>
                gateway.Step(new DeviceCommandSet(1, 0.1, new[] { torque, new DeviceCommand("mtq.x", Dipole(0.25)) })));

            Assert.That(log[0].Outcome, Is.EqualTo(CommandOutcome.Clamped));
            Assert.That(log[0].Applied.Value.Commands[0].Values.Vector3("torque_body_n_m"), Is.EqualTo(new Vector3d(0.0, 0.0, -1e-3)));
            Assert.That(log[0].Applied.Value.Commands[1].Values.Number("dipole_a_m2"), Is.EqualTo(-0.2));
            Assert.That((log[1].Outcome, log[1].Reason), Is.EqualTo((CommandOutcome.Rejected,
                "Device 'mtq.x' command values[\"dipole_a_m2\"] 0.25 is past max_dipole_a_m2 0.2, and its limit_policy is reject.")));
        }

        [Test]
        public void BackendFailureEndsTheRunAndKeepsTheCause()
        {
            InvalidOperationException cause = new InvalidOperationException("Basilisk Step failed: Unavailable");
            SimulationGateway gateway = new SimulationGateway(new FailingEngine(Engine(), failAtStep: 1, cause));
            List<Observation> terminated = new List<Observation>();
            gateway.RunTerminated += terminated.Add;
            gateway.Reset(0, Scenario(0.5));

            Observation failed = gateway.Step();

            // The observation describes the last step that exists; the reason names the one that failed.
            Assert.That((failed.Status, failed.Sequence, failed.Frames.Count), Is.EqualTo((ObservationStatus.BackendFailed, 0L, 0)));
            Assert.That(failed.TerminationReason, Is.EqualTo("Step 1 failed: Basilisk Step failed: Unavailable"));
            Assert.That(gateway.Failure, Is.SameAs(cause));
            Assert.That(terminated, Is.EqualTo(new[] { failed }));
            Assert.That((gateway.IsRunning, gateway.NextSequence), Is.EqualTo((false, 0L)));
            Assert.Throws<InvalidOperationException>(() => gateway.Step());

            // Anything else is a bug, not a backend failure, and propagates. It still ends the run, so
            // a retry is rejected instead of logging a second Applied record for step 0.
            SimulationGateway buggy = new SimulationGateway(new FailingEngine(Engine(), failAtStep: 1, new ArgumentException("bug")));
            List<CommandRecord> log = new List<CommandRecord>();
            buggy.CommandLogged += log.Add;
            buggy.Reset(0, Scenario(0.5));
            Assert.Throws<ArgumentException>(() => buggy.Step());
            Assert.Throws<InvalidOperationException>(() => buggy.Step());
            Assert.That(log.ConvertAll(record => (record.Outcome, record.Sequence)),
                Is.EqualTo(new[] { (CommandOutcome.Applied, 0L), (CommandOutcome.Rejected, 0L) }));

            // Cancellation is neither: it propagates and ends the run Stopped, not BackendFailed.
            SimulationGateway cancelled = new SimulationGateway(
                new FailingEngine(Engine(), failAtStep: 1, new OperationCanceledException("Basilisk Step was cancelled.")));
            List<Observation> stopped = new List<Observation>();
            cancelled.RunTerminated += stopped.Add;
            cancelled.Reset(0, Scenario(0.5));
            Assert.Throws<OperationCanceledException>(() => cancelled.Step());
            Assert.That(stopped.ConvertAll(end => (end.Status, end.Sequence, end.TerminationReason)),
                Is.EqualTo(new[] { (ObservationStatus.Stopped, 0L, "Step 1 was cancelled: Basilisk Step was cancelled.") }));
            Assert.That((cancelled.IsRunning, cancelled.Failure), Is.EqualTo((false, (Exception)null)));
        }

        [Test]
        public void AThrowingCommandLoggedSubscriberEndsTheRunAndSaysWhy()
        {
            SimulationGateway gateway = new SimulationGateway(Engine());
            List<CommandRecord> log = new List<CommandRecord>();
            List<Observation> terminated = new List<Observation>();
            bool fail = true;
            gateway.CommandLogged += log.Add;
            gateway.CommandLogged += record =>
            {
                if (fail)
                {
                    fail = false;
                    throw new IOException("disk full");
                }
            };
            gateway.RunTerminated += terminated.Add;

            // No run has started, so nothing is logged.
            Assert.Throws<InvalidOperationException>(() => gateway.Step());
            Assert.That(log, Is.Empty);

            gateway.Reset(0, Scenario(0.5));
            Assert.Throws<IOException>(() => gateway.Step());

            Assert.That(gateway.IsRunning, Is.False);
            Assert.That(terminated.Count, Is.EqualTo(1));
            Assert.That((terminated[0].Status, terminated[0].Sequence, terminated[0].TerminationReason),
                Is.EqualTo((ObservationStatus.Stopped, 0L, "Logging the command for step 0 failed: disk full")));
            Assert.Throws<InvalidOperationException>(() => gateway.Step());
            Assert.That(log.ConvertAll(record => (record.Outcome, record.Sequence)),
                Is.EqualTo(new[] { (CommandOutcome.Applied, 0L), (CommandOutcome.Rejected, 0L) }));
        }

        [Test]
        public void AThrowingRunStartedHandlerLeavesNoRunAndStopEndsOne()
        {
            SimulationGateway gateway = new SimulationGateway(Engine());
            gateway.RunStarted += (run, sensors) => throw new IOException("recorder could not start");
            Assert.Throws<IOException>(() => gateway.Reset(0, Scenario(0.5)));
            Assert.That(gateway.IsRunning, Is.False);
            Assert.Throws<InvalidOperationException>(() => gateway.Step());

            SimulationGateway stopped = new SimulationGateway(Engine());
            List<Observation> terminated = new List<Observation>();
            stopped.RunTerminated += terminated.Add;
            stopped.Reset(0, Scenario(0.5));
            stopped.Step();
            Observation end = stopped.Stop("Cancelled (Ctrl+C)");

            Assert.That((end.Status, end.Sequence, end.TerminationReason), Is.EqualTo((ObservationStatus.Stopped, 1L, "Cancelled (Ctrl+C)")));
            Assert.That(terminated, Is.EqualTo(new[] { end }));
            Assert.That(stopped.IsRunning, Is.False);
            Assert.That(stopped.Stop("again"), Is.Null);
        }

        // A command for step 0 naming the ideal body torque device "torque.body".
        private static DeviceCommandSet Torque(double x) =>
            Command(0, "torque.body", new DeviceValues.Builder().Add("torque_body_n_m", new Vector3d(x, 0.0, 0.0)).Build());

        // A command for one device at a 0.1 s step.
        private static DeviceCommandSet Command(long sequence, string deviceId, DeviceValues values) =>
            new DeviceCommandSet(sequence, sequence * 0.1, new[] { new DeviceCommand(deviceId, values) });

        private static string[] DeviceIds(DeviceCommandSet command)
        {
            string[] ids = new string[command.Commands.Count];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = command.Commands[i].DeviceId;
            }

            return ids;
        }

        // An actuator has no period and publishes no frames (docs/device-model.md §1).
        private static SensorDefinition Actuator(string deviceId) =>
            new SensorDefinition(deviceId, "test-actuator", deviceId, 0.0, "basilisk", SensorMount.Identity, isControllerVisible: false);

        private static DeviceConfiguration IdealBodyTorque(string deviceId, ActuatorLimitPolicy policy = ActuatorLimitPolicy.Clamp) =>
            IdealBodyTorqueKind.Instance.Configure(
                Actuator(deviceId),
                new IdealBodyTorqueProfile(new Vector3d(1e-3, 1e-3, 1e-3), policy));

        private static DeviceValues TorqueValues(Vector3d torque) =>
            new DeviceValues.Builder().Add("torque_body_n_m", torque).Build();

        private static DeviceValues Dipole(double dipole) => new DeviceValues.Builder().Add("dipole_a_m2", dipole).Build();

        private static DeviceConfiguration MagneticTorquer(string deviceId) => MagneticTorquerKind.Instance.Configure(
            Actuator(deviceId),
            new MagneticTorquerProfile(new Vector3d(1.0, 0.0, 0.0), 0.2, ActuatorLimitPolicy.Reject));

        // The manifest lists each device as configured, while its frames carry the ID, frame and
        // source of the sensor the factory built, on that sensor's period; so the two must agree
        // (CORE-22 design review). A mismatch fails before the engine is configured, which for
        // Basilisk is a call to the service (CORE-22 review).
        [TestCase("rate.other", "rate.main", 0.1, DeviceSource.Basilisk)]
        [TestCase("rate.main", "other_frame", 0.1, DeviceSource.Basilisk)]
        [TestCase("rate.main", "rate.main", 0.2, DeviceSource.Basilisk)]
        [TestCase("rate.main", "rate.main", 0.1, "simulation")]
        public void ResetRejectsASensorThatDisagreesWithItsDevice(string sensorId, string frameId, double periodSeconds, string source)
        {
            AnalyticSimulationEngine engine = Engine();
            SimulationGateway gateway = new SimulationGateway(
                engine,
                device => new IdealBodyRateSensorModel(sensorId, frameId, periodSeconds, source));

            ArgumentException error = Assert.Throws<ArgumentException>(
                () => gateway.Reset(0, Scenario(0.1, Rate("rate.main", isControllerVisible: true))));
            Assert.That(error.Message, Does.Contain("'rate.main'"));
            Assert.That(engine.IsInitialized, Is.False);
        }

        // A camera places its frames by its own mount, so the built sensor must keep its device's
        // position and rotation (up to the quaternion's sign). The device turns 120 degrees about
        // (1, 1, 1); flipping one sign of it gives a turn 120 degrees away. "zero" forgets the
        // mount's rotation and "scaled" doubles its quaternion: both invalid mounts, which the
        // rotation comparison alone would accept (CORE-22 review).
        [TestCase("x")]
        [TestCase("y")]
        [TestCase("z")]
        [TestCase("w")]
        [TestCase("position")]
        [TestCase("zero")]
        [TestCase("scaled")]
        public void ResetRejectsASensorMountedElsewhereThanItsDevice(string changed)
        {
            SensorMount mount = new SensorMount(new Vector3d(0.1, 0.0, 0.0), new Quaterniond(0.5, 0.5, 0.5, 0.5));
            SensorMount builtMount = new SensorMount(
                changed == "position" ? new Vector3d(0.0, 0.0, 0.0) : mount.PositionBodyMeters,
                changed == "zero" ? new Quaterniond(0.0, 0.0, 0.0, 0.0)
                    : changed == "scaled" ? new Quaterniond(1.0, 1.0, 1.0, 1.0)
                    : new Quaterniond(
                        changed == "x" ? -0.5 : 0.5,
                        changed == "y" ? -0.5 : 0.5,
                        changed == "z" ? -0.5 : 0.5,
                        changed == "w" ? -0.5 : 0.5));
            AnalyticSimulationEngine engine = Engine();
            DeviceConfiguration device = ImuKind.Instance.Configure(
                new SensorDefinition("rate.main", "test-rate", "rate.main", 0.1, DeviceSource.Basilisk, mount),
                TestImu.P0LikeProfile());
            SimulationGateway gateway = new SimulationGateway(
                engine,
                configured => new DefinitionOnlySensor(new SensorDefinition(
                    configured.Definition.SensorId,
                    configured.Definition.ModelName,
                    configured.Definition.FrameId,
                    configured.Definition.SamplePeriodSeconds,
                    configured.Definition.Source,
                    builtMount,
                    configured.Definition.IsControllerVisible)));

            ArgumentException error = Assert.Throws<ArgumentException>(() => gateway.Reset(0, Scenario(0.1, device)));
            Assert.That(error.Message, Is.EqualTo("The sensor built for device 'rate.main' must keep its device's mount; it has another."));
            Assert.That(engine.IsInitialized, Is.False);
        }

        // The same rotation can be written differently: q and -q are one rotation, and a valid mount
        // may be up to 1e-6 off unit length (Quaterniond.IsUnit), so a sensor may hold it normalised.
        // Both keep the device's mount (CORE-22 review). The device's turn is 2e-7 short of unit
        // length, so a raw dot product without normalising would fall short of 1 in both cases.
        [TestCase("negated")]
        [TestCase("normalised")]
        public void ResetAcceptsASensorWhoseMountRotationIsWrittenDifferently(string written)
        {
            Quaterniond turn = new Quaterniond(0.5, 0.5, 0.5, 0.4999996);
            SensorMount mount = new SensorMount(new Vector3d(0.1, 0.0, 0.0), turn);
            SensorMount builtMount = new SensorMount(
                mount.PositionBodyMeters,
                written == "negated" ? new Quaterniond(-turn.X, -turn.Y, -turn.Z, -turn.W) : turn.Normalized());
            AnalyticSimulationEngine engine = Engine();
            DeviceConfiguration device = ImuKind.Instance.Configure(
                new SensorDefinition("rate.main", "test-rate", "rate.main", 0.1, DeviceSource.Basilisk, mount),
                TestImu.P0LikeProfile());
            SimulationGateway gateway = new SimulationGateway(
                engine,
                configured => new DefinitionOnlySensor(new SensorDefinition(
                    configured.Definition.SensorId,
                    configured.Definition.ModelName,
                    configured.Definition.FrameId,
                    configured.Definition.SamplePeriodSeconds,
                    configured.Definition.Source,
                    builtMount,
                    configured.Definition.IsControllerVisible)));

            Assert.DoesNotThrow(() => gateway.Reset(0, Scenario(0.1, device)));
            Assert.That(engine.IsInitialized, Is.True);
        }

        // A Reset refused by the gateway's own checks (an invalid scenario, or a sensor that disagrees
        // with its device) changes nothing: the run in progress keeps running (CORE-22 review). A Reset
        // the engine refuses still ends it, as before CORE-22.
        [Test]
        public void ARefusedResetLeavesTheRunInProgressRunning()
        {
            SimulationGateway gateway = new SimulationGateway(
                Engine(),
                device => device.Definition.SensorId == "rate.bad"
                    ? new IdealBodyRateSensorModel("rate.bad", "rate.bad", 0.1, "simulation")
                    : SameDefinitionStandIn(device));
            gateway.Reset(0, Scenario(0.1, Rate("rate.main", isControllerVisible: true)));
            gateway.Step();

            Assert.Throws<ArgumentException>(() => gateway.Reset(0, Scenario(0.1, Rate("rate.bad", isControllerVisible: true))));

            Assert.That(gateway.IsRunning, Is.True);
            Assert.DoesNotThrow(() => gateway.Step());
        }

        [Test]
        public void ResetRejectsAFactoryThatBuildsNoSensor()
        {
            SimulationGateway gateway = new SimulationGateway(Engine(), device => null);

            ArgumentException error = Assert.Throws<ArgumentException>(
                () => gateway.Reset(0, Scenario(0.1, Rate("rate.main", isControllerVisible: true))));
            Assert.That(error.Message, Does.Contain("'rate.main'"));
        }

        // An ideal body-rate sensor with its device's ID, frame, period and source.
        private static ISensor SameDefinitionStandIn(DeviceConfiguration device) =>
            new IdealBodyRateSensorModel(
                device.Definition.SensorId,
                device.Definition.FrameId,
                device.Definition.SamplePeriodSeconds,
                device.Definition.Source);

        // A sensor with any definition, for the guard to compare; it never samples.
        private sealed class DefinitionOnlySensor : ISensor
        {
            public DefinitionOnlySensor(SensorDefinition definition)
            {
                Definition = definition;
            }

            public SensorDefinition Definition { get; }

            public void Reset(SensorResetContext context)
            {
            }

            public bool TrySample(SensorSampleContext context, out ISensorFrame frame)
            {
                frame = null;
                return false;
            }
        }

        private static AnalyticSimulationEngine Engine() => new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0);


        private static SimulationConfiguration Scenario(double fixedStepSeconds, params DeviceConfiguration[] sensors) =>
            new SimulationConfiguration("test-run", DateTimeOffset.UnixEpoch, fixedStepSeconds, devices: sensors);

        // A camera at the fixed step's period, so it samples at every step.
        private static DeviceConfiguration Camera(string sensorId, bool isControllerVisible) =>
            CameraKind.Instance.Configure(
                new SensorDefinition(sensorId, "test-camera", sensorId, 0.1, "argus", SensorMount.Identity, isControllerVisible),
                TestCameras.Imx708Profile());

        // Any valid sensor configuration; the test's factory samples it as an ideal body-rate sensor.
        private static DeviceConfiguration Rate(string sensorId, bool isControllerVisible)
        {
            return ImuKind.Instance.Configure(
                new SensorDefinition(sensorId, "test-rate", sensorId, 0.1, DeviceSource.Basilisk, SensorMount.Identity, isControllerVisible),
                TestImu.P0LikeProfile());
        }
    }
}
