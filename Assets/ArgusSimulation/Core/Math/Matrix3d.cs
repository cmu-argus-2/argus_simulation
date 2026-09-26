using System;

namespace Argus.Simulation.Core
{
    // Row-major 3x3 matrix. Transform(v) computes M * v for column vector v.
    public readonly struct Matrix3d
    {
        public Matrix3d(
            double m00, double m01, double m02,
            double m10, double m11, double m12,
            double m20, double m21, double m22)
        {
            M00 = m00; M01 = m01; M02 = m02;
            M10 = m10; M11 = m11; M12 = m12;
            M20 = m20; M21 = m21; M22 = m22;
        }

        public double M00 { get; }
        public double M01 { get; }
        public double M02 { get; }
        public double M10 { get; }
        public double M11 { get; }
        public double M12 { get; }
        public double M20 { get; }
        public double M21 { get; }
        public double M22 { get; }

        public static Matrix3d Identity => new Matrix3d(1, 0, 0, 0, 1, 0, 0, 0, 1);

        public bool IsFinite =>
            IsFiniteValue(M00) && IsFiniteValue(M01) && IsFiniteValue(M02) &&
            IsFiniteValue(M10) && IsFiniteValue(M11) && IsFiniteValue(M12) &&
            IsFiniteValue(M20) && IsFiniteValue(M21) && IsFiniteValue(M22);

        public Matrix3d Transposed =>
            new Matrix3d(M00, M10, M20, M01, M11, M21, M02, M12, M22);

        public static Matrix3d FromRowMajor(double[] values)
        {
            if (values == null || values.Length != 9)
            {
                throw new ArgumentException("Expected 9 row-major values.", nameof(values));
            }

            return new Matrix3d(
                values[0], values[1], values[2],
                values[3], values[4], values[5],
                values[6], values[7], values[8]);
        }

        public double[] ToRowMajor() =>
            new[] { M00, M01, M02, M10, M11, M12, M20, M21, M22 };

        public Vector3d Transform(Vector3d value) =>
            new Vector3d(
                M00 * value.X + M01 * value.Y + M02 * value.Z,
                M10 * value.X + M11 * value.Y + M12 * value.Z,
                M20 * value.X + M21 * value.Y + M22 * value.Z);

        public static Matrix3d operator *(Matrix3d a, Matrix3d b) =>
            new Matrix3d(
                a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
                a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
                a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
                a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
                a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
                a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
                a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
                a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
                a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);

        public static Matrix3d operator +(Matrix3d a, Matrix3d b) =>
            new Matrix3d(
                a.M00 + b.M00, a.M01 + b.M01, a.M02 + b.M02,
                a.M10 + b.M10, a.M11 + b.M11, a.M12 + b.M12,
                a.M20 + b.M20, a.M21 + b.M21, a.M22 + b.M22);

        public static Matrix3d operator *(Matrix3d value, double scalar) =>
            new Matrix3d(
                value.M00 * scalar, value.M01 * scalar, value.M02 * scalar,
                value.M10 * scalar, value.M11 * scalar, value.M12 * scalar,
                value.M20 * scalar, value.M21 * scalar, value.M22 * scalar);

        private static bool IsFiniteValue(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
