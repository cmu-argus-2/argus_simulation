namespace Argus.Simulation.Core
{
    // A force along a body-fixed direction, at the device's mount position (D11). Comments name the
    // Basilisk thrusterDynamicEffector field each sets (BSK-8, #21); thrLoc_B is the mount's
    // PositionBodyMeters.
    public readonly struct ThrusterProfile
    {
        public ThrusterProfile(Vector3d thrustDirectionBody, double maxThrustNewtons, ActuatorLimitPolicy limitPolicy)
        {
            ThrustDirectionBody = thrustDirectionBody;
            MaxThrustNewtons = maxThrustNewtons;
            LimitPolicy = limitPolicy;
        }

        // thrDir_B: the direction of the force on the body, a unit vector.
        public Vector3d ThrustDirectionBody { get; }

        // MaxThrust.
        public double MaxThrustNewtons { get; }

        public ActuatorLimitPolicy LimitPolicy { get; }

        public bool IsValid =>
            ActuatorGeometry.IsUnitAxis(ThrustDirectionBody) &&
            ActuatorGeometry.IsPositive(MaxThrustNewtons) &&
            LimitPolicy != ActuatorLimitPolicy.Unknown;
    }
}
