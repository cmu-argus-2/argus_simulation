namespace Argus.Simulation.Core
{
    // Publishes the Basilisk coarseSunSensor measurements that BasiliskEngine maps into each SimulationState.
    public sealed class LightSensor : BackendSensorModel<LightSensorMeasurement>
    {
        public LightSensor(DeviceConfiguration configuration)
            : base(configuration, LightSensorKind.Instance)
        {
        }

        protected override bool IsMeasurementValid(LightSensorMeasurement measurement) => measurement.IsValid;
    }
}
