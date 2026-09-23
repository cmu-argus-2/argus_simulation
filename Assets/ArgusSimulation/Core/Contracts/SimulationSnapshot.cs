namespace Argus.Simulation.Core
{
    // Transport-neutral state published to Unity, agents, hardware adapters, and recorders.
    public readonly struct SimulationSnapshot
    {
        public SimulationSnapshot(
            string runId,
            string dynamicsBackend,
            SpacecraftState spacecraft,
            ActuatorCommandSet appliedCommands)
        {
            RunId = runId;
            DynamicsBackend = dynamicsBackend;
            Spacecraft = spacecraft;
            AppliedCommands = appliedCommands;
        }

        public string RunId { get; }
        public string DynamicsBackend { get; }
        public SpacecraftState Spacecraft { get; }
        public ActuatorCommandSet AppliedCommands { get; }

        public bool IsValid =>
            !string.IsNullOrWhiteSpace(RunId) &&
            !string.IsNullOrWhiteSpace(DynamicsBackend) &&
            Spacecraft.IsValid &&
            AppliedCommands.IsValid &&
            Spacecraft.Sequence == AppliedCommands.Sequence;
    }
}
