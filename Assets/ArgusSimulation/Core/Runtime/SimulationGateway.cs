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
            _engine.Initialize(run);
            _scenario = run;
            _sensors = BuildSensors(run);
            _controllerVisibleSensorIds = ControllerVisibleSensorIds(run);

            if (!_engine.TryReset(out SimulationState state) || !IsExpectedState(state, 0, 0.0, null))
            {
                throw new InvalidOperationException(
                    $"Simulation backend '{_engine.BackendName}' failed to produce a valid state at t_0.");
            }

            _nextSequence = 0;
            _nextSimulationTimeSeconds = 0.0;
            _isRunning = true;
            return Publish(state);
        }

        // A zero command for the next step.
        public Observation Step()
        {
            return Step(ActuatorCommandSet.None(_nextSequence, _nextSimulationTimeSeconds));
        }

        // Applies command k over [t_k, t_k+1) and returns observation k+1. A backend failure ends
        // the run with a BackendFailed observation instead of throwing.
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
            string failure;
            try
            {
                failure = _engine.TryStep(new SimulationStepInput(_nextSequence, _nextSimulationTimeSeconds, command), out state) &&
                    IsExpectedState(state, sequence, simulationTimeSeconds, command)
                        ? null
                        : $"Simulation backend '{_engine.BackendName}' failed to produce a valid state at step {sequence}.";
            }
            catch (Exception exception)
            {
                state = default;
                failure = exception.Message;
            }

            if (failure != null)
            {
                _isRunning = false;
                return new Observation(
                    _scenario.RunId,
                    sequence,
                    simulationTimeSeconds,
                    _scenario.EpochUtc + TimeSpan.FromTicks((long)Math.Round(simulationTimeSeconds * TimeSpan.TicksPerSecond)),
                    _engine.BackendName,
                    Array.Empty<ISensorFrame>(),
                    ObservationStatus.BackendFailed,
                    failure);
            }

            _nextSequence = sequence;
            _nextSimulationTimeSeconds = simulationTimeSeconds;
            return Publish(state);
        }

        // Publishes truth and every frame, and returns the controller-visible frames.
        private Observation Publish(SimulationState state)
        {
            StateProduced?.Invoke(state);
            SensorOutputSet output = _sensors.Sample(new SensorSampleContext(_scenario.RunId, state));
            SensorOutputProduced?.Invoke(output);

            List<ISensorFrame> visible = new List<ISensorFrame>(output.Frames.Count);
            foreach (ISensorFrame frame in output.Frames)
            {
                if (_controllerVisibleSensorIds.Contains(frame.SensorId))
                {
                    visible.Add(frame);
                }
            }

            SpacecraftState spacecraft = state.Spacecraft;
            return new Observation(
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
