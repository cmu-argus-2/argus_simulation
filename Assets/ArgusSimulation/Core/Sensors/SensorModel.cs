using System;

namespace Argus.Simulation.Core
{
    public abstract class SensorModel<TPayload> : ISensor
    {
        private const double TimeToleranceSeconds = 1e-9;

        private bool _isReset;
        private long _nextSequence;
        // Scheduled slots can advance past skipped deadlines independently of emitted frames.
        private long _nextSampleIndex;
        private double _nextSampleTimeSeconds;
        private string _runId;

        protected SensorModel(SensorDefinition definition)
        {
            if (!definition.IsValid)
            {
                throw new ArgumentException("A valid sensor definition is required.", nameof(definition));
            }

            Definition = definition;
        }

        public SensorDefinition Definition { get; }

        public void Reset(SensorResetContext context)
        {
            if (!context.IsValid)
            {
                throw new ArgumentException("A valid sensor reset context is required.", nameof(context));
            }

            _runId = context.RunId;
            _nextSequence = 0;
            _nextSampleIndex = 0;
            _nextSampleTimeSeconds = 0.0;
            _isReset = true;
            OnReset(context);
        }

        public bool TrySample(SensorSampleContext context, out ISensorFrame frame)
        {
            if (!_isReset)
            {
                throw new InvalidOperationException(
                    $"Sensor '{Definition.SensorId}' must be reset before sampling.");
            }

            if (!context.IsValid || context.RunId != _runId)
            {
                throw new ArgumentException(
                    "The sample context must be valid and belong to the active run.",
                    nameof(context));
            }

            double sampleTime = context.Spacecraft.SimulationTimeSeconds;
            if (sampleTime + TimeToleranceSeconds < _nextSampleTimeSeconds)
            {
                frame = null;
                return false;
            }

            SensorFrameStatus status = Measure(context, out TPayload payload);
            SensorFrame<TPayload> typedFrame = new SensorFrame<TPayload>(
                _runId,
                Definition.SensorId,
                Definition.FrameId,
                _nextSequence,
                sampleTime,
                context.Spacecraft.TimestampUtc,
                status,
                Definition.Source,
                payload);

            _nextSequence++;
            // Anchor every deadline to simulation time zero instead of accumulating rounding
            // error. If a step crosses several deadlines, emit only for the supplied state and
            // advance to the next future slot; intermediate states are not available to measure.
            do
            {
                _nextSampleIndex++;
                _nextSampleTimeSeconds = _nextSampleIndex * Definition.SamplePeriodSeconds;
            }
            while (_nextSampleTimeSeconds <= sampleTime + TimeToleranceSeconds);

            frame = typedFrame;
            return true;
        }

        protected virtual void OnReset(SensorResetContext context)
        {
        }

        // Concrete models return Unavailable or Invalid rather than manufacturing a value.
        protected abstract SensorFrameStatus Measure(
            SensorSampleContext context,
            out TPayload payload);
    }
}
