namespace Argus.Simulation.Core
{
    public interface IGyroMeasurementSource
    {
        bool TryGetGyroMeasurement(
            long sequence,
            double simulationTimeSeconds,
            out SensorFrame<Vector3d> measurement);
    }
}
