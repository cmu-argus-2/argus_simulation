namespace Argus.Simulation.Core
{
    // Per-step environment from SPICE inside Basilisk (decisions D3, D4). Only BasiliskEngine produces
    // it; engines without SPICE report none rather than approximating it.
    public readonly struct EnvironmentState
    {
        public EnvironmentState(
            Vector3d sunPositionItrf93Meters,
            Quaterniond j2000ToItrf93,
            Vector3d earthAngularVelocityItrf93RadiansPerSecond,
            double spacecraftShadowFactor)
        {
            SunPositionItrf93Meters = sunPositionItrf93Meters;
            J2000ToItrf93 = j2000ToItrf93;
            EarthAngularVelocityItrf93RadiansPerSecond = earthAngularVelocityItrf93RadiansPerSecond;
            SpacecraftShadowFactor = spacecraftShadowFactor;
        }

        // Earth centre to Sun centre, geometric (SPICE aberration correction "NONE").
        public Vector3d SunPositionItrf93Meters { get; }

        // v_itrf93 = J2000ToItrf93.Rotate(v_j2000), from SPICE J20002Pfix.
        public Quaterniond J2000ToItrf93 { get; }

        // ITRF93 relative to J2000, in ITRF93 axes; v_itrf93 = R v_j2000 - omega x r_itrf93.
        public Vector3d EarthAngularVelocityItrf93RadiansPerSecond { get; }

        // Basilisk EclipseMsg.shadowFactor: 1 is fully sunlit, 0 is umbra.
        public double SpacecraftShadowFactor { get; }

        public bool IsValid =>
            SunPositionItrf93Meters.IsFinite &&
            SunPositionItrf93Meters.Magnitude > 0.0 &&
            J2000ToItrf93.IsUnit &&
            EarthAngularVelocityItrf93RadiansPerSecond.IsFinite &&
            SpacecraftShadowFactor >= 0.0 &&
            SpacecraftShadowFactor <= 1.0;
    }
}
