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
        public SensorSampleContext(string runId, SpacecraftState spacecraft)
        {
            RunId = runId;
            Spacecraft = spacecraft;
        }

        public string RunId { get; }
        public SpacecraftState Spacecraft { get; }

        public bool IsValid =>
            !string.IsNullOrWhiteSpace(RunId) &&
            Spacecraft.IsValid;
    }
}
