namespace Argus.Simulation.Core
{
    // Osculating Earth-centred elements at SimulationConfiguration.EpochUtc, in J2000 axes for
    // Basilisk (RAAN is measured from J2000 x, not from Greenwich).
    public readonly struct ClassicalOrbitElements
    {
        private const double EarthEquatorialRadiusMeters = 6_378_137.0;

        public ClassicalOrbitElements(
            double semiMajorAxisMeters,
            double eccentricity,
            double inclinationDegrees,
            double raanDegrees,
            double argumentOfPeriapsisDegrees,
            double trueAnomalyDegrees)
        {
            SemiMajorAxisMeters = semiMajorAxisMeters;
            Eccentricity = eccentricity;
            InclinationDegrees = inclinationDegrees;
            RaanDegrees = raanDegrees;
            ArgumentOfPeriapsisDegrees = argumentOfPeriapsisDegrees;
            TrueAnomalyDegrees = trueAnomalyDegrees;
        }

        public double SemiMajorAxisMeters { get; }
        public double Eccentricity { get; }
        public double InclinationDegrees { get; }
        public double RaanDegrees { get; }
        public double ArgumentOfPeriapsisDegrees { get; }
        public double TrueAnomalyDegrees { get; }

        // Periapsis above the equatorial radius also catches an altitude entered as the semi-major axis.
        public bool IsValid =>
            IsFinite(SemiMajorAxisMeters) &&
            IsFinite(Eccentricity) &&
            IsFinite(InclinationDegrees) &&
            IsFinite(RaanDegrees) &&
            IsFinite(ArgumentOfPeriapsisDegrees) &&
            IsFinite(TrueAnomalyDegrees) &&
            Eccentricity >= 0.0 &&
            Eccentricity < 1.0 &&
            InclinationDegrees >= 0.0 &&
            InclinationDegrees <= 180.0 &&
            SemiMajorAxisMeters * (1.0 - Eccentricity) > EarthEquatorialRadiusMeters;

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
