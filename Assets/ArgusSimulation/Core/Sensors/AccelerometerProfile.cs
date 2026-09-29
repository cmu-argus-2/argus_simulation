namespace Argus.Simulation.Core
{
    // Accelerometer half of an ImuProfile, with the same shape and rules as GyroscopeProfile.
    // Comments name the Basilisk imuSensor attribute each field sets.
    public readonly struct AccelerometerProfile
    {
        public AccelerometerProfile(
            double noiseDensityMetersPerSecondSquaredPerRootHertz,
            double biasRandomWalkMetersPerSecondSquaredPerRootSecond,
            double biasRandomWalkBoundMetersPerSecondSquared,
            Vector3d constantBiasSensorMetersPerSecondSquared,
            Vector3d scaleFactorPerAxis,
            double rangeMetersPerSecondSquared,
            double resolutionMetersPerSecondSquared)
        {
            NoiseDensityMetersPerSecondSquaredPerRootHertz = noiseDensityMetersPerSecondSquaredPerRootHertz;
            BiasRandomWalkMetersPerSecondSquaredPerRootSecond = biasRandomWalkMetersPerSecondSquaredPerRootSecond;
            BiasRandomWalkBoundMetersPerSecondSquared = biasRandomWalkBoundMetersPerSecondSquared;
            ConstantBiasSensorMetersPerSecondSquared = constantBiasSensorMetersPerSecondSquared;
            ScaleFactorPerAxis = scaleFactorPerAxis;
            RangeMetersPerSecondSquared = rangeMetersPerSecondSquared;
            ResolutionMetersPerSecondSquared = resolutionMetersPerSecondSquared;
        }

        // White noise: PMatrixAccel with AMatrixAccel = 0.
        public double NoiseDensityMetersPerSecondSquaredPerRootHertz { get; }

        // Bias random walk: PMatrixAccel with AMatrixAccel = I.
        public double BiasRandomWalkMetersPerSecondSquaredPerRootSecond { get; }

        // walkBoundsAccel; 0 means unbounded.
        public double BiasRandomWalkBoundMetersPerSecondSquared { get; }

        // senTransBias.
        public Vector3d ConstantBiasSensorMetersPerSecondSquared { get; }

        // accelScale.
        public Vector3d ScaleFactorPerAxis { get; }

        // senTransMax, symmetric on every axis.
        public double RangeMetersPerSecondSquared { get; }

        // First argument of setLSBs; 0 means no quantisation.
        public double ResolutionMetersPerSecondSquared { get; }

        public bool IsValid =>
            IsFinite(NoiseDensityMetersPerSecondSquaredPerRootHertz) &&
            NoiseDensityMetersPerSecondSquaredPerRootHertz >= 0.0 &&
            IsFinite(BiasRandomWalkMetersPerSecondSquaredPerRootSecond) &&
            BiasRandomWalkMetersPerSecondSquaredPerRootSecond >= 0.0 &&
            IsFinite(BiasRandomWalkBoundMetersPerSecondSquared) &&
            BiasRandomWalkBoundMetersPerSecondSquared >= 0.0 &&
            ConstantBiasSensorMetersPerSecondSquared.IsFinite &&
            ScaleFactorPerAxis.IsFinite &&
            ScaleFactorPerAxis.X > 0.0 &&
            ScaleFactorPerAxis.Y > 0.0 &&
            ScaleFactorPerAxis.Z > 0.0 &&
            IsFinite(RangeMetersPerSecondSquared) &&
            RangeMetersPerSecondSquared > 0.0 &&
            IsFinite(ResolutionMetersPerSecondSquared) &&
            ResolutionMetersPerSecondSquared >= 0.0 &&
            ResolutionMetersPerSecondSquared < RangeMetersPerSecondSquared;

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
