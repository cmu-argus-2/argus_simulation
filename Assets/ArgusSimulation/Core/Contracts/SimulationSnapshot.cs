namespace Argus.Simulation.Core
{
    // Transport-neutral state published to Unity, agents, hardware adapters, and recorders. Deeply
    // immutable, so it is safe to hand between threads.
    public readonly struct SimulationSnapshot
    {
        private readonly SensorMeasurementSet _sensorMeasurements;

        public SimulationSnapshot(
            string runId,
            string dynamicsBackend,
            SpacecraftState spacecraft,
            ActuatorCommandSet appliedCommands,
            EnvironmentState? environment = null,
            SensorMeasurementSet sensorMeasurements = null)
        {
            RunId = runId;
            DynamicsBackend = dynamicsBackend;
            Spacecraft = spacecraft;
            AppliedCommands = appliedCommands;
            Environment = environment;
            _sensorMeasurements = sensorMeasurements;
        }

        public string RunId { get; }
        public string DynamicsBackend { get; }
        public SpacecraftState Spacecraft { get; }

        // The command applied over [t_k, t_k+1). The state at t_k was produced under snapshot k-1's
        // command.
        public ActuatorCommandSet AppliedCommands { get; }

        // Present exactly when the spacecraft state is in ITRF93, that is, when SPICE produced it.
        public EnvironmentState? Environment { get; }
        public bool HasEnvironment => Environment.HasValue;

        // Measurements the backend's sensor models produced at t_k (D10).
        public SensorMeasurementSet SensorMeasurements => _sensorMeasurements ?? SensorMeasurementSet.Empty;

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
