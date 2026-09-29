namespace Argus.Simulation.Core
{
    // Earth-fixed frame of SpacecraftState position, velocity (relative to that frame) and BodyToEcef.
    // AnalyticEarthFixed is CircularOrbitModel's frame: z is the spin axis, it rotates at a constant
    // 7.2921150e-5 rad/s, and x is the fixture's inertial x at the epoch. It is not ITRF93 and must
    // never be converted with SPICE data. Append new values only; never renumber.
    public enum ReferenceFrame
    {
        Unspecified = 0,
        AnalyticEarthFixed = 1,
        Itrf93 = 2
    }
}
