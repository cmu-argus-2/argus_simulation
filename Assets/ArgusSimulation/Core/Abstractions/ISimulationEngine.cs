namespace Argus.Simulation.Core
{
    // Replaceable dynamics boundary, implemented by AnalyticSimulationEngine (dev and test fixture,
    // D9) and BasiliskEngine (headless/Host).
    public interface ISimulationEngine
    {
        string BackendName { get; }
        bool IsInitialized { get; }

        void Initialize(SimulationConfiguration configuration);
        void Reset();
        bool TryStep(SimulationStepInput input, out SimulationState state);
    }
}
