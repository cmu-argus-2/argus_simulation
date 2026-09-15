using System;

namespace Argus.Simulation.Core
{
    public readonly struct SpacecraftState
    {
        public SpacecraftState(
            long sequence,
            double simulationTimeSeconds,
            DateTimeOffset timestampUtc,
            Vector3d positionEcefMeters,
            Vector3d velocityEcefMetersPerSecond,
            Quaterniond bodyToEcef,
            Vector3d angularVelocityBodyRadiansPerSecond)
        {
            Sequence = sequence;
            SimulationTimeSeconds = simulationTimeSeconds;
            TimestampUtc = timestampUtc.ToUniversalTime();
            PositionEcefMeters = positionEcefMeters;
            VelocityEcefMetersPerSecond = velocityEcefMetersPerSecond;
            BodyToEcef = bodyToEcef;
            AngularVelocityBodyRadiansPerSecond = angularVelocityBodyRadiansPerSecond;
        }

        public long Sequence { get; }
        public double SimulationTimeSeconds { get; }
        public DateTimeOffset TimestampUtc { get; }
        public Vector3d PositionEcefMeters { get; }
        public Vector3d VelocityEcefMetersPerSecond { get; }
        public Quaterniond BodyToEcef { get; }
        public Vector3d AngularVelocityBodyRadiansPerSecond { get; }

        public bool IsValid =>
            Sequence >= 0 &&
            !double.IsNaN(SimulationTimeSeconds) &&
            !double.IsInfinity(SimulationTimeSeconds) &&
            PositionEcefMeters.IsFinite &&
            VelocityEcefMetersPerSecond.IsFinite &&
            BodyToEcef.IsFinite &&
            AngularVelocityBodyRadiansPerSecond.IsFinite;
    }
}
