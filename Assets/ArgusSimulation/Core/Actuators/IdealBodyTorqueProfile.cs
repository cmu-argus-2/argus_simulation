namespace Argus.Simulation.Core
{
    // A torque applied directly to the body, for a spacecraft whose actuators aren't known yet
    // (D11). Basilisk's extForceTorque (BSK-8, #21).
    public readonly struct IdealBodyTorqueProfile
    {
        public IdealBodyTorqueProfile(Vector3d maxTorqueBodyNewtonMeters, ActuatorLimitPolicy limitPolicy)
        {
            MaxTorqueBodyNewtonMeters = maxTorqueBodyNewtonMeters;
            LimitPolicy = limitPolicy;
        }

        // Bounds |torque| on each body axis.
        public Vector3d MaxTorqueBodyNewtonMeters { get; }
        public ActuatorLimitPolicy LimitPolicy { get; }

        public bool IsValid =>
            ActuatorGeometry.IsPositive(MaxTorqueBodyNewtonMeters.X) &&
            ActuatorGeometry.IsPositive(MaxTorqueBodyNewtonMeters.Y) &&
            ActuatorGeometry.IsPositive(MaxTorqueBodyNewtonMeters.Z) &&
            LimitPolicy != ActuatorLimitPolicy.Unknown;
    }
}
