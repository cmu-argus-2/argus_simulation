using System;
using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    // Deterministic closed-loop session, used by the tests and headless/Host. It returns only
    // controller-visible observations (D7) in the DATA-5 order (target-architecture §4):
    // Reset(seed, scenario) returns observation 0, and Step(command k) applies command k over
    // [t_k, t_k+1) and returns observation k+1. Truth and every sensor frame, including ground
    // truth, reach the recorder and Unity through StateProduced and SensorOutputProduced.
    // TODO(D6, D7, G1): a command log for the recorder, actuator-limit validation, authority and
    // heartbeat. Serving it to HIL and external controllers is headless/Host GatewayServer.
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

        // sensorFactory builds the run's sensors from the scenario; SensorFactory.Create by default.
        public SimulationGateway(ISimulationEngine engine, Func<SensorConfiguration, ISensor> sensorFactory = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _sensorFactory = sensorFactory ?? SensorFactory.Create;
        }

        // Every truth state, starting with the state at t_0.
        public event Action<SimulationState> StateProduced;

        // Every sensor frame sampled at a step, controller-visible or not.
        public event Action<SensorOutputSet> SensorOutputProduced;

        // The terminal observation when the run ends, so the recorder knows why (D6).
        public event Action<Observation> RunTerminated;

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

            Observation observation = Publish(state);
            _nextSequence = 0;
            _nextSimulationTimeSeconds = 0.0;
            _isRunning = true;
            return observation;
        }

        // A zero command for the next step.
        public Observation Step()
        {
            return Step(ActuatorCommandSet.None(_nextSequence, _nextSimulationTimeSeconds));
        }

        // Applies command k over [t_k, t_k+1) and returns observation k+1. A backend failure ends
        // the run with a BackendFailed observation instead of throwing; Failure keeps the cause.
        public Observation Step(ActuatorCommandSet command)
        {
            if (!_isRunning)
            {
                throw new InvalidOperationException("The run has not started or has ended; call Reset.");
            }

            if (!command.IsValid ||
                command.Sequence != _nextSequence ||
                !SimulationTime.AreSame(command.ApplyAtSimulationTimeSeconds, _nextSimulationTimeSeconds))
            {
                throw new ArgumentException(
                    "The command must carry the gateway's next sequence and simulation time.",
                    nameof(command));
            }

            long sequence = _nextSequence + 1;
            double simulationTimeSeconds = sequence * _scenario.FixedStepSeconds;
            SimulationState state;
            try
            {
                if (!_engine.TryStep(new SimulationStepInput(_nextSequence, _nextSimulationTimeSeconds, command), out state) ||
                    !IsExpectedState(state, sequence, simulationTimeSeconds, command))
                {
                    return Terminate(new InvalidOperationException(
                        $"Simulation backend '{_engine.BackendName}' failed to produce a valid state at step {sequence}."));
                }
            }
            catch (InvalidOperationException exception)
            {
                // A backend failure, such as BasiliskEngine's wrapped RpcException. Any other
                // exception is a bug and propagates with its stack trace.
                return Terminate(exception);
            }

            Observation observation = Publish(state);
            _nextSequence = sequence;
            _nextSimulationTimeSeconds = simulationTimeSeconds;
            return observation;
        }

        // Ends the run. The observation is the last step that exists, k, so its time and UTC are the
        // backend's own; the reason names the step that failed.
        private Observation Terminate(Exception failure)
        {
            _isRunning = false;
            Failure = failure;
            Observation observation = new Observation(
                _scenario.RunId,
                _lastSpacecraft.Sequence,
                _lastSpacecraft.SimulationTimeSeconds,
                _lastSpacecraft.TimestampUtc,
                _engine.BackendName,
                Array.Empty<ISensorFrame>(),
                ObservationStatus.BackendFailed,
                $"Step {_lastSpacecraft.Sequence + 1} failed: {failure.Message}");
            RunTerminated?.Invoke(observation);
            return observation;
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
            catch
            {
                _isRunning = false;
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
