using System;

namespace Argus.Simulation.Core
{
    // Deterministic development source. It is intentionally replaceable, not a flight-dynamics model.
    // The orbit is defined in J2000: inclination is measured from the J2000 equator and RAAN from
    // the J2000 +X axis. Earth-fixed output uses either an ephemeris sample (J2000 -> ITRF93) or,
    // when none is supplied, a simplified constant spin with zero rotation angle at the epoch.
    public sealed class CircularOrbitModel
    {
        public const double EarthEquatorialRadiusMeters = 6_378_137.0;
        public const double EarthGravitationalParameter = 3.986004418e14;
        public const double EarthRotationRadiansPerSecond = 7.2921150e-5;

        private readonly DateTimeOffset _epochUtc;
        private readonly double _radiusMeters;
        private readonly double _inclinationRadians;
        private readonly double _raanRadians;
        private readonly double _phaseRadians;
        private readonly double _meanMotionRadiansPerSecond;

        public CircularOrbitModel(
            DateTimeOffset epochUtc,
            double altitudeMeters,
            double inclinationDegrees,
            double raanDegrees,
            double phaseDegrees)
        {
            if (altitudeMeters <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(altitudeMeters));
            }

            _epochUtc = epochUtc.ToUniversalTime();
            _radiusMeters = EarthEquatorialRadiusMeters + altitudeMeters;
            _inclinationRadians = DegreesToRadians(inclinationDegrees);
            _raanRadians = DegreesToRadians(raanDegrees);
            _phaseRadians = DegreesToRadians(phaseDegrees);
            _meanMotionRadiansPerSecond = Math.Sqrt(
                EarthGravitationalParameter / (_radiusMeters * _radiusMeters * _radiusMeters));
        }

        public double MeanMotionRadiansPerSecond => _meanMotionRadiansPerSecond;

        // Simplified Earth rotation. Longitudes are offset by the true Earth rotation angle at the epoch.
        public SpacecraftState Sample(long sequence, double simulationTimeSeconds)
        {
            SampleJ2000(simulationTimeSeconds, out Vector3d positionJ2000, out Vector3d velocityJ2000);
            RotateToEarthFixed(
                positionJ2000,
                velocityJ2000,
                simulationTimeSeconds,
                out Vector3d positionEcef,
                out Vector3d velocityEcef);

            return BuildState(
                sequence,
                simulationTimeSeconds,
                _epochUtc.AddSeconds(simulationTimeSeconds),
                positionEcef,
                velocityEcef);
        }

        // Earth-fixed state via the ephemeris J2000 -> ITRF93 state transform at the sample's time.
        public SpacecraftState Sample(long sequence, EphemerisSample environment)
        {
            double simulationTimeSeconds = environment.SimulationTimeSeconds;
            SampleJ2000(simulationTimeSeconds, out Vector3d positionJ2000, out Vector3d velocityJ2000);
            environment.TransformStateToItrf93(
                positionJ2000,
                velocityJ2000,
                out Vector3d positionItrf93,
                out Vector3d velocityItrf93);

            return BuildState(
                sequence,
                simulationTimeSeconds,
                environment.TimestampUtc,
                positionItrf93,
                velocityItrf93);
        }

        // Orbit state in J2000, independent of Earth rotation.
        public void SampleJ2000(
            double simulationTimeSeconds,
            out Vector3d positionJ2000Meters,
            out Vector3d velocityJ2000MetersPerSecond)
        {
            double argument = _phaseRadians + _meanMotionRadiansPerSecond * simulationTimeSeconds;
            Vector3d positionOrbital = new Vector3d(
                _radiusMeters * Math.Cos(argument),
                _radiusMeters * Math.Sin(argument),
                0.0);
            Vector3d velocityOrbital = new Vector3d(
                -_radiusMeters * _meanMotionRadiansPerSecond * Math.Sin(argument),
                _radiusMeters * _meanMotionRadiansPerSecond * Math.Cos(argument),
                0.0);

            positionJ2000Meters = RotateZ(RotateX(positionOrbital, _inclinationRadians), _raanRadians);
            velocityJ2000MetersPerSecond = RotateZ(RotateX(velocityOrbital, _inclinationRadians), _raanRadians);
        }

        // Body +X along Earth-fixed velocity, +Z toward Earth's center (nadir), +Y completing the triad.
        public static Quaterniond NadirTrackingAttitude(Vector3d positionEcefMeters, Vector3d velocityEcefMetersPerSecond)
        {
            Vector3d bodyX = velocityEcefMetersPerSecond.Normalized();
            Vector3d bodyZ = (-positionEcefMeters).Normalized();
            Vector3d bodyY = Vector3d.Cross(bodyZ, bodyX).Normalized();
            bodyX = Vector3d.Cross(bodyY, bodyZ).Normalized();
            return Quaterniond.FromBasis(bodyX, bodyY, bodyZ);
        }

        private SpacecraftState BuildState(
            long sequence,
            double simulationTimeSeconds,
            DateTimeOffset timestampUtc,
            Vector3d positionEcef,
            Vector3d velocityEcef)
        {
            return new SpacecraftState(
                sequence,
                simulationTimeSeconds,
                timestampUtc,
                positionEcef,
                velocityEcef,
                NadirTrackingAttitude(positionEcef, velocityEcef),
                new Vector3d(0.0, _meanMotionRadiansPerSecond, 0.0));
        }

        // Simplified Earth rotation: a constant spin about +Z with zero rotation angle at the epoch.
        private static void RotateToEarthFixed(
            Vector3d positionEci,
            Vector3d velocityEci,
            double simulationTimeSeconds,
            out Vector3d positionEcef,
            out Vector3d velocityEcef)
        {
            double earthAngle = EarthRotationRadiansPerSecond * simulationTimeSeconds;
            positionEcef = RotateZ(positionEci, -earthAngle);
            Vector3d earthCrossPosition = new Vector3d(
                -EarthRotationRadiansPerSecond * positionEci.Y,
                EarthRotationRadiansPerSecond * positionEci.X,
                0.0);
            velocityEcef = RotateZ(velocityEci - earthCrossPosition, -earthAngle);
        }

        private static double DegreesToRadians(double value) => value * Math.PI / 180.0;

        private static Vector3d RotateX(Vector3d value, double angle)
        {
            double cosine = Math.Cos(angle);
            double sine = Math.Sin(angle);
            return new Vector3d(
                value.X,
                cosine * value.Y - sine * value.Z,
                sine * value.Y + cosine * value.Z);
        }

        private static Vector3d RotateZ(Vector3d value, double angle)
        {
            double cosine = Math.Cos(angle);
            double sine = Math.Sin(angle);
            return new Vector3d(
                cosine * value.X - sine * value.Y,
                sine * value.X + cosine * value.Y,
                value.Z);
        }
    }
}
