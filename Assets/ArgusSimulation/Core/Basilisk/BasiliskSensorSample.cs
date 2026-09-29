namespace Argus.Simulation.Core
{
    // One sensor output of a Basilisk step, already converted to its Argus measurement type (D8).
    internal readonly struct BasiliskSensorSample
    {
        public BasiliskSensorSample(string sensorId, long sampleTimeNanoseconds, object measurement)
        {
            SensorId = sensorId;
            SampleTimeNanoseconds = sampleTimeNanoseconds;
            Measurement = measurement;
        }

        public string SensorId { get; }

        // timeWritten() of the service's reader subscribed to the sensor's output message.
        public long SampleTimeNanoseconds { get; }

        // An ImuMeasurement, MagnetometerMeasurement or LightSensorMeasurement; null means the sensor
        // ran this step without producing data.
        public object Measurement { get; }
    }
}
