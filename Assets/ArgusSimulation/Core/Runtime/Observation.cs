using System;
using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    public enum ObservationStatus
    {
        // The step ran and the frames are what the sensors produced.
        Ok = 0,

        // The dynamics backend failed; the observation has no frames and the run has ended.
        BackendFailed = 1
    }

    // What a controller sees after Reset (observation 0) or Step(command k) (observation k+1): the
    // controller-visible sensor frames sampled at one step, never truth (D7, target-architecture §4).
    // Truth reaches the recorder and Unity through SimulationGateway.StateProduced.
    public sealed class Observation
    {
        private readonly ISensorFrame[] _frames;

        public Observation(
            string runId,
            long sequence,
            double simulationTimeSeconds,
            DateTimeOffset timestampUtc,
            string source,
            IReadOnlyList<ISensorFrame> frames,
            ObservationStatus status,
            string terminationReason = null)
            : this(runId, sequence, simulationTimeSeconds, timestampUtc, source, Copy(frames), status, terminationReason)
        {
        }

        private Observation(
            string runId,
            long sequence,
            double simulationTimeSeconds,
            DateTimeOffset timestampUtc,
            string source,
            ISensorFrame[] frames,
            ObservationStatus status,
            string terminationReason)
        {
            // Only a failure ends a run today, so the status and the reason always agree.
            if ((status == ObservationStatus.BackendFailed) != (terminationReason != null))
            {
                throw new ArgumentException(
                    "A BackendFailed observation needs a termination reason, and only it has one.",
                    nameof(terminationReason));
            }

            RunId = runId;
            Sequence = sequence;
            SimulationTimeSeconds = simulationTimeSeconds;
            TimestampUtc = timestampUtc.ToUniversalTime();
            Source = source;
            _frames = frames;
            Status = status;
            TerminationReason = terminationReason;
        }

        // For the gateway, which builds a fresh array per step: takes ownership instead of copying.
        internal static Observation Owning(
            string runId,
            long sequence,
            double simulationTimeSeconds,
            DateTimeOffset timestampUtc,
            string source,
            ISensorFrame[] frames,
            ObservationStatus status) =>
            new Observation(runId, sequence, simulationTimeSeconds, timestampUtc, source, frames, status, null);

        public string RunId { get; }
        public long Sequence { get; }
        public double SimulationTimeSeconds { get; }
        public DateTimeOffset TimestampUtc { get; }

        // The dynamics backend that produced the step.
        public string Source { get; }

        // Controller-visible frames sampled at this step; a sensor that was not sampled has none.
        public IReadOnlyList<ISensorFrame> Frames => _frames;

        public ObservationStatus Status { get; }

        // Set when the run has ended; Step is rejected until the next Reset.
        public string TerminationReason { get; }
        public bool IsTerminal => TerminationReason != null;

        public bool TryGetFrame<TPayload>(string sensorId, out SensorFrame<TPayload> frame)
        {
            foreach (ISensorFrame candidate in _frames)
            {
                if (candidate.SensorId == sensorId && candidate is SensorFrame<TPayload> typed)
                {
                    frame = typed;
                    return true;
                }
            }

            frame = default;
            return false;
        }

        private static ISensorFrame[] Copy(IReadOnlyList<ISensorFrame> frames)
        {
            if (frames == null)
            {
                throw new ArgumentNullException(nameof(frames));
            }

            ISensorFrame[] copy = new ISensorFrame[frames.Count];
            for (int index = 0; index < copy.Length; index++)
            {
                copy[index] = frames[index];
            }

            return copy;
        }
    }
}
