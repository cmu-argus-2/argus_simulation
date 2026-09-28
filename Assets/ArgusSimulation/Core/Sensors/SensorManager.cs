using System;
using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    public sealed class SensorManager
    {
        private readonly List<ISensor> _sensors = new List<ISensor>();
        private string _runId;
        private bool _isReset;

        public IReadOnlyList<ISensor> Sensors => _sensors;

        public void Register(ISensor sensor)
        {
            if (sensor == null)
            {
                throw new ArgumentNullException(nameof(sensor));
            }

            if (_isReset)
            {
                throw new InvalidOperationException(
                    "Sensors cannot be registered after the manager has been reset for a run.");
            }

            for (int index = 0; index < _sensors.Count; index++)
            {
                if (string.Equals(
                    _sensors[index].Definition.SensorId,
                    sensor.Definition.SensorId,
                    StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"A sensor with ID '{sensor.Definition.SensorId}' is already registered.",
                        nameof(sensor));
                }
            }

            _sensors.Add(sensor);
        }

        public void Reset(SensorResetContext context)
        {
            if (!context.IsValid)
            {
                throw new ArgumentException("A valid sensor reset context is required.", nameof(context));
            }

            _runId = context.RunId;
            for (int index = 0; index < _sensors.Count; index++)
            {
                _sensors[index].Reset(context);
            }

            _isReset = true;
        }

        public SensorOutputSet Sample(SensorSampleContext context)
        {
            if (!_isReset)
            {
                throw new InvalidOperationException("The sensor manager must be reset before sampling.");
            }

            if (!context.IsValid || context.RunId != _runId)
            {
                throw new ArgumentException(
                    "The sample context must be valid and belong to the active run.",
                    nameof(context));
            }

            List<ISensorFrame> frames = new List<ISensorFrame>(_sensors.Count);
            for (int index = 0; index < _sensors.Count; index++)
            {
                if (_sensors[index].TrySample(context, out ISensorFrame frame))
                {
                    frames.Add(frame);
                }
            }

            return new SensorOutputSet(
                _runId,
                context.Spacecraft.Sequence,
                context.Spacecraft.SimulationTimeSeconds,
                frames);
        }
    }
}
