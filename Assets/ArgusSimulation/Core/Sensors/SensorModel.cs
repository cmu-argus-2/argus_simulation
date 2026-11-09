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
            if (sampleTime + TimeToleranceSeconds >= _nextSampleTimeSeconds)
            {
                SensorSlot slot = new SensorSlot(
                    _nextSequence,
                    context.Spacecraft.Sequence,
                    sampleTime,
                    context.Spacecraft.TimestampUtc);

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

                if (!TryDefer(context, slot))
                {
                    SensorFrameStatus status = Measure(context, out TPayload payload);
                    frame = CreateFrame(slot, status, payload);
                    return true;
                }
            }

            if (TryCompleteDeferred(
                    context,
                    out SensorSlot completed,
                    out SensorFrameStatus completedStatus,
                    out TPayload completedPayload))
            {
                frame = CreateFrame(completed, completedStatus, completedPayload);
                return true;
            }

            frame = null;
            return false;
        }

        protected virtual void OnReset(SensorResetContext context)
        {
        }

        // Concrete models return Unavailable or Invalid rather than manufacturing a value.
        protected abstract SensorFrameStatus Measure(
            SensorSampleContext context,
            out TPayload payload);

        // A model that cannot measure within TrySample (a camera waiting for a render) returns true
        // to start the slot now and publish it later through TryCompleteDeferred. The frame keeps
        // the slot's sequence, state and time, not those of the step it is published in (G3).
        // A model that defers must publish its slots in sequence order and defer every slot, so
        // sequences stay increasing in output order.
        protected virtual bool TryDefer(SensorSampleContext context, SensorSlot slot) => false;

        // Called on every TrySample that did not measure a slot directly; returns at most one
        // finished slot.
        protected virtual bool TryCompleteDeferred(
            SensorSampleContext context,
            out SensorSlot slot,
            out SensorFrameStatus status,
            out TPayload payload)
        {
            slot = default;
            status = default;
            payload = default;
            return false;
        }

        private SensorFrame<TPayload> CreateFrame(SensorSlot slot, SensorFrameStatus status, TPayload payload) =>
            new SensorFrame<TPayload>(
                _runId,
                Definition.SensorId,
                Definition.FrameId,
                slot.Sequence,
                slot.StateSequence,
                slot.SimulationTimeSeconds,
                slot.TimestampUtc,
                status,
                Definition.Source,
                payload);
    }

    // One scheduled sample: the frame's sequence and the state it was taken at.
    public readonly struct SensorSlot
    {
        public SensorSlot(long sequence, long stateSequence, double simulationTimeSeconds, DateTimeOffset timestampUtc)
        {
            Sequence = sequence;
            StateSequence = stateSequence;
            SimulationTimeSeconds = simulationTimeSeconds;
            TimestampUtc = timestampUtc;
        }

        public long Sequence { get; }
        public long StateSequence { get; }
        public double SimulationTimeSeconds { get; }
        public DateTimeOffset TimestampUtc { get; }
    }
}
