namespace Argus.Simulation.Core
{
    // Publishes the Basilisk magnetometer measurements that BasiliskEngine maps into each snapshot.
    public sealed class MagnetometerSensor : BackendSensorModel<MagnetometerMeasurement>
    {
        public MagnetometerSensor(SensorConfiguration configuration)
            : base(configuration, SensorKind.Magnetometer)
        {
        }

        protected override bool IsMeasurementValid(MagnetometerMeasurement measurement) => measurement.IsValid;
    }
}
