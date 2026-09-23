using System;

namespace Argus.Simulation.Core
{
    public readonly struct Vector3d : IEquatable<Vector3d>
    {
        public Vector3d(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        public double Magnitude => Math.Sqrt(Dot(this, this));

        public bool IsFinite =>
            !double.IsNaN(X) && !double.IsInfinity(X) &&
            !double.IsNaN(Y) && !double.IsInfinity(Y) &&
            !double.IsNaN(Z) && !double.IsInfinity(Z);

        public Vector3d Normalized()
        {
            double magnitude = Magnitude;
            if (magnitude <= 1e-12)
            {
                throw new InvalidOperationException("Cannot normalize a zero-length vector.");
            }

            return this / magnitude;
        }

        public static double Dot(Vector3d left, Vector3d right) =>
            left.X * right.X + left.Y * right.Y + left.Z * right.Z;

        public static Vector3d Cross(Vector3d left, Vector3d right) =>
            new Vector3d(
                left.Y * right.Z - left.Z * right.Y,
                left.Z * right.X - left.X * right.Z,
                left.X * right.Y - left.Y * right.X);

        public static Vector3d operator +(Vector3d left, Vector3d right) =>
            new Vector3d(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

        public static Vector3d operator -(Vector3d left, Vector3d right) =>
            new Vector3d(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

        public static Vector3d operator -(Vector3d value) =>
            new Vector3d(-value.X, -value.Y, -value.Z);

        public static Vector3d operator *(Vector3d value, double scalar) =>
            new Vector3d(value.X * scalar, value.Y * scalar, value.Z * scalar);

        public static Vector3d operator /(Vector3d value, double scalar) =>
            new Vector3d(value.X / scalar, value.Y / scalar, value.Z / scalar);

        public bool Equals(Vector3d other) =>
            X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

        public override bool Equals(object obj) => obj is Vector3d other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public override string ToString() => $"({X:R}, {Y:R}, {Z:R})";
    }
}
