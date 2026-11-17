using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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

        // CORE-21: a stop, a backend failure, a finished run and a second Reset each end the run, so the
        // old camera cancels the render it still has in flight. No camera frame is published either
        // way, since the held render never completes; the token is the check.
        [TestCase("finish")]
        [TestCase("stop")]
        [TestCase("backend failure")]
        [TestCase("second reset")]
        public void EndingARunCancelsTheOldCamerasPendingRenders(string end)
        {
            HeldRenderer renderer = new HeldRenderer();
            ISimulationEngine engine = end == "backend failure"
                ? new FailingEngine(Engine(), failAtStep: 1, new InvalidOperationException("Basilisk Step failed: Unavailable"))
                : (ISimulationEngine)Engine();
            SimulationGateway gateway = new SimulationGateway(engine, device => SensorFactory.Create(device, renderer));
            List<SensorOutputSet> outputs = new List<SensorOutputSet>();
            gateway.SensorOutputProduced += outputs.Add;
            SimulationConfiguration scenario = Scenario(0.1, Camera("cam.test", isControllerVisible: false));

            gateway.Reset(0, scenario);
            Assert.That(renderer.Tokens.Count, Is.EqualTo(1));
            Assert.That(renderer.Tokens[0].IsCancellationRequested, Is.False);

            switch (end)
            {
                case "stop":
                    Assert.That(gateway.Stop("test").Status, Is.EqualTo(ObservationStatus.Stopped));
                    break;
                case "backend failure":
                    Assert.That(gateway.Step().Status, Is.EqualTo(ObservationStatus.BackendFailed));
                    break;
                case "finish":
                    gateway.Finish();
                    break;
                default:
                    gateway.Reset(0, scenario);
                    gateway.Step();
                    Assert.That(renderer.Tokens.Count, Is.EqualTo(3));
                    Assert.That(renderer.Tokens[1].IsCancellationRequested, Is.False, "the new run's render");
                    break;
            }

            Assert.That(renderer.Tokens[0].IsCancellationRequested, Is.True);
            Assert.That(outputs, Has.All.Matches<SensorOutputSet>(output => output.Frames.Count == 0));
        }

        // CORE-21 review: a renderer whose cancellation callback throws stops nothing. The camera's other
        // pending render is still cancelled, and a backend failure still ends the run as BackendFailed
        // instead of throwing from Step.
        [Test]
        public void ACancellationCallbackThatThrowsStopsNoOtherCleanup()
        {
            HeldRenderer renderer = new HeldRenderer { ThrowOnCancelAt = 0 };
            FailingEngine engine = new FailingEngine(Engine(), failAtStep: 2, new InvalidOperationException("Basilisk Step failed: Unavailable"));
            SimulationGateway gateway = new SimulationGateway(engine, device => SensorFactory.Create(device, renderer));
            List<Observation> terminated = new List<Observation>();
            gateway.RunTerminated += terminated.Add;
            List<Exception> endFailures = new List<Exception>();
            gateway.SensorEndFailed += endFailures.Add;
            gateway.Reset(0, Scenario(0.1, Camera("cam.test", isControllerVisible: false)));
            gateway.Step();
            Assert.That(renderer.Tokens.Count, Is.EqualTo(2));

            Observation failed = gateway.Step();

            Assert.That(failed.Status, Is.EqualTo(ObservationStatus.BackendFailed));
            Assert.That(terminated.Count, Is.EqualTo(1));
            Assert.That(renderer.Tokens[0].IsCancellationRequested, Is.True);
            Assert.That(renderer.Tokens[1].IsCancellationRequested, Is.True, "the render after the one whose callback threw");
            Assert.That(endFailures.Count, Is.EqualTo(1), "the callback's failure is reported, not lost");
            Assert.That(endFailures[0].ToString(), Does.Contain("The renderer's cancellation callback failed."));
        }

        // A camera the factory returns again for the next run is ended before it is reset, so a
        // cancellation callback that throws is reported and the Reset still starts the new run.
        [Test]
        public void AReusedCameraWhoseCancellationThrowsStillResets()
        {
            HeldRenderer renderer = new HeldRenderer { ThrowOnCancelAt = 0 };
            Dictionary<string, ISensor> built = new Dictionary<string, ISensor>();
            SimulationGateway gateway = new SimulationGateway(
                Engine(),
                device => built.TryGetValue(device.Definition.SensorId, out ISensor sensor)
                    ? sensor
                    : built[device.Definition.SensorId] = SensorFactory.Create(device, renderer));
            List<Exception> endFailures = new List<Exception>();
            gateway.SensorEndFailed += endFailures.Add;
            List<SensorOutputSet> outputs = new List<SensorOutputSet>();
            gateway.SensorOutputProduced += outputs.Add;
            gateway.Reset(0, Scenario(0.1, Camera("cam.test", isControllerVisible: false)));

            Assert.DoesNotThrow(() => gateway.Reset(1, Scenario(0.1, Camera("cam.test", isControllerVisible: false))));

            Assert.That(gateway.IsRunning, Is.True);
            Assert.That(endFailures.Count, Is.EqualTo(1));
            Assert.That(renderer.Tokens[0].IsCancellationRequested, Is.True);
            renderer.Complete(1);
            gateway.Step();
            Assert.That(outputs[outputs.Count - 1].TryGetFrame("cam.test", out SensorFrame<ImageFrame> frame), Is.True);
            Assert.That((frame.Sequence, frame.Status), Is.EqualTo((0L, SensorFrameStatus.Valid)));
        }

        // A finished run raises no RunTerminated, since nothing went wrong, and takes no more commands.
        [Test]
        public void AFinishedRunRaisesNoTerminationAndTakesNoMoreCommands()
        {
            SimulationGateway gateway = new SimulationGateway(Engine());
            List<Observation> terminated = new List<Observation>();
            gateway.RunTerminated += terminated.Add;
            gateway.Reset(0, Scenario(0.1, Rate("rate.main", isControllerVisible: true)));
            gateway.Step();

            gateway.Finish();
            gateway.Finish();

            Assert.That(gateway.IsRunning, Is.False);
            Assert.That(terminated, Is.Empty);
            Assert.Throws<InvalidOperationException>(() => gateway.Step());
            Assert.That(gateway.Stop("after finish"), Is.Null);
        }

        // CORE-21: a render that faults after its run ended has its exception observed, so it never
        // reaches TaskScheduler.UnobservedTaskException. A faulted task nobody observes is the control:
        // if this runtime does not finalize it here, the check cannot run.
        [TestCase(false)]
        [TestCase(true)]
        public void ARenderThatFaultsAfterItsRunEndedIsObserved(bool callbackThrows)
        {
            List<string> unobserved = new List<string>();
            EventHandler<UnobservedTaskExceptionEventArgs> record = (sender, args) =>
            {
                foreach (Exception exception in args.Exception.InnerExceptions)
                {
                    unobserved.Add(exception.Message);
                }

                args.SetObserved();
            };
            TaskScheduler.UnobservedTaskException += record;
            try
            {
                FaultAfterTheRunEnds("render lost after stop", callbackThrows);
                FaultUnobserved("control");
                for (int pass = 0; pass < 2; pass++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= record;
            }

            if (!unobserved.Contains("control"))
            {
                Assert.Inconclusive("This runtime did not finalize the unobserved control task.");
            }

            Assert.That(unobserved, Does.Not.Contain("render lost after stop"));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FaultAfterTheRunEnds(string message, bool callbackThrows)
        {
            HeldRenderer renderer = new HeldRenderer { ThrowOnCancelAt = callbackThrows ? 0 : -1 };
            SimulationGateway gateway = new SimulationGateway(Engine(), device => SensorFactory.Create(device, renderer));
            gateway.Reset(0, Scenario(0.1, Camera("cam.test", isControllerVisible: false)));
            gateway.Stop("test");
            renderer.Fail(0, message);
            renderer.Forget();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FaultUnobserved(string message)
        {
            TaskCompletionSource<ImageFrame> render = new TaskCompletionSource<ImageFrame>();
            render.SetException(new InvalidOperationException(message));
        }

        // CORE-21: a sensor with nothing pending is unaffected by its run ending, and ending it twice
        // (a stop, then the Reset that replaces it) is harmless.
        [Test]
        public void SensorsWithNothingPendingCarryOnAfterTheRunEnds()
        {
            HeldRenderer renderer = new HeldRenderer();
            SimulationGateway gateway = new SimulationGateway(
                Engine(),
                device => device.KindDefinition is CameraKind ? SensorFactory.Create(device, renderer) : SameDefinitionStandIn(device));
            List<SensorOutputSet> outputs = new List<SensorOutputSet>();
            gateway.SensorOutputProduced += outputs.Add;
            SimulationConfiguration scenario = Scenario(0.1, Rate("rate.body", isControllerVisible: true), Camera("cam.test", isControllerVisible: false));

            gateway.Reset(0, scenario);
            gateway.Step();
            gateway.Stop("test");
            gateway.Reset(0, scenario);
            Observation observation = gateway.Step();

            Assert.That(observation.Status, Is.EqualTo(ObservationStatus.Ok));
            Assert.That(observation.Frames.Count, Is.EqualTo(1));
            Assert.That(observation.Frames[0].SensorId, Is.EqualTo("rate.body"));
            Assert.That(observation.Frames[0].Sequence, Is.EqualTo(1));
            Assert.That(renderer.Tokens.Count, Is.EqualTo(4));
            Assert.That(new[] { 0, 1 }, Has.All.Matches<int>(index => renderer.Tokens[index].IsCancellationRequested));
            Assert.That(new[] { 2, 3 }, Has.All.Matches<int>(index => !renderer.Tokens[index].IsCancellationRequested));
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
            // The refused step didn't run: the engine last ran step 0's command, and step 1 comes next.
            Assert.That(engine.LastCommands.Value.Sequence, Is.EqualTo(0));
            Assert.That(gateway.NextSequence, Is.EqualTo(1));
            gateway.Step(new DeviceCommandSet(1, 0.1, new[] { torque }));
            Assert.That((log[2].Outcome, engine.LastCommands.Value.Sequence, gateway.NextSequence),
                Is.EqualTo((CommandOutcome.Clamped, 1L, 2L)));
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

        // CORE-22 with CORE-21: Reset checks the new sensors before it ends the old ones, so a refused
        // Reset leaves the old camera's render in flight, and its frame is still published.
        [Test]
        public void ARefusedResetLeavesTheOldCamerasRenderInFlight()
        {
            HeldRenderer renderer = new HeldRenderer();
            SimulationGateway gateway = new SimulationGateway(
                Engine(),
                device => device.Definition.SensorId == "rate.bad"
                    ? new IdealBodyRateSensorModel("rate.bad", "rate.bad", 0.1, "simulation")
                    : SensorFactory.Create(device, renderer));
            List<SensorOutputSet> outputs = new List<SensorOutputSet>();
            gateway.SensorOutputProduced += outputs.Add;
            gateway.Reset(0, Scenario(0.1, Camera("cam.test", isControllerVisible: false)));

            Assert.Throws<ArgumentException>(() => gateway.Reset(0, Scenario(0.1, Rate("rate.bad", isControllerVisible: true))));

            Assert.That(renderer.Tokens[0].IsCancellationRequested, Is.False);
            renderer.Complete(0);
            gateway.Step();
            Assert.That(outputs[1].TryGetFrame("cam.test", out SensorFrame<ImageFrame> frame), Is.True);
            Assert.That((frame.Sequence, frame.Status), Is.EqualTo((0L, SensorFrameStatus.Valid)));
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

            public void End()
            {
            }
        }

        private static AnalyticSimulationEngine Engine() => new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0);

        // Each render stays in flight until the test faults it; it records the token it was given.
        private sealed class HeldRenderer : IImageRenderer
        {
            private readonly List<TaskCompletionSource<ImageFrame>> _renders = new List<TaskCompletionSource<ImageFrame>>();

            public string RendererName => "held-renderer";
            public List<CancellationToken> Tokens { get; } = new List<CancellationToken>();
            public List<RenderRequest> Requests { get; } = new List<RenderRequest>();

            // The render at this index registers a cancellation callback that throws; -1 for none.
            public int ThrowOnCancelAt { get; set; } = -1;

            public Task<ImageFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
            {
                if (Tokens.Count == ThrowOnCancelAt)
                {
                    cancellationToken.Register(() => throw new InvalidOperationException("The renderer's cancellation callback failed."));
                }

                Tokens.Add(cancellationToken);
                Requests.Add(request);
                TaskCompletionSource<ImageFrame> render = new TaskCompletionSource<ImageFrame>();
                _renders.Add(render);
                return render.Task;
            }

            public void Fail(int index, string message) => _renders[index].SetException(new InvalidOperationException(message));

            // Completes a render with a mid-grey image of the requested size.
            public void Complete(int index)
            {
                RenderRequest request = Requests[index];
                byte[] pixels = new byte[request.Intrinsics.WidthPixels * request.Intrinsics.HeightPixels * 3];
                _renders[index].SetResult(new ImageFrame(request, ImageEncoding.Rgb8, pixels, RendererName));
            }

            // Drops every render, so only the camera could still be observing one.
            public void Forget() => _renders.Clear();
        }

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

        // ---- CORE-9: authority, heartbeat, failsafe, late and duplicate commands, the inhibit ----

        // One controller holds authority; a competing claim is refused and logged with the holder's ID;
        // after a release another controller can claim.
        [Test]
        public void OneControllerHoldsAuthorityAndAnotherClaimIsRefusedUntilItIsReleased()
        {
            Harness h = new Harness();

            Assert.That(h.Gateway.TryClaim("fsw", out string refusal), Is.True);
            Assert.That(refusal, Is.Null);
            Assert.That(h.Gateway.TryClaim("agent", out refusal), Is.False);
            Assert.That(refusal, Does.Contain("'fsw'"));
            Assert.That(h.Gateway.TryRelease("agent", out _), Is.False);
            Assert.That(h.Gateway.TryRelease("fsw", out _), Is.True);
            Assert.That(h.Gateway.TryClaim("agent", out _), Is.True);

            Assert.That(h.Changes(), Is.EqualTo(new[]
            {
                (AuthorityChange.Claimed, "fsw"), (AuthorityChange.Refused, "agent"),
                (AuthorityChange.Released, "fsw"), (AuthorityChange.Claimed, "agent"),
            }));
            Assert.That(h.Authority[1].Reason, Does.Contain("'fsw'"));
            Assert.That(h.Gateway.AuthorityHolder, Is.EqualTo("agent"));
        }

        // Commands from a controller without authority, and the owner's own command while a controller
        // holds it, are rejected and logged with the reason; the engine never sees them.
        [Test]
        public void CommandsFromOtherControllersAreRejectedAndNeverApplied()
        {
            Harness h = new Harness();
            h.Gateway.TryClaim("fsw", out _);

            Assert.That(h.Gateway.TrySubmit("agent", TorqueAt(0, 5e-4), out string refusal), Is.False);
            Assert.That(refusal, Does.Contain("'fsw'"));
            Assert.Throws<InvalidOperationException>(() => h.Gateway.Step(TorqueAt(0, 5e-4)));
            Assert.Throws<InvalidOperationException>(() => h.Gateway.Step("fsw", TorqueAt(0, 5e-4)));
            h.Gateway.Step();

            Assert.That(h.Outcomes(), Is.EqualTo(new[]
            {
                CommandOutcome.Rejected, CommandOutcome.Rejected, CommandOutcome.Rejected, CommandOutcome.Held,
            }));
            Assert.That(h.Commands[0].ControllerId, Is.EqualTo("agent"));
            Assert.That(h.Commands[1].ControllerId, Is.Null);
            Assert.That(h.Commands[1].Reason, Does.Contain("'fsw'"));
            Assert.That(h.Commands[2].Reason, Does.Contain("TrySubmit"));
            Assert.That(h.AppliedTorque(), Is.EqualTo(0.0));
        }

        // Acceptance 3, the timing test: a heartbeat missed past the timeout triggers the failsafe once.
        // Authority is dropped, a Lost record names the gap and the timeout, and every actuator runs
        // idle, logged Failsafe, until a controller claims again; that tenure starts from idle.
        [Test]
        public void AMissedHeartbeatFailsTheRunSafeOnce()
        {
            Harness h = new Harness(timeoutSeconds: 1.0);
            h.Gateway.TryClaim("fsw", out _);
            h.Gateway.TrySubmit("fsw", TorqueAt(0, 5e-4), out _);

            h.Clock.Set(0.5);
            h.Gateway.Step();
            Assert.That(h.AppliedTorque(), Is.EqualTo(5e-4));
            h.Clock.Set(1.0);
            h.Gateway.Step();
            Assert.That((h.Commands[1].Outcome, h.AppliedTorque()), Is.EqualTo((CommandOutcome.Held, 5e-4)));

            h.Clock.Set(1.6);
            h.Gateway.Step();
            Assert.That((h.Commands[2].Outcome, h.AppliedTorque(), h.Commands[2].ControllerId), Is.EqualTo((CommandOutcome.Failsafe, 0.0, (string)null)));
            Assert.That(h.Changes()[1], Is.EqualTo((AuthorityChange.Lost, "fsw")));
            Assert.That(h.Authority[1].Reason, Does.Contain("1.6").And.Contain("timeout is 1 s"));
            Assert.That((h.Gateway.AuthorityHolder, h.Gateway.IsFailsafe), Is.EqualTo(((string)null, true)));

            h.Clock.Set(5.0);
            h.Gateway.Step();
            Assert.That(h.Commands[3].Outcome, Is.EqualTo(CommandOutcome.Failsafe));
            Assert.That(h.Changes().Count(change => change.Item1 == AuthorityChange.Lost), Is.EqualTo(1));
            // The owner cannot replace the failsafe with its own command either.
            Assert.Throws<InvalidOperationException>(() => h.Gateway.Step(TorqueAt(4, 5e-4)));
            Assert.That(h.Commands[4].Outcome, Is.EqualTo(CommandOutcome.Rejected));

            Assert.That(h.Gateway.TryClaim("agent", out _), Is.True);
            h.Gateway.Step();
            Assert.That((h.Commands[5].Outcome, h.AppliedTorque(), h.Gateway.IsFailsafe), Is.EqualTo((CommandOutcome.Held, 0.0, false)));
        }

        // Each accepted call from the holder is its heartbeat: a heartbeat, a submission and a lockstep step
        // each keep its authority, so a holder heard within the timeout is never lost (blind review
        // round 1, finding 1). Without any one refresh, a step here finds the holder silent too long.
        [Test]
        public void EachCallFromTheHolderKeepsItsAuthority()
        {
            Harness h = new Harness(timeoutSeconds: 1.0);
            h.Gateway.TryClaim("fsw", out _);
            h.Clock.Set(0.8);
            Assert.That(h.Gateway.TryHeartbeat("fsw", out _), Is.True);
            h.Clock.Set(1.5);
            h.Gateway.Step();
            h.Clock.Set(1.7);
            Assert.That(h.Gateway.TrySubmit("fsw", TorqueAt(1, 5e-4), out _), Is.True);
            h.Clock.Set(2.6);
            h.Gateway.Step();

            Assert.That(h.Outcomes(), Is.EqualTo(new[] { CommandOutcome.Held, CommandOutcome.Applied }));
            Assert.That(h.Gateway.AuthorityHolder, Is.EqualTo("fsw"));

            Harness lockstep = new Harness(timeoutSeconds: 1.0, stepDriver: StepDriver.Controller);
            lockstep.Gateway.TryClaim("agent", out _);
            lockstep.Clock.Set(0.9);
            lockstep.Gateway.Step("agent", TorqueAt(0, 5e-4));
            lockstep.Clock.Set(1.8);
            lockstep.Gateway.Step("agent", TorqueAt(1, 5e-4));

            Assert.That(lockstep.Gateway.NextSequence, Is.EqualTo(2));
            Assert.That(lockstep.Changes(), Is.EqualTo(new[] { (AuthorityChange.Claimed, "agent") }));
        }

        // A refused call is not a heartbeat: a holder whose every command is refused, such as one whose
        // sequence counter froze, and that sends no heartbeat, is lost after the timeout instead of
        // holding its last command for ever, in either step driver (final review, finding 1).
        [Test]
        public void RefusedCommandsAreNotHeartbeats()
        {
            Harness h = new Harness(timeoutSeconds: 1.0);
            h.Gateway.TryClaim("fsw", out _);
            h.Gateway.TrySubmit("fsw", TorqueAt(0, 5e-4), out _);
            h.Gateway.Step();
            for (int i = 1; i <= 3; i++)
            {
                h.Clock.Set(0.9 * i);
                Assert.That(h.Gateway.TrySubmit("fsw", TorqueAt(0, 5e-4), out _), Is.False);
                h.Gateway.Step();
            }

            Assert.That(h.Changes().Last(), Is.EqualTo((AuthorityChange.Lost, "fsw")));
            Assert.That((h.Outcomes().Last(), h.AppliedTorque()), Is.EqualTo((CommandOutcome.Failsafe, 0.0)));

            Harness lockstep = new Harness(timeoutSeconds: 1.0, stepDriver: StepDriver.Controller);
            lockstep.Gateway.TryClaim("agent", out _);
            lockstep.Clock.Set(0.9);
            Assert.Throws<ArgumentException>(() => lockstep.Gateway.Step("agent", TorqueAt(5, 5e-4)));
            lockstep.Clock.Set(1.5);
            Assert.Throws<InvalidOperationException>(() => lockstep.Gateway.Step("agent", TorqueAt(0, 5e-4)));
            Assert.That(lockstep.Changes().Last(), Is.EqualTo((AuthorityChange.Lost, "agent")));
        }

        // An expired holder's own submission finds it lost first, so it can never revive itself with
        // a command in place of the failsafe (blind review round 1, finding 1).
        [Test]
        public void AnExpiredHoldersLateSubmissionIsRefusedAndNeverApplied()
        {
            Harness h = new Harness(timeoutSeconds: 1.0);
            h.Gateway.TryClaim("fsw", out _);

            h.Clock.Set(10.0);
            Assert.That(h.Gateway.TrySubmit("fsw", TorqueAt(0, 5e-4), out string refusal), Is.False);
            h.Gateway.Step();

            Assert.That(refusal, Does.Contain("lost"));
            Assert.That(h.Changes().Last(), Is.EqualTo((AuthorityChange.Lost, "fsw")));
            Assert.That(h.Outcomes(), Is.EqualTo(new[] { CommandOutcome.Rejected, CommandOutcome.Failsafe }));
            Assert.That(h.AppliedTorque(), Is.EqualTo(0.0));
        }

        // A clamped command is the holder's last applied command too: a held step repeats it at its
        // limit (blind review round 1, finding 4).
        [Test]
        public void AHeldStepRepeatsAClampedCommandAtItsLimit()
        {
            Harness h = new Harness();
            h.Gateway.TryClaim("fsw", out _);
            h.Gateway.TrySubmit("fsw", TorqueAt(0, 5e-3), out _);
            h.Gateway.Step();
            h.Gateway.Step();
            h.Gateway.Step();

            Assert.That(h.Outcomes(), Is.EqualTo(new[] { CommandOutcome.Clamped, CommandOutcome.Held, CommandOutcome.Held }));
            // A hold names the step whose command was sent, not an earlier hold.
            Assert.That(h.Commands[1].Reason, Does.Contain("holding step 0"));
            Assert.That(h.Commands[2].Reason, Does.Contain("holding step 0"));
            Assert.That(h.AppliedTorque(), Is.EqualTo(1e-3));
        }

        // An invalid command is reported as invalid, not as a duplicate of the one already waiting.
        [Test]
        public void AnInvalidSubmissionIsReportedAsInvalidEvenWhenACommandWaits()
        {
            Harness h = new Harness();
            h.Gateway.TryClaim("fsw", out _);
            h.Gateway.TrySubmit("fsw", TorqueAt(0, 5e-4), out _);

            Assert.That(h.Gateway.TrySubmit("fsw", TorqueAt(0, double.NaN), out string refusal), Is.False);
            Assert.That(refusal, Does.StartWith("The command is invalid"));
        }

        // Controllers stop on RunEnded, so every way a run ends raises it: a Reset that replaces a
        // running run, and an end whose RunTerminated subscriber throws (blind review round 1,
        // finding 3).
        [Test]
        public void EveryWayARunEndsIsAnnounced()
        {
            Harness h = new Harness(publish: false);
            int ended = 0;
            List<Observation> published = new List<Observation>();
            h.Gateway.RunEnded += () => ended++;
            h.Gateway.Reset(0, h.Scenario);
            h.Gateway.Reset(0, h.Scenario);
            Assert.That(ended, Is.EqualTo(1));

            h.Gateway.RunTerminated += observation => throw new IOException("recorder gone");
            h.Gateway.ObservationPublished += published.Add;
            h.Gateway.ObservationPublished += observation => throw new InvalidOperationException("controller gone");
            // The first subscriber's exception reaches the caller, not a later one's (round 2).
            Assert.Throws<IOException>(() => h.Gateway.Stop("stopped by the test"));

            Assert.That(ended, Is.EqualTo(2));
            Assert.That(published.Count, Is.EqualTo(1));
            Assert.That(published[0].IsTerminal, Is.True);
        }

        // The silence in a loss's reason is rounded up to the millisecond, so it never reads as no
        // longer than the timeout (blind review round 2).
        [Test]
        public void ALossReasonRoundsTheSilenceUp()
        {
            Harness h = new Harness(timeoutSeconds: 1.0);
            h.Gateway.TryClaim("fsw", out _);

            h.Clock.SetTicks(10_004_000);
            h.Gateway.Step();

            Assert.That(h.Authority.Last().Reason, Is.EqualTo("no heartbeat for 1.001 s; the timeout is 1 s"));
        }

        [Test]
        public void ATimeoutOfZeroNeverFires()
        {
            Harness h = new Harness(timeoutSeconds: 0.0);
            h.Gateway.TryClaim("fsw", out _);

            h.Clock.Set(1e6);
            h.Gateway.Step();

            Assert.That((h.Commands[0].Outcome, h.Gateway.AuthorityHolder), Is.EqualTo((CommandOutcome.Held, "fsw")));
        }

        // An expired holder is lost before its own late call can refresh it.
        [Test]
        public void AnExpiredHoldersOwnCallsAreRefusedAndTheLossIsLogged()
        {
            Harness h = new Harness(timeoutSeconds: 1.0);
            h.Gateway.TryClaim("fsw", out _);

            h.Clock.Set(2.0);
            Assert.That(h.Gateway.TryHeartbeat("fsw", out string refusal), Is.False);
            Assert.That(refusal, Does.Contain("lost"));
            Assert.That(h.Gateway.TrySubmit("fsw", TorqueAt(0, 5e-4), out _), Is.False);
            h.Gateway.Step();

            Assert.That(h.Changes(), Is.EqualTo(new[] { (AuthorityChange.Claimed, "fsw"), (AuthorityChange.Lost, "fsw") }));
            Assert.That(h.Outcomes(), Is.EqualTo(new[] { CommandOutcome.Rejected, CommandOutcome.Failsafe }));
            Assert.That(h.AppliedTorque(), Is.EqualTo(0.0));
        }

        // Acceptance 4: a command for a step that already ran (late), for a later step (early), or a
        // second one for the same step (duplicate) is rejected and recorded, never applied. The first
        // accepted command stands, and a step with no new command holds the last one.
        [Test]
        public void LateEarlyAndDuplicateCommandsAreRejectedAndTheStepHolds()
        {
            Harness h = new Harness();
            h.Gateway.TryClaim("fsw", out _);

            Assert.That(h.Gateway.TrySubmit("fsw", TorqueAt(0, 5e-4), out _), Is.True);
            Assert.That(h.Gateway.TrySubmit("fsw", TorqueAt(0, 6e-4), out string duplicate), Is.False);
            Assert.That(h.Gateway.TrySubmit("fsw", TorqueAt(1, 6e-4), out string early), Is.False);
            h.Gateway.Step();
            Assert.That(h.Gateway.TrySubmit("fsw", TorqueAt(0, 7e-4), out string late), Is.False);
            h.Gateway.Step();

            Assert.That((duplicate, early, late), Is.EqualTo((h.Commands[0].Reason, h.Commands[1].Reason, h.Commands[3].Reason)));
            Assert.That(duplicate, Does.Contain("duplicate"));
            Assert.That(early, Does.Contain("early"));
            Assert.That(late, Does.Contain("late"));
            Assert.That(h.Outcomes(), Is.EqualTo(new[]
            {
                CommandOutcome.Rejected, CommandOutcome.Rejected, CommandOutcome.Applied, CommandOutcome.Rejected, CommandOutcome.Held,
            }));
            Assert.That(h.Commands[2].ControllerId, Is.EqualTo("fsw"));
            Assert.That(h.Commands[4].Reason, Does.Contain("holding step 0"));
            Assert.That((h.AppliedTorque(), h.Engine.LastCommands.Value.Sequence), Is.EqualTo((5e-4, 1L)));
            Assert.That(h.Commands[4].Requested.Commands, Is.Empty);
        }

        // A new tenure starts from idle, so a takeover never holds the previous controller's command,
        // the owner's, or a command a claim discarded (design review round 1, finding 2).
        [Test]
        public void ATakeoverHoldsIdleNotThePreviousCommand()
        {
            Harness h = new Harness(timeoutSeconds: 1.0);
            h.Gateway.TryClaim("fsw", out _);
            h.Gateway.TrySubmit("fsw", TorqueAt(0, 5e-4), out _);
            h.Gateway.Step();
            h.Gateway.TrySubmit("fsw", TorqueAt(1, 6e-4), out _);

            h.Clock.Set(2.0);
            Assert.That(h.Gateway.TryClaim("agent", out _), Is.True);
            h.Gateway.Step();

            Assert.That(h.Changes(), Is.EqualTo(new[]
            {
                (AuthorityChange.Claimed, "fsw"), (AuthorityChange.Lost, "fsw"), (AuthorityChange.Claimed, "agent"),
            }));
            Assert.That(h.Outcomes(), Is.EqualTo(new[] { CommandOutcome.Applied, CommandOutcome.Rejected, CommandOutcome.Held }));
            Assert.That((h.Commands[1].ControllerId, h.Commands[1].Requested.Sequence), Is.EqualTo(("fsw", 1L)));
            Assert.That(h.Commands[1].Reason, Does.Contain("Discarded"));
            Assert.That((h.Commands[2].ControllerId, h.AppliedTorque()), Is.EqualTo(("agent", 0.0)));
        }

        [Test]
        public void AClaimAfterTheOwnersCommandOrAReleaseHoldsIdle()
        {
            Harness h = new Harness();
            h.Gateway.Step(TorqueAt(0, 5e-4));
            h.Gateway.TryClaim("fsw", out _);
            h.Gateway.Step();
            Assert.That((h.Commands[1].Outcome, h.AppliedTorque()), Is.EqualTo((CommandOutcome.Held, 0.0)));

            h.Gateway.TrySubmit("fsw", TorqueAt(2, 5e-4), out _);
            h.Gateway.Step();
            h.Gateway.TryRelease("fsw", out _);
            h.Gateway.TryClaim("agent", out _);
            h.Gateway.Step();

            Assert.That((h.Commands[3].Outcome, h.Commands[3].ControllerId, h.AppliedTorque()), Is.EqualTo((CommandOutcome.Held, "agent", 0.0)));
        }

        // A re-claim by the holder starts a new tenure: a restarted controller inherits nothing.
        [Test]
        public void AReclaimStartsANewTenure()
        {
            Harness h = new Harness();
            h.Gateway.TryClaim("fsw", out _);
            h.Gateway.TrySubmit("fsw", TorqueAt(0, 5e-4), out _);
            h.Gateway.Step();
            h.Gateway.TrySubmit("fsw", TorqueAt(1, 6e-4), out _);

            Assert.That(h.Gateway.TryClaim("fsw", out _), Is.True);
            h.Gateway.Step();

            Assert.That(h.Changes(), Is.EqualTo(new[] { (AuthorityChange.Claimed, "fsw"), (AuthorityChange.Claimed, "fsw") }));
            Assert.That(h.Outcomes(), Is.EqualTo(new[] { CommandOutcome.Applied, CommandOutcome.Rejected, CommandOutcome.Held }));
            Assert.That(h.AppliedTorque(), Is.EqualTo(0.0));
        }

        // Every accepted command that never reaches its step is logged as discarded, under its run,
        // when the run finishes, stops or is reset (design review round 1, finding 3).
        [Test]
        public void APendingCommandIsLoggedWhenTheRunEnds()
        {
            foreach (Action<Harness> end in new Action<Harness>[]
            {
                harness => harness.Gateway.Finish(),
                harness => harness.Gateway.Stop("stopped by the test"),
                harness => harness.Gateway.Reset(0, harness.Scenario),
            })
            {
                Harness h = new Harness();
                List<string> order = new List<string>();
                h.Gateway.CommandLogged += record => order.Add(record.Outcome.ToString());
                h.Gateway.RunStarted += (run, sensors) => order.Add("RunStarted");
                h.Gateway.TryClaim("fsw", out _);
                h.Gateway.Step();
                h.Gateway.TrySubmit("fsw", TorqueAt(1, 5e-4), out _);

                end(h);

                CommandRecord discarded = h.Commands[1];
                Assert.That((discarded.Outcome, discarded.ControllerId, discarded.Sequence), Is.EqualTo((CommandOutcome.Rejected, "fsw", 1L)));
                Assert.That(discarded.Reason, Does.Contain("Discarded"));
                Assert.That(order.Take(2), Is.EqualTo(new[] { "Held", "Rejected" }));
                Assert.That(h.AppliedTorque(), Is.EqualTo(0.0));
            }
        }

        // A discard whose log subscriber throws ends the run once, without recursion, and the caller
        // gets the exception; a reset that failed this way starts no new run (round 2, finding N2).
        [Test]
        public void ADiscardWhoseLogFailsEndsTheRunOnce()
        {
            foreach (bool atReset in new[] { false, true })
            {
                Harness h = new Harness();
                List<Observation> terminated = new List<Observation>();
                int started = 0;
                h.Gateway.RunTerminated += terminated.Add;
                h.Gateway.RunStarted += (run, sensors) => started++;
                h.Gateway.TryClaim("fsw", out _);
                h.Gateway.TrySubmit("fsw", TorqueAt(0, 5e-4), out _);
                h.Gateway.CommandLogged += record =>
                {
                    if (record.Outcome == CommandOutcome.Rejected)
                    {
                        throw new IOException("disk full");
                    }
                };

                Assert.Throws<IOException>(() =>
                {
                    if (atReset)
                    {
                        h.Gateway.Reset(0, h.Scenario);
                    }
                    else
                    {
                        h.Gateway.Finish();
                    }
                });

                Assert.That(terminated.Count, Is.EqualTo(1), atReset ? "Reset" : "Finish");
                Assert.That(terminated[0].Status, Is.EqualTo(ObservationStatus.Stopped));
                Assert.That((h.Gateway.IsRunning, started), Is.EqualTo((false, 0)));
            }
        }

        // Lockstep (StepDriver.Controller): only the holder steps; the owner and other controllers
        // cannot, and an expired holder is lost when it next calls.
        [Test]
        public void InALockstepRunOnlyTheHolderSteps()
        {
            Harness h = new Harness(timeoutSeconds: 1.0, stepDriver: StepDriver.Controller);
            Assert.Throws<InvalidOperationException>(() => h.Gateway.Step());
            Assert.Throws<InvalidOperationException>(() => h.Gateway.Step(TorqueAt(0, 5e-4)));
            h.Gateway.TryClaim("agent", out _);
            Assert.Throws<InvalidOperationException>(() => h.Gateway.Step("other", TorqueAt(0, 5e-4)));
            Assert.That(h.Gateway.TrySubmit("agent", TorqueAt(0, 5e-4), out string refusal), Is.False);
            Assert.That(refusal, Does.Contain("Step"));

            Observation next = h.Gateway.Step("agent", TorqueAt(0, 5e-4));
            Assert.That((next.Sequence, h.AppliedTorque(), h.Commands.Last().ControllerId), Is.EqualTo((1L, 5e-4, "agent")));

            h.Clock.Set(5.0);
            Assert.Throws<InvalidOperationException>(() => h.Gateway.Step("agent", TorqueAt(1, 5e-4)));
            Assert.That(h.Changes().Last(), Is.EqualTo((AuthorityChange.Lost, "agent")));
            Assert.That(h.Gateway.NextSequence, Is.EqualTo(1));
        }

        [Test]
        public void CheckHeartbeatLogsALossWithoutStepping()
        {
            Harness h = new Harness(timeoutSeconds: 1.0, stepDriver: StepDriver.Controller);
            h.Gateway.TryClaim("agent", out _);

            h.Clock.Set(0.5);
            h.Gateway.CheckHeartbeat();
            Assert.That(h.Gateway.AuthorityHolder, Is.EqualTo("agent"));
            h.Clock.Set(1.5);
            h.Gateway.CheckHeartbeat();
            h.Gateway.CheckHeartbeat();

            Assert.That(h.Changes(), Is.EqualTo(new[] { (AuthorityChange.Claimed, "agent"), (AuthorityChange.Lost, "agent") }));
            Assert.That((h.Gateway.AuthorityHolder, h.Gateway.NextSequence), Is.EqualTo(((string)null, 0L)));
        }

        // Acceptance 5 and the design reviews: physical actuators are refused where a frozen run could
        // not stop them (lockstep), and while they are enabled only a controller with authority may
        // command a non-idle value.
        [Test]
        public void PhysicalActuatorsAreRefusedInALockstepRunAndGuardedInAPacedOne()
        {
            PhysicalActuatorSetting lifted = new PhysicalActuatorSetting(true, "bench-coil-1");
            SimulationGateway lockstep = new SimulationGateway(new ApplyingEngine(Engine()), stepDriver: StepDriver.Controller);
            Assert.Throws<ArgumentException>(() => lockstep.Reset(0, Harness.ScenarioWith(1.0, lifted)));
            Assert.That(lockstep.IsRunning, Is.False);

            Harness h = new Harness(timeoutSeconds: 1.0, physical: lifted);
            Assert.That(h.Gateway.PhysicalActuatorsEnabled, Is.True);
            Assert.Throws<ArgumentException>(() => h.Gateway.Step(TorqueAt(0, 5e-4)));
            h.Gateway.Step(TorqueAt(0, 0.0));
            h.Gateway.TryClaim("fsw", out _);
            h.Gateway.TrySubmit("fsw", TorqueAt(1, 5e-4), out _);
            h.Gateway.Step();

            Assert.That(h.Outcomes(), Is.EqualTo(new[] { CommandOutcome.Rejected, CommandOutcome.Applied, CommandOutcome.Applied }));
            Assert.That(h.AppliedTorque(), Is.EqualTo(5e-4));
            Assert.That(new Harness().Gateway.PhysicalActuatorsEnabled, Is.False);
            // The flag is the current run's: an ended run arms nothing.
            h.Gateway.Stop("stopped by the test");
            Assert.That(h.Gateway.PhysicalActuatorsEnabled, Is.False);
        }

        // HOST-6 revokes a session's authority when the link closes; it is a loss.
        [Test]
        public void ARevokeIsALossAndARevokeOfANonHolderDoesNothing()
        {
            SimulationGateway idle = new SimulationGateway(Engine());
            idle.Revoke("fsw", "no run yet");

            Harness h = new Harness();
            h.Gateway.TryClaim("fsw", out _);
            h.Gateway.TrySubmit("fsw", TorqueAt(0, 5e-4), out _);
            h.Gateway.Revoke("agent", "never held it");
            Assert.That(h.Authority.Count, Is.EqualTo(1));

            h.Gateway.Revoke("fsw", "link session closed");
            h.Gateway.Step();

            Assert.That(h.Changes().Last(), Is.EqualTo((AuthorityChange.Lost, "fsw")));
            Assert.That(h.Authority.Last().Reason, Is.EqualTo("link session closed"));
            Assert.That(h.Outcomes(), Is.EqualTo(new[] { CommandOutcome.Rejected, CommandOutcome.Failsafe }));
        }

        // Observations reach controllers only once their step is committed, so a handler already sees
        // the next step; a completed run tells controllers it ended.
        [Test]
        public void ObservationsArePublishedOnceCommittedAndTheRunEndIsAnnounced()
        {
            Harness h = new Harness(publish: false);
            List<(long, long)> published = new List<(long, long)>();
            int ended = 0;
            h.Gateway.ObservationPublished += observation => published.Add((observation.Sequence, h.Gateway.NextSequence));
            h.Gateway.RunEnded += () => ended++;

            h.Gateway.Reset(0, h.Scenario);
            h.Gateway.Step();
            Observation end = h.Gateway.Stop("stopped by the test");

            Assert.That(published, Is.EqualTo(new[] { (0L, 0L), (1L, 1L), (end.Sequence, 1L) }));
            Assert.That(ended, Is.EqualTo(1));

            h.Gateway.Reset(0, h.Scenario);
            h.Gateway.Finish();
            Assert.That(ended, Is.EqualTo(2));
        }

        // A controller answers after the call that published returns: a gateway call from inside one
        // of its events is refused, and nothing is stepped or logged twice (round 2, finding N1).
        [Test]
        public void ACallFromInsideAGatewayEventIsRefused()
        {
            Harness h = new Harness(publish: false);
            List<Exception> refused = new List<Exception>();
            h.Gateway.ObservationPublished += observation => Catch(refused, () => h.Gateway.Step());
            h.Gateway.CommandLogged += record => Catch(refused, () => h.Gateway.TryClaim("fsw", out _));
            h.Gateway.CommandLogged += record => Catch(refused, () => h.Gateway.TrySubmit("fsw", TorqueAt(1, 5e-4), out _));

            h.Gateway.Reset(0, h.Scenario);
            h.Gateway.Step();

            Assert.That(refused.Count, Is.EqualTo(4));
            Assert.That(refused, Is.All.InstanceOf<InvalidOperationException>());
            Assert.That((h.Gateway.NextSequence, h.Commands.Count, h.Gateway.AuthorityHolder), Is.EqualTo((1L, 1, (string)null)));
        }

        [Test]
        public void ClaimsNeedARunningRunAndAUsableControllerId()
        {
            SimulationGateway idle = new SimulationGateway(Engine());
            Assert.That(idle.TryClaim("fsw", out string refusal), Is.False);
            Assert.That(refusal, Is.Not.Null);

            Harness h = new Harness();
            foreach (string id in new[] { null, "", " ", new string('a', 65), "fsw\n1" })
            {
                Assert.That(h.Gateway.TryClaim(id, out _), Is.False, id);
            }

            Assert.That(h.Authority, Is.Empty);
            h.Gateway.TryClaim("fsw", out _);
            h.Gateway.Reset(0, h.Scenario);
            Assert.That(h.Gateway.AuthorityHolder, Is.Null);
            Assert.That(h.Gateway.TryClaim("agent", out _), Is.True);
        }

        // Production gateways time the heartbeat on the wall clock (no clock passed): a holder silent
        // past the timeout is lost (final review 2).
        [Test]
        public void TheDefaultClockIsTheWallClock()
        {
            SimulationGateway gateway = new SimulationGateway(new ApplyingEngine(Engine()));
            List<AuthorityRecord> authority = new List<AuthorityRecord>();
            gateway.AuthorityLogged += authority.Add;
            gateway.Reset(0, Harness.ScenarioWith(0.05, default));
            gateway.TryClaim("fsw", out _);

            Thread.Sleep(200);
            gateway.Step();

            Assert.That(authority.Last().Change, Is.EqualTo(AuthorityChange.Lost));
        }

        // A lockstep step's own engine and publish time is not controller silence: the holder cannot
        // call while the gateway runs its step (final review 2).
        [Test]
        public void ALockstepStepsOwnDurationIsNotSilence()
        {
            Harness h = new Harness(timeoutSeconds: 1.0, stepDriver: StepDriver.Controller);
            h.Gateway.TryClaim("agent", out _);
            h.Gateway.StateProduced += state => h.Clock.Set(h.Clock.Read().TotalSeconds + 3.0);

            h.Gateway.Step("agent", TorqueAt(0, 5e-4));
            h.Gateway.Step("agent", TorqueAt(1, 5e-4));

            Assert.That(h.Gateway.NextSequence, Is.EqualTo(2));
            Assert.That(h.Changes(), Is.EqualTo(new[] { (AuthorityChange.Claimed, "agent") }));
        }

        // Controllers of a replaced run learn it ended even when a new sensor fails to reset.
        [Test]
        public void AReplacingResetAnnouncesTheEndEvenIfANewSensorFailsToReset()
        {
            bool fail = false;
            SimulationGateway gateway = new SimulationGateway(Engine(), device => new ResetFailingSensor(device.Definition, () => fail));
            int ended = 0;
            gateway.RunEnded += () => ended++;
            SimulationConfiguration scenario = Scenario(0.1, Rate("imu.rate", isControllerVisible: false));
            gateway.Reset(0, scenario);

            fail = true;
            Assert.Throws<InvalidOperationException>(() => gateway.Reset(0, scenario));

            Assert.That((ended, gateway.IsRunning), Is.EqualTo((1, false)));
        }

        // A replacing Reset ends the old sensors before it announces the end, so a RunEnded
        // subscriber that throws cannot leave their renders running (CORE-21; final review 3).
        [Test]
        public void AReplacingResetEndsTheOldSensorsBeforeAnnouncingTheEnd()
        {
            List<ResetFailingSensor> built = new List<ResetFailingSensor>();
            SimulationGateway gateway = new SimulationGateway(Engine(), device =>
            {
                ResetFailingSensor sensor = new ResetFailingSensor(device.Definition, () => false);
                built.Add(sensor);
                return sensor;
            });
            SimulationConfiguration scenario = Scenario(0.1, Rate("imu.rate", isControllerVisible: false));
            gateway.Reset(0, scenario);
            gateway.RunEnded += () => throw new IOException("controller gone");

            Assert.Throws<IOException>(() => gateway.Reset(0, scenario));

            Assert.That(built[0].EndCount, Is.EqualTo(1));
        }

        // A refusal for an unusable controller ID says so, even with no run, since its line carries no
        // controller (final review 2).
        [Test]
        public void AnUnusableControllerIdIsTheReasonEvenWithNoRun()
        {
            Harness h = new Harness();
            h.Gateway.Stop("stopped by the test");

            Assert.That(h.Gateway.TrySubmit("fsw\n1", TorqueAt(0, 0.0), out string refusal), Is.False);
            Assert.Throws<ArgumentException>(() => h.Gateway.Step("fsw\n1", TorqueAt(0, 0.0)));

            Assert.That(refusal, Does.Contain("control characters"));
            Assert.That(h.Commands.Select(record => (record.Reason, record.ControllerId)), Is.EqualTo(new[] { (refusal, (string)null), (refusal, (string)null) }));
        }

        [Test]
        public void AnUndefinedStepDriverIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationGateway(Engine(), stepDriver: (StepDriver)7));
        }

        // A sensor whose Reset throws while fails() is true.
        private sealed class ResetFailingSensor : ISensor
        {
            private readonly Func<bool> _fails;

            public ResetFailingSensor(SensorDefinition definition, Func<bool> fails)
            {
                Definition = definition;
                _fails = fails;
            }

            public SensorDefinition Definition { get; }

            public void Reset(SensorResetContext context)
            {
                if (_fails())
                {
                    throw new InvalidOperationException("The sensor could not reset.");
                }
            }

            public bool TrySample(SensorSampleContext context, out ISensorFrame frame)
            {
                frame = null;
                return false;
            }

            public int EndCount { get; private set; }

            public void End() => EndCount++;
        }

        private static void Catch(List<Exception> caught, Action call)
        {
            try
            {
                call();
            }
            catch (Exception exception)
            {
                caught.Add(exception);
            }
        }

        // A command for step k at a 0.1 s step naming the ideal body torque device, x axis only.
        private static DeviceCommandSet TorqueAt(long sequence, double x) =>
            Command(sequence, "torque.body", TorqueValues(new Vector3d(x, 0.0, 0.0)));

        private sealed class FakeClock
        {
            private TimeSpan _now;

            public TimeSpan Read() => _now;

            public void Set(double seconds) => _now = TimeSpan.FromSeconds(seconds);

            // Exact ticks: Mono's TimeSpan.FromSeconds rounds to the millisecond.
            public void SetTicks(long ticks) => _now = TimeSpan.FromTicks(ticks);
        }

        // A host-stepped (by default) gateway over an engine that applies commands, with one ideal body
        // torque device (clamp at 1e-3 N m), a fake clock at 0, and its command and authority logs.
        private sealed class Harness
        {
            public Harness(
                double timeoutSeconds = 0.0,
                StepDriver stepDriver = StepDriver.Host,
                PhysicalActuatorSetting physical = default,
                bool publish = true)
            {
                Engine = new ApplyingEngine(SimulationGatewayTests.Engine());
                Gateway = new SimulationGateway(Engine, stepDriver: stepDriver, clock: Clock.Read);
                Gateway.CommandLogged += Commands.Add;
                Gateway.AuthorityLogged += Authority.Add;
                Scenario = ScenarioWith(timeoutSeconds, physical);
                if (publish)
                {
                    Gateway.Reset(0, Scenario);
                }
            }

            public FakeClock Clock { get; } = new FakeClock();
            public ApplyingEngine Engine { get; }
            public SimulationGateway Gateway { get; }
            public SimulationConfiguration Scenario { get; }
            public List<CommandRecord> Commands { get; } = new List<CommandRecord>();
            public List<AuthorityRecord> Authority { get; } = new List<AuthorityRecord>();

            public static SimulationConfiguration ScenarioWith(double timeoutSeconds, PhysicalActuatorSetting physical) =>
                new SimulationConfiguration(
                    "test-run",
                    DateTimeOffset.UnixEpoch,
                    0.1,
                    devices: new[] { IdealBodyTorque("torque.body") },
                    heartbeatTimeoutWallSeconds: timeoutSeconds,
                    physicalActuators: physical);

            public CommandOutcome[] Outcomes() => Commands.Select(record => record.Outcome).ToArray();

            public (AuthorityChange, string)[] Changes() =>
                Authority.Select(record => (record.Change, record.ControllerId)).ToArray();

            // The x torque of the last command the engine ran; 0 when it ran none.
            public double AppliedTorque() =>
                Engine.LastCommands.HasValue
                    ? Engine.LastCommands.Value.Commands[0].Values.Vector3("torque_body_n_m").X
                    : 0.0;
        }
    }
}
