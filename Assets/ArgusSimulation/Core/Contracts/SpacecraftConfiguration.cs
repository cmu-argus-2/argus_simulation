using System;

namespace Argus.Simulation.Core
{
    // Rigid-body properties and initial attitude for Basilisk. The body origin is the centre of mass.
    public readonly struct SpacecraftConfiguration
    {
        public SpacecraftConfiguration(
            double massKilograms,
            Matrix3d inertiaBodyKilogramSquareMeters,
            Quaterniond initialBodyToJ2000,
            Vector3d initialAngularVelocityBodyRadiansPerSecond)
        {
            MassKilograms = massKilograms;
            InertiaBodyKilogramSquareMeters = inertiaBodyKilogramSquareMeters;
            InitialBodyToJ2000 = initialBodyToJ2000;
            InitialAngularVelocityBodyRadiansPerSecond = initialAngularVelocityBodyRadiansPerSecond;
        }

        public double MassKilograms { get; }

        // About the centre of mass, in body axes.
        public Matrix3d InertiaBodyKilogramSquareMeters { get; }

        public Quaterniond InitialBodyToJ2000 { get; }

        // Body relative to J2000, in body axes.
        public Vector3d InitialAngularVelocityBodyRadiansPerSecond { get; }

        public bool IsValid =>
            !double.IsNaN(MassKilograms) &&
            !double.IsInfinity(MassKilograms) &&
            MassKilograms > 0.0 &&
            IsSymmetricPositiveDefinite(InertiaBodyKilogramSquareMeters) &&
            InitialBodyToJ2000.IsUnit &&
            InitialAngularVelocityBodyRadiansPerSecond.IsFinite;

        // Symmetric within 1e-12 of the trace, with positive leading principal minors (Sylvester).
        private static bool IsSymmetricPositiveDefinite(Matrix3d matrix)
        {
            if (!matrix.IsFinite)
            {
                return false;
            }

            Vector3d row0 = matrix.Row0;
            Vector3d row1 = matrix.Row1;
            Vector3d row2 = matrix.Row2;
            double tolerance = 1e-12 * Math.Abs(row0.X + row1.Y + row2.Z);
            if (Math.Abs(row0.Y - row1.X) > tolerance ||
                Math.Abs(row0.Z - row2.X) > tolerance ||
                Math.Abs(row1.Z - row2.Y) > tolerance)
            {
                return false;
            }

            double firstMinor = row0.X;
            double secondMinor = row0.X * row1.Y - row0.Y * row1.X;
            double determinant = Vector3d.Dot(row0, Vector3d.Cross(row1, row2));
            return firstMinor > 0.0 && secondMinor > 0.0 && determinant > 0.0;
        }
    }
}
