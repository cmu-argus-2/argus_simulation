namespace Argus.Simulation.Core
{
    // Basilisk imuSensor IMUSensorMsgPayload.AngVelPlatform and AccelPlatform. The sensor frame is the
    // Basilisk platform frame P.
    public readonly struct ImuMeasurement
    {
        public ImuMeasurement(
            Vector3d angularVelocitySensorRadiansPerSecond,
            Vector3d specificForceSensorMetersPerSecondSquared)
        {
            AngularVelocitySensorRadiansPerSecond = angularVelocitySensorRadiansPerSecond;
            SpecificForceSensorMetersPerSecondSquared = specificForceSensorMetersPerSecondSquared;
        }

        // Body rate relative to inertial, in sensor axes.
        public Vector3d AngularVelocitySensorRadiansPerSecond { get; }

        // Non-gravitational acceleration at the mount, including the lever-arm terms
        // dw/dt x r + w x (w x r); zero in free fall only at the centre of mass with no rotation.
        public Vector3d SpecificForceSensorMetersPerSecondSquared { get; }

        public bool IsValid =>
            AngularVelocitySensorRadiansPerSecond.IsFinite &&
            SpecificForceSensorMetersPerSecondSquared.IsFinite;
    }
}
