namespace Argus.Simulation.Core
{
    // Replaceable dynamics boundary, implemented by AnalyticSimulationEngine (dev and test fixture,
    // D9) and BasiliskEngine (headless/Host). Step ordering (DATA-5, target-architecture §4):
    // TryReset returns the state at t_0, and TryStep applies command k over [t_k, t_k+1) and returns
    // the state at t_k+1, so a controller can compute command k from the state at t_k.
    public interface ISimulationEngine
    {
        string BackendName { get; }
        bool IsInitialized { get; }

        // Configures a run; produces no state.
        void Initialize(SimulationConfiguration configuration);

        // Rebuilds the run from its configuration and returns the state at t_0, which carries no
        // applied command.
        bool TryReset(out SimulationState state);

        // input.Sequence is k and input.Commands is command k; returns the state at t_k+1, whose
        // AppliedCommands is command k.
        bool TryStep(SimulationStepInput input, out SimulationState state);
    }
}
