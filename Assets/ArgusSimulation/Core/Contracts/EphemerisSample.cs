using System;

namespace Argus.Simulation.Core
{
    // Environment geometry at one simulation time: Earth orientation and Sun position.
    // Inertial frame is J2000 (EME2000); Earth-fixed frame is ITRF93. SI units throughout.
    public readonly struct EphemerisSample
    {
        public EphemerisSample(
            double simulationTimeSeconds,
            DateTimeOffset timestampUtc,
            double ephemerisTimeSeconds,
            Matrix3d j2000ToItrf93,
            Matrix3d j2000ToItrf93Rate,
            Vector3d sunPositionJ2000Meters)
        {
            SimulationTimeSeconds = simulationTimeSeconds;
            TimestampUtc = timestampUtc.ToUniversalTime();
            EphemerisTimeSeconds = ephemerisTimeSeconds;
            J2000ToItrf93 = j2000ToItrf93;
            J2000ToItrf93Rate = j2000ToItrf93Rate;
            SunPositionJ2000Meters = sunPositionJ2000Meters;
        }

        public double SimulationTimeSeconds { get; }
        public DateTimeOffset TimestampUtc { get; }

        // SPICE ephemeris time: TDB seconds past J2000.
        public double EphemerisTimeSeconds { get; }

        // R such that v_itrf93 = R * v_j2000.
        public Matrix3d J2000ToItrf93 { get; }

        // dR/dt in 1/s. With R this forms the SPICE 6x6 state transform [[R, 0], [dR/dt, R]].
        public Matrix3d J2000ToItrf93Rate { get; }

        // Geometric Sun position relative to Earth's center.
        public Vector3d SunPositionJ2000Meters { get; }

        public Vector3d SunPositionItrf93Meters => J2000ToItrf93.Transform(SunPositionJ2000Meters);

        public bool IsValid =>
            !double.IsNaN(SimulationTimeSeconds) &&
            !double.IsInfinity(SimulationTimeSeconds) &&
            !double.IsNaN(EphemerisTimeSeconds) &&
            !double.IsInfinity(EphemerisTimeSeconds) &&
            J2000ToItrf93.IsFinite &&
            J2000ToItrf93Rate.IsFinite &&
            SunPositionJ2000Meters.IsFinite;

        public Vector3d TransformPositionToItrf93(Vector3d positionJ2000Meters) =>
            J2000ToItrf93.Transform(positionJ2000Meters);

        // Applies the full state transform, so velocity includes the rotating-frame term.
        public void TransformStateToItrf93(
            Vector3d positionJ2000Meters,
            Vector3d velocityJ2000MetersPerSecond,
            out Vector3d positionItrf93Meters,
            out Vector3d velocityItrf93MetersPerSecond)
        {
            positionItrf93Meters = J2000ToItrf93.Transform(positionJ2000Meters);
            velocityItrf93MetersPerSecond =
                J2000ToItrf93.Transform(velocityJ2000MetersPerSecond) +
                J2000ToItrf93Rate.Transform(positionJ2000Meters);
        }

        // Inverse state transform: [[R^T, 0], [dR^T/dt, R^T]].
        public void TransformStateToJ2000(
            Vector3d positionItrf93Meters,
            Vector3d velocityItrf93MetersPerSecond,
            out Vector3d positionJ2000Meters,
            out Vector3d velocityJ2000MetersPerSecond)
        {
            Matrix3d inverse = J2000ToItrf93.Transposed;
            positionJ2000Meters = inverse.Transform(positionItrf93Meters);
            velocityJ2000MetersPerSecond =
                inverse.Transform(velocityItrf93MetersPerSecond) +
                J2000ToItrf93Rate.Transposed.Transform(positionItrf93Meters);
        }
    }
}
