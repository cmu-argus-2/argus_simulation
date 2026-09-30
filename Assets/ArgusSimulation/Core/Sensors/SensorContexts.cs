using System;

namespace Argus.Simulation.Core
{
    public readonly struct SensorResetContext
    {
        public SensorResetContext(string runId, DateTimeOffset epochUtc, int randomSeed)
        {
            RunId = runId;
            EpochUtc = epochUtc.ToUniversalTime();
            RandomSeed = randomSeed;
        }

        public string RunId { get; }
        public DateTimeOffset EpochUtc { get; }
        public int RandomSeed { get; }

        public bool IsValid => !string.IsNullOrWhiteSpace(RunId);
    }

    public readonly struct SensorSampleContext
    {
        private readonly SimulationState _state;
        private readonly bool _hasState;

        public SensorSampleContext(string runId, SpacecraftState spacecraft)
        {
            RunId = runId;
            Spacecraft = spacecraft;
            _state = default;
            _hasState = false;
        }

        public SensorSampleContext(string runId, SimulationState state)
        {
            RunId = runId;
            Spacecraft = state.Spacecraft;
            _state = state;
            _hasState = true;
        }

        public string RunId { get; }
        public SpacecraftState Spacecraft { get; }

        // Backend measurements for BackendSensorModel; empty for a state-only context.
        public SensorMeasurementSet Measurements =>
            _hasState ? _state.SensorMeasurements : SensorMeasurementSet.Empty;

        public bool IsValid =>
            !string.IsNullOrWhiteSpace(RunId) &&
            Spacecraft.IsValid &&
            (!_hasState || (_state.IsValid && string.Equals(_state.RunId, RunId, StringComparison.Ordinal)));
    }
}
