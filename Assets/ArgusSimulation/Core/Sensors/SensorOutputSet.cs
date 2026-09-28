using System;
using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    public sealed class SensorOutputSet
    {
        private readonly ISensorFrame[] _frames;

        public SensorOutputSet(
            string runId,
            long spacecraftSequence,
            double simulationTimeSeconds,
            IReadOnlyList<ISensorFrame> frames)
        {
            RunId = runId;
            SpacecraftSequence = spacecraftSequence;
            SimulationTimeSeconds = simulationTimeSeconds;
            _frames = CopyFrames(frames);
        }

        public string RunId { get; }
        public long SpacecraftSequence { get; }
        public double SimulationTimeSeconds { get; }
        public IReadOnlyList<ISensorFrame> Frames => _frames;

        public bool IsValid
        {
            get
            {
                if (string.IsNullOrWhiteSpace(RunId) ||
                    SpacecraftSequence < 0 ||
                    SimulationTimeSeconds < 0.0 ||
                    double.IsNaN(SimulationTimeSeconds) ||
                    double.IsInfinity(SimulationTimeSeconds))
                {
                    return false;
                }

                for (int index = 0; index < _frames.Length; index++)
                {
                    if (_frames[index] == null ||
                        !_frames[index].HasValidEnvelope ||
                        _frames[index].RunId != RunId)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        public bool TryGetFrame<TPayload>(string sensorId, out SensorFrame<TPayload> frame)
        {
            for (int index = _frames.Length - 1; index >= 0; index--)
            {
                if (_frames[index].SensorId == sensorId &&
                    _frames[index] is SensorFrame<TPayload> typedFrame)
                {
                    frame = typedFrame;
                    return true;
                }
            }

            frame = default;
            return false;
        }

        private static ISensorFrame[] CopyFrames(IReadOnlyList<ISensorFrame> frames)
        {
            if (frames == null)
            {
                throw new ArgumentNullException(nameof(frames));
            }

            ISensorFrame[] copy = new ISensorFrame[frames.Count];
            for (int index = 0; index < frames.Count; index++)
            {
                copy[index] = frames[index];
            }

            return copy;
        }
    }
}
