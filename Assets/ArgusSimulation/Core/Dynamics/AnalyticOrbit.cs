namespace Argus.Simulation.Core
{
    // AnalyticSimulationEngine's circular orbit. The angles are in the fixture's own inertial frame,
    // not J2000, so they are not comparable with ClassicalOrbitElements (D9).
    public readonly struct AnalyticOrbit
    {
        public AnalyticOrbit(double altitudeMeters, double inclinationDegrees, double raanDegrees, double phaseDegrees)
        {
            AltitudeMeters = altitudeMeters;
            InclinationDegrees = inclinationDegrees;
            RaanDegrees = raanDegrees;
            PhaseDegrees = phaseDegrees;
        }

        public double AltitudeMeters { get; }
        public double InclinationDegrees { get; }
        public double RaanDegrees { get; }
        public double PhaseDegrees { get; }
    }
}
