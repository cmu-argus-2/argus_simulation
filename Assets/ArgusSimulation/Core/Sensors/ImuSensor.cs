namespace Argus.Simulation.Core
{
    // Publishes the Basilisk imuSensor measurements that BasiliskEngine maps into each SimulationState.
    public sealed class ImuSensor : BackendSensorModel<ImuMeasurement>
    {
        public ImuSensor(DeviceConfiguration configuration)
            : base(configuration, ImuKind.Instance)
        {
        }

        protected override bool IsMeasurementValid(ImuMeasurement measurement) => measurement.IsValid;
    }
}
