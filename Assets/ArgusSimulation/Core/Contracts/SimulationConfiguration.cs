using System;

namespace Argus.Simulation.Core
{
    public readonly struct SimulationConfiguration
    {
        public SimulationConfiguration(
            string runId,
            DateTimeOffset epochUtc,
            double fixedStepSeconds)
        {
            RunId = runId;
            EpochUtc = epochUtc.ToUniversalTime();
            FixedStepSeconds = fixedStepSeconds;
        }

        public string RunId { get; }
        public DateTimeOffset EpochUtc { get; }
        public double FixedStepSeconds { get; }

        public bool IsValid =>
            !string.IsNullOrWhiteSpace(RunId) &&
            FixedStepSeconds > 0.0 &&
            !double.IsNaN(FixedStepSeconds) &&
            !double.IsInfinity(FixedStepSeconds);
    }
}
