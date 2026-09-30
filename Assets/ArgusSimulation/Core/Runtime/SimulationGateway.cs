using System;

namespace Argus.Simulation.Core
{
    // Deterministic closed-loop session used by in-process agents and future transport adapters.
    // TODO(D7, G1): Step returns controller-visible observations instead of truth states; add
    // Reset(seed, scenario), a command log for the recorder (D6), actuator-limit validation,
    // authority and heartbeat. Serving it to agents and HIL is headless/Host GatewayServer.
    // Also open: letting command k be computed from observation k (target-architecture §11).
    public sealed class SimulationGateway
    {
        private readonly ISimulationEngine _engine;
        private SimulationConfiguration _configuration;
        private long _nextSequence;
        private double _nextSimulationTimeSeconds;

        public SimulationGateway(ISimulationEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public event Action<SimulationState> StateProduced;

        public bool IsInitialized => _engine.IsInitialized;
        public long NextSequence => _nextSequence;
        public double NextSimulationTimeSeconds => _nextSimulationTimeSeconds;

        public void Initialize(SimulationConfiguration configuration)
        {
            if (!configuration.IsValid)
            {
                throw new ArgumentException("A valid simulation configuration is required.", nameof(configuration));
            }

            _configuration = configuration;
            _engine.Initialize(configuration);
            _nextSequence = 0;
            _nextSimulationTimeSeconds = 0.0;
        }

        public void Reset()
        {
            EnsureInitialized();
            _engine.Reset();
            _nextSequence = 0;
            _nextSimulationTimeSeconds = 0.0;
        }

        public SimulationState Step()
        {
            return Step(ActuatorCommandSet.None(_nextSequence, _nextSimulationTimeSeconds));
        }

        public SimulationState Step(ActuatorCommandSet commands)
        {
            EnsureInitialized();
            if (!commands.IsValid ||
                commands.Sequence != _nextSequence ||
                !SimulationTime.AreSame(commands.ApplyAtSimulationTimeSeconds, _nextSimulationTimeSeconds))
            {
                throw new ArgumentException(
                    "Commands must target the gateway's next sequence and simulation time.",
                    nameof(commands));
            }

            SimulationStepInput input = new SimulationStepInput(
                _nextSequence,
                _nextSimulationTimeSeconds,
                commands);
            if (!_engine.TryStep(input, out SimulationState state) ||
                !state.IsValid ||
                state.RunId != _configuration.RunId ||
                state.Spacecraft.Sequence != _nextSequence ||
                !SimulationTime.AreSame(state.Spacecraft.SimulationTimeSeconds, _nextSimulationTimeSeconds) ||
                !SimulationTime.AreSame(state.AppliedCommands.ApplyAtSimulationTimeSeconds, _nextSimulationTimeSeconds))
            {
                throw new InvalidOperationException(
                    $"Simulation backend '{_engine.BackendName}' failed to produce a valid state.");
            }

            _nextSequence++;
            _nextSimulationTimeSeconds = _nextSequence * _configuration.FixedStepSeconds;
            StateProduced?.Invoke(state);
            return state;
        }

        private void EnsureInitialized()
        {
            if (!_engine.IsInitialized)
            {
                throw new InvalidOperationException("The simulation gateway has not been initialized.");
            }
        }
    }
}
