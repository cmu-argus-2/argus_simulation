namespace Argus.Simulation.Core
{
    // Publishes the Basilisk coarseSunSensor measurements that BasiliskEngine maps into each SimulationState.
    public sealed class LightSensor : BackendSensorModel<LightSensorMeasurement>
    {
        public LightSensor(SensorConfiguration configuration)
            : base(configuration, SensorKind.LightSensor)
        {
        }

        protected override bool IsMeasurementValid(LightSensorMeasurement measurement) => measurement.IsValid;
    }
}
