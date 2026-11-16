using System;

namespace Argus.Simulation.Core
{
    // The geometry rules every actuator kind shares (D11, CON-2): its axis is a body-frame unit
    // vector parameter, so its mount never rotates. validation.py repeats both rules.
    public static class ActuatorGeometry
    {
        // validation.py's AXIS_UNIT_TOLERANCE and QUATERNION_UNIT_TOLERANCE.
        public const double UnitTolerance = 1e-6;

        public static bool IsUnitAxis(Vector3d axis) => axis.IsFinite && Math.Abs(axis.Magnitude - 1.0) <= UnitTolerance;

        // +-(0, 0, 0, 1). Only x, y and z are checked, so this holds only for a unit quaternion:
        // callers check SensorMount.IsValid first.
        public static bool IsIdentityRotation(Quaterniond rotation) =>
            Math.Abs(rotation.X) <= UnitTolerance &&
            Math.Abs(rotation.Y) <= UnitTolerance &&
            Math.Abs(rotation.Z) <= UnitTolerance;

        internal static bool IsPositive(double value) => value > 0.0 && !double.IsInfinity(value);
    }
}
