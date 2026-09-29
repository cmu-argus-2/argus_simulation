namespace Argus.Simulation.Core
{
    // Gyroscope half of an ImuProfile. Output is scale x (true + noise + bias), quantised, then
    // clipped. Comments name the Basilisk imuSensor attribute each field sets; the service rejects
    // combinations Basilisk 2.11.1 cannot represent (see Argus.Basilisk/README.md).
    public readonly struct GyroscopeProfile
    {
        public GyroscopeProfile(
            double noiseDensityRadiansPerSecondPerRootHertz,
            double biasRandomWalkRadiansPerSecondPerRootSecond,
            double biasRandomWalkBoundRadiansPerSecond,
            Vector3d constantBiasSensorRadiansPerSecond,
            Vector3d scaleFactorPerAxis,
            double rangeRadiansPerSecond,
            double resolutionRadiansPerSecond)
        {
            NoiseDensityRadiansPerSecondPerRootHertz = noiseDensityRadiansPerSecondPerRootHertz;
            BiasRandomWalkRadiansPerSecondPerRootSecond = biasRandomWalkRadiansPerSecondPerRootSecond;
            BiasRandomWalkBoundRadiansPerSecond = biasRandomWalkBoundRadiansPerSecond;
            ConstantBiasSensorRadiansPerSecond = constantBiasSensorRadiansPerSecond;
            ScaleFactorPerAxis = scaleFactorPerAxis;
            RangeRadiansPerSecond = rangeRadiansPerSecond;
            ResolutionRadiansPerSecond = resolutionRadiansPerSecond;
        }

        // White rate noise: PMatrixGyro with AMatrixGyro = 0.
        public double NoiseDensityRadiansPerSecondPerRootHertz { get; }

        // Rate random walk: PMatrixGyro with AMatrixGyro = I.
        public double BiasRandomWalkRadiansPerSecondPerRootSecond { get; }

        // walkBoundsGyro; 0 means unbounded.
        public double BiasRandomWalkBoundRadiansPerSecond { get; }

        // senRotBias.
        public Vector3d ConstantBiasSensorRadiansPerSecond { get; }

        // gyroScale.
        public Vector3d ScaleFactorPerAxis { get; }

        // senRotMax, symmetric on every axis.
        public double RangeRadiansPerSecond { get; }

        // Second argument of setLSBs; 0 means no quantisation.
        public double ResolutionRadiansPerSecond { get; }

        public bool IsValid =>
            IsFinite(NoiseDensityRadiansPerSecondPerRootHertz) &&
            NoiseDensityRadiansPerSecondPerRootHertz >= 0.0 &&
            IsFinite(BiasRandomWalkRadiansPerSecondPerRootSecond) &&
            BiasRandomWalkRadiansPerSecondPerRootSecond >= 0.0 &&
            IsFinite(BiasRandomWalkBoundRadiansPerSecond) &&
            BiasRandomWalkBoundRadiansPerSecond >= 0.0 &&
            ConstantBiasSensorRadiansPerSecond.IsFinite &&
            ScaleFactorPerAxis.IsFinite &&
            ScaleFactorPerAxis.X > 0.0 &&
            ScaleFactorPerAxis.Y > 0.0 &&
            ScaleFactorPerAxis.Z > 0.0 &&
            IsFinite(RangeRadiansPerSecond) &&
            RangeRadiansPerSecond > 0.0 &&
            IsFinite(ResolutionRadiansPerSecond) &&
            ResolutionRadiansPerSecond >= 0.0 &&
            ResolutionRadiansPerSecond < RangeRadiansPerSecond;

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
