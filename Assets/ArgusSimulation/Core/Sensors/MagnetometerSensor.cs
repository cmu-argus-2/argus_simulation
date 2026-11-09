namespace Argus.Simulation.Core
{
    // Publishes the Basilisk magnetometer measurements that BasiliskEngine maps into each SimulationState.
    public sealed class MagnetometerSensor : BackendSensorModel<MagnetometerMeasurement>
    {
        public MagnetometerSensor(DeviceConfiguration configuration)
            : base(configuration, MagnetometerKind.Instance)
        {
        }

        protected override bool IsMeasurementValid(MagnetometerMeasurement measurement) => measurement.IsValid;
    }
}
