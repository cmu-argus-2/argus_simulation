namespace Argus.Simulation.Core
{
    // The SpicePlanetStateMsgPayload subset BasiliskEngine reads, written by Basilisk's
    // spiceInterface (D3).
    internal readonly struct BasiliskPlanetState
    {
        public BasiliskPlanetState(
            string planetName,
            Vector3d positionInertialMeters,
            Vector3d velocityInertialMetersPerSecond,
            Matrix3d j2000ToPlanetFixed,
            Matrix3d j2000ToPlanetFixedRatePerSecond,
            bool orientationComputed)
        {
            PlanetName = planetName;
            PositionInertialMeters = positionInertialMeters;
            VelocityInertialMetersPerSecond = velocityInertialMetersPerSecond;
            J2000ToPlanetFixed = j2000ToPlanetFixed;
            J2000ToPlanetFixedRatePerSecond = j2000ToPlanetFixedRatePerSecond;
            OrientationComputed = orientationComputed;
        }

        public string PlanetName { get; }
        public Vector3d PositionInertialMeters { get; }
        public Vector3d VelocityInertialMetersPerSecond { get; }

        // J20002Pfix: v_planetFixed = J2000ToPlanetFixed v_j2000.
        public Matrix3d J2000ToPlanetFixed { get; }

        // J20002Pfix_dot.
        public Matrix3d J2000ToPlanetFixedRatePerSecond { get; }

        // computeOrient; false means the two matrices were not filled.
        public bool OrientationComputed { get; }

        public bool IsValid =>
            !string.IsNullOrWhiteSpace(PlanetName) &&
            PositionInertialMeters.IsFinite &&
            VelocityInertialMetersPerSecond.IsFinite &&
            J2000ToPlanetFixed.IsFinite &&
            J2000ToPlanetFixedRatePerSecond.IsFinite;
    }
}
