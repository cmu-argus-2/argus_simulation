namespace Argus.Simulation.Core
{
    // Publishes the Basilisk imuSensor measurements that BasiliskEngine maps into each SimulationState.
    public sealed class ImuSensor : BackendSensorModel<ImuMeasurement>
    {
        public ImuSensor(SensorConfiguration configuration)
            : base(configuration, SensorKind.Imu)
        {
        }

        protected override bool IsMeasurementValid(ImuMeasurement measurement) => measurement.IsValid;
    }
}
