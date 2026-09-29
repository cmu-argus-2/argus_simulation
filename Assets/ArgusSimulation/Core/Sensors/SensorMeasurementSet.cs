using System;
using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    // Backend measurements of one engine step, keyed by sensor ID (ordinal). Payloads are Argus
    // measurement types (D8). Immutable. "Not sampled" and "sampled but unavailable" stay distinct.
    public sealed class SensorMeasurementSet
    {
        private readonly Dictionary<string, object> _measurements;
        private readonly HashSet<string> _unavailableSensorIds;

        // unavailableSensorIds: sensors the backend ran this step without producing data, such as
        // Basilisk imuSensor's first update.
        public SensorMeasurementSet(
            IReadOnlyDictionary<string, object> measurementsBySensorId,
            IEnumerable<string> unavailableSensorIds = null)
        {
            if (measurementsBySensorId == null)
            {
                throw new ArgumentNullException(nameof(measurementsBySensorId));
            }

            _measurements = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> entry in measurementsBySensorId)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value == null)
                {
                    throw new ArgumentException(
                        "Measurements need a sensor ID and a payload.",
                        nameof(measurementsBySensorId));
                }

                _measurements.Add(entry.Key, entry.Value);
            }

            _unavailableSensorIds = new HashSet<string>(StringComparer.Ordinal);
            if (unavailableSensorIds == null)
            {
                return;
            }

            foreach (string sensorId in unavailableSensorIds)
            {
                if (string.IsNullOrWhiteSpace(sensorId) || _measurements.ContainsKey(sensorId))
                {
                    throw new ArgumentException(
                        $"Unavailable sensor ID '{sensorId}' is blank or also has a measurement.",
                        nameof(unavailableSensorIds));
                }

                _unavailableSensorIds.Add(sensorId);
            }
        }

        public static SensorMeasurementSet Empty { get; } =
            new SensorMeasurementSet(new Dictionary<string, object>());

        // True when the backend measured the sensor this step or ran it without data.
        public bool WasSampled(string sensorId) =>
            sensorId != null &&
            (_measurements.ContainsKey(sensorId) || _unavailableSensorIds.Contains(sensorId));

        // Throws when the stored payload has another type, which is a backend mapping bug.
        public bool TryGetMeasurement<TMeasurement>(string sensorId, out TMeasurement measurement)
            where TMeasurement : struct
        {
            if (sensorId == null || !_measurements.TryGetValue(sensorId, out object value))
            {
                measurement = default;
                return false;
            }

            if (!(value is TMeasurement typed))
            {
                throw new InvalidOperationException(
                    $"Sensor '{sensorId}' has a {value.GetType().Name}, not a {typeof(TMeasurement).Name}.");
            }

            measurement = typed;
            return true;
        }
    }
}
