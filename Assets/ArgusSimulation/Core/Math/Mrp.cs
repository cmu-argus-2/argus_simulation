namespace Argus.Simulation.Core
{
    // Modified Rodrigues parameters in Basilisk's convention: sigma_BN is body B relative to
    // inertial N. Shadow sets (|sigma| > 1) describe the same attitude and are valid.
    public readonly struct Mrp
    {
        public Mrp(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        public bool IsFinite =>
            !double.IsNaN(X) && !double.IsInfinity(X) &&
            !double.IsNaN(Y) && !double.IsInfinity(Y) &&
            !double.IsNaN(Z) && !double.IsInfinity(Z);

        // Returns the active body-to-N rotation (Hamilton, scalar last). The result is unit for any
        // finite sigma; W is negative exactly when sigma is a shadow set.
        public Quaterniond ToQuaternion()
        {
            double squaredNorm = X * X + Y * Y + Z * Z;
            double denominator = 1.0 + squaredNorm;
            return new Quaterniond(
                2.0 * X / denominator,
                2.0 * Y / denominator,
                2.0 * Z / denominator,
                (1.0 - squaredNorm) / denominator);
        }
    }
}
