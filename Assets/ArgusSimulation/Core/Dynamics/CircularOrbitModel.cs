using System;

namespace Argus.Simulation.Core
{
    // Deterministic development source. It is intentionally replaceable, not a flight-dynamics model.
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

        public SpacecraftState Sample(long sequence, double simulationTimeSeconds)
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

            Vector3d positionEci = RotateZ(RotateX(positionOrbital, _inclinationRadians), _raanRadians);
            Vector3d velocityEci = RotateZ(RotateX(velocityOrbital, _inclinationRadians), _raanRadians);
            double earthAngle = EarthRotationRadiansPerSecond * simulationTimeSeconds;
            Vector3d positionEcef = RotateZ(positionEci, -earthAngle);
            Vector3d earthCrossPosition = new Vector3d(
                -EarthRotationRadiansPerSecond * positionEci.Y,
                EarthRotationRadiansPerSecond * positionEci.X,
                0.0);
            Vector3d velocityEcef = RotateZ(velocityEci - earthCrossPosition, -earthAngle);

            Vector3d bodyX = velocityEcef.Normalized();
            Vector3d bodyZ = (-positionEcef).Normalized();
            Vector3d bodyY = Vector3d.Cross(bodyZ, bodyX).Normalized();
            bodyX = Vector3d.Cross(bodyY, bodyZ).Normalized();

            return new SpacecraftState(
                sequence,
                simulationTimeSeconds,
                _epochUtc.AddSeconds(simulationTimeSeconds),
                positionEcef,
                velocityEcef,
                Quaterniond.FromBasis(bodyX, bodyY, bodyZ),
                new Vector3d(0.0, _meanMotionRadiansPerSecond, 0.0));
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
