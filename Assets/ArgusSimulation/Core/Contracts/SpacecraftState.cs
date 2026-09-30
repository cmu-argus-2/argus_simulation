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
            Vector3d angularVelocityBodyRadiansPerSecond,
            ReferenceFrame earthFixedFrame = ReferenceFrame.Unspecified)
        {
            Sequence = sequence;
            SimulationTimeSeconds = simulationTimeSeconds;
            TimestampUtc = timestampUtc.ToUniversalTime();
            PositionEcefMeters = positionEcefMeters;
            VelocityEcefMetersPerSecond = velocityEcefMetersPerSecond;
            BodyToEcef = bodyToEcef;
            AngularVelocityBodyRadiansPerSecond = angularVelocityBodyRadiansPerSecond;
            EarthFixedFrame = earthFixedFrame;
        }

        public long Sequence { get; }
        public double SimulationTimeSeconds { get; }
        public DateTimeOffset TimestampUtc { get; }
        public Vector3d PositionEcefMeters { get; }
        public Vector3d VelocityEcefMetersPerSecond { get; }
        public Quaterniond BodyToEcef { get; }
        // Body rate relative to inertial, in body axes (Basilisk omega_BN_B).
        public Vector3d AngularVelocityBodyRadiansPerSecond { get; }

        // Frame of the Ecef-named members. SimulationState requires it to be set.
        public ReferenceFrame EarthFixedFrame { get; }

        public bool IsValid =>
            Sequence >= 0 &&
            !double.IsNaN(SimulationTimeSeconds) &&
            !double.IsInfinity(SimulationTimeSeconds) &&
            PositionEcefMeters.IsFinite &&
            VelocityEcefMetersPerSecond.IsFinite &&
            BodyToEcef.IsFinite &&
            AngularVelocityBodyRadiansPerSecond.IsFinite &&
            EarthFixedFrame >= ReferenceFrame.Unspecified &&
            EarthFixedFrame <= ReferenceFrame.Itrf93;
    }
}
