using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace Argus.Simulation.Core
{
    // Who steps a gateway's runs (CORE-9), fixed when it is built.
    public enum StepDriver
    {
        // The gateway's owner (headless/Host, a test) calls Step() or Step(command); a controller with
        // authority submits the next step's command with TrySubmit. Paced real-time and HIL runs, and
        // unpaced host runs.
        Host = 0,

        // Only the controller holding authority steps, with Step(controllerId, command): lockstep SIL
        // and agent runs (target-architecture §4). Physical actuators are refused.
        Controller = 1
    }

    // Deterministic closed-loop session, used by the tests and headless/Host. It returns only
    // controller-visible observations (D7) in the DATA-5 order (target-architecture §4):
    // Reset(seed, scenario) returns observation 0, and a step applies command k over [t_k, t_k+1) and
    // returns observation k+1. Truth and every sensor frame, including ground truth, reach the
    // recorder and Unity through StateProduced and SensorOutputProduced; every command it receives,
    // applies, holds or discards reaches it through CommandLogged, and every authority change through
    // AuthorityLogged (D6).
    // Each actuator command is held to its device's limits by its kind, then clamped or rejected per
    // the device's limit_policy (CORE-10). One controller at a time holds actuator authority
    // (IControllerGateway, CORE-9); its accepted calls are its heartbeat, and a missed heartbeat past the run's
    // timeout drops it; an owner-stepped run then runs every actuator idle (Failsafe) until a controller
    // claims again, and a lockstep run waits for a claim. Each
    // tenure starts from idle. Single-threaded: call it from one thread, never from inside one of its
    // events. Serving it to HIL and external controllers is headless/Host GatewayServer (HOST-6).
    public sealed class SimulationGateway : IControllerGateway
    {
        private const int MaximumControllerIdLength = 64;
        private const string NotRunning = "The run has not started or has ended; call Reset.";

        private readonly ISimulationEngine _engine;
        private readonly Func<DeviceConfiguration, ISensor> _sensorFactory;
        private readonly StepDriver _stepDriver;
        private readonly Func<TimeSpan> _clock;
        private SimulationConfiguration _scenario;
        private SensorManager _sensors;
        private HashSet<string> _controllerVisibleSensorIds;
        private long _nextSequence;
        private double _nextSimulationTimeSeconds;
        private SpacecraftState _lastSpacecraft;
        private bool _isRunning;
        // RunStarted has fired for _scenario, so a command can be logged against that run.
        private bool _hasStartedRun;

        // Authority (CORE-9). The holder and when it was last heard; the failsafe after a loss; the
        // one submitted command waiting for the next step; and the command a held step repeats, with
        // the step it was sent for (-1: idle, as every tenure starts).
        private string _holder;
        private TimeSpan _lastHeard;
        private bool _isFailsafe;
        private string _failsafeReason;
        private PendingCommand _pending;
        private DeviceCommandSet _held;
        private long _heldFromSequence = -1;
        // Above 0 while one of the gateway's events runs; every public call is refused then.
        private int _eventDepth;

        // sensorFactory builds the run's sensors from the scenario; SensorFactory.Create by default.
        // clock is a monotonic wall clock for the heartbeat; a Stopwatch by default.
        public SimulationGateway(
            ISimulationEngine engine,
            Func<DeviceConfiguration, ISensor> sensorFactory = null,
            StepDriver stepDriver = StepDriver.Host,
            Func<TimeSpan> clock = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _sensorFactory = sensorFactory ?? SensorFactory.Create;
            switch (stepDriver)
            {
                case StepDriver.Host:
                case StepDriver.Controller:
                    _stepDriver = stepDriver;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(stepDriver), stepDriver, "Not a known step driver.");
            }

            _clock = clock ?? StopwatchClock();
        }

        // Raised by each successful Reset, before that run's state at t_0, with the run as configured
        // (the seed applied) and its sensors; the recorder starts a dataset directory here (D6).
        public event Action<SimulationConfiguration, IReadOnlyList<SensorDefinition>> RunStarted;

        // Every truth state, starting with the state at t_0.
        public event Action<SimulationState> StateProduced;

        // Every sensor frame sampled at a step, controller-visible or not.
        public event Action<SensorOutputSet> SensorOutputProduced;

        // The terminal observation when the run ends, so the recorder knows why (D6).
        public event Action<Observation> RunTerminated;

        // The command log (D6, D7): every command the gateway receives once a run has started, applied,
        // clamped, held, failsafe or rejected (a discarded submission included), raised before the
        // engine runs it or the call throws.
        public event Action<CommandRecord> CommandLogged;

        // The authority log (D6, CORE-9): every claim, refusal, release and loss once a run has started.
        public event Action<AuthorityRecord> AuthorityLogged;

        // Every controller-visible observation, once its step is committed (NextSequence already
        // advanced): observation 0, each step's, and the terminal one (CORE-9).
        public event Action<Observation> ObservationPublished;

        // The run ended: stopped, failed, finished, or replaced by a Reset (CORE-9). Finish and a
        // replacing Reset publish no terminal observation. A subscriber that throws during a replacing
        // Reset fails that Reset, as a RunStarted subscriber's would: the old run has ended, and no new
        // one starts.
        public event Action RunEnded;

        // A sensor that could not release what it held when its run ended, such as a renderer whose
        // cancellation callback throws (CORE-21). The run ends as it would have; this only reports the
        // failure, for logs. A subscriber that throws is ignored.
        public event Action<Exception> SensorEndFailed;

        // The backend exception that ended the run, for logs. Its message is also in the terminal
        // observation's reason, which in-process controllers see (IControllerGateway.ObservationPublished);
        // the gateway link sends them the status only (HOST-6).
        public Exception Failure { get; private set; }

        // True from a successful Reset until the run terminates.
        public bool IsRunning => _isRunning;

        // The sequence and time the next command must carry: k and t_k.
        public long NextSequence => _nextSequence;
        public double NextSimulationTimeSeconds => _nextSimulationTimeSeconds;

        // The controller holding actuator authority, or null.
        public string AuthorityHolder => _holder;

        // A controller lost authority and no controller has claimed it since: every actuator runs idle.
        public bool IsFailsafe => _isFailsafe;

        // The physical-actuator inhibit of the current run is lifted (CORE-9); HIL-1 (#66) enforces it.
        public bool PhysicalActuatorsEnabled => _isRunning && _scenario.PhysicalActuators.Enabled;

        // Starts the scenario with seed in place of its RandomSeed and returns observation 0. The
        // same seed and scenario give the same observations. Every run starts with no controller.
        public Observation Reset(int seed, SimulationConfiguration scenario)
        {
            ThrowIfInsideEvent();
            if (seed < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(seed), "The seed must be >= 0.");
            }

            SimulationConfiguration run = scenario.WithRandomSeed(seed);
            if (!run.IsValid)
            {
                throw new ArgumentException("A valid scenario is required.", nameof(scenario));
            }

            // A frozen lockstep run cannot stop hardware that keeps running (D2: HIL runs are paced).
            if (run.PhysicalActuators.Enabled && !AllowsPhysicalActuators(_stepDriver))
            {
                throw new ArgumentException(
                    "A run its controller steps (lockstep) cannot enable physical actuators: hardware keeps running while the run waits.",
                    nameof(scenario));
            }

            // Sensors first, so a factory that disagrees with its device is refused like an invalid
            // scenario: before the engine is configured (for Basilisk, a call to the service) and with
            // the run in progress left running (CORE-22).
            SensorManager sensors = BuildSensors(run);
            // The run in progress, if any, ends here. A command a controller submitted for it is logged
            // as discarded first, under that run and while it still runs, so it lands in its segment
            // (CORE-9).
            DiscardPending("the run was reset");
            bool replacesRun = _isRunning;
            _isRunning = false;
            _hasStartedRun = false;
            Failure = null;
            ClearAuthority();
            // Its sensors are replaced without being sampled again, so they release their renders first
            // (CORE-21). End is idempotent, so sensors a stop, failure or Finish already ended are
            // unaffected. Only after BuildSensors, so a refused Reset leaves the run in progress and its
            // renders untouched (CORE-22). Before the new sensors' Reset, so a sensor the factory returns
            // again has nothing left to release there, and a failure to release is reported
            // (SensorEndFailed), not thrown.
            EndSensors();
            // Controllers of the replaced run stop here, as at any other end: once its sensors have
            // ended, and before anything below can fail.
            if (replacesRun)
            {
                Raise(RunEnded);
            }

            sensors.Reset(new SensorResetContext(run.RunId, run.EpochUtc, run.RandomSeed));
            _engine.Initialize(run);
            _scenario = run;
            _sensors = sensors;
            _controllerVisibleSensorIds = ControllerVisibleSensorIds(run);

            if (!_engine.TryReset(out SimulationState state) || !IsExpectedState(state, 0, 0.0, null))
            {
                throw new InvalidOperationException(
                    $"Simulation backend '{_engine.BackendName}' failed to produce a valid state at t_0.");
            }

            // The recorder starts its dataset directory before the state at t_0 (D6); the run is
            // committed only once that state and its frames are published.
            _nextSequence = 0;
            _nextSimulationTimeSeconds = 0.0;
            Raise(RunStarted, run, (IReadOnlyList<SensorDefinition>)SensorDefinitions(_sensors));
            _hasStartedRun = true;
            // Running from here, so a subscriber that throws in Publish ends the run at t_0.
            _lastSpacecraft = state.Spacecraft;
            _isRunning = true;
            Observation first = Publish(state);
            PublishToControllers(first);
            return first;
        }

        // The owner's step (StepDriver.Host). With no controller, the empty command: no output (D11).
        // With a controller holding authority, its submitted command for this step, or else its last
        // applied command again (Held). After a loss, every actuator idle (Failsafe).
        public Observation Step()
        {
            ThrowIfInsideEvent();
            RequireOwnerSteps();
            if (_isRunning)
            {
                CheckLiveness();
            }

            if (!_isRunning || (_holder == null && !_isFailsafe))
            {
                return OwnerStep(DeviceCommandSet.None(_nextSequence, _nextSimulationTimeSeconds));
            }

            if (_holder != null)
            {
                return _pending != null ? AdvancePending() : AdvanceHeld();
            }

            DeviceCommandSet none = DeviceCommandSet.None(_nextSequence, _nextSimulationTimeSeconds);
            return Advance(new CommandRecord(
                _scenario.RunId,
                _nextSequence,
                _nextSimulationTimeSeconds,
                CommandOutcome.Failsafe,
                none,
                WithIdleActuators(none),
                _failsafeReason));
        }

        // The owner's command (StepDriver.Host, no controller holding authority): applies command k over
        // [t_k, t_k+1) and returns observation k+1. A backend failure ends the run with a BackendFailed
        // observation instead of throwing; Failure keeps the cause. Every command is logged first
        // (CommandLogged), including one Step then rejects.
        public Observation Step(DeviceCommandSet command)
        {
            ThrowIfInsideEvent();
            if (_isRunning)
            {
                string refusal = null;
                if (_stepDriver != StepDriver.Host)
                {
                    refusal = "This run is stepped by the controller holding authority; its owner cannot step it.";
                }
                else
                {
                    CheckLiveness();
                    if (_holder != null)
                    {
                        refusal = $"Controller '{_holder}' holds actuator authority, so the owner's command is refused.";
                    }
                    else if (_isFailsafe)
                    {
                        refusal = $"{_failsafeReason} A controller must claim authority before any command.";
                    }
                }

                if (refusal != null)
                {
                    Log(Rejected(command, refusal));
                    throw new InvalidOperationException(refusal);
                }
            }

            return OwnerStep(command);
        }

        // StepDriver.Controller (lockstep): the controller holding authority applies command k and gets
        // observation k+1. An accepted step is its heartbeat; its liveness is checked first.
        public Observation Step(string controllerId, DeviceCommandSet command)
        {
            ThrowIfInsideEvent();
            // An unusable ID is the reason first: its line can carry no controller.
            string idProblem = ControllerIdProblem(controllerId);
            if (idProblem != null)
            {
                if (_hasStartedRun)
                {
                    Log(Rejected(command, idProblem));
                }

                throw new ArgumentException(idProblem, nameof(controllerId));
            }

            if (!_isRunning)
            {
                if (_hasStartedRun)
                {
                    Log(Rejected(command, NotRunning, controllerId));
                }

                throw new InvalidOperationException(NotRunning);
            }

            string refusal = null;
            if (_stepDriver != StepDriver.Controller)
            {
                refusal = "This run is stepped by its owner; submit commands with TrySubmit.";
            }
            else
            {
                CheckLiveness();
                if (_holder != controllerId)
                {
                    refusal = NotHolder(controllerId);
                }
            }

            if (refusal != null)
            {
                Log(Rejected(command, refusal, controllerId));
                throw new InvalidOperationException(refusal);
            }

            string rejection = Rejection(command, out DeviceCommandSet limited, out string clamped);
            if (rejection != null)
            {
                Log(Rejected(command, rejection, controllerId));
                throw new ArgumentException(rejection, nameof(command));
            }

            // Only an accepted command is a heartbeat: a controller whose every command is refused is
            // not controlling, so it fails safe after the timeout (CORE-9 final review).
            _lastHeard = _clock();
            Observation next = Advance(new CommandRecord(
                _scenario.RunId,
                _nextSequence,
                _nextSimulationTimeSeconds,
                clamped == null ? CommandOutcome.Applied : CommandOutcome.Clamped,
                command,
                WithIdleActuators(limited),
                clamped,
                controllerId));
            // The step's own engine and publish time is not silence: the holder cannot call during it.
            if (_holder == controllerId)
            {
                _lastHeard = _clock();
            }

            return next;
        }

        // StepDriver.Host: the controller holding authority submits the command for the next step,
        // which the owner's Step() applies. Only for step k, before the owner starts it: an earlier
        // step's command is late, a later one early, a second one for k a duplicate. A refusal is
        // logged, never applied; an accepted command is logged at the step that applies it, or as
        // discarded if it never gets there.
        public bool TrySubmit(string controllerId, DeviceCommandSet command, out string refusal)
        {
            ThrowIfInsideEvent();
            refusal = SubmissionRefusal(controllerId, command, out DeviceCommandSet limited, out string clamped);
            if (refusal != null)
            {
                if (_hasStartedRun)
                {
                    Log(Rejected(command, refusal, UsableOrNull(controllerId)));
                }

                return false;
            }

            // Only an accepted command is a heartbeat (see Step(controllerId, command)).
            _lastHeard = _clock();
            _pending = new PendingCommand(controllerId, command, limited, clamped);
            return true;
        }

        // Granted when no controller holds authority, when the holder has missed its heartbeat (it is
        // lost first), or to the holder itself; each grant starts a new tenure from idle, so a
        // restarted controller inherits nothing. Refused while another controller holds it (logged).
        public bool TryClaim(string controllerId, out string refusal)
        {
            ThrowIfInsideEvent();
            refusal = ControllerIdProblem(controllerId) ?? (_isRunning ? null : NotRunning);
            if (refusal != null)
            {
                return false;
            }

            CheckLiveness();
            if (_holder != null && _holder != controllerId)
            {
                refusal = $"Controller '{_holder}' holds actuator authority.";
                LogAuthority(AuthorityChange.Refused, controllerId, refusal);
                return false;
            }

            if (_holder != null)
            {
                DiscardPending($"controller '{controllerId}' claimed authority again");
            }

            _holder = controllerId;
            _lastHeard = _clock();
            _isFailsafe = false;
            _failsafeReason = null;
            _held = WithIdleActuators(DeviceCommandSet.None(_nextSequence, _nextSimulationTimeSeconds));
            _heldFromSequence = -1;
            LogAuthority(AuthorityChange.Claimed, controllerId, null);
            return true;
        }

        // The holder gives authority up; the run is uncontrolled again and its owner's steps idle.
        public bool TryRelease(string controllerId, out string refusal)
        {
            ThrowIfInsideEvent();
            refusal = HolderRefusal(controllerId);
            if (refusal != null)
            {
                return false;
            }

            _holder = null;
            DiscardPending($"controller '{controllerId}' released authority");
            ClearAuthority();
            LogAuthority(AuthorityChange.Released, controllerId, null);
            return true;
        }

        public bool TryHeartbeat(string controllerId, out string refusal)
        {
            ThrowIfInsideEvent();
            refusal = HolderRefusal(controllerId);
            if (refusal != null)
            {
                return false;
            }

            _lastHeard = _clock();
            return true;
        }

        // The owner's liveness check without a step (HOST-6 calls it while it waits): a holder silent
        // past the timeout is lost.
        public void CheckHeartbeat()
        {
            ThrowIfInsideEvent();
            if (_isRunning)
            {
                CheckLiveness();
            }
        }

        // The owner drops the holder, such as when its link session ends (HOST-6): a loss, logged with
        // the reason. Does nothing for a controller that does not hold authority, or with no run.
        public void Revoke(string controllerId, string reason)
        {
            ThrowIfInsideEvent();
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("A reason is required.", nameof(reason));
            }

            if (_isRunning && _holder != null && _holder == controllerId)
            {
                Lose(reason);
            }
        }

        // Ends a running run on the caller's behalf (Ctrl+C, a recorder failure), so subscribers such
        // as the recorder learn why it ended. Returns the terminal observation, or null when no run
        // is running.
        public Observation Stop(string reason)
        {
            ThrowIfInsideEvent();
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("A reason is required.", nameof(reason));
            }

            return _isRunning ? End(ObservationStatus.Stopped, reason) : null;
        }

        // The run reached its last step: no sensor is sampled again, so each releases what it holds,
        // such as a camera's renders (CORE-21), and Step refuses further commands. Nothing went wrong,
        // so there is no terminal observation and RunTerminated is not raised; the recording ends as
        // complete. A submitted command that never ran is logged as discarded first. Does nothing when
        // no run is running. Every driver that stops stepping at a run's last step calls it
        // (headless/Host does); otherwise the sensors keep their renders until the next Reset.
        public void Finish()
        {
            ThrowIfInsideEvent();
            if (!_isRunning)
            {
                return;
            }

            DiscardPending("the run reached its last step");
            _isRunning = false;
            ClearAuthority();
            EndSensors();
            Raise(RunEnded);
        }

        // The owner's own command, with no controller holding authority.
        private Observation OwnerStep(DeviceCommandSet command)
        {
            if (!_isRunning)
            {
                if (_hasStartedRun)
                {
                    Log(Rejected(command, NotRunning));
                }

                throw new InvalidOperationException(NotRunning);
            }

            string rejection = Rejection(command, out DeviceCommandSet limited, out string clamped);
            // With the inhibit lifted, only a controller holding authority moves an actuator (CORE-9).
            if (rejection == null && PhysicalActuatorsEnabled && !IsIdle(limited))
            {
                rejection = "Physical actuators are enabled, so only a controller holding authority may command a non-idle value.";
            }

            if (rejection != null)
            {
                Log(Rejected(command, rejection));
                throw new ArgumentException(rejection, nameof(command));
            }

            // The record keeps the command as sent and what the engine runs: within the limits, or
            // clamped to them with the reason (CORE-10).
            return Advance(new CommandRecord(
                _scenario.RunId,
                _nextSequence,
                _nextSimulationTimeSeconds,
                clamped == null ? CommandOutcome.Applied : CommandOutcome.Clamped,
                command,
                WithIdleActuators(limited),
                clamped));
        }

        // The holder's submitted command for this step.
        private Observation AdvancePending()
        {
            PendingCommand pending = _pending;
            _pending = null;
            if (pending.Requested.Sequence != _nextSequence)
            {
                // Every step consumes or discards the pending command, so this is a bug.
                throw new InvalidOperationException(
                    $"A command submitted for step {pending.Requested.Sequence} was still pending at step {_nextSequence}.");
            }

            return Advance(new CommandRecord(
                _scenario.RunId,
                _nextSequence,
                _nextSimulationTimeSeconds,
                pending.Clamped == null ? CommandOutcome.Applied : CommandOutcome.Clamped,
                pending.Requested,
                WithIdleActuators(pending.Limited),
                pending.Clamped,
                pending.ControllerId));
        }

        // No command arrived for this step: the tenure's last applied command again, restamped to it.
        // Its requested set is empty, since nothing was sent for this step.
        private Observation AdvanceHeld()
        {
            DeviceCommandSet held = new DeviceCommandSet(_nextSequence, _nextSimulationTimeSeconds, _held.Commands);
            string reason = _heldFromSequence < 0
                ? $"No command for step {_nextSequence} from controller '{_holder}'; holding idle."
                : $"No command for step {_nextSequence} from controller '{_holder}'; holding step {_heldFromSequence}'s.";
            return Advance(new CommandRecord(
                _scenario.RunId,
                _nextSequence,
                _nextSimulationTimeSeconds,
                CommandOutcome.Held,
                DeviceCommandSet.None(_nextSequence, _nextSimulationTimeSeconds),
                held,
                reason,
                _holder));
        }

        // Logs the record, runs its applied command over [t_k, t_k+1), publishes observation k+1, and
        // only then publishes it to controllers.
        private Observation Advance(CommandRecord record)
        {
            DeviceCommandSet applied = record.Applied.Value;
            Log(record);

            long sequence = _nextSequence + 1;
            double simulationTimeSeconds = sequence * _scenario.FixedStepSeconds;
            SimulationState state;
            bool produced;
            try
            {
                produced = _engine.TryStep(new SimulationStepInput(_nextSequence, _nextSimulationTimeSeconds, applied), out state) &&
                    IsExpectedState(state, sequence, simulationTimeSeconds, applied);
            }
            catch (InvalidOperationException exception)
            {
                // A backend failure, such as BasiliskEngine's wrapped RpcException.
                return Terminate(exception);
            }
            catch (OperationCanceledException exception)
            {
                // Cancellation, such as Ctrl+C during a Basilisk RPC, is not a backend failure. The
                // engine may or may not have run the step, so the run ends Stopped and a retry cannot
                // log a second Applied record for it.
                EndBeforeRethrow(() => End(ObservationStatus.Stopped, $"Step {sequence} was cancelled: {exception.Message}"));
                throw;
            }
            catch (Exception exception)
            {
                // Any other exception is a bug and propagates with its stack trace. It still ends the
                // run, so a retry cannot log a second Applied record for this step.
                EndBeforeRethrow(() => Terminate(exception));
                throw;
            }

            if (!produced)
            {
                return Terminate(new InvalidOperationException(
                    $"Simulation backend '{_engine.BackendName}' failed to produce a valid state at step {sequence}."));
            }

            Observation observation = Publish(state);
            _nextSequence = sequence;
            _nextSimulationTimeSeconds = simulationTimeSeconds;
            // The command a held step repeats is the holder's last one that was sent and applied.
            if (_holder != null && record.ControllerId == _holder && IsSentCommand(record.Outcome))
            {
                _held = applied;
                _heldFromSequence = record.Sequence;
            }

            PublishToControllers(observation);
            return observation;
        }

        // A command a controller sent and the engine runs, as opposed to one the gateway made up for
        // the step (Held, Failsafe) or refused.
        private static bool IsSentCommand(CommandOutcome outcome)
        {
            switch (outcome)
            {
                case CommandOutcome.Applied:
                case CommandOutcome.Clamped:
                    return true;
                case CommandOutcome.Held:
                case CommandOutcome.Failsafe:
                case CommandOutcome.Rejected:
                    return false;
                default:
                    throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a known command outcome.");
            }
        }

        // Whether a step driver may run physical actuators: only one that keeps stepping while a
        // controller is silent, so the failsafe can act (D2).
        private static bool AllowsPhysicalActuators(StepDriver stepDriver)
        {
            switch (stepDriver)
            {
                case StepDriver.Host:
                    return true;
                case StepDriver.Controller:
                    return false;
                default:
                    throw new ArgumentOutOfRangeException(nameof(stepDriver), stepDriver, "Not a known step driver.");
            }
        }

        // Why a submission is refused, or null.
        private string SubmissionRefusal(string controllerId, DeviceCommandSet command, out DeviceCommandSet limited, out string clamped)
        {
            limited = command;
            clamped = null;
            // An unusable ID is the reason first: its line can carry no controller.
            string idProblem = ControllerIdProblem(controllerId);
            if (idProblem != null)
            {
                return idProblem;
            }

            if (!_isRunning)
            {
                return NotRunning;
            }

            if (_stepDriver != StepDriver.Host)
            {
                return "This run is stepped by the controller holding authority; call Step(controllerId, command).";
            }

            CheckLiveness();
            if (_holder != controllerId)
            {
                return NotHolder(controllerId);
            }

            // An invalid command is refused as invalid first, so it is never mistaken for a duplicate.
            if (!command.IsValid)
            {
                return Rejection(command, out limited, out clamped);
            }

            if (command.Sequence < _nextSequence)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "The command for step {0} is late: step {1} is next.",
                    command.Sequence,
                    _nextSequence);
            }

            if (command.Sequence > _nextSequence)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "The command for step {0} is early: only the next step, {1}, takes a command.",
                    command.Sequence,
                    _nextSequence);
            }

            if (_pending != null)
            {
                return $"A command for step {_nextSequence} is already accepted, so this one is a duplicate.";
            }

            return Rejection(command, out limited, out clamped);
        }

        // Why a release or heartbeat from this controller is refused, or null.
        private string HolderRefusal(string controllerId)
        {
            string refusal = ControllerIdProblem(controllerId) ?? (_isRunning ? null : NotRunning);
            if (refusal != null)
            {
                return refusal;
            }

            CheckLiveness();
            return _holder == controllerId ? null : NotHolder(controllerId);
        }

        private string NotHolder(string controllerId)
        {
            if (_holder != null)
            {
                return $"Controller '{controllerId}' does not hold actuator authority; controller '{_holder}' does.";
            }

            return _isFailsafe
                ? $"Controller '{controllerId}' does not hold actuator authority. {_failsafeReason} Claim it first."
                : $"Controller '{controllerId}' does not hold actuator authority; claim it first.";
        }

        // A holder silent past the run's timeout is lost; a timeout of 0 never fires.
        private void CheckLiveness()
        {
            double timeoutSeconds = _scenario.HeartbeatTimeoutWallSeconds;
            if (_holder == null || timeoutSeconds <= 0.0)
            {
                return;
            }

            // From ticks, since TimeSpan.TotalSeconds differs in its last bits between Mono (Unity) and
            // .NET. The reason gives the silence rounded up to the millisecond, so it never reads as
            // no longer than the timeout.
            long silentTicks = (_clock() - _lastHeard).Ticks;
            double silentSeconds = silentTicks / (double)TimeSpan.TicksPerSecond;
            if (silentSeconds > timeoutSeconds)
            {
                long silentMilliseconds = (silentTicks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond;
                Lose(string.Format(
                    CultureInfo.InvariantCulture,
                    "no heartbeat for {0:0.###} s; the timeout is {1:R} s",
                    silentMilliseconds / 1000.0,
                    timeoutSeconds));
            }
        }

        // The holder loses authority: its pending command is discarded and the run fails safe.
        private void Lose(string reason)
        {
            string lost = _holder;
            _holder = null;
            _isFailsafe = true;
            _failsafeReason = $"Failsafe since step {_nextSequence}: controller '{lost}' lost authority ({reason}).";
            _held = default;
            _heldFromSequence = -1;
            DiscardPending($"controller '{lost}' lost authority ({reason})");
            LogAuthority(AuthorityChange.Lost, lost, reason);
        }

        // Clears the pending command first, then logs it as discarded under the current run, so a log
        // subscriber that throws (which ends the run through End) finds nothing left to discard.
        private void DiscardPending(string reason)
        {
            PendingCommand pending = _pending;
            _pending = null;
            if (pending == null || !_hasStartedRun)
            {
                return;
            }

            Log(new CommandRecord(
                _scenario.RunId,
                _nextSequence,
                _nextSimulationTimeSeconds,
                CommandOutcome.Rejected,
                pending.Requested,
                null,
                $"Discarded before step {_nextSequence} ran: {reason}.",
                pending.ControllerId));
        }

        private void ClearAuthority()
        {
            _holder = null;
            _isFailsafe = false;
            _failsafeReason = null;
            _pending = null;
            _held = default;
            _heldFromSequence = -1;
        }

        private void RequireOwnerSteps()
        {
            if (_stepDriver != StepDriver.Host)
            {
                throw new InvalidOperationException(
                    "This run is stepped by the controller holding authority; its owner cannot step it.");
            }
        }

        // Ends the run. The observation is the last step that exists, k, so its time and UTC are the
        // backend's own; the reason names the step that failed.
        private Observation Terminate(Exception failure)
        {
            Failure = failure;
            return End(ObservationStatus.BackendFailed, $"Step {_lastSpacecraft.Sequence + 1} failed: {failure.Message}");
        }

        private Observation End(ObservationStatus status, string reason)
        {
            // A submitted command that never ran is logged under this run while it still runs; a
            // discard whose log fails ends the run here again, with nothing left to discard.
            DiscardPending($"the run ended ({reason})");
            _isRunning = false;
            ClearAuthority();
            Observation observation = new Observation(
                _scenario.RunId,
                _lastSpacecraft.Sequence,
                _lastSpacecraft.SimulationTimeSeconds,
                _lastSpacecraft.TimestampUtc,
                _engine.BackendName,
                Array.Empty<ISensorFrame>(),
                status,
                reason);
            // No sensor is sampled again for this run, so each releases what it holds, such as a
            // camera's renders (CORE-21).
            EndSensors();
            // Controllers learn of the end even when a RunTerminated subscriber (the recorder) throws,
            // and the first subscriber's exception, not a later one's, reaches the caller.
            ExceptionDispatchInfo failure = null;
            RaiseCapturing(() => Raise(RunTerminated, observation), ref failure);
            RaiseCapturing(() => Raise(ObservationPublished, observation), ref failure);
            RaiseCapturing(() => Raise(RunEnded), ref failure);
            failure?.Throw();
            return observation;
        }

        // Ends the current sensors' run. A sensor that throws (a renderer's token callback) is
        // reported through SensorEndFailed instead of thrown: every other sensor has still been ended
        // (SensorManager.End), and it must not change how the run ended, such as a BackendFailed
        // observation becoming an exception from Step.
        private void EndSensors()
        {
            try
            {
                _sensors?.End();
            }
            catch (Exception failure)
            {
                try
                {
                    Raise(SensorEndFailed, failure);
                }
                catch
                {
                }
            }
        }

        // Ends a running run before an exception propagates, so RunTerminated still tells subscribers
        // such as the recorder why. A RunTerminated subscriber that also throws is ignored here: the
        // run has ended either way, and the caller sees the original exception.
        private void EndBeforeRethrow(Func<Observation> end)
        {
            if (!_isRunning)
            {
                return;
            }

            try
            {
                end();
            }
            catch
            {
            }
        }

        // Why Step refuses a command for step k, or null when it is valid and for k at t_k.
        // limited is the command held to its devices' limits, and clamped says which values a clamp
        // policy changed (null when none did).
        private string Rejection(DeviceCommandSet command, out DeviceCommandSet limited, out string clamped)
        {
            limited = command;
            clamped = null;
            if (!command.IsValid)
            {
                string problem = command.FindProblem();
                return problem != null
                    ? $"The command is invalid: {problem}."
                    : "The command is invalid: its sequence or time is negative or not finite.";
            }

            if (command.Sequence != _nextSequence)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "The command is for sequence {0}; the next step is {1}.",
                    command.Sequence,
                    _nextSequence);
            }

            if (!SimulationTime.AreSame(command.ApplyAtSimulationTimeSeconds, _nextSimulationTimeSeconds))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "The command is for t = {0:R} s; step {1} is at t = {2:R} s.",
                    command.ApplyAtSimulationTimeSeconds,
                    _nextSequence,
                    _nextSimulationTimeSeconds);
            }

            // Only the run's actuators take commands, each with exactly its kind's command values,
            // then held to the device's limits by its kind (CORE-10): a reject policy refuses the whole
            // command, and a clamp policy passes each value at its nearest limit.
            List<DeviceCommand> devices = new List<DeviceCommand>(command.Commands.Count);
            List<string> clamps = new List<string>();
            foreach (DeviceCommand device in command.Commands)
            {
                DeviceConfiguration? configured = ConfiguredActuator(device.DeviceId);
                if (!configured.HasValue)
                {
                    return $"Device '{device.DeviceId}' is not an actuator this run configures.";
                }

                IActuatorKind actuator = (IActuatorKind)configured.Value.KindDefinition;
                string problem = actuator.FindCommandProblem(device.Values);
                if (problem != null)
                {
                    return $"Device '{device.DeviceId}' command {problem}.";
                }

                DeviceValues parameters = configured.Value.Parameters;
                DeviceValues values = actuator.Limit(parameters, device.Values, out string excess);
                if (excess != null)
                {
                    switch (actuator.LimitPolicyOf(parameters))
                    {
                        case ActuatorLimitPolicy.Clamp:
                            clamps.Add($"Device '{device.DeviceId}' command {excess}; clamped.");
                            break;
                        case ActuatorLimitPolicy.Reject:
                            return $"Device '{device.DeviceId}' command {excess}, and its limit_policy is reject.";
                        default:
                            // A valid configuration has a known policy, so this is a bug, not a refusal.
                            throw new InvalidOperationException(
                                $"Device '{device.DeviceId}' has limit_policy {actuator.LimitPolicyOf(parameters)}, which the gateway does not apply.");
                    }
                }

                // An engine that only records commands (the analytic fixture) would log a command as
                // Applied that never acted, so refuse it and the run continues. BasiliskEngine applies
                // every actuator its service accepted at ConfigureRun (BSK-8, #21).
                if (!_engine.AppliesActuatorCommands && !actuator.IsIdle(values))
                {
                    return $"Device '{device.DeviceId}': {_engine.BackendName} does not apply actuator commands.";
                }

                devices.Add(excess == null ? device : new DeviceCommand(device.DeviceId, values));
            }

            if (clamps.Count > 0)
            {
                limited = new DeviceCommandSet(command.Sequence, command.ApplyAtSimulationTimeSeconds, devices);
                clamped = string.Join(" ", clamps);
            }

            return null;
        }

        // Every commanded actuator is at its kind's idle values.
        private bool IsIdle(DeviceCommandSet command)
        {
            foreach (DeviceCommand device in command.Commands)
            {
                DeviceConfiguration? configured = ConfiguredActuator(device.DeviceId);
                if (configured.HasValue && !((IActuatorKind)configured.Value.KindDefinition).IsIdle(device.Values))
                {
                    return false;
                }
            }

            return true;
        }

        // The run's actuator with this device ID, or null.
        private DeviceConfiguration? ConfiguredActuator(string deviceId)
        {
            foreach (DeviceConfiguration device in _scenario.Devices)
            {
                if (device.Definition.SensorId == deviceId)
                {
                    return device.KindDefinition is IActuatorKind ? device : (DeviceConfiguration?)null;
                }
            }

            return null;
        }

        // The command the engine runs: the controller's, then every configured actuator it leaves
        // out at its kind's idle values, so one record replays the step (docs/device-model.md §6).
        private DeviceCommandSet WithIdleActuators(DeviceCommandSet command)
        {
            List<DeviceCommand> commands = new List<DeviceCommand>(command.Commands);
            foreach (DeviceConfiguration device in _scenario.Devices)
            {
                if (device.KindDefinition is IActuatorKind actuator && !IsCommanded(command, device.Definition.SensorId))
                {
                    commands.Add(new DeviceCommand(device.Definition.SensorId, actuator.IdleValues));
                }
            }

            return commands.Count == command.Commands.Count
                ? command
                : new DeviceCommandSet(command.Sequence, command.ApplyAtSimulationTimeSeconds, commands);
        }

        private static bool IsCommanded(DeviceCommandSet command, string deviceId)
        {
            foreach (DeviceCommand device in command.Commands)
            {
                if (device.DeviceId == deviceId)
                {
                    return true;
                }
            }

            return false;
        }

        private CommandRecord Rejected(DeviceCommandSet command, string reason, string controllerId = null) =>
            new CommandRecord(
                _scenario.RunId, _nextSequence, _nextSimulationTimeSeconds, CommandOutcome.Rejected, command, null, reason, controllerId);

        // A subscriber that throws ends the run, so the engine never runs a command the log missed,
        // and the caller gets the subscriber's exception, even in place of a rejection: a log with a
        // gap is not a log. Subscribers that already logged the record learn from RunTerminated that
        // the run ended at this step.
        private void Log(CommandRecord record)
        {
            try
            {
                Raise(CommandLogged, record);
            }
            catch (Exception exception)
            {
                EndBeforeRethrow(() => End(
                    ObservationStatus.Stopped, $"Logging the command for step {record.Sequence} failed: {exception.Message}"));
                throw;
            }
        }

        // As Log, for the authority log.
        private void LogAuthority(AuthorityChange change, string controllerId, string reason)
        {
            AuthorityRecord record = new AuthorityRecord(
                _scenario.RunId, _nextSequence, _nextSimulationTimeSeconds, controllerId, change, reason);
            try
            {
                Raise(AuthorityLogged, record);
            }
            catch (Exception exception)
            {
                EndBeforeRethrow(() => End(
                    ObservationStatus.Stopped, $"Logging the authority change at step {record.Sequence} failed: {exception.Message}"));
                throw;
            }
        }

        // Publishes truth and every frame, and returns the controller-visible frames. A sensor or
        // subscriber that throws ends the run, so the gateway never advances past a step whose
        // frames were not all published.
        private Observation Publish(SimulationState state)
        {
            SensorOutputSet output;
            try
            {
                Raise(StateProduced, state);
                output = _sensors.Sample(new SensorSampleContext(_scenario.RunId, state));
                Raise(SensorOutputProduced, output);
            }
            catch (Exception exception)
            {
                EndBeforeRethrow(() => End(
                    ObservationStatus.Stopped, $"Publishing step {state.Spacecraft.Sequence} failed: {exception.Message}"));
                throw;
            }

            int visibleCount = 0;
            foreach (ISensorFrame frame in output.Frames)
            {
                if (_controllerVisibleSensorIds.Contains(frame.SensorId))
                {
                    visibleCount++;
                }
            }

            ISensorFrame[] visible = new ISensorFrame[visibleCount];
            int index = 0;
            foreach (ISensorFrame frame in output.Frames)
            {
                if (_controllerVisibleSensorIds.Contains(frame.SensorId))
                {
                    visible[index++] = frame;
                }
            }

            SpacecraftState spacecraft = state.Spacecraft;
            _lastSpacecraft = spacecraft;
            return Observation.Owning(
                state.RunId,
                spacecraft.Sequence,
                spacecraft.SimulationTimeSeconds,
                spacecraft.TimestampUtc,
                state.DynamicsBackend,
                visible,
                ObservationStatus.Ok);
        }

        // ObservationPublished, once the step is committed. A subscriber that throws ends the run, as
        // in Publish.
        private void PublishToControllers(Observation observation)
        {
            try
            {
                Raise(ObservationPublished, observation);
            }
            catch (Exception exception)
            {
                EndBeforeRethrow(() => End(
                    ObservationStatus.Stopped, $"Publishing observation {observation.Sequence} failed: {exception.Message}"));
                throw;
            }
        }

        // Raises an event and keeps the first exception any of a series throws.
        private static void RaiseCapturing(Action raise, ref ExceptionDispatchInfo failure)
        {
            try
            {
                raise();
            }
            catch (Exception exception)
            {
                failure = failure ?? ExceptionDispatchInfo.Capture(exception);
            }
        }

        // Every event is raised through these, so a gateway call from inside a subscriber is refused
        // (ThrowIfInsideEvent): a controller answers after the call that published returns (CORE-9).
        private void Raise(Action handler)
        {
            if (handler == null)
            {
                return;
            }

            _eventDepth++;
            try
            {
                handler();
            }
            finally
            {
                _eventDepth--;
            }
        }

        private void Raise<T>(Action<T> handler, T value)
        {
            if (handler == null)
            {
                return;
            }

            _eventDepth++;
            try
            {
                handler(value);
            }
            finally
            {
                _eventDepth--;
            }
        }

        private void Raise<T1, T2>(Action<T1, T2> handler, T1 first, T2 second)
        {
            if (handler == null)
            {
                return;
            }

            _eventDepth++;
            try
            {
                handler(first, second);
            }
            finally
            {
                _eventDepth--;
            }
        }

        private void ThrowIfInsideEvent()
        {
            if (_eventDepth > 0)
            {
                throw new InvalidOperationException(
                    "A gateway call from inside one of its events is refused; answer after the call that raised it returns.");
            }
        }

        // Non-blank, at most 64 characters, no control characters; HOST-6 gives each link session its own.
        private static string ControllerIdProblem(string controllerId)
        {
            if (string.IsNullOrWhiteSpace(controllerId))
            {
                return "A controller ID is required.";
            }

            if (controllerId.Length > MaximumControllerIdLength)
            {
                return $"A controller ID has at most {MaximumControllerIdLength} characters; this one has {controllerId.Length}.";
            }

            foreach (char character in controllerId)
            {
                if (char.IsControl(character))
                {
                    return "A controller ID has no control characters.";
                }
            }

            return null;
        }

        private static string UsableOrNull(string controllerId) =>
            ControllerIdProblem(controllerId) == null ? controllerId : null;

        private static Func<TimeSpan> StopwatchClock()
        {
            Stopwatch watch = Stopwatch.StartNew();
            return () => watch.Elapsed;
        }

        private bool IsExpectedState(
            SimulationState state,
            long sequence,
            double simulationTimeSeconds,
            DeviceCommandSet? command)
        {
            if (!state.IsValid ||
                state.RunId != _scenario.RunId ||
                state.Spacecraft.Sequence != sequence ||
                !SimulationTime.AreSame(state.Spacecraft.SimulationTimeSeconds, simulationTimeSeconds) ||
                state.HasAppliedCommands != command.HasValue)
            {
                return false;
            }

            return !command.HasValue ||
                (state.AppliedCommands.Value.Sequence == command.Value.Sequence &&
                 SimulationTime.AreSame(
                     state.AppliedCommands.Value.ApplyAtSimulationTimeSeconds,
                     command.Value.ApplyAtSimulationTimeSeconds));
        }

        // Builds and checks the run's sensors without resetting them: Reset resets them once the run in
        // progress has ended.
        private SensorManager BuildSensors(SimulationConfiguration run)
        {
            SensorManager sensors = new SensorManager();
            // Only sensor devices publish frames; actuators take commands (D11).
            foreach (DeviceConfiguration device in run.Devices)
            {
                if (IsSensor(device))
                {
                    ISensor sensor = _sensorFactory(device);
                    if (sensor == null)
                    {
                        throw new ArgumentException($"The sensor factory built no sensor for device '{device.Definition.SensorId}'.");
                    }

                    string differs = DifferingPart(sensor.Definition, device.Definition);
                    if (differs != null)
                    {
                        throw new ArgumentException(
                            $"The sensor built for device '{device.Definition.SensorId}' must keep its device's " +
                            $"{differs}; it has another.");
                    }

                    sensors.Register(sensor);
                }
            }

            return sensors;
        }

        // The manifest lists each device as configured, while its frames carry the built sensor's
        // ID, frame and source, on that sensor's period, and a camera places its frames by that
        // sensor's mount; so those must agree (CORE-22). The model name and controller visibility
        // are recorded and enforced from the configuration alone, so a stand-in model may differ.
        private static string DifferingPart(SensorDefinition built, SensorDefinition configured)
        {
            if (!string.Equals(built.SensorId, configured.SensorId, StringComparison.Ordinal))
            {
                return "ID";
            }

            if (!string.Equals(built.FrameId, configured.FrameId, StringComparison.Ordinal))
            {
                return "frame ID";
            }

            if (built.SamplePeriodSeconds != configured.SamplePeriodSeconds)
            {
                return "sample period";
            }

            if (!string.Equals(built.Source, configured.Source, StringComparison.Ordinal))
            {
                return "source";
            }

            // An invalid built mount (a zero, infinite or far off-unit quaternion) is refused first:
            // the rotation comparison scales with it and would accept it.
            if (!built.Mount.IsValid ||
                !built.Mount.PositionBodyMeters.Equals(configured.Mount.PositionBodyMeters) ||
                !IsSameRotation(built.Mount.SensorToBody, configured.Mount.SensorToBody))
            {
                return "mount";
            }

            return null;
        }

        // q and -q are one rotation, and a valid mount may be up to 1e-6 off unit length
        // (Quaterniond.IsUnit), so two quaternions are the same rotation when their normalised dot
        // product is +1 or -1. The margin allows about 3 µrad (CORE-22).
        private static bool IsSameRotation(Quaterniond a, Quaterniond b)
        {
            double dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            return Math.Abs(dot) >= (1.0 - 1e-12) * a.Magnitude * b.Magnitude;
        }

        private static SensorDefinition[] SensorDefinitions(SensorManager sensors)
        {
            SensorDefinition[] definitions = new SensorDefinition[sensors.Sensors.Count];
            for (int i = 0; i < definitions.Length; i++)
            {
                definitions[i] = sensors.Sensors[i].Definition;
            }

            return definitions;
        }

        private static HashSet<string> ControllerVisibleSensorIds(SimulationConfiguration run)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (DeviceConfiguration device in run.Devices)
            {
                if (IsSensor(device) && device.Definition.IsControllerVisible)
                {
                    ids.Add(device.Definition.SensorId);
                }
            }

            return ids;
        }

        private static bool IsSensor(DeviceConfiguration device) => device.KindDefinition?.Role == DeviceRole.Sensor;

        // A controller's accepted command for the next step, validated and limited at submission.
        private sealed class PendingCommand
        {
            public PendingCommand(string controllerId, DeviceCommandSet requested, DeviceCommandSet limited, string clamped)
            {
                ControllerId = controllerId;
                Requested = requested;
                Limited = limited;
                Clamped = clamped;
            }

            public string ControllerId { get; }
            public DeviceCommandSet Requested { get; }
            public DeviceCommandSet Limited { get; }
            public string Clamped { get; }
        }
    }
}
