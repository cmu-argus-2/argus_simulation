namespace Argus.Simulation.Core
{
    // Transport-neutral state published to Unity, agents, hardware adapters, and recorders. Deeply
    // immutable, so it is safe to hand between threads.
    public readonly struct SimulationSnapshot
    {
        public SimulationSnapshot(
            string runId,
            string dynamicsBackend,
            SpacecraftState spacecraft,
            ActuatorCommandSet appliedCommands,
            EnvironmentState? environment = null)
        {
            RunId = runId;
            DynamicsBackend = dynamicsBackend;
            Spacecraft = spacecraft;
            AppliedCommands = appliedCommands;
            Environment = environment;
        }

        public string RunId { get; }
        public string DynamicsBackend { get; }
        public SpacecraftState Spacecraft { get; }
        public ActuatorCommandSet AppliedCommands { get; }

        // Present exactly when the spacecraft state is in ITRF93, that is, when SPICE produced it.
        public EnvironmentState? Environment { get; }
        public bool HasEnvironment => Environment.HasValue;

        public bool IsValid =>
            !string.IsNullOrWhiteSpace(RunId) &&
            !string.IsNullOrWhiteSpace(DynamicsBackend) &&
            Spacecraft.IsValid &&
            AppliedCommands.IsValid &&
            Spacecraft.Sequence == AppliedCommands.Sequence &&
            Spacecraft.EarthFixedFrame != ReferenceFrame.Unspecified &&
            HasEnvironment == (Spacecraft.EarthFixedFrame == ReferenceFrame.Itrf93) &&
            (!HasEnvironment || Environment.Value.IsValid);
    }
}
