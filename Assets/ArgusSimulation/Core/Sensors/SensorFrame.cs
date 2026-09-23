using System;

namespace Argus.Simulation.Core
{
    public enum SensorFrameStatus
    {
        Valid = 0,
        Unavailable = 1,
        Stale = 2,
        Invalid = 3
    }

    // Common envelope for simulated output, physical sensor input, and recorded replay data.
    public readonly struct SensorFrame<TPayload>
    {
        public SensorFrame(
            string sensorId,
            long sequence,
            double simulationTimeSeconds,
            DateTimeOffset timestampUtc,
            SensorFrameStatus status,
            string source,
            TPayload payload)
        {
            SensorId = sensorId;
            Sequence = sequence;
            SimulationTimeSeconds = simulationTimeSeconds;
            TimestampUtc = timestampUtc.ToUniversalTime();
            Status = status;
            Source = source;
            Payload = payload;
        }

        public string SensorId { get; }
        public long Sequence { get; }
        public double SimulationTimeSeconds { get; }
        public DateTimeOffset TimestampUtc { get; }
        public SensorFrameStatus Status { get; }
        public string Source { get; }
        public TPayload Payload { get; }

        public bool HasValidEnvelope =>
            !string.IsNullOrWhiteSpace(SensorId) &&
            Sequence >= 0 &&
            SimulationTimeSeconds >= 0.0 &&
            !double.IsNaN(SimulationTimeSeconds) &&
            !double.IsInfinity(SimulationTimeSeconds) &&
            !string.IsNullOrWhiteSpace(Source);
    }
}
