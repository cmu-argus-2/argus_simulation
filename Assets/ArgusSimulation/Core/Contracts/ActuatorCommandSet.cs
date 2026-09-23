namespace Argus.Simulation.Core
{
    // Controller output in SI units and the spacecraft body frame.
    public readonly struct ActuatorCommandSet
    {
        public ActuatorCommandSet(
            long sequence,
            double applyAtSimulationTimeSeconds,
            Vector3d reactionWheelTorqueBodyNewtonMeters,
            Vector3d magnetorquerDipoleBodyAmpereSquareMeters,
            Vector3d thrusterForceBodyNewtons)
        {
            Sequence = sequence;
            ApplyAtSimulationTimeSeconds = applyAtSimulationTimeSeconds;
            ReactionWheelTorqueBodyNewtonMeters = reactionWheelTorqueBodyNewtonMeters;
            MagnetorquerDipoleBodyAmpereSquareMeters =
                magnetorquerDipoleBodyAmpereSquareMeters;
            ThrusterForceBodyNewtons = thrusterForceBodyNewtons;
        }

        public long Sequence { get; }
        public double ApplyAtSimulationTimeSeconds { get; }
        public Vector3d ReactionWheelTorqueBodyNewtonMeters { get; }
        public Vector3d MagnetorquerDipoleBodyAmpereSquareMeters { get; }
        public Vector3d ThrusterForceBodyNewtons { get; }

        public bool IsValid =>
            Sequence >= 0 &&
            !double.IsNaN(ApplyAtSimulationTimeSeconds) &&
            !double.IsInfinity(ApplyAtSimulationTimeSeconds) &&
            ApplyAtSimulationTimeSeconds >= 0.0 &&
            ReactionWheelTorqueBodyNewtonMeters.IsFinite &&
            MagnetorquerDipoleBodyAmpereSquareMeters.IsFinite &&
            ThrusterForceBodyNewtons.IsFinite;

        public static ActuatorCommandSet None(long sequence, double simulationTimeSeconds) =>
            new ActuatorCommandSet(
                sequence,
                simulationTimeSeconds,
                new Vector3d(0.0, 0.0, 0.0),
                new Vector3d(0.0, 0.0, 0.0),
                new Vector3d(0.0, 0.0, 0.0));
    }
}
