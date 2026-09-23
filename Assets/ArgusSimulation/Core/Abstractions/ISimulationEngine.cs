namespace Argus.Simulation.Core
{
    // Replaceable dynamics boundary. A future Basilisk adapter implements this interface.
    public interface ISimulationEngine
    {
        string BackendName { get; }
        bool IsInitialized { get; }

        void Initialize(SimulationConfiguration configuration);
        void Reset();
        bool TryStep(SimulationStepInput input, out SimulationSnapshot snapshot);
    }
}
