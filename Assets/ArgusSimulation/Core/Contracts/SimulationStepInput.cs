namespace Argus.Simulation.Core
{
    public readonly struct SimulationStepInput
    {
        public SimulationStepInput(
            long sequence,
            double simulationTimeSeconds,
            ActuatorCommandSet commands)
        {
            Sequence = sequence;
            SimulationTimeSeconds = simulationTimeSeconds;
            Commands = commands;
        }

        public long Sequence { get; }
        public double SimulationTimeSeconds { get; }
        public ActuatorCommandSet Commands { get; }

        public bool IsValid =>
            Sequence >= 0 &&
            SimulationTimeSeconds >= 0.0 &&
            !double.IsNaN(SimulationTimeSeconds) &&
            !double.IsInfinity(SimulationTimeSeconds) &&
            Commands.IsValid &&
            Commands.Sequence == Sequence &&
            SimulationTime.AreSame(Commands.ApplyAtSimulationTimeSeconds, SimulationTimeSeconds);
    }
}
