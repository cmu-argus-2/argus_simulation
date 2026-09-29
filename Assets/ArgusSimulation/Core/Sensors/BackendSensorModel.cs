using System;

namespace Argus.Simulation.Core
{
    // A sensor whose physics and cadence live in the backend (D10). It publishes a frame exactly when
    // the step's SensorMeasurementSet says the sensor was sampled, and never computes, holds or
    // repeats a value itself.
    public abstract class BackendSensorModel<TMeasurement> : ISensor
        where TMeasurement : struct
    {
        private bool _isReset;
        private long _nextSequence;
        private string _runId;

        protected BackendSensorModel(SensorConfiguration configuration, SensorKind expectedKind)
        {
            if (!configuration.IsValid || configuration.Kind != expectedKind)
            {
                throw new ArgumentException(
                    $"A valid {expectedKind} sensor configuration is required.",
                    nameof(configuration));
            }

            Configuration = configuration;
        }

        public SensorConfiguration Configuration { get; }
        public SensorDefinition Definition => Configuration.Definition;

        public void Reset(SensorResetContext context)
        {
            if (!context.IsValid)
            {
                throw new ArgumentException("A valid sensor reset context is required.", nameof(context));
            }

            _runId = context.RunId;
            _nextSequence = 0;
            _isReset = true;
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

            string sensorId = Definition.SensorId;
            if (!context.Measurements.WasSampled(sensorId))
            {
                frame = null;
                return false;
            }

            SensorFrameStatus status = SensorFrameStatus.Unavailable;
            if (context.Measurements.TryGetMeasurement(sensorId, out TMeasurement measurement))
            {
                status = IsMeasurementValid(measurement) ? SensorFrameStatus.Valid : SensorFrameStatus.Invalid;
            }

            frame = new SensorFrame<TMeasurement>(
                _runId,
                sensorId,
                Definition.FrameId,
                _nextSequence,
                context.Spacecraft.SimulationTimeSeconds,
                context.Spacecraft.TimestampUtc,
                status,
                Definition.Source,
                measurement);
            _nextSequence++;
            return true;
        }

        protected abstract bool IsMeasurementValid(TMeasurement measurement);
    }
}
