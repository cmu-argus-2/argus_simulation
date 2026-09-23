using System;

namespace Argus.Simulation.Core
{
    // Deterministic closed-loop session used by in-process agents and future transport adapters.
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

        public event Action<SimulationSnapshot> SnapshotProduced;

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

        public SimulationSnapshot Step()
        {
            return Step(ActuatorCommandSet.None(_nextSequence, _nextSimulationTimeSeconds));
        }

        public SimulationSnapshot Step(ActuatorCommandSet commands)
        {
            EnsureInitialized();
            if (!commands.IsValid ||
                commands.Sequence != _nextSequence ||
                Math.Abs(commands.ApplyAtSimulationTimeSeconds - _nextSimulationTimeSeconds) > 1e-9)
            {
                throw new ArgumentException(
                    "Commands must target the gateway's next sequence and simulation time.",
                    nameof(commands));
            }

            SimulationStepInput input = new SimulationStepInput(
                _nextSequence,
                _nextSimulationTimeSeconds,
                commands);
            if (!_engine.TryStep(input, out SimulationSnapshot snapshot) ||
                !snapshot.IsValid ||
                snapshot.RunId != _configuration.RunId ||
                snapshot.Spacecraft.Sequence != _nextSequence ||
                Math.Abs(snapshot.Spacecraft.SimulationTimeSeconds - _nextSimulationTimeSeconds) > 1e-9 ||
                Math.Abs(snapshot.AppliedCommands.ApplyAtSimulationTimeSeconds -
                    _nextSimulationTimeSeconds) > 1e-9)
            {
                throw new InvalidOperationException(
                    $"Simulation backend '{_engine.BackendName}' failed to produce a valid snapshot.");
            }

            _nextSequence++;
            _nextSimulationTimeSeconds = _nextSequence * _configuration.FixedStepSeconds;
            SnapshotProduced?.Invoke(snapshot);
            return snapshot;
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
