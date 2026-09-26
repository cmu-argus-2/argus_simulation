namespace Argus.Simulation.Core
{
    // Sun geometry seen from the spacecraft at one simulation time. Directions are unit vectors
    // from the spacecraft toward the Sun's center.
    public readonly struct SunObservation
    {
        public SunObservation(
            double simulationTimeSeconds,
            Vector3d sunDirectionItrf93,
            Vector3d sunDirectionBody,
            double sunDistanceMeters,
            double illuminationFraction)
        {
            SimulationTimeSeconds = simulationTimeSeconds;
            SunDirectionItrf93 = sunDirectionItrf93;
            SunDirectionBody = sunDirectionBody;
            SunDistanceMeters = sunDistanceMeters;
            IlluminationFraction = illuminationFraction;
        }

        public double SimulationTimeSeconds { get; }
        public Vector3d SunDirectionItrf93 { get; }

        // In the spacecraft body frame, using the state's BodyToEcef attitude.
        public Vector3d SunDirectionBody { get; }

        public double SunDistanceMeters { get; }

        // Visible fraction of the Sun's disk: 1 sunlit, 0 umbra.
        public double IlluminationFraction { get; }

        public SunlightCondition Condition => EarthShadowModel.Classify(IlluminationFraction);
    }
}
