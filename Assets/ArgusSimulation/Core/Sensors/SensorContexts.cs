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
        private readonly SimulationSnapshot _snapshot;
        private readonly bool _hasSnapshot;

        public SensorSampleContext(string runId, SpacecraftState spacecraft)
        {
            RunId = runId;
            Spacecraft = spacecraft;
            _snapshot = default;
            _hasSnapshot = false;
        }

        public SensorSampleContext(string runId, SimulationSnapshot snapshot)
        {
            RunId = runId;
            Spacecraft = snapshot.Spacecraft;
            _snapshot = snapshot;
            _hasSnapshot = true;
        }

        public string RunId { get; }
        public SpacecraftState Spacecraft { get; }

        // Backend measurements for BackendSensorModel; empty for a state-only context.
        public SensorMeasurementSet Measurements =>
            _hasSnapshot ? _snapshot.SensorMeasurements : SensorMeasurementSet.Empty;

        public bool IsValid =>
            !string.IsNullOrWhiteSpace(RunId) &&
            Spacecraft.IsValid &&
            (!_hasSnapshot || (_snapshot.IsValid && string.Equals(_snapshot.RunId, RunId, StringComparison.Ordinal)));
    }
}
