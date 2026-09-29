namespace Argus.Simulation.Core
{
    // The SCStatesMsgPayload subset BasiliskEngine reads, in Basilisk's J2000 frame N.
    internal readonly struct BasiliskSpacecraftState
    {
        public BasiliskSpacecraftState(
            Vector3d positionBNInertialMeters,
            Vector3d velocityBNInertialMetersPerSecond,
            Mrp sigmaBN,
            Vector3d omegaBNBodyRadiansPerSecond)
        {
            PositionBNInertialMeters = positionBNInertialMeters;
            VelocityBNInertialMetersPerSecond = velocityBNInertialMetersPerSecond;
            SigmaBN = sigmaBN;
            OmegaBNBodyRadiansPerSecond = omegaBNBodyRadiansPerSecond;
        }

        // r_BN_N and v_BN_N: from the inertial origin, which is not Earth's centre in general.
        public Vector3d PositionBNInertialMeters { get; }
        public Vector3d VelocityBNInertialMetersPerSecond { get; }

        public Mrp SigmaBN { get; }

        // omega_BN_B.
        public Vector3d OmegaBNBodyRadiansPerSecond { get; }

        public bool IsValid =>
            PositionBNInertialMeters.IsFinite &&
            VelocityBNInertialMetersPerSecond.IsFinite &&
            SigmaBN.IsFinite &&
            OmegaBNBodyRadiansPerSecond.IsFinite;
    }
}
