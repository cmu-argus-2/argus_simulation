namespace Argus.Simulation.Core
{
    // A dipole along a body-fixed axis (D11). Comments name the Basilisk MtbEffector field each sets
    // (BSK-8, #21).
    public readonly struct MagneticTorquerProfile
    {
        public MagneticTorquerProfile(Vector3d dipoleAxisBody, double maxDipoleAmpereSquareMeters, ActuatorLimitPolicy limitPolicy)
        {
            DipoleAxisBody = dipoleAxisBody;
            MaxDipoleAmpereSquareMeters = maxDipoleAmpereSquareMeters;
            LimitPolicy = limitPolicy;
        }

        // One column of GtMatrix_B, a unit vector.
        public Vector3d DipoleAxisBody { get; }

        // maxMtbDipoles.
        public double MaxDipoleAmpereSquareMeters { get; }

        public ActuatorLimitPolicy LimitPolicy { get; }

        public bool IsValid =>
            ActuatorGeometry.IsUnitAxis(DipoleAxisBody) &&
            ActuatorGeometry.IsPositive(MaxDipoleAmpereSquareMeters) &&
            LimitPolicy != ActuatorLimitPolicy.Unknown;
    }
}
