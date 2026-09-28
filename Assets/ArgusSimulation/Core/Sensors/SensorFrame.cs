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

    public interface ISensorFrame
    {
        string RunId { get; }
        string SensorId { get; }
        string FrameId { get; }
        long Sequence { get; }
        double SimulationTimeSeconds { get; }
        DateTimeOffset TimestampUtc { get; }
        SensorFrameStatus Status { get; }
        string Source { get; }
        Type PayloadType { get; }
        object UntypedPayload { get; }
        bool HasValidEnvelope { get; }
    }

    // Common envelope for simulated output, physical sensor input, and recorded replay data.
    public readonly struct SensorFrame<TPayload> : ISensorFrame
    {
        public SensorFrame(
            string runId,
            string sensorId,
            string frameId,
            long sequence,
            double simulationTimeSeconds,
            DateTimeOffset timestampUtc,
            SensorFrameStatus status,
            string source,
            TPayload payload)
        {
            RunId = runId;
            SensorId = sensorId;
            FrameId = frameId;
            Sequence = sequence;
            SimulationTimeSeconds = simulationTimeSeconds;
            TimestampUtc = timestampUtc.ToUniversalTime();
            Status = status;
            Source = source;
            Payload = payload;
        }

        public string RunId { get; }
        public string SensorId { get; }
        public string FrameId { get; }
        public long Sequence { get; }
        public double SimulationTimeSeconds { get; }
        public DateTimeOffset TimestampUtc { get; }
        public SensorFrameStatus Status { get; }
        public string Source { get; }
        public TPayload Payload { get; }
        public Type PayloadType => typeof(TPayload);
        public object UntypedPayload => Payload;

        public bool HasValidEnvelope =>
            !string.IsNullOrWhiteSpace(RunId) &&
            !string.IsNullOrWhiteSpace(SensorId) &&
            !string.IsNullOrWhiteSpace(FrameId) &&
            Sequence >= 0 &&
            SimulationTimeSeconds >= 0.0 &&
            !double.IsNaN(SimulationTimeSeconds) &&
            !double.IsInfinity(SimulationTimeSeconds) &&
            !string.IsNullOrWhiteSpace(Source);
    }
}
