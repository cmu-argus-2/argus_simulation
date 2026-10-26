namespace Argus.Simulation.Core
{
    // Everything true at one step t_k: spacecraft state, environment, backend sensor measurements
    // and the applied commands, always delivered together so every part describes the same instant.
    // Transport-neutral; published to Unity and the run recorder. Controllers, including HIL flight
    // computers, never receive it: they get observations through the gateway (D7). Deeply
    // immutable, so it is safe to hand between threads.
    public readonly struct SimulationState
    {
        private readonly SensorMeasurementSet _sensorMeasurements;

        public SimulationState(
            string runId,
            string dynamicsBackend,
            SpacecraftState spacecraft,
            ActuatorCommandSet? appliedCommands,
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

        // The command that acted over [t_k-1, t_k) and produced this state: command k-1 (DATA-5,
        // target-architecture §4). None at t_0, and none from a source that applies no commands.
        public ActuatorCommandSet? AppliedCommands { get; }
        public bool HasAppliedCommands => AppliedCommands.HasValue;

        // Present exactly when the spacecraft state is in ITRF93, that is, when SPICE produced it.
        public EnvironmentState? Environment { get; }
        public bool HasEnvironment => Environment.HasValue;

        // Measurements the backend's sensor models produced at t_k (D10).
        public SensorMeasurementSet SensorMeasurements => _sensorMeasurements ?? SensorMeasurementSet.Empty;

        public bool IsValid =>
            !string.IsNullOrWhiteSpace(RunId) &&
            !string.IsNullOrWhiteSpace(DynamicsBackend) &&
            Spacecraft.IsValid &&
            (!HasAppliedCommands ||
                (AppliedCommands.Value.IsValid && AppliedCommands.Value.Sequence == Spacecraft.Sequence - 1)) &&
            Spacecraft.EarthFixedFrame != ReferenceFrame.Unspecified &&
            HasEnvironment == (Spacecraft.EarthFixedFrame == ReferenceFrame.Itrf93) &&
            (!HasEnvironment || Environment.Value.IsValid);
    }
}
