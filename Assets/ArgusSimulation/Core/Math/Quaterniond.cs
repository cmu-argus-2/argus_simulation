using System;

namespace Argus.Simulation.Core
{
    public readonly struct Quaterniond
    {
        public Quaterniond(double x, double y, double z, double w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public double W { get; }

        public bool IsFinite =>
            !double.IsNaN(X) && !double.IsInfinity(X) &&
            !double.IsNaN(Y) && !double.IsInfinity(Y) &&
            !double.IsNaN(Z) && !double.IsInfinity(Z) &&
            !double.IsNaN(W) && !double.IsInfinity(W);

        public double Magnitude => Math.Sqrt(X * X + Y * Y + Z * Z + W * W);

        public bool IsUnit => IsFinite && Math.Abs(Magnitude - 1.0) <= 1e-6;

        public Quaterniond Normalized()
        {
            double magnitude = Magnitude;
            if (magnitude <= 1e-12)
            {
                throw new InvalidOperationException("Cannot normalize a zero-length quaternion.");
            }

            return new Quaterniond(X / magnitude, Y / magnitude, Z / magnitude, W / magnitude);
        }

        public static Quaterniond Identity => new Quaterniond(0.0, 0.0, 0.0, 1.0);

        // Inverse rotation for a unit quaternion.
        public Quaterniond Conjugate => new Quaterniond(-X, -Y, -Z, W);

        // Right-handed rotation of angleRadians about axis.
        public static Quaterniond FromAxisAngle(Vector3d axis, double angleRadians)
        {
            Vector3d unit = axis.Normalized();
            double sine = Math.Sin(0.5 * angleRadians);
            return new Quaterniond(unit.X * sine, unit.Y * sine, unit.Z * sine, Math.Cos(0.5 * angleRadians));
        }

        // Hamilton product: (a * b).Rotate(v) == a.Rotate(b.Rotate(v)).
        public static Quaterniond operator *(Quaterniond a, Quaterniond b) =>
            new Quaterniond(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        // For a body-to-frame quaternion, maps body-frame vectors into that frame.
        public Vector3d Rotate(Vector3d value)
        {
            Vector3d axis = new Vector3d(X, Y, Z);
            Vector3d t = Vector3d.Cross(axis, value) * 2.0;
            return value + t * W + Vector3d.Cross(axis, t);
        }

        // The inputs are the body-frame axes expressed in the target frame.
        public static Quaterniond FromBasis(Vector3d xAxis, Vector3d yAxis, Vector3d zAxis)
        {
            double m00 = xAxis.X;
            double m01 = yAxis.X;
            double m02 = zAxis.X;
            double m10 = xAxis.Y;
            double m11 = yAxis.Y;
            double m12 = zAxis.Y;
            double m20 = xAxis.Z;
            double m21 = yAxis.Z;
            double m22 = zAxis.Z;
            double trace = m00 + m11 + m22;

            Quaterniond result;
            if (trace > 0.0)
            {
                double s = Math.Sqrt(trace + 1.0) * 2.0;
                result = new Quaterniond((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25 * s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                double s = Math.Sqrt(1.0 + m00 - m11 - m22) * 2.0;
                result = new Quaterniond(0.25 * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                double s = Math.Sqrt(1.0 + m11 - m00 - m22) * 2.0;
                result = new Quaterniond((m01 + m10) / s, 0.25 * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            else
            {
                double s = Math.Sqrt(1.0 + m22 - m00 - m11) * 2.0;
                result = new Quaterniond((m02 + m20) / s, (m12 + m21) / s, 0.25 * s, (m10 - m01) / s);
            }

            return result.Normalized();
        }
    }
}
