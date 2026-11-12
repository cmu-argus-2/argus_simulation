using Argus.Simulation.Core;

namespace Argus.Simulation.Tests
{
    // The analytic engine, declaring that it applies actuator commands as BasiliskEngine does, and
    // keeping the last command it got. The analytic orbit still ignores them.
    internal sealed class ApplyingEngine : ISimulationEngine
    {
        private readonly ISimulationEngine _inner;

        public ApplyingEngine(ISimulationEngine inner) => _inner = inner;

        public DeviceCommandSet? LastCommands { get; private set; }
        public string BackendName => _inner.BackendName;
        public bool IsInitialized => _inner.IsInitialized;
        public bool AppliesActuatorCommands => true;
        public void Initialize(SimulationConfiguration configuration) => _inner.Initialize(configuration);
        public bool TryReset(out SimulationState state) => _inner.TryReset(out state);

        public bool TryStep(SimulationStepInput input, out SimulationState state)
        {
            LastCommands = input.Commands;
            return _inner.TryStep(input, out state);
        }
    }
}
