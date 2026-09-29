namespace Argus.Simulation.Core
{
    // Basilisk magnetometer TAMSensorMsgPayload.tam_S.
    public readonly struct MagnetometerMeasurement
    {
        public MagnetometerMeasurement(Vector3d magneticFieldSensorTesla)
        {
            MagneticFieldSensorTesla = magneticFieldSensorTesla;
        }

        public Vector3d MagneticFieldSensorTesla { get; }

        public bool IsValid => MagneticFieldSensorTesla.IsFinite;
    }
}
