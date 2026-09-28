namespace Argus.Simulation.Core
{
    public readonly struct AngularRateMeasurement
    {
        public AngularRateMeasurement(Vector3d angularVelocitySensorRadiansPerSecond)
        {
            AngularVelocitySensorRadiansPerSecond = angularVelocitySensorRadiansPerSecond;
        }

        public Vector3d AngularVelocitySensorRadiansPerSecond { get; }

        public bool IsValid => AngularVelocitySensorRadiansPerSecond.IsFinite;
    }
}
