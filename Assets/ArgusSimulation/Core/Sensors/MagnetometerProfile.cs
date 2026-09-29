namespace Argus.Simulation.Core
{
    // Output is scale x (field + noise + bias), then clipped to +/-range. Basilisk 2.11.1 has no
    // magnetometer quantisation, so there is no resolution field. Comments name the Basilisk
    // magnetometer attribute each field sets.
    public readonly struct MagnetometerProfile
    {
        public MagnetometerProfile(
            double noiseStdTesla,
            Vector3d constantBiasSensorTesla,
            double scaleFactor,
            double rangeTesla)
        {
            NoiseStdTesla = noiseStdTesla;
            ConstantBiasSensorTesla = constantBiasSensorTesla;
            ScaleFactor = scaleFactor;
            RangeTesla = rangeTesla;
        }

        // Per-sample white noise at the configured sample period: senNoiseStd with AMatrix = 0.
        public double NoiseStdTesla { get; }

        // senBias.
        public Vector3d ConstantBiasSensorTesla { get; }

        // scaleFactor.
        public double ScaleFactor { get; }

        // maxOutput = +range, minOutput = -range.
        public double RangeTesla { get; }

        public bool IsValid =>
            IsFinite(NoiseStdTesla) &&
            NoiseStdTesla >= 0.0 &&
            ConstantBiasSensorTesla.IsFinite &&
            IsFinite(ScaleFactor) &&
            ScaleFactor > 0.0 &&
            IsFinite(RangeTesla) &&
            RangeTesla > 0.0;

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
