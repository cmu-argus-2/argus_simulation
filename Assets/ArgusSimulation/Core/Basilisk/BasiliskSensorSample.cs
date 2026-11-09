namespace Argus.Simulation.Core
{
    // One sensor device's output of a Basilisk step, as the backend's output map (D8, D11);
    // BasiliskStateMapper reads it into the measurement type of the device's kind.
    internal readonly struct BasiliskSensorSample
    {
        public BasiliskSensorSample(string sensorId, long sampleTimeNanoseconds, DeviceValues output)
        {
            SensorId = sensorId;
            SampleTimeNanoseconds = sampleTimeNanoseconds;
            Output = output ?? DeviceValues.Empty;
        }

        public string SensorId { get; }

        // timeWritten() of the service's reader subscribed to the sensor's output message.
        public long SampleTimeNanoseconds { get; }

        // The kind's output keys; empty means the sensor ran this step without producing data.
        public DeviceValues Output { get; }
    }
}
