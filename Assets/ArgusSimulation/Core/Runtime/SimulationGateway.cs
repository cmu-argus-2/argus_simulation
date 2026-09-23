using System;

namespace Argus.Simulation.Core
{
    // Deterministic closed-loop session used by in-process agents and future transport adapters.
    public sealed class SimulationGateway
    {
        private const double TimeToleranceSeconds = 1e-9;

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
                Math.Abs(commands.ApplyAtSimulationTimeSeconds - _nextSimulationTimeSeconds) >
                    TimeToleranceSeconds)
            {
                throw new ArgumentException(
                    "Commands must target the gateway's next sequence and simulation time.",
                    nameof(commands));
            }

            SimulationStepInput input = new SimulationStepInput(
                _nextSequence,
                _nextSimulationTimeSeconds,
                commands);
            if (!_engine.TryStep(input, out SimulationSnapshot snapshot))
            {
                throw new InvalidOperationException(
                    $"Simulation backend '{_engine.BackendName}' failed to produce a snapshot.");
            }

            if (!TryValidateSnapshot(snapshot, input, out string validationError))
            {
                throw new InvalidOperationException(
                    $"Simulation backend '{_engine.BackendName}' produced an invalid or " +
                    $"out-of-sync snapshot: {validationError}");
            }

            _nextSequence++;
            _nextSimulationTimeSeconds = _nextSequence * _configuration.FixedStepSeconds;
            SnapshotProduced?.Invoke(snapshot);
            return snapshot;
        }

        private bool TryValidateSnapshot(
            SimulationSnapshot snapshot,
            SimulationStepInput input,
            out string validationError)
        {
            if (!snapshot.IsValid)
            {
                validationError = "the snapshot contract is invalid.";
                return false;
            }

            if (!string.Equals(snapshot.RunId, _configuration.RunId, StringComparison.Ordinal))
            {
                validationError =
                    $"run ID '{snapshot.RunId}' does not match '{_configuration.RunId}'.";
                return false;
            }

            if (snapshot.Spacecraft.Sequence != input.Sequence)
            {
                validationError =
                    $"state sequence {snapshot.Spacecraft.Sequence} does not match " +
                    $"requested sequence {input.Sequence}.";
                return false;
            }

            if (Math.Abs(
                    snapshot.Spacecraft.SimulationTimeSeconds - input.SimulationTimeSeconds) >
                TimeToleranceSeconds)
            {
                validationError =
                    $"state time {snapshot.Spacecraft.SimulationTimeSeconds:R} does not match " +
                    $"requested time {input.SimulationTimeSeconds:R}.";
                return false;
            }

            DateTimeOffset expectedTimestamp =
                _configuration.EpochUtc.AddSeconds(input.SimulationTimeSeconds);
            if (snapshot.Spacecraft.TimestampUtc != expectedTimestamp)
            {
                validationError =
                    $"state timestamp {snapshot.Spacecraft.TimestampUtc:O} does not match " +
                    $"expected timestamp {expectedTimestamp:O}.";
                return false;
            }

            if (snapshot.AppliedCommands.Sequence != input.Sequence)
            {
                validationError =
                    $"command sequence {snapshot.AppliedCommands.Sequence} does not match " +
                    $"requested sequence {input.Sequence}.";
                return false;
            }

            if (Math.Abs(
                    snapshot.AppliedCommands.ApplyAtSimulationTimeSeconds -
                    input.SimulationTimeSeconds) > TimeToleranceSeconds)
            {
                validationError =
                    $"command time " +
                    $"{snapshot.AppliedCommands.ApplyAtSimulationTimeSeconds:R} does not match " +
                    $"requested time {input.SimulationTimeSeconds:R}.";
                return false;
            }

            validationError = null;
            return true;
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
