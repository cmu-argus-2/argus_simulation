using System;
using System.Collections.Generic;
using System.Globalization;

namespace Argus.Simulation.Core
{
    // Deterministic closed-loop session, used by the tests and headless/Host. It returns only
    // controller-visible observations (D7) in the DATA-5 order (target-architecture §4):
    // Reset(seed, scenario) returns observation 0, and Step(command k) applies command k over
    // [t_k, t_k+1) and returns observation k+1. Truth and every sensor frame, including ground
    // truth, reach the recorder and Unity through StateProduced and SensorOutputProduced, and every
    // command Step receives reaches it through CommandLogged.
    // TODO(D7, G1): actuator-limit validation (CORE-10) and authority and heartbeat (CORE-9), which
    // log their decisions through CommandLogged. Serving it to HIL and external controllers is
    // headless/Host GatewayServer.
    public sealed class SimulationGateway
    {
        private readonly ISimulationEngine _engine;
        private readonly Func<SensorConfiguration, ISensor> _sensorFactory;
        private SimulationConfiguration _scenario;
        private SensorManager _sensors;
        private HashSet<string> _controllerVisibleSensorIds;
        private long _nextSequence;
        private double _nextSimulationTimeSeconds;
        private SpacecraftState _lastSpacecraft;
        private bool _isRunning;
        // RunStarted has fired for _scenario, so a command can be logged against that run.
        private bool _hasStartedRun;

        // sensorFactory builds the run's sensors from the scenario; SensorFactory.Create by default.
        public SimulationGateway(ISimulationEngine engine, Func<SensorConfiguration, ISensor> sensorFactory = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _sensorFactory = sensorFactory ?? SensorFactory.Create;
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

        // The command log (D6, D7): every command Step receives once a run has started, applied or
        // rejected, raised before the engine runs it or Step throws.
        public event Action<CommandRecord> CommandLogged;

        // The backend exception that ended the run, for logs; never shown to a controller.
        public Exception Failure { get; private set; }

        // True from a successful Reset until the run terminates.
        public bool IsRunning => _isRunning;

        // The sequence and time the next command must carry: k and t_k.
        public long NextSequence => _nextSequence;
        public double NextSimulationTimeSeconds => _nextSimulationTimeSeconds;

        // Starts the scenario with seed in place of its RandomSeed and returns observation 0. The
        // same seed and scenario give the same observations.
        public Observation Reset(int seed, SimulationConfiguration scenario)
        {
            if (seed < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(seed), "The seed must be >= 0.");
            }

            SimulationConfiguration run = WithRandomSeed(scenario, seed);
            if (!run.IsValid)
            {
                throw new ArgumentException("A valid scenario is required.", nameof(scenario));
            }

            _isRunning = false;
            _hasStartedRun = false;
            Failure = null;
            _engine.Initialize(run);
            _scenario = run;
            _sensors = BuildSensors(run);
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
            RunStarted?.Invoke(run, SensorDefinitions(_sensors));
            _hasStartedRun = true;
            // Running from here, so a subscriber that throws in Publish ends the run at t_0.
            _lastSpacecraft = state.Spacecraft;
            _isRunning = true;
            return Publish(state);
        }

        // A zero command for the next step.
        public Observation Step()
        {
            return Step(ActuatorCommandSet.None(_nextSequence, _nextSimulationTimeSeconds));
        }

        // Applies command k over [t_k, t_k+1) and returns observation k+1. A backend failure ends
        // the run with a BackendFailed observation instead of throwing; Failure keeps the cause.
        // Every command is logged first (CommandLogged), including one Step then rejects.
        public Observation Step(ActuatorCommandSet command)
        {
            if (!_isRunning)
            {
                const string notRunning = "The run has not started or has ended; call Reset.";
                if (_hasStartedRun)
                {
                    Log(Rejected(command, notRunning));
                }

                throw new InvalidOperationException(notRunning);
            }

            string rejection = Rejection(command);
            if (rejection != null)
            {
                Log(Rejected(command, rejection));
                throw new ArgumentException(rejection, nameof(command));
            }

            ActuatorCommandSet applied = command;
            Log(new CommandRecord(
                _scenario.RunId, _nextSequence, _nextSimulationTimeSeconds, CommandOutcome.Applied, command, applied));

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
            return observation;
        }

        // Ends a running run on the caller's behalf (Ctrl+C, a recorder failure), so subscribers such
        // as the recorder learn why it ended. Returns the terminal observation, or null when no run
        // is running.
        public Observation Stop(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("A reason is required.", nameof(reason));
            }

            return _isRunning ? End(ObservationStatus.Stopped, reason) : null;
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
            _isRunning = false;
            Observation observation = new Observation(
                _scenario.RunId,
                _lastSpacecraft.Sequence,
                _lastSpacecraft.SimulationTimeSeconds,
                _lastSpacecraft.TimestampUtc,
                _engine.BackendName,
                Array.Empty<ISensorFrame>(),
                status,
                reason);
            RunTerminated?.Invoke(observation);
            return observation;
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
        private string Rejection(ActuatorCommandSet command)
        {
            if (!command.IsValid)
            {
                return "The command is invalid: its sequence or time is negative, or a value is not finite.";
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

            return null;
        }

        private CommandRecord Rejected(ActuatorCommandSet command, string reason) =>
            new CommandRecord(
                _scenario.RunId, _nextSequence, _nextSimulationTimeSeconds, CommandOutcome.Rejected, command, null, reason);

        // A subscriber that throws ends the run, so the engine never runs a command the log missed,
        // and the caller gets the subscriber's exception, even in place of a rejection: a log with a
        // gap is not a log. Subscribers that already logged the record learn from RunTerminated that
        // the run ended at this step.
        private void Log(CommandRecord record)
        {
            try
            {
                CommandLogged?.Invoke(record);
            }
            catch (Exception exception)
            {
                EndBeforeRethrow(() => End(
                    ObservationStatus.Stopped, $"Logging the command for step {record.Sequence} failed: {exception.Message}"));
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
                StateProduced?.Invoke(state);
                output = _sensors.Sample(new SensorSampleContext(_scenario.RunId, state));
                SensorOutputProduced?.Invoke(output);
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

        private bool IsExpectedState(
            SimulationState state,
            long sequence,
            double simulationTimeSeconds,
            ActuatorCommandSet? command)
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

        private SensorManager BuildSensors(SimulationConfiguration run)
        {
            SensorManager sensors = new SensorManager();
            foreach (SensorConfiguration sensor in run.Sensors)
            {
                sensors.Register(_sensorFactory(sensor));
            }

            sensors.Reset(new SensorResetContext(run.RunId, run.EpochUtc, run.RandomSeed));
            return sensors;
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
            foreach (SensorConfiguration sensor in run.Sensors)
            {
                if (sensor.Definition.IsControllerVisible)
                {
                    ids.Add(sensor.Definition.SensorId);
                }
            }

            return ids;
        }

        private static SimulationConfiguration WithRandomSeed(SimulationConfiguration scenario, int seed) =>
            new SimulationConfiguration(
                scenario.RunId,
                scenario.EpochUtc,
                scenario.FixedStepSeconds,
                initialOrbit: scenario.InitialOrbit,
                randomSeed: seed,
                kernelSetId: scenario.KernelSetId,
                spacecraft: scenario.Spacecraft,
                sensors: scenario.Sensors);
    }
}
